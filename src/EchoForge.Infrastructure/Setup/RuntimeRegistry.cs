using System.Globalization;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Setup;
using EchoForge.Infrastructure.Artifacts;
using EchoForge.Infrastructure.Summaries;

namespace EchoForge.Infrastructure.Setup;

/// <summary>What the machine can do right now, and what each component is waiting for.</summary>
public sealed record SetupSnapshot(
    IReadOnlyList<RuntimeComponentState> Components,
    IReadOnlyList<CapabilityState> Capabilities,
    string TranscriptionProfileId,
    string SummaryProfileId)
{
    public RuntimeComponentState Component(RuntimeComponentId id) =>
        Components.First(c => c.Id == id);

    public CapabilityState Capability(CapabilityLevel level) =>
        Capabilities.First(c => c.Level == level);

    public bool CanTranscribe => Capability(CapabilityLevel.Transcription).Available;

    public bool CanSummarize => Capability(CapabilityLevel.Summarization).Available;

    /// <summary>Everything still to fetch for the chosen profiles, excluding anything optional.</summary>
    public long BytesOutstanding => Capabilities
        .Where(c => !c.IsOptional && !c.Available)
        .Sum(c => c.BytesOutstanding);
}

/// <summary>
/// One place to ask what is installed.
///
/// <para>
/// <b>It is a view, not a second source of truth.</b> Every digest still comes from the artifact
/// manifest and every "is this file what it should be" answer still comes from the registry that
/// hashed it. This composes those answers with the two things the manifest cannot describe — an
/// unpacked interpreter and an installed package environment — and groups them into components a
/// person can be told about and can repair.
/// </para>
///
/// <para>
/// Storing status here would be the mistake: a cached "ready" that outlived the file it described
/// is exactly how an application ends up launching a model that is no longer on disk. Nothing is
/// remembered between calls.
/// </para>
/// </summary>
public sealed class RuntimeRegistry
{
    private readonly ArtifactRegistry _artifacts;
    private readonly PythonRuntimeInstaller _python;
    private readonly WorkerEnvironmentInstaller _worker;
    private readonly LlamaRuntimeStager? _llama;

    public RuntimeRegistry(
        ArtifactRegistry artifacts,
        PythonRuntimeInstaller python,
        WorkerEnvironmentInstaller worker,
        LlamaRuntimeStager? llama = null)
    {
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _python = python ?? throw new ArgumentNullException(nameof(python));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _llama = llama;
    }

    /// <summary>Everything, for the profiles the user has chosen.</summary>
    public SetupSnapshot Snapshot(string transcriptionProfileId, string summaryProfileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcriptionProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(summaryProfileId);

        RuntimeComponentState python = _python.Status();
        RuntimeComponentState environment = _worker.Status();
        RuntimeComponentState speech = SpeechModelStatus(transcriptionProfileId);
        RuntimeComponentState summaryRuntime = SummaryRuntimeStatus(summaryProfileId);
        RuntimeComponentState summaryModel = SummaryModelStatus(summaryProfileId);
        RuntimeComponentState benchmark = BenchmarkStatus();

        List<RuntimeComponentState> components =
            [python, environment, speech, summaryRuntime, summaryModel, benchmark];

        List<CapabilityState> capabilities =
        [
            new(
                CapabilityLevel.Recording,
                true,
                "Recording, playback, the library, search and exports work with nothing downloaded.",
                [],
                0),

            Capability(
                CapabilityLevel.Transcription,
                "Speech recognition",
                [python, environment, speech]),

            Capability(
                CapabilityLevel.Summarization,
                "Local summaries",
                [python, environment, summaryRuntime, summaryModel]),

            Capability(
                CapabilityLevel.Benchmarking,
                "The comparison model",
                [benchmark]),
        ];

        return new SetupSnapshot(components, capabilities, transcriptionProfileId, summaryProfileId);
    }

