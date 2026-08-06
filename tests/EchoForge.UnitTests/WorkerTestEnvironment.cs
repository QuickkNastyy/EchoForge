using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Sessions;
using EchoForge.Contracts.Workers;
using EchoForge.Core.Transcripts;
using EchoForge.Infrastructure.Workers;

namespace EchoForge.UnitTests;

/// <summary>
/// Finds the repository, the worker package, and a usable Python, and builds the sessions
/// the supervisor tests transcribe.
///
/// <para>
/// Nothing here hard-codes a developer's machine. If the required runtime is missing the
/// tests skip with a message naming what to install, rather than failing in a way that
/// looks like a defect in EchoForge.
/// </para>
/// </summary>
internal static class WorkerTestEnvironment
{
    private static readonly Lazy<string> RepositoryRootValue = new(FindRepositoryRoot);
    private static readonly Lazy<PythonRuntime?> PythonValue = new(PythonRuntimeLocator.Locate);

    public static string RepositoryRoot => RepositoryRootValue.Value;

    public static string WorkerRoot => Path.Combine(RepositoryRoot, "worker");

    public static string StubRoot => Path.Combine(AppContext.BaseDirectory, "PythonStubs");

    public static PythonRuntime? Python => PythonValue.Value;

    /// <summary>
    /// Why the worker cannot be run here, or <c>null</c> when it can.
    ///
    /// <para>
    /// Named rather than boolean so the skip message says what to install. A test that
    /// simply vanished would look like coverage that exists.
    /// </para>
    /// </summary>
    public static string? UnavailableReason
    {
        get
        {
            if (!File.Exists(Path.Combine(WorkerRoot, "echoforge_worker", "main.py")))
            {
                return $"The worker package was not found at {WorkerRoot}.";
            }

            return Python is null ? PythonRuntimeLocator.DescribeMissingRuntime() : null;
        }
    }

    public static WorkerLaunchOptions Options(
        TimeSpan? timeout = null,
        bool allowTestModes = true,
        string? workerRoot = null,
        string? moduleName = null,
        TimeSpan? cancelGrace = null) => new()
        {
            PythonExecutable = Python!.ExecutablePath,
            WorkerRoot = workerRoot ?? WorkerRoot,
            ModuleName = moduleName ?? "echoforge_worker",
            Timeout = timeout ?? TimeSpan.FromMinutes(2),
            CancelGracePeriod = cancelGrace ?? TimeSpan.FromSeconds(10),
            ExitGracePeriod = TimeSpan.FromSeconds(20),
            AllowTestModes = allowTestModes,
        };

    /// <summary>
    /// Writes a small two-track session and returns the request that describes it.
    ///
    /// <para>
    /// The audio is real PCM16 written by the same writer the recorder uses, not a stub
    /// file: the worker reads it with the standard library's RIFF parser, so a header this
    /// project could not actually produce would prove nothing.
    /// </para>
    /// </summary>
    public static TranscriptionRequest BuildSession(
        string root,
        double seconds = 3.0,
        bool silent = false,
        string backend = WorkerProtocol.MockBackend,
        string? testMode = null,
        double? testDelaySeconds = null,
        string sessionId = "01JTESTSESSION")
    {
        string sessionRoot = Path.Combine(root, "session");
        const int sampleRate = 8000;
        long frames = (long)Math.Round(seconds * sampleRate);

        List<SessionTrack> tracks = [];
        foreach (SourceTrack track in (SourceTrack[])[SourceTrack.Microphone, SourceTrack.System])
        {
            string name = track == SourceTrack.Microphone ? "microphone" : "system";
            string relative = $"tracks/{name}/chunks/000001.wav";
            string path = Path.Combine(sessionRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            WriteChunk(path, sampleRate, frames, silent, seed: track == SourceTrack.Microphone ? 1 : 2);

            tracks.Add(new SessionTrack(
                track,
                $"{name}-device",
                $"{name} device",
                new CaptureFormat(sampleRate, 1, 16),
                [
                    new AudioChunkMetadata(
                        Index: 1,
                        RelativePath: relative,
                        Track: track,
                        StartSeconds: 0,
                        EndSeconds: seconds,
                        SampleRate: sampleRate,
                        Channels: 1,
                        SampleFrames: frames,
                        Sha256: new string('0', 64),
                        Discontinuities: [],
                        EpochIndex: 1)
                ]));
        }

        DateTimeOffset created = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
        SessionSnapshot snapshot = new(
            sessionId,
            SessionState.Recorded,
            created,
            created,
            created.AddSeconds(seconds),
            [new SessionEpoch(1, created, created.AddSeconds(seconds), 0, 1, EpochEndReason.Stopped)],
            tracks);

        RequestBuildResult built = TranscriptionRequestBuilder.Build(
            snapshot,
            sessionRoot,
            Path.Combine(root, "transcript", "transcript.v1.json"),
            transcriptRevision: 1,
            createdAtUtc: created,
            options: new RequestOptions
            {
                Backend = backend,
                TestMode = testMode,
                TestDelaySeconds = testDelaySeconds,
            });

        Assert.True(built.Succeeded, built.Failure?.Detail);
        return built.Request!;
    }

    /// <summary>A PCM16 chunk whose content is arithmetic, so the same arguments always agree.</summary>
    private static void WriteChunk(string path, int sampleRate, long frames, bool silent, int seed)
    {
        byte[] payload = new byte[frames * 2];
        if (!silent)
        {
            for (long frame = 0; frame < frames; frame++)
            {
                short value = (short)(12000 * Math.Sin(2 * Math.PI * (110 + (seed * 7)) * frame / sampleRate));
                BitConverter.TryWriteBytes(payload.AsSpan((int)(frame * 2), 2), value);
            }
        }

        using EchoForge.Audio.Windows.WavPcm16Writer writer = new(path, new CaptureFormat(sampleRate, 1, 16));
        writer.WriteFrames(payload, frames);
        writer.Close();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EchoForge.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("EchoForge.slnx was not found above the test output directory.");
    }
}
