using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Workers;

namespace EchoForge.Core.Processing;

/// <summary>
/// Divides a prepared session into transcription work.
///
/// <para>
/// Four rules shape every plan. <b>Tracks stay apart</b>, because the microphone being You and the
/// endpoint being everyone else is the only deterministic speaker signal EchoForge has, and mixing
/// the two would throw it away. <b>Windows stay inside one epoch</b>, because an epoch boundary is
/// a moment when recording genuinely stopped and audio either side of it is not continuous speech.
/// <b>Windows overlap</b>, so a sentence spoken across a boundary is heard whole by at least one
/// of them. And <b>source chunk boundaries are ignored</b>: they fall every sixty seconds for
/// storage reasons and have nothing to do with where anybody stopped talking.
/// </para>
///
/// <para>
/// The plan is a pure function of the session, the derivatives, and the options. Re-planning after
/// a failure produces the same window IDs in the same order, which is what lets a re-run reuse
/// what already succeeded instead of starting again.
/// </para>
/// </summary>
public static class TranscriptionWindowPlanner
{
    public static WindowPlan Plan(
        TranscriptionRequest request,
        DerivativeSet derivatives,
        string processingProfile,
        WindowPlanOptions? options = null,
        IReadOnlyList<WindowCheckpoint>? existing = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(derivatives);
        ArgumentException.ThrowIfNullOrWhiteSpace(processingProfile);

        options ??= new WindowPlanOptions();

        if (options.WindowSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.WindowSeconds, "a window must have a length");
        }