    private static CapabilityState Capability(
        CapabilityLevel level, string name, IReadOnlyList<RuntimeComponentState> required)
    {
        List<RuntimeComponentState> blocking = [.. required.Where(c => !c.IsReady && c.Status != RuntimeComponentStatus.NotNeeded)];

        if (blocking.Count == 0)
        {
            return new CapabilityState(level, true, name + ": ready.", [], 0);
        }

        long outstanding = blocking.Sum(c => c.BytesOutstanding);

        return new CapabilityState(
            level,
            false,
            outstanding > 0
                ? string.Create(CultureInfo.CurrentCulture, $"{name}: {Describe(outstanding)} still to download.")
                : name + ": not ready yet.",
            [.. blocking.Select(c => c.Id)],
            outstanding);
    }

    // -- components ---------------------------------------------------------------------------------

    private RuntimeComponentState SpeechModelStatus(string profileId)
    {
        ProcessingProfile? profile = _artifacts.Profile(profileId);

        if (profile is null)
        {
            return new RuntimeComponentState(
                RuntimeComponentId.SpeechModel,
                RuntimeComponentStatus.Incompatible,
                "That transcription profile is not in this build's manifest.");
        }

        if (string.Equals(profileId, ProcessingProfile.Mock, StringComparison.Ordinal))
        {
            return RuntimeComponentState.NotNeeded(
                RuntimeComponentId.SpeechModel,
                "The deterministic placeholder needs no model. It recognises no speech.");
        }

        return Aggregate(
            RuntimeComponentId.SpeechModel,
            [.. profile.Artifacts.Where(a => string.Equals(a.Kind, "speech-model", StringComparison.Ordinal))],
            "The speech model");
    }

    private RuntimeComponentState SummaryRuntimeStatus(string profileId)
    {
        if (_llama is null || _artifacts.Profile(profileId) is not { } profile)
        {
            return new RuntimeComponentState(
                RuntimeComponentId.SummaryRuntime,
                RuntimeComponentStatus.Incompatible,
                "This installation cannot run local summaries.");
        }

        IReadOnlyList<ArtifactEntry> archives =
            [.. profile.Artifacts.Where(a => a.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))];

        RuntimeComponentState downloaded = Aggregate(RuntimeComponentId.SummaryRuntime, archives, "The summary runtime");

        if (!downloaded.IsReady)
        {
            return downloaded;
        }

