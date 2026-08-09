using System.Text.Json.Serialization;

namespace EchoForge.Contracts.Artifacts;

/// <summary>
/// One pinned file EchoForge is allowed to download.
///
/// <para>
/// Mirrors <c>schemas/artifact-manifest.schema.json</c>, which stays authoritative. Every field
/// here exists so that a download can be refused rather than trusted: an artifact is identified
/// by an immutable revision, fetched from one exact URL, and accepted only when its length and
/// digest are the ones recorded when it was pinned.
/// </para>
/// </summary>
public sealed record ArtifactEntry
{
    [JsonPropertyName("artifact_id")]
    public required string ArtifactId { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>The project this came from. Identity, not a fetch location.</summary>
    [JsonPropertyName("repository")]
    public required string Repository { get; init; }

    /// <summary>The one exact URL this file is fetched from.</summary>
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    /// <summary>Full immutable commit SHA or release tag. Never a branch or a moving alias.</summary>
    [JsonPropertyName("revision")]
    public required string Revision { get; init; }

    [JsonPropertyName("filename")]
    public required string FileName { get; init; }

    /// <summary>
    /// Optional collision-free name used only when assembling a multi-repository model directory.
    /// The downloaded file retains <see cref="FileName"/> in its independently verified artifact
    /// store; old manifests omit this property and stage under that original name.
    /// </summary>
    [JsonPropertyName("stage_filename")]
    public string? StageFileName { get; init; }

    [JsonPropertyName("size_bytes")]
    public required long SizeBytes { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("license")]
    public required string License { get; init; }

    [JsonPropertyName("license_file")]
    public required string LicenseFile { get; init; }

    [JsonPropertyName("provenance")]
    public string? Provenance { get; init; }

    [JsonPropertyName("runtime_version")]
    public required string RuntimeVersion { get; init; }

    /// <summary>Processing profiles this artifact belongs to. <c>any</c> means every profile.</summary>
    [JsonPropertyName("profiles")]
    public IReadOnlyList<string> Profiles { get; init; } = [];

    [JsonPropertyName("verified_utc")]
    public required DateTimeOffset VerifiedUtc { get; init; }

    public bool BelongsTo(string profile) =>
        Profiles.Contains("any", StringComparer.Ordinal) ||
        Profiles.Contains(profile, StringComparer.Ordinal);

    public string EffectiveStageFileName => StageFileName ?? FileName;
}

/// <summary>Every file EchoForge may download. Nothing outside it may be fetched.</summary>
public sealed record ArtifactManifest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    [JsonPropertyName("artifacts")]
    public IReadOnlyList<ArtifactEntry> Artifacts { get; init; } = [];
}

/// <summary>Where an artifact has got to. Only <see cref="Installed"/> may be used.</summary>
public enum ArtifactStatus
{
    /// <summary>Nothing on disk.</summary>
    NotInstalled,

    /// <summary>Bytes are arriving. A partial file exists.</summary>
    Downloading,

    /// <summary>Bytes are all here and are being checked.</summary>
    Verifying,

    /// <summary>Present, the right length, and matching the pinned digest.</summary>
    Installed,

    /// <summary>Present but wrong. Never used, never silently replaced.</summary>
    Invalid,

    /// <summary>The attempt did not finish. Says why.</summary>
    Failed,
}

/// <summary>What is known about one artifact on this machine.</summary>
public sealed record ArtifactState(
    ArtifactEntry Entry,
    ArtifactStatus Status,
    string? Detail = null,
    long BytesOnDisk = 0)
{
    public long TotalBytes => Entry.SizeBytes;

    public double Fraction => Entry.SizeBytes <= 0
        ? 0
        : Math.Clamp((double)BytesOnDisk / Entry.SizeBytes, 0, 1);

    /// <summary>True only for an artifact that has been proven, never merely present.</summary>
    public bool IsUsable => Status == ArtifactStatus.Installed;
}

/// <summary>Progress while bytes move. Carries no path and no meeting content.</summary>
public sealed class ArtifactProgressEventArgs(
    string artifactId,
    ArtifactStatus status,
    long bytesCompleted,
    long totalBytes) : EventArgs
{
    public string ArtifactId { get; } = artifactId;

    public ArtifactStatus Status { get; } = status;

    public long BytesCompleted { get; } = bytesCompleted;

    public long TotalBytes { get; } = totalBytes;

    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesCompleted / TotalBytes, 0, 1);
}

/// <summary>
/// A named set of artifacts that must all be installed before that kind of processing can run.
///
/// <para>
/// Derived from the manifest rather than listed separately: an artifact declares the profiles it
/// belongs to, so there is no second place that can disagree about what a profile needs.
/// </para>
/// </summary>
public sealed record ProcessingProfile(
    string Id,
    string Description,
    IReadOnlyList<ArtifactEntry> Artifacts)
{
    public long TotalBytes => Artifacts.Sum(a => a.SizeBytes);

    /// <summary>The placeholder profile, which needs nothing downloaded and recognises no speech.</summary>
    public const string Mock = "mock";

    public const string CpuInt8 = "cpu-int8";
    public const string CudaFp16 = "cuda-fp16";
    public const string CudaInt8Float16 = "cuda-int8-float16";

    /// <summary>
    /// Model artifact profiles are independent of compute. A verified model directory can be
    /// loaded with any compute type the model/backend registry permits.
    /// </summary>
    public const string AsrWhisperLargeV3Turbo = "asr-whisper-large-v3-turbo";

    public const string AsrWhisperLargeV3 = "asr-whisper-large-v3";

    public const string AsrParakeetUnifiedEn06B = "asr-parakeet-unified-en-0.6b";

    public const string AsrCanaryQwen25B = "asr-canary-qwen-2.5b";

    /// <summary>
    /// What the NVIDIA models need before their weights mean anything: the tool EchoForge uses to
    /// provision its own CPython 3.11 and the hash-locked NeMo closure inside WSL.
    ///
    /// <para>
    /// Separate from the weights on purpose. Downloading a 2.5 GB checkpoint and calling the model
    /// installed, when nothing on the machine can load it, is exactly the state this profile
    /// exists to make impossible.
    /// </para>
    /// </summary>
    public const string AsrNemoRuntime = "asr-nemo-runtime";

    /// <summary>
    /// Summarisation profiles. Kept separate from the speech ones rather than reusing
    /// <see cref="CudaFp16"/> and friends, because the two stages need different files, run at
    /// different times, and one being installed says nothing about the other. A machine can
    /// transcribe perfectly well with no summary model on it at all.
    /// </summary>
    public const string SummaryCudaQ4 = "summary-cuda-q4";

    public const string SummaryCpuQ4 = "summary-cpu-q4";

    /// <summary>
    /// The comparison model's profile. Deliberately not one of <see cref="SummaryProfiles"/>: a
    /// bake-off candidate is something EchoForge measures, not something it will quietly start
    /// summarising with because it happens to be installed.
    /// </summary>
    public const string SummaryBakeoff = "summary-bakeoff";

    public const string SummaryGptOss20B = "summary-gpt-oss-20b";

    /// <summary>Every summarisation profile, most capable first.</summary>
    public static readonly IReadOnlyList<string> SummaryProfiles = [SummaryCudaQ4, SummaryCpuQ4];

    public static bool IsSummaryProfile(string? id) =>
        id is not null && SummaryProfiles.Contains(id, StringComparer.Ordinal);
}