        if (options.OverlapSeconds < 0 || options.OverlapSeconds >= options.WindowSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.OverlapSeconds, "overlap must be shorter than the window it joins");
        }

        string manifest = TranscriptionRequestBuilderBridge.SourceManifest(request);
        List<TranscriptionWindow> windows = [];

        foreach (RequestTrack track in request.Tracks)
        {
            DerivativeRecord? derivative = derivatives.For(track.SourceTrack);
            if (derivative is null)
            {
                continue;
            }

            foreach (RequestEpoch epoch in request.Epochs)
            {
                windows.AddRange(PlanEpoch(request, track, epoch, derivative, processingProfile, options, manifest));
            }
        }

        return new WindowPlan
        {
            SessionId = request.SessionId,
            SourceManifestSha256 = manifest,
            ProcessingProfile = processingProfile,
            PlanningVersion = options.PlanningVersion,
            StrategyId = options.StrategyId,
            WindowSeconds = options.WindowSeconds,
            OverlapSeconds = options.OverlapSeconds,
            Windows = windows,
            Checkpoints = Reconcile(windows, existing),
        };
    }

    /// <summary>
    /// Windows for one track inside one epoch.
    ///
    /// <para>
    /// Only the stretch this track actually recorded in is covered. An epoch where the other track
    /// was capturing and this one was not is silence in the derivative, and asking a recogniser to
    /// listen to ten minutes of nothing costs time and invites hallucination.
    /// </para>
    /// </summary>
    private static IEnumerable<TranscriptionWindow> PlanEpoch(
        TranscriptionRequest request,
        RequestTrack track,
        RequestEpoch epoch,
        DerivativeRecord derivative,
        string processingProfile,
        WindowPlanOptions options,
        string manifest)
    {
        List<RequestChunk> chunks = [.. track.Chunks.Where(c => c.Epoch == epoch.Index).OrderBy(c => c.Index)];
        if (chunks.Count == 0)
        {
            yield break;
        }

        double coveredStart = Math.Max(epoch.StartSeconds, chunks.Min(c => c.StartSeconds));
        double coveredEnd = Math.Min(epoch.EndSeconds, chunks.Max(c => c.EndSeconds));

        if (coveredEnd <= coveredStart)
        {
            yield break;
        }

        int rate = derivative.SampleRate;
        int ordinal = 0;
        double cursor = coveredStart;

        while (true)
        {
            double end = Math.Min(cursor + options.WindowSeconds, coveredEnd);
            bool last = end >= coveredEnd - Tolerance;

            yield return Build(
                request, track, epoch, derivative, processingProfile, options, manifest,
                ordinal,
                cursor,
                end,
                overlapBefore: ordinal == 0 ? 0 : options.OverlapSeconds,
                overlapAfter: last ? 0 : options.OverlapSeconds,
                rate);

            if (last)
            {
                yield break;
            }

            // Advance by the window less the overlap, so adjacent windows share exactly that
            // much audio. Taken from this window's end rather than accumulated, so the last
            // window of a long meeting sits where the arithmetic says it should.
            //
            // A short final window is fine and expected, but it can never be degenerate: it runs
            // from the previous window's end minus the overlap, so it is always at least as long
            // as the overlap itself. There is no remainder small enough to need special handling.
            cursor = end - options.OverlapSeconds;
            ordinal++;
        }
    }

    /// <summary>Slack for comparing times that crossed a double. A microsecond is far below a frame.</summary>
    private const double Tolerance = 1e-9;

    private static TranscriptionWindow Build(
        TranscriptionRequest request,
        RequestTrack track,
        RequestEpoch epoch,
        DerivativeRecord derivative,
        string processingProfile,
        WindowPlanOptions options,
        string manifest,
        int ordinal,
        double startSeconds,
        double endSeconds,
        double overlapBefore,
        double overlapAfter,
        int rate)
    {
        long startFrame = FrameAt(startSeconds, rate);
        long endFrame = Math.Min(FrameAt(endSeconds, rate), derivative.TotalFrames);

        string id = string.Create(
            CultureInfo.InvariantCulture,
            $"w-{track.SourceTrack}-e{epoch.Index:D3}-{ordinal:D4}");

        // Everything the result depends on. Anything that could change the audio, the boundaries,
        // or the recogniser changes this, and a stale checkpoint stops matching.
        string fingerprint = Fingerprint(
            request.SessionId,
            track.SourceTrack,
            epoch.Index,
            derivative.Sha256,
            derivative.ProcessingVersion,
            startFrame,
            endFrame,
            processingProfile,
            options.StrategyId,
            options.PlanningVersion,
            manifest);

        return new TranscriptionWindow
        {
            Id = id,
            SourceTrack = track.SourceTrack,
            Epoch = epoch.Index,
            DerivativeRelativePath = derivative.RelativePath,
            DerivativeSha256 = derivative.Sha256,
            StartFrame = startFrame,
            EndFrame = endFrame,
            SessionStartSeconds = startSeconds,
            SessionEndSeconds = endSeconds,
            OverlapBeforeSeconds = overlapBefore,
            OverlapAfterSeconds = overlapAfter,
            InputFingerprint = fingerprint,
        };
    }

    /// <summary>
    /// Keeps the checkpoints that still describe the plan, and forgets the rest.
    ///
    /// <para>
    /// A successful window survives a re-plan only when its fingerprint is unchanged. Failed and
    /// cancelled ones are dropped back to pending, because a failure says nothing about whether
    /// the work can succeed now — and, crucially, dropping them does not disturb the windows that
    /// already finished.
    /// </para>
    /// </summary>
    private static List<WindowCheckpoint> Reconcile(
        IReadOnlyList<TranscriptionWindow> windows,
        IReadOnlyList<WindowCheckpoint>? existing)
    {
        if (existing is null || existing.Count == 0)
        {
            return [.. windows.Select(Pending)];
        }

        Dictionary<string, WindowCheckpoint> byId = new(StringComparer.Ordinal);
        foreach (WindowCheckpoint checkpoint in existing)
        {
            byId[checkpoint.WindowId] = checkpoint;
        }

        List<WindowCheckpoint> reconciled = [];
        foreach (TranscriptionWindow window in windows)
        {
            reconciled.Add(
                byId.TryGetValue(window.Id, out WindowCheckpoint? found) && found.IsReusableFor(window)
                    ? found
                    : Pending(window));
        }

        return reconciled;
    }

    private static WindowCheckpoint Pending(TranscriptionWindow window) => new()
    {
        WindowId = window.Id,
        State = WindowCheckpointState.Pending,
        InputFingerprint = window.InputFingerprint,
    };

    /// <summary>The same rounding the derivative was laid out with, so frames line up exactly.</summary>
    private static long FrameAt(double sessionSeconds, int sampleRate) =>
        (long)Math.Round(Math.Max(0, sessionSeconds) * sampleRate, MidpointRounding.AwayFromZero);

    private static string Fingerprint(params object[] parts)
    {
        StringBuilder builder = new();
        foreach (object part in parts)
        {
            builder.Append(Convert.ToString(part, CultureInfo.InvariantCulture)).Append('');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}

/// <summary>
/// Reaches the request builder's manifest digest without the planner depending on transcript
/// construction. One definition of what identifies a session's audio, used by both.
/// </summary>
internal static class TranscriptionRequestBuilderBridge
{
    public static string SourceManifest(TranscriptionRequest request) =>
        Transcripts.TranscriptionRequestBuilder.SourceManifestSha256(request);
}
