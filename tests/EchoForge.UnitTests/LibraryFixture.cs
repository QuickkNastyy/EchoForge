using System.Security.Cryptography;
using System.Text.Json;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Library;
using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;
using EchoForge.Core.Summaries;
using EchoForge.Infrastructure.Library;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Sessions;
using EchoForge.Infrastructure.Summaries;

namespace EchoForge.UnitTests;

/// <summary>
/// Builds real session folders for library tests.
///
/// <para>
/// Real ones on purpose: the whole claim of the index is that it can be rebuilt from what is on
/// disk, and a fixture that handed the projection an in-memory object would test everything
/// except that. Every meeting here is written through the same stores the application writes
/// through, and the index then reads the folders exactly as it will in production.
/// </para>
/// </summary>
public sealed class LibraryFixture : IDisposable
{
    private readonly TempDirectory _temp = new();
    private DateTimeOffset _clock = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    public LibraryFixture()
    {
        Sessions = new FileSessionStore(_temp.Path);
        Transcripts = new FileTranscriptionStore(Sessions);
        Summaries = new FileSummaryStore(Sessions);
        Aliases = new FileSpeakerAliasStore(Sessions);
        Projection = new LibraryProjection(Sessions, Transcripts, Summaries, Aliases);
    }

    public string Root => _temp.Path;

    public FileSessionStore Sessions { get; }

    public FileTranscriptionStore Transcripts { get; }

    public FileSummaryStore Summaries { get; }

    public FileSpeakerAliasStore Aliases { get; }

    public LibraryProjection Projection { get; }

    public string IndexPath => Path.Combine(_temp.Path, "library.db");

    public SqliteLibraryIndex NewIndex() => new(IndexPath, Projection);

    /// <summary>A fresh projection over the same folders, as a restart would produce.</summary>
    public LibraryProjection RestartedProjection()
    {
        FileSessionStore sessions = new(_temp.Path);
        return new LibraryProjection(
            sessions,
            new FileTranscriptionStore(sessions),
            new FileSummaryStore(sessions),
            new FileSpeakerAliasStore(sessions));
    }

    private DateTimeOffset Tick() => _clock = _clock.AddMinutes(1);

    // -- building meetings -------------------------------------------------------------------------

    /// <summary>Creates a recorded session with a snapshot the projection can read.</summary>
    public string AddSession(string sessionId, string? title = null, SessionState state = SessionState.Recorded)
    {
        Sessions.Create(sessionId);

        DateTimeOffset created = Tick();

        Sessions.WriteSnapshot(new SessionSnapshot(
            SessionId: sessionId,
            State: state,
            CreatedUtc: created,
            StartedUtc: created,
            EndedUtc: created.AddMinutes(30),
            Epochs: [new SessionEpoch(1, created, created.AddMinutes(30), 0, 1000, EpochEndReason.Stopped)],
            Tracks:
            [
                new SessionTrack(
                    SourceTrack.Microphone,
                    "device-mic",
                    "Microphone",
                    new CaptureFormat(48000, 1, 16),
                    [new AudioChunkMetadata(1, "tracks/microphone/chunks/000001.wav", SourceTrack.Microphone, 0, 60, 48000, 1, 48000 * 60, new string('a', 64), [])]),
            ],
            Title: title));

        return sessionId;
    }

