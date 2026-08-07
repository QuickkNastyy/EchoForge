using EchoForge.Contracts.Playback;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Transcripts;
using EchoForge.Infrastructure.Processing;

namespace EchoForge.Infrastructure.Playback;

/// <summary>What preparing a meeting for listening produced, or why it could not.</summary>
public sealed record PlaybackPreparation(
    PlaybackDerivativeRecord? Record,
    string? AudioPath,
    string? Code,
    string? Message)
{
    public bool Succeeded => Record is not null && AudioPath is not null;

    public static PlaybackPreparation Fail(string code, string message) => new(null, null, code, message);
}

/// <summary>
/// Turns a stored session into audio that can be played from a timestamp.
///
/// <para>
/// The session is described to the playback builder with exactly the same request the transcription
/// pipeline is given, which is the whole point: the timeline a transcript's timestamps live on and
/// the timeline the audio is laid out on are computed once, by one piece of code, from one snapshot.
/// A second, playback-only notion of where things sit would be a second thing to be wrong.
/// </para>
///
/// <para>
/// Source chunks are verified first, with the same verifier transcription uses. A meeting whose
/// audio no longer matches what was recorded is refused rather than played with a hole in it.
/// </para>
/// </summary>
public sealed class PlaybackPreparer(ISessionStore sessions)
{
    private readonly ISessionStore _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    public event EventHandler<PlaybackBuildProgressEventArgs>? Progress;

    public async Task<PlaybackPreparation> PrepareAsync(
        string sessionId,
        PlaybackOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        options ??= new PlaybackOptions();

        SessionSnapshot? snapshot;
        try
        {
            snapshot = _sessions.ReadSnapshot(sessionId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PlaybackPreparation.Fail("session_unreadable", "That recording could not be read.");
        }

        if (snapshot is null)
        {
            return PlaybackPreparation.Fail("session_unreadable", "That recording could not be read.");
        }

        if (!snapshot.HasAudio)
        {
            return PlaybackPreparation.Fail("no_audio", "That recording has no saved audio to play.");
        }

        SessionPaths paths = _sessions.Resolve(sessionId);

        SourceVerification verification = SourceChunkVerifier.Verify(snapshot, paths.Root);
        if (!verification.Ok)
        {
            return PlaybackPreparation.Fail(verification.Code!, Describe(verification.Code!));
        }

        RequestBuildResult built = TranscriptionRequestBuilder.Build(
            snapshot,
            paths.Root,
            // Nothing is written to this path: playback needs the timeline, not an output file.
            Path.Combine(paths.Root, "playback.request"),
            transcriptRevision: 1,
            createdAtUtc: snapshot.CreatedUtc,
            options: new RequestOptions { Backend = "playback" });

        if (!built.Succeeded)
        {
            return PlaybackPreparation.Fail(
                built.Failure?.Code ?? "playback_unavailable",
                "That recording's timeline is incomplete, so it cannot be played.");
        }

        PlaybackDerivativeBuilder builder = new(_sessions);
        void Forward(object? sender, PlaybackBuildProgressEventArgs e) => Progress?.Invoke(this, e);

        builder.Progress += Forward;
        try
        {
            PlaybackBuildResult result = await builder
                .BuildAsync(built.Request!, options, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                return PlaybackPreparation.Fail(result.Code ?? "playback_unavailable", Describe(result.Code!));
            }

            string audioPath = Path.Combine(
                PlaybackDerivativeBuilder.PlaybackDirectory(paths, options), "playback.wav");

            return new PlaybackPreparation(result.Record, audioPath, null, null);
        }
        finally
        {
            builder.Progress -= Forward;
        }
    }

    /// <summary>
    /// Safe sentences chosen by code. None of them can contain a path or anything read off disk.
    /// </summary>
    private static string Describe(string code) => code switch
    {
        "cancelled" => "Preparing the audio was cancelled. Nothing was changed.",
        "no_audio" => "That recording has no saved audio to play.",
        "chunk_missing" => "Some of that recording's audio is missing, so it cannot be played. Nothing has been deleted.",
        "chunk_changed" => "That recording's audio no longer matches what was saved, so it will not be played.",
        "source_audio_invalid" or "duplicate_chunk" or "chunk_format_invalid" or "chunk_path_invalid"
            or "chunk_path_escapes" => "That recording's saved audio could not be read, so it cannot be played.",
        "playback_write_failed" => "The audio could not be prepared. Check there is space on the drive and try again.",
        "session_not_settled" => "That recording has not finished saving yet. Try again once it has.",
        "no_epochs" or "epoch_open" => "That recording did not finish cleanly, so its timeline is incomplete.",
        _ => "That recording cannot be played as it stands.",
    };
}
