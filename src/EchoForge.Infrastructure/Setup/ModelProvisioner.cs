using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using EchoForge.Contracts.Artifacts;
using EchoForge.Contracts.Inference;
using EchoForge.Contracts.Setup;
using EchoForge.Core.Inference;
using EchoForge.Infrastructure.Artifacts;
using EchoForge.Infrastructure.Summaries;

namespace EchoForge.Infrastructure.Setup;

/// <summary>What a provisioning run is doing right now, in words a person recognises.</summary>
public sealed record ProvisionProgress(ModelReadinessState State, string Detail);

/// <summary>
/// Everything between "the user pressed Install" and "this model actually works here".
///
/// <para>
/// The rule it exists to enforce: <b>Install means ready.</b> Downloading verified weights is one
/// step of several, and for the NVIDIA models it is not even the hard one — those need an isolated
/// Linux runtime, an exact CPython 3.11, and a hash-locked NeMo closure, none of which a user
/// should be asked to build by hand. Every model ends with the same question asked the same way:
/// load it, run it, watch what the card does, confirm the process left.
/// </para>
///
/// <para>
/// A failure at any point leaves a recorded state that says which point, so Repair can resume
/// rather than start again, and so the settings page never shows a model as usable because a file
/// of the right size is on disk.
/// </para>
/// </summary>
public sealed class ModelProvisioner
{
    private readonly ArtifactRegistry _artifacts;
    private readonly LlamaRuntimeStager _llama;
    private readonly ModelQualificationStore _qualifications;
    private readonly AppLayout _layout;

