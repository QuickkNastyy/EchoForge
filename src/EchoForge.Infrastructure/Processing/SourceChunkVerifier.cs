using System.Globalization;
using System.Security.Cryptography;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Sessions;

namespace EchoForge.Infrastructure.Processing;

/// <summary>The verdict on a session's source audio. A refusal names what is wrong.</summary>
public sealed record SourceVerification(bool Ok, string? Code, string? Detail, int ChunksChecked)
{
    public static SourceVerification Pass(int chunks) => new(true, null, null, chunks);

    public static SourceVerification Refuse(string code, string detail) => new(false, code, detail, 0);
}

/// <summary>
/// Checks that a session's audio is actually there and actually unchanged before anything is
/// asked to transcribe it.
///
/// <para>
/// The digest recorded when a chunk was finalized is the identity of that chunk. Re-checking it
/// here is what makes "the audio changed underneath this session" a refusal rather than a
/// transcript that quietly describes different audio from the one its manifest names. It is also
/// the cheapest possible protection against transcribing a half-recovered session.
/// </para>
///
/// <para>
/// Nothing here writes, moves, or repairs anything. A session that fails verification is reported
/// and left exactly as it is.
/// </para>
/// </summary>
public static class SourceChunkVerifier
{
    /// <summary>States in which a session's audio is settled enough to be processed.</summary>
    private static readonly SessionState[] Processable = [SessionState.Recorded, SessionState.NeedsAttention];

    public static SourceVerification Verify(SessionSnapshot snapshot, string sessionRoot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoot);

        if (!Processable.Contains(snapshot.State))
        {
            return SourceVerification.Refuse(
                "session_not_settled",
                $"the session is {snapshot.State.ToString().ToLowerInvariant()} and its audio is not finished");
        }

        if (!snapshot.HasAudio)
        {
            return SourceVerification.Refuse("no_audio", "the session has no finalized audio chunks");
        }

        if (snapshot.Epochs.Count == 0)
        {
            return SourceVerification.Refuse("no_epochs", "the session records no capture epochs");
        }

        if (snapshot.Epochs.Any(e => e.IsOpen))
        {
            // An open epoch means the recorder never closed it. Its length is unknown, so every
            // transcript time inside it would be unbounded.
            return SourceVerification.Refuse("epoch_open", "the session still has an unfinished capture epoch");
        }

        string root = Path.GetFullPath(sessionRoot);
        int checkedChunks = 0;

        foreach (SessionTrack track in snapshot.Tracks)
        {
            HashSet<int> seen = [];

            foreach (AudioChunkMetadata chunk in track.Chunks.OrderBy(c => c.EpochIndex).ThenBy(c => c.Index))
            {
                if (!seen.Add(chunk.Index))
                {
                    return SourceVerification.Refuse(
                        "duplicate_chunk",
                        Describe($"chunk {chunk.Index} appears twice on the {track.Track} track"));
                }

                if (chunk.SampleRate <= 0 || chunk.Channels <= 0)
                {
                    return SourceVerification.Refuse(
                        "chunk_format_invalid",
                        Describe($"chunk {chunk.Index} on the {track.Track} track has an unusable format"));
                }

                string resolved;
                try
                {
                    resolved = Path.GetFullPath(Path.Combine(root, chunk.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return SourceVerification.Refuse(
                        "chunk_path_invalid",
                        Describe($"chunk {chunk.Index} on the {track.Track} track has a malformed path"));
                }

                if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    return SourceVerification.Refuse(
                        "chunk_path_escapes",
                        Describe($"chunk {chunk.Index} on the {track.Track} track points outside the session"));
                }

                if (!File.Exists(resolved))
                {
                    return SourceVerification.Refuse(
                        "chunk_missing",
                        Describe($"chunk {chunk.Index} on the {track.Track} track is missing"));
                }

                string? mismatch = VerifyDigest(resolved, chunk);
                if (mismatch is not null)
                {
                    return SourceVerification.Refuse("chunk_changed", mismatch);
                }

                checkedChunks++;
            }
        }

        return SourceVerification.Pass(checkedChunks);
    }

    private static string? VerifyDigest(string path, AudioChunkMetadata chunk)
    {
        if (string.IsNullOrWhiteSpace(chunk.Sha256))
        {
            // Nothing to compare against. The chunk is still usable; it simply cannot be proven
            // unchanged, and inventing a comparison would be worse than admitting that.
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            string actual = Convert.ToHexStringLower(SHA256.HashData(stream));

            return string.Equals(actual, chunk.Sha256, StringComparison.OrdinalIgnoreCase)
                ? null
                : Describe($"chunk {chunk.Index} on the {chunk.Track} track no longer matches the digest recorded when it was saved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Describe($"chunk {chunk.Index} on the {chunk.Track} track could not be read ({ex.GetType().Name})");
        }
    }

    /// <summary>Diagnostics name indexes and tracks, never paths. A session path is private.</summary>
    private static string Describe(FormattableString message) => message.ToString(CultureInfo.InvariantCulture);
}