        return _llama.TryResolve(profileId) is null
            ? new RuntimeComponentState(
                RuntimeComponentId.SummaryRuntime,
                RuntimeComponentStatus.Installing,
                "The summary runtime has been downloaded and still has to be unpacked.",
                downloaded.BytesInstalled,
                downloaded.BytesRequired)
            : downloaded;
    }

    private RuntimeComponentState SummaryModelStatus(string profileId)
    {
        if (_artifacts.Profile(profileId) is not { } profile)
        {
            return new RuntimeComponentState(
                RuntimeComponentId.SummaryModel,
                RuntimeComponentStatus.Incompatible,
                "That summary profile is not in this build's manifest.");
        }

        return Aggregate(
            RuntimeComponentId.SummaryModel,
            [.. profile.Artifacts.Where(a => string.Equals(a.Kind, "summary-model", StringComparison.Ordinal))],
            "The summary model");
    }

    /// <summary>
    /// The comparison model, which is never required.
    ///
    /// <para>
    /// Reported as <see cref="RuntimeComponentStatus.NotNeeded"/> rather than missing when it is
    /// absent, because a bake-off candidate that showed up as a problem would eventually get
    /// downloaded by somebody trying to make a red light go green — and it is eight gigabytes.
    /// </para>
    /// </summary>
    private RuntimeComponentState BenchmarkStatus()
    {
        if (_artifacts.Profile(ProcessingProfile.SummaryBakeoff) is not { } profile)
        {
            return RuntimeComponentState.NotNeeded(
                RuntimeComponentId.BenchmarkModel, "No comparison model is pinned in this build.");
        }

        IReadOnlyList<ArtifactEntry> model =
            [.. profile.Artifacts.Where(a => string.Equals(a.Kind, "summary-model", StringComparison.Ordinal))];

        RuntimeComponentState state = Aggregate(RuntimeComponentId.BenchmarkModel, model, "The comparison model");

        return state.Status == RuntimeComponentStatus.NotInstalled
            ? state with
            {
                Status = RuntimeComponentStatus.NotNeeded,
                Detail = "Not installed. Only needed to measure the default summariser against.",
            }
            : state;
    }

    private RuntimeComponentState Aggregate(
        RuntimeComponentId id, IReadOnlyList<ArtifactEntry> entries, string name)
    {
        if (entries.Count == 0)
        {
            return RuntimeComponentState.NotNeeded(id, name + " is not needed for this profile.");
        }

        IReadOnlyList<ArtifactState> states = [.. entries.Select(_artifacts.Status)];
        long required = entries.Sum(e => e.SizeBytes);
        long installed = states.Sum(s => s.IsUsable ? s.Entry.SizeBytes : s.BytesOnDisk);

        if (states.All(s => s.IsUsable))
        {
            return RuntimeComponentState.Ready(id, name + " is ready.", required);
        }

        if (states.FirstOrDefault(s => s.Status == ArtifactStatus.Invalid) is { } invalid)
        {
            // The artifact's own words rather than a summary of them. "Present but never
            // verified" and "does not match the pinned digest" are both Invalid and they call for
            // very different reassurance: the first is repaired by re-hashing what is already
            // there, and telling somebody their 1.6 GB model is corrupt when it is merely
            // unproven would send them to download it again for nothing.
            return new RuntimeComponentState(
                id,
                RuntimeComponentStatus.Corrupt,
                string.Create(CultureInfo.CurrentCulture, $"{name}: {invalid.Detail ?? "not usable"}. Repairing checks it before downloading anything."),
                installed,
                required);
        }

        return new RuntimeComponentState(
            id,
            installed > 0 ? RuntimeComponentStatus.Downloading : RuntimeComponentStatus.NotInstalled,
            installed > 0
                ? string.Create(CultureInfo.CurrentCulture, $"{name} is {Describe(installed)} of {Describe(required)} downloaded.")
                : string.Create(CultureInfo.CurrentCulture, $"{name} needs {Describe(required)}."),
            installed,
            required);
    }

    // -- installing and repairing --------------------------------------------------------------

    /// <summary>
    /// Installs one component and nothing else.
    ///
    /// <para>
    /// Component-scoped on purpose. "Install everything" is how a user who wanted speech
    /// recognition ends up downloading eight gigabytes of comparison model, and how a failure in
    /// one download becomes a failure of the whole setup.
    /// </para>
    /// </summary>
    public async Task<RuntimeComponentState> InstallAsync(
        RuntimeComponentId id,
        string transcriptionProfileId,
        string summaryProfileId,
        IProgress<ArtifactProgressEventArgs>? progress = null,
        CancellationToken cancellationToken = default)
    {
        switch (id)
        {
            case RuntimeComponentId.PythonRuntime:
                await _python.EnsureAsync(progress, cancellationToken).ConfigureAwait(false);
                break;

            case RuntimeComponentId.WorkerEnvironment:
                await _worker.EnsureAsync(progress, cancellationToken).ConfigureAwait(false);
                break;

            case RuntimeComponentId.SpeechModel:
                await EnsureArtifactsAsync(
                    SpeechArtifacts(transcriptionProfileId), progress, cancellationToken).ConfigureAwait(false);
                break;

            case RuntimeComponentId.SummaryRuntime or RuntimeComponentId.SummaryModel:
                if (_llama is not null)
                {
                    await _llama.EnsureAsync(summaryProfileId, progress, cancellationToken).ConfigureAwait(false);
                }

                break;

            case RuntimeComponentId.BenchmarkModel:
                if (_llama is not null)
                {
                    await _llama.EnsureAsync(ProcessingProfile.SummaryBakeoff, progress, cancellationToken)
                        .ConfigureAwait(false);
                }

                break;

            default:
                break;
        }

        return Snapshot(transcriptionProfileId, summaryProfileId).Component(id);
    }

    /// <summary>
    /// Repairs one component.
    ///
    /// <para>
    /// <b>Verify before downloading.</b> A file that is present, the right length, and simply has
    /// no proof recorded against it is repaired by hashing it — not by fetching 1.6 GB again.
    /// Re-downloading first would be the easy implementation and would punish the most common
    /// case, which is an artifact installed by something other than this application.
    /// </para>
    ///
    /// <para>
    /// Nothing here touches a session, a transcript revision, a summary revision, or any component
    /// other than the one named.
    /// </para>
    /// </summary>
    public async Task<RuntimeComponentState> RepairAsync(
        RuntimeComponentId id,
        string transcriptionProfileId,
        string summaryProfileId,
        IProgress<ArtifactProgressEventArgs>? progress = null,
        CancellationToken cancellationToken = default)
    {
        switch (id)
        {
            case RuntimeComponentId.PythonRuntime:
                await _python.RepairAsync(progress, cancellationToken).ConfigureAwait(false);
                break;

            case RuntimeComponentId.WorkerEnvironment:
                await _worker.RepairAsync(progress, cancellationToken).ConfigureAwait(false);
                break;

            default:
                IReadOnlyList<ArtifactEntry> entries = id switch
                {
                    RuntimeComponentId.SpeechModel => SpeechArtifacts(transcriptionProfileId),
                    RuntimeComponentId.SummaryRuntime => ProfileArtifacts(summaryProfileId, a =>
                        a.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)),
                    RuntimeComponentId.SummaryModel => ProfileArtifacts(summaryProfileId, a =>
                        string.Equals(a.Kind, "summary-model", StringComparison.Ordinal)),
                    RuntimeComponentId.BenchmarkModel => ProfileArtifacts(ProcessingProfile.SummaryBakeoff, a =>
                        string.Equals(a.Kind, "summary-model", StringComparison.Ordinal)),
                    _ => [],
                };

                foreach (ArtifactEntry entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _artifacts.VerifyInstalledAsync(entry.ArtifactId, cancellationToken).ConfigureAwait(false);
                }

                await EnsureArtifactsAsync(entries, progress, cancellationToken).ConfigureAwait(false);

                if (id is RuntimeComponentId.SummaryRuntime && _llama is not null)
                {
                    await _llama.EnsureAsync(summaryProfileId, progress, cancellationToken).ConfigureAwait(false);
                }

                break;
        }

        return Snapshot(transcriptionProfileId, summaryProfileId).Component(id);
    }

    private async Task EnsureArtifactsAsync(
        IReadOnlyList<ArtifactEntry> entries,
        IProgress<ArtifactProgressEventArgs>? progress,
        CancellationToken cancellationToken)
    {
        foreach (ArtifactEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _artifacts.EnsureAsync(entry.ArtifactId, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    private IReadOnlyList<ArtifactEntry> SpeechArtifacts(string profileId) =>
        ProfileArtifacts(profileId, a => string.Equals(a.Kind, "speech-model", StringComparison.Ordinal));

    private IReadOnlyList<ArtifactEntry> ProfileArtifacts(string profileId, Func<ArtifactEntry, bool> predicate) =>
        _artifacts.Profile(profileId) is { } profile ? [.. profile.Artifacts.Where(predicate)] : [];

    /// <summary>Sizes as a person would say them. Never a byte count in a sentence.</summary>
    public static string Describe(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024 * 1024):F1} GB"),
        >= 1024L * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024):F0} MB"),
        >= 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024.0:F0} KB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes} bytes"),
    };
}
