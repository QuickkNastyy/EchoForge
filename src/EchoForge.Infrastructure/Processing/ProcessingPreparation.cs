using System.Text.Json;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Processing;
using EchoForge.Infrastructure.Artifacts;

namespace EchoForge.Infrastructure.Processing;

/// <summary>How far preparation for a production profile has got.</summary>
public enum PreparationStage
{
    Idle,

    /// <summary>Looking at what is installed. Cheap and offline.</summary>
    CheckingArtifacts,

    /// <summary>Fetching pinned artifacts that are missing.</summary>
    Downloading,

    /// <summary>Streaming source chunks into 16 kHz mono processing audio.</summary>
    PreparingAudio,

    PlanningWindows,

    /// <summary>Everything a production run needs is present and verified.</summary>
    Ready,

    /// <summary>Artifacts are missing and installing them was not asked for.</summary>
    ArtifactsMissing,

    Failed,
    Cancelled,

    /// <summary>Refused because recording has priority.</summary>
    Blocked,
}

/// <summary>Progress during preparation. Names a stage, never content.</summary>
public sealed class PreparationProgressEventArgs(PreparationStage stage, string detail, double fraction) : EventArgs
{
    public PreparationStage Stage { get; } = stage;

    public string Detail { get; } = detail;

    public double Fraction { get; } = Math.Clamp(fraction, 0, 1);
}

/// <summary>What preparation produced, or why it stopped.</summary>
public sealed record PreparationResult(
    PreparationStage Stage,
    string Message,
    string? FailureCode = null,
    WindowPlan? Plan = null,
    DerivativeSet? Derivatives = null,
    IReadOnlyList<ArtifactState>? Artifacts = null)
{
    public bool IsReady => Stage == PreparationStage.Ready;
}

