using System.Globalization;
using System.Security.Cryptography;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Sessions;

namespace EchoForge.Infrastructure.Recovery;

/// <summary>Whether a metadata record may be believed, and why not when it may not.</summary>
public sealed record MetadataVerdict(bool Accepted, string? Reason)
{
    public static readonly MetadataVerdict Ok = new(true, null);

    public static MetadataVerdict Reject(string reason) => new(false, reason);
}

/// <summary>
/// Checks a chunk's <c>.meta.json</c> against the audio, the filesystem, and the journal before
/// reconciliation is allowed to treat it as canonical.
///
/// <para>
/// The authority model is deliberate. A finalized WAV plus a <em>validated</em> finalized metadata
/// record is the canonical source chunk; the journal is the canonical lifecycle ledger. Metadata
/// is therefore evidence, not testimony: it never overrides a contradictory WAV, path, filename,
/// or journal fact. Where they disagree, nothing is changed, nothing is journalled as canonical,
/// and the session is marked for a human to look at.
/// </para>
/// </summary>
public static class ChunkMetadataValidator
{
    /// <summary>The only record schema this build understands.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <param name="record">The record read from disk.</param>
    /// <param name="wavPath">The finalized WAV the record claims to describe.</param>
    /// <param name="trackDirectoryName">Directory the WAV sits under, e.g. "microphone".</param>
    /// <param name="sessionRoot">Session folder, for resolving the record's relative path.</param>
    /// <param name="journalChunks">Existing chunk events, so a contradiction can be detected.</param>
    /// <param name="repairer">Used to decode the WAV independently of anything that wrote it.</param>
    public static MetadataVerdict Verify(
        ChunkRecord record,
        string wavPath,
        string trackDirectoryName,
        string sessionRoot,
        IReadOnlyList<JournalEvent> journalChunks,
        IActiveChunkRepairer repairer)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(repairer);

        if (record.SchemaVersion != SupportedSchemaVersion)
        {
            return MetadataVerdict.Reject($"metadata schema version {record.SchemaVersion} is not supported");
        }

        if (!record.Finalized)
        {
            return MetadataVerdict.Reject("metadata is not marked finalized");
        }

        if (!string.Equals(record.Track, trackDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            return MetadataVerdict.Reject($"metadata track '{record.Track}' does not match directory '{trackDirectoryName}'");
        }

        string stem = Path.GetFileNameWithoutExtension(wavPath);
        if (!int.TryParse(stem, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fileIndex)
            || fileIndex != record.Index)
        {
            return MetadataVerdict.Reject($"metadata index {record.Index} does not match file name '{stem}'");
        }

        MetadataVerdict pathVerdict = VerifyPath(record, wavPath, sessionRoot);
        if (!pathVerdict.Accepted)
        {
            return pathVerdict;
        }

        if (record.SampleRate <= 0 || record.Channels <= 0 || record.BitsPerSample != 16)
        {
            return MetadataVerdict.Reject("metadata declares an unusable audio format");
        }

        if (record.Epoch < 1)
        {
            return MetadataVerdict.Reject($"metadata epoch {record.Epoch} is not a valid epoch");
        }

        if (record.Frames < 0 || !double.IsFinite(record.StartSeconds) || record.StartSeconds < 0)
        {
            return MetadataVerdict.Reject("metadata start time or frame count is out of range");
        }

        if (record.Discontinuities.Any(d => d is null || d.Frames < 0 || d.AtFrame < 0))
        {
            return MetadataVerdict.Reject("metadata contains an unusable discontinuity entry");
        }

        // Decode the audio without trusting anything that wrote it.
        ChunkValidation wav = repairer.Validate(wavPath);
        if (!wav.IsValid)
        {
            return MetadataVerdict.Reject($"the audio did not validate: {wav.Problem}");
        }

        if (wav.SampleRate != record.SampleRate || wav.Channels != record.Channels)
        {
            return MetadataVerdict.Reject(
                $"the audio is {wav.SampleRate} Hz / {wav.Channels} ch but the metadata claims " +
                $"{record.SampleRate} Hz / {record.Channels} ch");
        }

        if (wav.FrameCount != record.Frames)
        {
            return MetadataVerdict.Reject(
                $"the audio holds {wav.FrameCount} frames but the metadata claims {record.Frames}");
        }

        if (string.IsNullOrWhiteSpace(record.Sha256))
        {
            return MetadataVerdict.Reject("metadata carries no hash for the finalized audio");
        }

        string actual = Sha256(wavPath);
        if (!string.Equals(actual, record.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return MetadataVerdict.Reject("the audio does not match the hash recorded in its metadata");
        }

        return VerifyAgainstJournal(record, journalChunks);
    }

    /// <summary>
    /// The record's relative path must resolve to exactly this file, inside the session root.
    /// A path that escapes the session is refused outright rather than followed.
    /// </summary>
    private static MetadataVerdict VerifyPath(ChunkRecord record, string wavPath, string sessionRoot)
    {
        if (string.IsNullOrWhiteSpace(record.RelativePath))
        {
            return MetadataVerdict.Reject("metadata carries no relative path");
        }

        if (Path.IsPathRooted(record.RelativePath))
        {
            return MetadataVerdict.Reject("metadata relative path is absolute");
        }

        string root = Path.GetFullPath(sessionRoot);
        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(root, record.RelativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return MetadataVerdict.Reject("metadata relative path is malformed");
        }

        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!resolved.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return MetadataVerdict.Reject("metadata relative path points outside the session folder");
        }

        if (!string.Equals(resolved, Path.GetFullPath(wavPath), StringComparison.OrdinalIgnoreCase))
        {
            return MetadataVerdict.Reject("metadata relative path does not point at this audio file");
        }

        return MetadataVerdict.Ok;
    }

    /// <summary>
    /// The journal wins on lifecycle facts. If it already describes this chunk differently, the
    /// metadata is not allowed to quietly replace it.
    /// </summary>
    private static MetadataVerdict VerifyAgainstJournal(ChunkRecord record, IReadOnlyList<JournalEvent> journalChunks)
    {
        foreach (JournalEvent existing in journalChunks)
        {
            if (!string.Equals(existing.Field("track"), record.Track, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (existing.IntField("index") != record.Index)
            {
                continue;
            }

            string? sha = existing.Field("sha256");
            if (!string.IsNullOrEmpty(sha) && !string.Equals(sha, record.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return MetadataVerdict.Reject("the journal already records this chunk with a different hash");
            }

            long? frames = existing.LongField("frames");
            if (frames is not null && frames != record.Frames)
            {
                return MetadataVerdict.Reject("the journal already records this chunk with a different frame count");
            }

            int? epoch = existing.IntField("epoch");
            if (epoch is not null && epoch != record.Epoch)
            {
                return MetadataVerdict.Reject("the journal already records this chunk in a different epoch");
            }
        }

        return MetadataVerdict.Ok;
    }

    private static string Sha256(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
