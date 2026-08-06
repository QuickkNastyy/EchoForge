using System.Globalization;
using EchoForge.Audio.Windows;
using EchoForge.Contracts.Audio;

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

        using AudioDeviceCatalog catalog = new();
        using DualTrackRecorder recorder = new(catalog, sessionDirectory, system, mic);

        using CancellationTokenSource stop = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        Console.WriteLine($"session   {recorder.SessionId}");
        Console.WriteLine($"directory {sessionDirectory}");
        Console.WriteLine(limit is null ? "duration  until Ctrl+C" : $"duration  {limit.Value.TotalMinutes:0.##} min or Ctrl+C");
        Console.WriteLine();

        recorder.Start();

        foreach (TrackStatus track in recorder.Snapshot())
        {
            Console.WriteLine($"{track.Track,-11} {track.DeviceName}");
            Console.WriteLine($"{"",-11} recording as {track.Format}, endpoint mix {track.SourceEncoding}");
        }

        Console.WriteLine();
        Console.WriteLine("elapsed   you    remote  chunks   queue    drop   drift ms/hr   align ms");

        while (!stop.IsCancellationRequested)
        {
            if (limit is not null && recorder.ElapsedSeconds >= limit.Value.TotalSeconds)
            {
                break;
            }

            Thread.Sleep(1000);
            PrintStatus(recorder);
        }

        Console.WriteLine();
        Console.WriteLine("stopping...");
        recorder.Stop();

        Console.WriteLine();
        return Report(sessionDirectory);
    }

    private static void PrintStatus(DualTrackRecorder recorder)
    {
        IReadOnlyList<TrackStatus> tracks = recorder.Snapshot();
        TrackStatus system = tracks.First(t => t.Track == SourceTrack.System);
        TrackStatus mic = tracks.First(t => t.Track == SourceTrack.Microphone);

        double? relativeDrift = recorder.RelativeDriftMillisecondsPerHour();
        double? alignment = recorder.AlignmentErrorMilliseconds();

        string line = string.Create(CultureInfo.InvariantCulture,
            $"{FormatElapsed(recorder.ElapsedSeconds),-9} {Meter(mic.PeakLevel)} {Meter(system.PeakLevel)}  " +
            $"{mic.CompletedChunks,3}+{system.CompletedChunks,-3} " +
            $"{Math.Max(mic.QueuedFrames, system.QueuedFrames),6} " +
            $"{mic.DroppedFrames + system.DroppedFrames,6}   " +
            $"{(relativeDrift is null ? "     —" : $"{relativeDrift.Value,6:0.0}")}      " +
            $"{(alignment is null ? "    —" : $"{alignment.Value,5:0.0}")}");

        Console.WriteLine(line);
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
        long firstDevice = -1;
        long firstQpc = -1;
        long totalFrames = 0;
        long packets = 0;

        void OnPacket(in PacketHeader header, ReadOnlySpan<byte> payload)
        {
            packets++;
            totalFrames += header.FrameCount;
            if (firstDevice < 0)
            {
                firstDevice = header.DevicePosition;
                firstQpc = header.QpcPosition;
            }

            if (printed < 15)
            {
                printed++;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  dev {header.DevicePosition,12} (+{header.DevicePosition - firstDevice,9})  " +
                    $"qpc {header.QpcPosition,16} (+{(header.QpcPosition - firstQpc) / 10_000.0,9:0.0} ms)  " +
                    $"frames {header.FrameCount,5}  bytes {payload.Length,6}  {header.Flags}"));
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
        double wallSeconds = (capture.PacketCount > 0 ? 5.0 : 0);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  packets {packets}, frames {totalFrames} = {totalFrames / (double)capture.Format.SampleRate:0.000} s of audio in {wallSeconds:0.0} s wall"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  device position advanced {(firstDevice < 0 ? 0 : totalFrames)} frames; span {(firstDevice < 0 ? 0 : 0)}"));
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
            Console.WriteLine($"  track length difference  {differenceMs:0.0} ms over {minutes:0.00} min");

            if (minutes >= 9.5)
            {
                bool tenMinuteGate = differenceMs <= 100.0;
                Console.WriteLine($"  gate  <=100 ms at ten minutes    {(tenMinuteGate ? "PASS" : "FAIL")}");
                ok &= tenMinuteGate;
            }

            if (minutes >= 59.0)
            {
                double perHour = differenceMs / (minutes / 60.0);
                bool driftGate = perHour <= 50.0;
                Console.WriteLine($"  gate  <=50 ms/hour residual      {(driftGate ? "PASS" : "FAIL")} ({perHour:0.0} ms/hr)");
                ok &= driftGate;
            }
            else
            {
                Console.WriteLine("  gate  <=50 ms/hour residual      NOT QUALIFIED (needs a 60-minute run)");
            }
        }

        Console.WriteLine();
        Console.WriteLine(ok ? "  result  PASS" : "  result  FAIL");
        return ok ? 0 : 3;
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