    /// <summary>Activates a transcript revision containing the given lines.</summary>
    public TranscriptDocument AddTranscript(string sessionId, params (string Track, string Text)[] lines)
    {
        TranscriptionAttempt attempt = Transcripts.BeginAttempt(
            sessionId, $"job-t{Guid.NewGuid():n}", new string('a', 64), new TranscriptionOptions(), Tick());

        List<TranscriptSegment> segments = [];
        for (int i = 0; i < lines.Length; i++)
        {
            (string track, string text) = lines[i];
            (string speakerId, string speakerName) = TranscriptSpeakers.For(track);

            segments.Add(new TranscriptSegment
            {
                Id = $"segment-{i + 1:D6}",
                Epoch = 1,
                StartSeconds = i * 10.0,
                EndSeconds = (i * 10.0) + 8.0,
                SpeakerId = speakerId,
                SpeakerName = speakerName,
                SourceTrack = track,
                Text = text,
                Confidence = null,
                Language = "en",
                Words = [],
            });
        }

        TranscriptDocument transcript = new()
        {
            SessionId = sessionId,
            TranscriptRevision = attempt.Revision,
            CreatedAtUtc = Tick(),
            SourceManifestSha256 = new string('a', 64),
            DurationSeconds = Math.Max(1, lines.Length * 10.0),
            Model = new TranscriptModel("echoforge-mock", "mock", "mock-v1", "mock-v1", "none", false, "0.1.0"),
            Epochs = [new TranscriptEpoch(1, 0, Math.Max(1, lines.Length * 10.0))],
            Speakers =
            [
                new TranscriptSpeaker(TranscriptSpeakers.YouId, TranscriptSpeakers.YouName, TranscriptSpeakers.MicrophoneTrack),
                new TranscriptSpeaker(TranscriptSpeakers.RemoteId, TranscriptSpeakers.RemoteName, TranscriptSpeakers.SystemTrack),
            ],
            Languages = [new TranscriptLanguage(TranscriptSpeakers.MicrophoneTrack, "en", null)],
            Segments = segments,
        };

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(transcript, TranscriptDocument.Json);
        Directory.CreateDirectory(Path.GetDirectoryName(attempt.StagingPath)!);
        File.WriteAllBytes(attempt.StagingPath, payload);

        ActivationOutcome outcome = Transcripts.Activate(
            new ActivationRequest(attempt, Convert.ToHexStringLower(SHA256.HashData(payload)), transcript, 1, null),
            Tick());

        Assert.True(outcome.Activated, outcome.Refusal);
        return transcript;
    }

    /// <summary>Activates a summary revision generated from a given transcript revision.</summary>
    public SummaryDocument AddSummary(
        string sessionId,
        int fromTranscriptRevision,
        IEnumerable<(string Text, string SegmentId)>? decisions = null,
        IEnumerable<(string Task, string SegmentId, string? Owner)>? actions = null,
        string overview = "A short overview of what was discussed.")
    {
        SummaryAttempt attempt = Summaries.BeginAttempt(
            sessionId, $"job-s{Guid.NewGuid():n}", fromTranscriptRevision, new string('b', 64), new SummaryOptions(), Tick());

        TranscriptDocument? source = Transcripts.ReadTranscript(sessionId, fromTranscriptRevision);
        Assert.NotNull(source);

        SummaryEvidence Cite(string segmentId) =>
            SummaryValidator.Cite(source!, segmentId) ?? throw new InvalidOperationException($"no {segmentId}");

        SummaryDocument summary = new()
        {
            SessionId = sessionId,
            SummaryRevision = attempt.Revision,
            CreatedAtUtc = Tick(),
            TranscriptRevision = fromTranscriptRevision,
            TranscriptSha256 = new string('b', 64),
            PromptVersion = "meeting-summary-v1",
            Model = new SummaryModel("llama.cpp (cuda-32k)", "gemma-4-12b", "gemma-4-12b-it-qat-q4_0", "rev", 32768, false, true, "0.1.0"),
            Overview = overview,
            Decisions =
            [
                .. (decisions ?? []).Select((d, i) => new SummaryItem
                {
                    Id = $"decision-{i + 1:D3}",
                    Text = d.Text,
                    Certainty = SupportStatuses.Explicit,
                    Confidence = null,
                    Evidence = [Cite(d.SegmentId)],
                })
            ],
            ActionItems =
            [
                .. (actions ?? []).Select((a, i) => new SummaryAction
                {
                    Id = $"action-{i + 1:D3}",
                    Task = a.Task,
                    Certainty = SupportStatuses.Explicit,
                    Confidence = null,
                    Evidence = [Cite(a.SegmentId)],
                    Owner = a.Owner,
                    OwnerStatus = a.Owner is null ? SupportStatuses.Unknown : SupportStatuses.Explicit,
                    DueDate = null,
                    DueDateText = null,
                    DueDateStatus = SupportStatuses.Unknown,
                })
            ],
        };

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(summary, SummaryDocument.Json);
        Directory.CreateDirectory(Path.GetDirectoryName(attempt.StagingPath)!);
        File.WriteAllBytes(attempt.StagingPath, payload);

        SummaryActivation activation = Summaries.Activate(
            attempt, Convert.ToHexStringLower(SHA256.HashData(payload)), summary, Tick());

        Assert.True(activation.Activated, activation.Refusal);
        return summary;
    }

    public LibraryEntry Entry(string sessionId)
    {
        LibraryDocument? document = Projection.Build(sessionId);
        Assert.NotNull(document);
        return document!.Entry;
    }

    public void Dispose() => _temp.Dispose();
}
