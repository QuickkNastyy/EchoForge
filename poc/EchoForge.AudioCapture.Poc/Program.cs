using System.Globalization;
using EchoForge.Audio.Windows;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Recording;

namespace EchoForge.AudioCapture.Poc;

/// <summary>
/// Phase 0 proof of concept. A console tool, deliberately not a GUI: it exists to prove
/// that two endpoints can be captured simultaneously onto one timeline, that the result
/// survives being killed, and that alignment holds. No WPF, no models, no worker.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return (args.Length == 0 ? "help" : args[0].ToLowerInvariant()) switch
            {
                "devices" => ListDevices(),
                "record" => Record(args),
                "diagnose" => Diagnose(args),
                "validate" => Validate(args),
                "repair" => Repair(args),
                _ => Help(),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            EchoForge Phase 0 capture proof

              devices
                  List render and capture endpoints with their stable IDs and formats.

              record --system <endpoint-id> --mic <endpoint-id> --out <dir> [--minutes N]
                  Capture both endpoints onto one timeline. Ctrl+C stops cleanly.

              validate <session-dir>
                  Independently decode every finalized chunk and report alignment.

              repair <session-dir>
                  Patch any active .part.wav left behind by an interrupted run.
            """);
        return 1;
    }

    private static int ListDevices()
    {
        using AudioDeviceCatalog catalog = new();

        Console.WriteLine("Render endpoints (loopback sources)");
        PrintEndpoints(catalog.GetRenderEndpoints());

        Console.WriteLine();
        Console.WriteLine("Capture endpoints (microphones)");
        PrintEndpoints(catalog.GetCaptureEndpoints());
        return 0;
    }

    private static void PrintEndpoints(IReadOnlyList<AudioEndpointInfo> endpoints)
    {
        if (endpoints.Count == 0)
        {
            Console.WriteLine("  (none active)");
            return;
        }

        foreach (AudioEndpointInfo endpoint in endpoints)
        {
            Console.WriteLine($"  {(endpoint.IsDefault ? "*" : " ")} {endpoint.FriendlyName}");
            Console.WriteLine($"      {endpoint.MixFormat}");
            Console.WriteLine($"      {endpoint.Id}");
        }
    }

    private static int Record(string[] args)
    {
        string? system = ValueOf(args, "--system");
        string? mic = ValueOf(args, "--mic");
        string output = ValueOf(args, "--out") ?? Path.Combine(Path.GetTempPath(), "echoforge-poc");
        string? minutesText = ValueOf(args, "--minutes");

        if (system is null || mic is null)
        {
            Console.Error.WriteLine("record needs --system and --mic endpoint IDs. Run 'devices' to list them.");
            return 1;
        }

        TimeSpan? limit = minutesText is null
            ? null
            : TimeSpan.FromMinutes(double.Parse(minutesText, CultureInfo.InvariantCulture));

        string sessionDirectory = Path.Combine(output, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        string tracksRoot = Path.Combine(sessionDirectory, "tracks");

        using AudioDeviceCatalog catalog = new();
        long epochQpc = CaptureClock.Now();
        using DualTrackCaptureEngine engine = new(
            catalog, new CaptureRequest(system, mic, tracksRoot, epochQpc, FirstChunkIndex: 1));

        using CancellationTokenSource stop = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        Console.WriteLine($"directory {sessionDirectory}");
        Console.WriteLine(limit is null ? "duration  until Ctrl+C" : $"duration  {limit.Value.TotalMinutes:0.##} min or Ctrl+C");
        Console.WriteLine();

        engine.Start();

        foreach (TrackLiveStatus track in engine.Status().Tracks)
        {
            Console.WriteLine($"{track.Track,-11} {track.DeviceName}");
            Console.WriteLine($"{"",-11} recording as {track.Format}");
        }

        Console.WriteLine();
        Console.WriteLine("Drift and offset below are packet/QPC ESTIMATES, not end-to-end alignment proof.");
        Console.WriteLine();
        Console.WriteLine("elapsed   you    remote  chunks   queue    drop   est ms/hr   est off ms");

        System.Diagnostics.Stopwatch wall = System.Diagnostics.Stopwatch.StartNew();
        while (!stop.IsCancellationRequested)
        {
            if (limit is not null && wall.Elapsed >= limit.Value)
            {
                break;
            }

            Thread.Sleep(1000);
            PrintStatus(engine, wall.Elapsed);
        }

        Console.WriteLine();
        Console.WriteLine("stopping...");
        engine.Stop(CaptureClock.Now());

        Console.WriteLine();
        return Report(sessionDirectory);
    }

    private static void PrintStatus(DualTrackCaptureEngine engine, TimeSpan elapsed)
    {
        RecorderStatus status = engine.Status();
        TrackLiveStatus system = status.Tracks.First(t => t.Track == SourceTrack.System);
        TrackLiveStatus mic = status.Tracks.First(t => t.Track == SourceTrack.Microphone);

        double? relativeDrift = engine.EstimatedRelativeDriftMillisecondsPerHour();
        double? offset = engine.EstimatedOffsetMilliseconds();

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{FormatElapsed(elapsed.TotalSeconds),-9} {Meter(mic.PeakLevel)} {Meter(system.PeakLevel)}  " +
            $"{mic.CompletedChunks,3}+{system.CompletedChunks,-3} " +
            $"{Math.Max(mic.QueuedFrames, system.QueuedFrames),6} " +
            $"{mic.DroppedFrames + system.DroppedFrames,6}   " +
            $"{(relativeDrift is null ? "     -" : $"{relativeDrift.Value,6:0.0}")}      " +
            $"{(offset is null ? "    -" : $"{offset.Value,5:0.0}")}"));
    }

    private static string FormatElapsed(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    private static string Meter(double level)
    {
        int filled = (int)Math.Round(Math.Clamp(level, 0, 1) * 5);
        return new string('#', filled).PadRight(5, '.');
    }

    /// <summary>
    /// Prints raw packet headers straight from the engine, with nothing in between. This is
    /// how the meaning of device position and QPC position gets established on real hardware
    /// rather than assumed from documentation.
    /// </summary>
    private static int Diagnose(string[] args)
    {
        string? endpointId = ValueOf(args, "--endpoint");
        bool loopback = Array.Exists(args, a => string.Equals(a, "--loopback", StringComparison.OrdinalIgnoreCase));
        if (endpointId is null)
        {
            Console.Error.WriteLine("diagnose needs --endpoint <id> and optionally --loopback");
            return 1;
        }

        using AudioDeviceCatalog catalog = new();
        using NAudio.CoreAudioApi.MMDevice device = catalog.OpenDevice(endpointId);

        int printed = 0;
        bool seen = false;
        long firstDevice = 0;
        long lastDeviceEnd = 0;
        long firstQpc = 0;
        long lastQpc = 0;
        long totalFrames = 0;
        long packets = 0;

        void OnPacket(in PacketHeader header, ReadOnlySpan<byte> payload)
        {
            packets++;
            totalFrames += header.FrameCount;

            if (!seen)
            {
                seen = true;
                firstDevice = header.DevicePosition;
                firstQpc = header.QpcPosition;
            }

            lastDeviceEnd = header.EndDevicePosition;
            lastQpc = header.QpcPosition;

            if (printed < 15)
            {
                printed++;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  dev {header.DevicePosition,12} (+{header.DevicePosition - firstDevice,9})  " +
                    $"qpc {header.QpcPosition,16} (+{(header.QpcPosition - firstQpc) / 10_000.0,9:0.0} ms)  " +
                    $"frames {header.FrameCount,5}  bytes {payload.Length,6}  {header.Conditions}"));
            }
        }

        using WasapiPacketCapture capture = new(device, loopback, OnPacket);
        Console.WriteLine($"endpoint  {device.FriendlyName}");
        Console.WriteLine($"mix       {capture.SourceEncoding}");
        Console.WriteLine($"recording {capture.Format}");
        Console.WriteLine();

        capture.Start();
        Thread.Sleep(5000);
        capture.Stop();

        Console.WriteLine();
        if (!seen)
        {
            Console.WriteLine("  no packets: the endpoint produced no audio during the window.");
            return 0;
        }

        double qpcSeconds = CaptureClock.UnitsToSeconds(lastQpc - firstQpc);
        long deviceSpan = lastDeviceEnd - firstDevice;
        double deliveredSeconds = totalFrames / (double)capture.Format.SampleRate;

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  packets           {packets}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  delivered frames  {totalFrames} = {deliveredSeconds:0.000} s at the {capture.Format.SampleRate} Hz mix rate"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  qpc span          {qpcSeconds:0.000} s"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  device position   {firstDevice} -> {lastDeviceEnd}, span {deviceSpan}"));

        double ratio = totalFrames == 0 ? 0 : deviceSpan / (double)totalFrames;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  device/delivered  {ratio:0.0000}  (implied device rate {ratio * capture.Format.SampleRate:0} Hz)"));

        if (Math.Abs(ratio - 1.0) > 0.01)
        {
            Console.WriteLine();
            Console.WriteLine("  Device position advances in the endpoint's own clock domain, not the mix");
            Console.WriteLine("  format's. It must NOT be used as a mix-format frame counter. Session time");
            Console.WriteLine("  is anchored on QPC; device position is a diagnostic and discontinuity signal.");
        }

        return 0;
    }

    private static int Validate(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("validate needs a session directory.");
            return 1;
        }

        return Report(args[1]);
    }

    /// <summary>
    /// Decodes every finalized chunk with the independent reader and reports what the run
    /// actually produced. Nothing here trusts the manifest the recorder wrote.
    /// </summary>
    private static int Report(string sessionDirectory)
    {
        string tracksRoot = Path.Combine(sessionDirectory, "tracks");
        if (!Directory.Exists(tracksRoot))
        {
            Console.Error.WriteLine($"no tracks directory under {sessionDirectory}");
            return 2;
        }

        bool ok = true;
        Console.WriteLine("validation report");
        Console.WriteLine($"  session  {sessionDirectory}");

        Dictionary<string, double> trackSeconds = [];

        foreach (string trackDirectory in Directory.GetDirectories(tracksRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(trackDirectory);
            string chunks = Path.Combine(trackDirectory, "chunks");
            if (!Directory.Exists(chunks))
            {
                continue;
            }

            string[] files = [.. Directory.GetFiles(chunks, "*.wav").OrderBy(f => f, StringComparer.Ordinal)];
            long totalFrames = 0;
            int invalid = 0;
            int? sampleRate = null;

            foreach (string file in files)
            {
                WavValidation validation = WavPcm16Reader.Validate(file);
                if (!validation.IsValid)
                {
                    invalid++;
                    ok = false;
                    Console.WriteLine($"  INVALID  {Path.GetFileName(file)}: {validation.Problem}");
                    continue;
                }

                totalFrames += validation.FrameCount;
                sampleRate ??= validation.Format!.SampleRate;
            }

            // Chunk indices must be contiguous; a hole means a chunk went missing.
            int expected = 1;
            foreach (string file in files)
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                if (!int.TryParse(stem, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index != expected)
                {
                    Console.WriteLine($"  GAP      expected chunk {expected:D6}, found {stem}");
                    ok = false;
                }

                expected++;
            }

            double seconds = sampleRate is null ? 0 : (double)totalFrames / sampleRate.Value;
            trackSeconds[name] = seconds;

            Console.WriteLine($"  {name,-11} {files.Length,3} chunks  {totalFrames,12} frames  " +
                $"{FormatElapsed(seconds)}  {(invalid == 0 ? "all valid" : $"{invalid} INVALID")}");
        }

        if (trackSeconds.Count == 2)
        {
            double[] values = [.. trackSeconds.Values];
            double differenceMs = Math.Abs(values[0] - values[1]) * 1000.0;
            double minutes = Math.Max(values[0], values[1]) / 60.0;

            Console.WriteLine();
            Console.WriteLine("  diagnostics (not gates)");
            Console.WriteLine($"    track length difference  {differenceMs:0.0} ms over {minutes:0.00} min");
            Console.WriteLine("    Track duration does NOT measure alignment. Both tracks are padded to a");
            Console.WriteLine("    shared stop instant, so equal durations say nothing about whether the");
            Console.WriteLine("    audio on them lines up. This number is a sanity check only.");
        }

        PrintTimingGates(sessionDirectory);

        Console.WriteLine();
        Console.WriteLine(ok
            ? "  result  chunk integrity PASS Â· timing NOT QUALIFIED"
            : "  result  FAIL");
        return ok ? 0 : 3;
    }

    /// <summary>
    /// Reports the Phase 0 timing gates from signal-based measurements when a session supplies
    /// them, and says NOT QUALIFIED when it does not. Nothing here infers a pass from durations.
    /// </summary>
    private static void PrintTimingGates(string sessionDirectory)
    {
        List<AlignmentSample> samples = LoadAlignmentSamples(sessionDirectory);
        AlignmentGateResult result = AlignmentQualification.Evaluate(samples);

        Console.WriteLine();
        Console.WriteLine("  timing gates");

        Console.WriteLine(result.TenMinuteEvaluated
            ? $"    <=100 ms alignment at ten minutes   {(result.TenMinutePassed ? "PASS" : "FAIL")} (worst {result.WorstOffsetMilliseconds:0.0} ms)"
            : "    <=100 ms alignment at ten minutes   NOT QUALIFIED");

        Console.WriteLine(result.DriftEvaluated
            ? $"    <=50 ms/hour residual drift         {(result.DriftPassed ? "PASS" : "FAIL")} ({result.DriftMillisecondsPerHour:0.0} ms/hr)"
            : "    <=50 ms/hour residual drift         NOT QUALIFIED");

        Console.WriteLine($"    {result.Explanation}");

        if (samples.Count == 0)
        {
            Console.WriteLine($"    Supply measurements at {Path.Combine("diagnostics", AlignmentSamplesFile)} to evaluate these gates:");
            Console.WriteLine("    [ { \"session_seconds\": 60.0, \"offset_ms\": 3.2 }, ... ]");
        }
    }

    private const string AlignmentSamplesFile = "alignment-measurements.json";

    /// <summary>
    /// Loads signal-based alignment measurements if the session carries them. The chirp harness
    /// that produces this file is deferred hardening work; until it exists the list is empty and
    /// the gates report NOT QUALIFIED.
    /// </summary>
    private static List<AlignmentSample> LoadAlignmentSamples(string sessionDirectory)
    {
        string path = Path.Combine(sessionDirectory, "diagnostics", AlignmentSamplesFile);
        if (!File.Exists(path))
        {
            return [];
        }

        using FileStream stream = File.OpenRead(path);
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(stream);

        List<AlignmentSample> samples = [];
        foreach (System.Text.Json.JsonElement element in document.RootElement.EnumerateArray())
        {
            samples.Add(new AlignmentSample(
                element.GetProperty("session_seconds").GetDouble(),
                element.GetProperty("offset_ms").GetDouble()));
        }

        return samples;
    }

    private static int Repair(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("repair needs a session directory.");
            return 1;
        }

        string tracksRoot = Path.Combine(args[1], "tracks");
        if (!Directory.Exists(tracksRoot))
        {
            Console.Error.WriteLine($"no tracks directory under {args[1]}");
            return 2;
        }

        int repaired = 0;
        foreach (string trackDirectory in Directory.GetDirectories(tracksRoot))
        {
            string active = Path.Combine(trackDirectory, "active");
            if (!Directory.Exists(active))
            {
                continue;
            }

            foreach (string part in Directory.GetFiles(active, "*.part.wav"))
            {
                CaptureFormat format = ReadSidecarFormat(part);
                WavRepairResult result = WavPcm16Reader.Repair(part, format);

                Console.WriteLine(result.Repaired
                    ? $"  repaired {Path.GetFileName(part)}: {result.FrameCount} frames kept, {result.TrimmedBytes} bytes trimmed"
                    : $"  QUARANTINE {Path.GetFileName(part)}: {result.Problem}");

                if (result.Repaired)
                {
                    repaired++;
                }
            }
        }

        Console.WriteLine($"  {repaired} active chunk(s) repaired");
        return 0;
    }

    /// <summary>
    /// Reads the format from the sidecar written alongside the active chunk. Recovery must
    /// not guess a format; if the sidecar is missing the caller is told rather than assumed at.
    /// </summary>
    private static CaptureFormat ReadSidecarFormat(string partPath)
    {
        string sidecar = Path.ChangeExtension(partPath, null) + ".state.json";
        if (!File.Exists(sidecar))
        {
            throw new InvalidOperationException(
                $"no sidecar for {Path.GetFileName(partPath)}; cannot repair without the recorded format");
        }

        using FileStream stream = File.OpenRead(sidecar);
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(stream);
        int sampleRate = document.RootElement.GetProperty("sample_rate").GetInt32();
        int channels = document.RootElement.GetProperty("channels").GetInt32();
        return new CaptureFormat(sampleRate, channels, 16);
    }

    private static string? ValueOf(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