/// <summary>
/// Everything a production transcription needs, done before any recogniser is loaded.
///
/// <para>
/// It installs the pinned artifacts, converts the immutable source chunks into the 16 kHz mono
/// audio a recogniser wants, and divides the result into overlapping windows. It stops there. No
/// model is loaded and nothing is transcribed during preparation; inference is a separate,
/// short-lived worker job so its model cannot remain resident while EchoForge is idle.
/// </para>
///
/// <para>
/// Splitting preparation from inference is deliberate. All of this is expensive, all of it is
/// re-runnable, and all of it can be proven correct on its own — which is exactly what cannot be
/// said of a stage that also happens to run a model.
/// </para>
/// </summary>
public sealed class ProcessingPreparation(
    ISessionStore sessions,
    ArtifactRegistry registry,
    DerivativeBuilder? derivatives = null)
{
    private readonly ISessionStore _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly ArtifactRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly DerivativeBuilder _derivatives = derivatives ?? new DerivativeBuilder(sessions);

    public event EventHandler<PreparationProgressEventArgs>? Progress;

    public ArtifactRegistry Registry => _registry;

    /// <summary>Where a session's window plans live, one directory per planning version.</summary>
    public static string PlanPath(SessionPaths paths, WindowPlanOptions options) =>
        Path.Combine(paths.Root, "derived", "windows", options.PlanningVersion, "plan.json");

    /// <summary>The profiles this build can offer, and whether each is ready to run.</summary>
    public IReadOnlyList<(ProcessingProfile Profile, bool Ready)> Profiles() =>
        [.. _registry.Profiles().Select(p => (p, _registry.IsProfileReady(p)))];

    public async Task<PreparationResult> PrepareAsync(
        TranscriptionRequest request,
        string profileId,
        bool installMissing,
        DerivativeOptions? derivativeOptions = null,
        WindowPlanOptions? windowOptions = null,
        string? planningIdentity = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        derivativeOptions ??= new DerivativeOptions();
        windowOptions ??= new WindowPlanOptions();

        ProcessingProfile? profile = _registry.Profile(profileId);
        if (profile is null)
        {
            return new PreparationResult(
                PreparationStage.Failed,
                "That processing profile is not available in this build.",
                "unknown_profile");
        }

        try
        {
            PreparationResult? blocked = await EnsureArtifactsAsync(profile, installMissing, cancellationToken)
                .ConfigureAwait(false);

            if (blocked is not null)
            {
                return blocked;
            }

            Report(PreparationStage.PreparingAudio, "Preparing audio for transcription", 0.4);

            DerivativeBuildResult built = await _derivatives
                .BuildAsync(request, derivativeOptions, cancellationToken)
                .ConfigureAwait(false);

            if (!built.Succeeded)
            {
                return built.Code == "cancelled"
                    ? new PreparationResult(PreparationStage.Cancelled, "Preparing audio was cancelled.", built.Code)
                    : new PreparationResult(
                        PreparationStage.Failed,
                        DescribeDerivativeFailure(built.Code),
                        built.Code);
            }

            Report(PreparationStage.PlanningWindows, "Planning transcription windows", 0.9);
            cancellationToken.ThrowIfCancellationRequested();

            SessionPaths paths = _sessions.Resolve(request.SessionId);
            string planPath = PlanPath(paths, windowOptions);

            WindowPlan plan = TranscriptionWindowPlanner.Plan(
                request, built.Set!, planningIdentity ?? profileId, windowOptions, LoadCheckpoints(planPath));

            Save(planPath, plan);

            Report(PreparationStage.Ready, "Ready for production transcription", 1);

            return new PreparationResult(
                PreparationStage.Ready,
                $"Ready: {plan.Windows.Count} transcription windows prepared. " +
                "Starting transcription will launch the selected short-lived ASR worker.",
                null,
                plan,
                built.Set,
                _registry.Status(profile));
        }
        catch (OperationCanceledException)
        {
            return new PreparationResult(PreparationStage.Cancelled, "Preparation was cancelled.", "cancelled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PreparationResult(
                PreparationStage.Failed,
                "Preparation could not be completed. Your recording is unchanged.",
                "preparation_failed");
        }
    }

    /// <summary>
    /// Makes sure the profile's artifacts are installed, downloading them only when asked.
    ///
    /// <para>
    /// Returns null when everything is present. A non-null result means preparation stopped, and
    /// says whether that was because artifacts are missing, a download failed, or recording took
    /// priority.
    /// </para>
    /// </summary>
    private async Task<PreparationResult?> EnsureArtifactsAsync(
        ProcessingProfile profile,
        bool installMissing,
        CancellationToken cancellationToken)
    {
        Report(PreparationStage.CheckingArtifacts, "Checking installed models", 0.05);

        IReadOnlyList<ArtifactState> states = _registry.Status(profile);
        if (states.All(s => s.IsUsable))
        {
            return null;
        }

        if (!installMissing)
        {
            long bytes = states.Where(s => !s.IsUsable).Sum(s => s.TotalBytes);
            return new PreparationResult(
                PreparationStage.ArtifactsMissing,
                $"This profile needs {Describe(bytes)} of models downloaded before it can run.",
                "artifacts_missing",
                Artifacts: states);
        }

        Report(PreparationStage.Downloading, "Downloading models", 0.1);

        Progress<ArtifactProgressEventArgs> artifactProgress = new(p =>
            Report(
                PreparationStage.Downloading,
                $"Downloading models ({Describe(p.BytesCompleted)} of {Describe(p.TotalBytes)})",
                0.1 + (0.3 * p.Fraction)));

        IReadOnlyList<ArtifactState> installed = await _registry
            .EnsureProfileAsync(profile.Id, artifactProgress, cancellationToken)
            .ConfigureAwait(false);

        if (installed.All(s => s.IsUsable))
        {
            return null;
        }

        ArtifactState failed = installed.First(s => !s.IsUsable);
        return new PreparationResult(
            PreparationStage.Failed,
            $"A required model could not be installed: {failed.Detail ?? failed.Status.ToString()}",
            "artifact_install_failed",
            Artifacts: installed);
    }

    // -- the plan on disk ------------------------------------------------------------------------

    private static IReadOnlyList<WindowCheckpoint>? LoadCheckpoints(string planPath)
    {
        if (!File.Exists(planPath))
        {
            return null;
        }

        try
        {
            WindowPlan? existing = JsonSerializer.Deserialize<WindowPlan>(File.ReadAllBytes(planPath), WindowPlan.Json);
            return existing?.Checkpoints;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable plan costs a re-run, not correctness: every window simply starts
            // pending again.
            return null;
        }
    }

    private static void Save(string planPath, WindowPlan plan)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);

        string temporary = planPath + ".partial";
        using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(plan, WindowPlan.Json));
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, planPath, overwrite: true);
    }

    private void Report(PreparationStage stage, string detail, double fraction) =>
        Progress?.Invoke(this, new PreparationProgressEventArgs(stage, detail, fraction));

    private static string DescribeDerivativeFailure(string? code) => code switch
    {
        "source_audio_invalid" =>
            "Some of that recording's audio could not be read, so it was not prepared. The files are left exactly as they are.",
        "derivative_write_failed" =>
            "The prepared audio could not be saved. Check free disk space; your recording is unchanged.",
        _ => "That recording could not be prepared. Your recording is unchanged.",
    };

    private static string Describe(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:0.0} GB",
        >= 1_000_000 => $"{bytes / 1_000_000.0:0} MB",
        >= 1_000 => $"{bytes / 1_000.0:0} kB",
        _ => $"{bytes} bytes",
    };
}
