namespace EchoForge.Infrastructure.Setup;

/// <summary>
/// The environment every EchoForge child process runs with, so none of them can reach the network.
///
/// <para>
/// <b>This is not a preference; it is the guarantee the product is built on.</b> Once the models
/// are installed, a meeting is transcribed and summarised entirely on the machine it was recorded
/// on. The libraries in the worker stack do not agree with that by default: <c>huggingface_hub</c>
/// will happily go and check whether a model has been updated, and one call like that turns a
/// local transcription into a request that says which model a private meeting is being run through.
/// </para>
///
/// <para>
/// So the flags are set explicitly rather than relied upon. Every one of them is the documented
/// switch for the library that reads it, and they are applied to the installer processes too — a
/// pip that inherited an index URL from the machine could still reach a package server despite
/// <c>--no-index</c>.
/// </para>
///
/// <para>
/// There is deliberately no telemetry anywhere in EchoForge. The telemetry variables here are set
/// to disable other people's.
/// </para>
/// </summary>
public static class OfflineEnvironment
{
    /// <summary>
    /// The variables, and what each one stops. Public so a test can assert on the list rather than
    /// on a copy of it that could drift.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Variables { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // huggingface_hub: never contact the hub, for any reason, including "is there a newer
        // revision". faster-whisper imports it even when the model path is local.
        ["HF_HUB_OFFLINE"] = "1",
        ["HF_HUB_DISABLE_TELEMETRY"] = "1",
        ["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1",

        // transformers and datasets are not in the closure today. Set anyway: if one arrives as a
        // transitive dependency, it arrives already offline rather than already talking.
        ["TRANSFORMERS_OFFLINE"] = "1",
        ["HF_DATASETS_OFFLINE"] = "1",

        // No implicit index, no user site-packages, no cached bytecode written into a read-only
        // installation directory.
        ["PIP_NO_INDEX"] = "1",
        ["PYTHONNOUSERSITE"] = "1",

        // UTF-8 on every stream, so a path with non-ASCII characters survives the pipe whatever
        // the machine's console code page is.
        ["PYTHONUTF8"] = "1",
        ["PYTHONIOENCODING"] = "utf-8",
    };

    /// <summary>Variables that would let something reach out, removed rather than overridden.</summary>
    public static IReadOnlyList<string> Removed { get; } =
    [
        "HF_ENDPOINT",
        "HUGGINGFACE_CO_URL_HOME",
        "PIP_INDEX_URL",
        "PIP_EXTRA_INDEX_URL",
    ];

    /// <summary>Applies the policy to a child process's environment.</summary>
    public static void Apply(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        foreach ((string name, string value) in Variables)
        {
            environment[name] = value;
        }

        foreach (string name in Removed)
        {
            environment.Remove(name);
        }
    }
}