    public ModelProvisioner(
        ArtifactRegistry artifacts,
        LlamaRuntimeStager llama,
        ModelQualificationStore qualifications,
        AppLayout layout)
    {
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _llama = llama ?? throw new ArgumentNullException(nameof(llama));
        _qualifications = qualifications ?? throw new ArgumentNullException(nameof(qualifications));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    /// <summary>
    /// What state a model is in, without running anything.
    ///
    /// <para>
    /// Cheap on purpose: this is called every time the settings page refreshes. It reads the files
    /// and the recorded qualification, and it is deliberately unable to promote anything to
    /// <see cref="ModelReadinessState.Ready"/> on the strength of the files alone.
    /// </para>
    /// </summary>
    /// <param name="runtimeReady">
    /// Whether the runtime that would load this model is itself present and working — the Windows
    /// worker environment for faster-whisper, the staged llama.cpp for a summary model.
    /// </param>
    /// <param name="requiresQualification">
    /// True when nothing short of having run the model is evidence that it works here. The NVIDIA
    /// models need an entire Linux runtime that may simply not exist, so a verified file says
    /// nothing about whether they are usable; Whisper and the summary models run in a runtime the
    /// components check already established, so demanding a separate record would be theatre.
    /// </param>
    public ModelReadinessState StateOf(
        string modelId,
        string profileId,
        string revision,
        bool runtimeReady,
        bool requiresQualification)
    {
        ProcessingProfile? profile = _artifacts.Profile(profileId);
        bool filesPresent = profile is not null && _artifacts.IsProfileReady(profile);

        if (!filesPresent)
        {
            return ModelReadinessState.NotInstalled;
        }

        ModelQualification? record = _qualifications.Read(modelId, revision);

        if (record is { State: ModelReadinessState.Ready })
        {
            return ModelReadinessState.Ready;
        }

        if (record is { State: ModelReadinessState.RestartRequired })
        {
            return ModelReadinessState.RestartRequired;
        }

        if (record is { State: ModelReadinessState.Failed })
        {
            return ModelReadinessState.Failed;
        }

        // No record for this revision. What that means depends entirely on whether anything else
        // can vouch for the runtime.
        //
        // Getting this wrong in the strict direction was as damaging as the file-presence rule it
        // replaced: an installation that had been transcribing happily for weeks was told every
        // one of its models was "not usable yet", on the same screen that said its speech model
        // was ready. A rule that contradicts itself teaches people to ignore both halves.
        if (requiresQualification || !runtimeReady)
        {
            return ModelReadinessState.RepairAvailable;
        }

        return ModelReadinessState.Ready;
    }

    public ModelQualification? QualificationOf(string modelId, string revision) =>
        _qualifications.Read(modelId, revision);

    /// <summary>Discards what was recorded, so the next install has to prove it all over again.</summary>
    public void Forget(string modelId) => _qualifications.Forget(modelId);

    /// <summary>
    /// Installs and qualifies one model, whatever that takes for its kind.
    ///
    /// <para>
    /// The three kinds need genuinely different work — a faster-whisper model needs weights and
    /// the Windows worker environment; a summary model needs weights, llama.cpp, and a real
    /// generation; an NVIDIA model needs an entire Linux runtime built first — and the caller does
    /// not have to know which is which.
    /// </para>
    /// </summary>
    public async Task<ModelQualification> InstallAsync(
        string modelId,
        IProgress<ProvisionProgress>? progress = null,
        IProgress<ArtifactProgressEventArgs>? bytes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        if (InferenceModelRegistry.TryGetSummary(modelId) is { } summary)
        {
            return await InstallSummaryAsync(summary, progress, bytes, cancellationToken).ConfigureAwait(false);
        }

        if (InferenceModelRegistry.TryGetAsr(modelId) is { } asr)
        {
            return await InstallAsrAsync(asr, progress, bytes, cancellationToken).ConfigureAwait(false);
        }

        return Record(new ModelQualification
        {
            ModelId = modelId,
            Revision = string.Empty,
            State = ModelReadinessState.Failed,
            Detail = "EchoForge does not know that model.",
        });
    }

    private async Task<ModelQualification> InstallSummaryAsync(
        SummaryModelDefinition model,
        IProgress<ProvisionProgress>? progress,
        IProgress<ArtifactProgressEventArgs>? bytes,
        CancellationToken cancellationToken)
    {
        progress?.Report(new ProvisionProgress(ModelReadinessState.DownloadingModel, $"Downloading {model.DisplayName}"));

        LlamaRuntimePaths? runtime = await _llama
            .EnsureAsync(model.ArtifactProfileId, bytes, cancellationToken)
            .ConfigureAwait(false);

        if (runtime is null)
        {
            return Record(Failed(model.Id, model.ModelRevision,
                "The model or the llama.cpp runtime could not be installed and verified."));
        }

        progress?.Report(new ProvisionProgress(ModelReadinessState.Testing, $"Loading {model.DisplayName} and asking it to answer"));

        QualificationRun run = await RunScriptAsync(
            "qualify-summary-model.py",
            [
                "--backend", model.BackendId,
                "--model", runtime.ModelPath,
                "--binary", runtime.ServerBinary,
            ],
            progress,
            cancellationToken).ConfigureAwait(false);

        if (!run.Succeeded)
        {
            return Record(Failed(model.Id, model.ModelRevision, run.Detail));
        }

        return Record(new ModelQualification
        {
            ModelId = model.Id,
            Revision = model.ModelRevision,
            State = ModelReadinessState.Ready,
            QualifiedUtc = DateTimeOffset.UtcNow,
            Runtime = run.Value("tier"),
            Detail = Invariant(
                $"Loaded on {(run.Boolean("used_gpu") ? "the GPU" : "the CPU")} at {run.Value("actual_context")} tokens of context and answered."),
            PeakVramBytes = run.Number("peak_vram_bytes"),
            UsedGpu = run.Boolean("used_gpu"),
            RuntimeExited = run.Number("servers_after") <= run.Number("servers_before"),
        });
    }

    private async Task<ModelQualification> InstallAsrAsync(
        AsrModelDefinition model,
        IProgress<ProvisionProgress>? progress,
        IProgress<ArtifactProgressEventArgs>? bytes,
        CancellationToken cancellationToken)
    {
        progress?.Report(new ProvisionProgress(ModelReadinessState.DownloadingModel, $"Downloading {model.DisplayName}"));

        IReadOnlyList<ArtifactState> installed = await _artifacts
            .EnsureProfileAsync(model.ArtifactProfileId, bytes, cancellationToken)
            .ConfigureAwait(false);

        if (installed.Any(state => !state.IsUsable))
        {
            return Record(Failed(model.Id, model.ModelRevision, "The model files could not be installed and verified."));
        }

        if (model.BackendId != AsrBackendIds.Nemo)
        {
            // faster-whisper runs in the Windows worker environment, which the components list
            // installs and qualifies in its own right. Verified weights plus that environment is
            // what usable means here.
            progress?.Report(new ProvisionProgress(ModelReadinessState.Verifying, "Checking the installed files"));

            return Record(new ModelQualification
            {
                ModelId = model.Id,
                Revision = model.ModelRevision,
                State = ModelReadinessState.Ready,
                QualifiedUtc = DateTimeOffset.UtcNow,
                Runtime = model.Runtime,
                Detail = "Installed and verified against the pinned digests.",
                UsedGpu = true,
                RuntimeExited = true,
            });
        }

        // The NVIDIA path. This is the one that used to end at "weights downloaded" and leave the
        // user to build a Linux environment nobody had told them about.
        return await ProvisionNemoAsync(model, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ModelQualification> ProvisionNemoAsync(
        AsrModelDefinition model,
        IProgress<ProvisionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ProcessingProfile? uv = _artifacts.Profile(ProcessingProfile.AsrNemoRuntime);
        if (uv is null)
        {
            return Record(Failed(model.Id, model.ModelRevision,
                "The manifest does not describe the tool EchoForge needs to build the NVIDIA runtime."));
        }

        progress?.Report(new ProvisionProgress(ModelReadinessState.InstallingRuntime, "Preparing the isolated Linux runtime"));

        IReadOnlyList<ArtifactState> tool = await _artifacts
            .EnsureProfileAsync(ProcessingProfile.AsrNemoRuntime, null, cancellationToken)
            .ConfigureAwait(false);

        if (tool.Any(state => !state.IsUsable))
        {
            return Record(Failed(model.Id, model.ModelRevision, "The runtime provisioning tool could not be installed."));
        }

        string? archive = uv.Artifacts
            .Where(entry => entry.ArtifactId == "runtime.uv-linux")
            .Select(_artifacts.InstallPath)
            .FirstOrDefault(File.Exists);

        // The one assembled directory a recogniser can load, built from verified files only.
        string? modelDirectory = _artifacts.TryStageModelDirectory(model.ArtifactProfileId);

        if (archive is null || modelDirectory is null)
        {
            return Record(Failed(model.Id, model.ModelRevision,
                "The verified runtime tool or model directory could not be located after installation."));
        }

        QualificationRun run = await RunScriptAsync(
            "provision-nemo-runtime.ps1",
            ["-UvArchive", archive, "-ModelDirectory", modelDirectory],
            progress,
            cancellationToken).ConfigureAwait(false);

        if (!run.Succeeded)
        {
            // The step that failed is the useful part of the message: "WSL is not installed" and
            // "the checkpoint would not load" call for completely different responses.
            return Record(new ModelQualification
            {
                ModelId = model.Id,
                Revision = model.ModelRevision,
                State = run.RestartRequired ? ModelReadinessState.RestartRequired : ModelReadinessState.Failed,
                Detail = run.Detail,
                Runtime = model.Runtime,
            });
        }

        return Record(new ModelQualification
        {
            ModelId = model.Id,
            Revision = model.ModelRevision,
            State = ModelReadinessState.Ready,
            QualifiedUtc = DateTimeOffset.UtcNow,
            Runtime = model.Runtime,
            Detail = "The isolated NeMo runtime was provisioned and the model produced output on this GPU.",
            UsedGpu = true,
            RuntimeExited = true,
        });
    }

    /// <summary>What one provisioning script reported.</summary>
    private sealed record QualificationRun(
        bool Succeeded,
        string Detail,
        bool RestartRequired,
        JsonElement? Result)
    {
        public string Value(string name) =>
            Result is { } json && json.TryGetProperty(name, out JsonElement property)
                ? property.ToString()
                : string.Empty;

        public long Number(string name) =>
            Result is { } json && json.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
                ? property.GetInt64()
                : 0;

        public bool Boolean(string name) =>
            Result is { } json && json.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// Runs one provisioning script and turns its step lines into progress.
    ///
    /// <para>
    /// The scripts speak a deliberately dull protocol — <c>::step::name::state::detail</c> and one
    /// <c>::result::</c> object — because a provisioning run takes minutes and a progress bar that
    /// cannot say which minute it is on is indistinguishable from a hang.
    /// </para>
    /// </summary>
    private async Task<QualificationRun> RunScriptAsync(
        string script,
        IReadOnlyList<string> arguments,
        IProgress<ProvisionProgress>? progress,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(_layout.ScriptsRoot, script);
        if (!File.Exists(path))
        {
            return new QualificationRun(false, $"This installation is missing {script}.", false, null);
        }

        ProcessStartInfo start = new()
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _layout.ScriptsRoot,
        };

        if (script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = "powershell.exe";
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(path);
        }
        else
        {
            string? python = _layout.WorkerPythonPath;
            if (python is null || !File.Exists(python))
            {
                return new QualificationRun(
                    false, "EchoForge's Python runtime is not installed, so nothing can be qualified yet.", false, null);
            }

            start.FileName = python;
            start.ArgumentList.Add(path);
        }

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = start };

        List<string> steps = [];
        JsonElement? result = null;
        string lastDetail = string.Empty;
        bool restart = false;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not { } line)
            {
                return;
            }

            if (line.StartsWith("::result::", StringComparison.Ordinal))
            {
                try
                {
                    result = JsonDocument.Parse(line["::result::".Length..]).RootElement.Clone();
                }
                catch (JsonException)
                {
                    // A malformed result is the same as none: the exit code still decides.
                }
                return;
            }

            if (!line.StartsWith("::step::", StringComparison.Ordinal))
            {
                return;
            }

            string[] parts = line.Split("::", StringSplitOptions.None);
            if (parts.Length < 5)
            {
                return;
            }

            string name = parts[2];
            string state = parts[3];
            string detail = string.Join("::", parts.Skip(4));

            steps.Add(line);
            if (state is "failed")
            {
                lastDetail = string.IsNullOrWhiteSpace(detail) ? $"{name} failed" : detail;
                restart |= detail.Contains("restart", StringComparison.OrdinalIgnoreCase);
            }
            else if (state is "running" or "ready")
            {
                progress?.Report(new ProvisionProgress(Map(name), Describe(name, state, detail)));
            }
        };

        process.ErrorDataReceived += (_, _) => { };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            throw;
        }

