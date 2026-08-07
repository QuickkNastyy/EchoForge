namespace EchoForge.Contracts.Setup;

/// <summary>
/// The installable pieces EchoForge needs, named once.
///
/// <para>
/// Deliberately coarser than the artifact manifest. A manifest entry is one file with one digest;
/// a component is something a person can be told about and can repair — "the interpreter", "the
/// speech model" — and one component may be several files. Repair operates at this level, which is
/// what stops a corrupt llama.cpp archive taking a 7 GB model down with it.
/// </para>
/// </summary>
public enum RuntimeComponentId
{
    /// <summary>EchoForge's own CPython. Everything that runs a worker needs it.</summary>
    PythonRuntime,

    /// <summary>The installed worker packages: faster-whisper, CTranslate2 and their closure.</summary>
    WorkerEnvironment,

    /// <summary>The speech model files for the selected transcription profile.</summary>
    SpeechModel,

    /// <summary>llama.cpp, and the CUDA runtime libraries when the GPU profile is chosen.</summary>
    SummaryRuntime,

    /// <summary>The default summary model.</summary>
    SummaryModel,

    /// <summary>The comparison model. Never required, never a default.</summary>
    BenchmarkModel,
}

/// <summary>
/// Where a component has got to.
///
/// <para>
/// <see cref="Ready"/> is the only value that permits use. Everything else is a distinct thing to
/// say to a user, which is the point of not collapsing them into a boolean: "not downloaded yet"
/// and "downloaded and does not match what was pinned" call for completely different actions.
/// </para>
/// </summary>
public enum RuntimeComponentStatus
{
    /// <summary>Nothing on disk.</summary>
    NotInstalled,

    Downloading,

    Verifying,

    /// <summary>Downloaded and being unpacked or installed.</summary>
    Installing,

    /// <summary>Present, proven, and usable.</summary>
    Ready,

    /// <summary>Present and wrong. Never used; repairable.</summary>
    Corrupt,

    /// <summary>This machine cannot run it — no suitable GPU, or an unsupported platform.</summary>
    Incompatible,

    /// <summary>Not needed for what the user chose. Not a problem, and not hidden either.</summary>
    NotNeeded,
}

/// <summary>What is known about one component right now.</summary>
public sealed record RuntimeComponentState(
    RuntimeComponentId Id,
    RuntimeComponentStatus Status,
    string Detail,
    long BytesInstalled = 0,
    long BytesRequired = 0)
{
    public bool IsReady => Status == RuntimeComponentStatus.Ready;

    /// <summary>True when a user could do something about it right now.</summary>
    public bool NeedsAction => Status
        is RuntimeComponentStatus.NotInstalled
        or RuntimeComponentStatus.Corrupt;

    public double Fraction => BytesRequired <= 0
        ? (IsReady ? 1 : 0)
        : Math.Clamp((double)BytesInstalled / BytesRequired, 0, 1);

    public long BytesOutstanding => Math.Max(0, BytesRequired - BytesInstalled);

    public static RuntimeComponentState Ready(RuntimeComponentId id, string detail, long bytes = 0) =>
        new(id, RuntimeComponentStatus.Ready, detail, bytes, bytes);

    public static RuntimeComponentState Missing(RuntimeComponentId id, string detail, long required = 0) =>
        new(id, RuntimeComponentStatus.NotInstalled, detail, 0, required);

    public static RuntimeComponentState NotNeeded(RuntimeComponentId id, string detail) =>
        new(id, RuntimeComponentStatus.NotNeeded, detail);
}

/// <summary>
/// What EchoForge can currently do.
///
/// <para>
/// Staged rather than all-or-nothing, because the stages are genuinely independent and the
/// download sizes are not small. Recording works with nothing installed at all — refusing to let
/// somebody record a meeting because a 7 GB summariser has not finished downloading would be
/// absurd, and losing the meeting is the only failure here that cannot be undone.
/// </para>
/// </summary>
public enum CapabilityLevel
{
    /// <summary>Record, play back, browse, search and export. Needs nothing downloaded.</summary>
    Recording,

    /// <summary>Speech recognition: the interpreter, the worker packages and a speech model.</summary>
    Transcription,

    /// <summary>Local summarisation: llama.cpp and the summary model.</summary>
    Summarization,

    /// <summary>The comparison model, for measuring the default against. Never required.</summary>
    Benchmarking,
}

/// <summary>One capability, whether it is available, and what it is still waiting for.</summary>
public sealed record CapabilityState(
    CapabilityLevel Level,
    bool Available,
    string Detail,
    IReadOnlyList<RuntimeComponentId> Blocking,
    long BytesOutstanding)
{
    public bool IsOptional => Level == CapabilityLevel.Benchmarking;
}