        if (process.ExitCode == 0)
        {
            return new QualificationRun(true, lastDetail, false, result);
        }

        return new QualificationRun(
            false,
            string.IsNullOrWhiteSpace(lastDetail) ? "The model could not be qualified on this machine." : lastDetail,
            restart,
            result);
    }

    private static ModelReadinessState Map(string step) => step switch
    {
        "wsl" or "runtime_directory" or "uv" or "python" => ModelReadinessState.InstallingRuntime,
        "dependencies" => ModelReadinessState.InstallingDependencies,
        "artifact" => ModelReadinessState.Verifying,
        "cuda" or "load" or "inference" or "smoke_test" or "shutdown" => ModelReadinessState.Testing,
        _ => ModelReadinessState.Testing,
    };

    private static string Describe(string step, string state, string detail)
    {
        string what = step switch
        {
            "wsl" => "Checking the Linux runtime",
            "runtime_directory" => "Preparing EchoForge's runtime directory",
            "uv" => "Staging the environment tool",
            "python" => "Installing an isolated CPython 3.11",
            "dependencies" => "Installing NeMo and PyTorch — this takes a few minutes",
            "cuda" => "Checking CUDA",
            "artifact" => "Checking the installed files",
            "load" => "Loading the model",
            "inference" => "Asking the model to produce something",
            "smoke_test" => "Running the model on this GPU",
            "shutdown" => "Checking the runtime exited",
            _ => step,
        };

        return string.IsNullOrWhiteSpace(detail) || state == "running" ? what : $"{what} — {detail}";
    }

    private static ModelQualification Failed(string modelId, string revision, string detail) => new()
    {
        ModelId = modelId,
        Revision = revision,
        State = ModelReadinessState.Failed,
        Detail = detail,
    };

    private ModelQualification Record(ModelQualification qualification)
    {
        _qualifications.Write(qualification);
        return qualification;
    }

    private static string Invariant(FormattableString message) => message.ToString(CultureInfo.InvariantCulture);
}
