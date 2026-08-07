#:project ../src/EchoForge.App/EchoForge.App.csproj
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property IsAotCompatible=false
#:property PublishTrimmed=false
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false

// The meeting library, opened for real, on a disposable synthetic meeting.
//
// Two things here cannot be checked by the unit suite, and both have already hidden a defect.
//
// The first is loading the window. A missing StaticResource is not a compile error and not a
// binding warning; it throws when the window loads, so the entire library was unopenable while
// every view-model test passed. This constructs the real window against the real application
// resources and renders it.
//
// The second is the audio device. Everything about *where* playback is happens in the transport
// and is tested with a fake, precisely so the suite never needs a sound card - but whether NAudio
// opens an endpoint on this machine can only be found out by opening one.
//
// A WPF window and an audio device are both process-wide, thread-affine things. Putting either in
// the test suite destabilises a run that has 750 other tests in it, which is why this is a script.
//
// Everything it touches is synthetic and lives under a temporary directory it creates. The
// deletion step recycles that synthetic meeting and nothing else.
//
//   dotnet run scripts/smoke-library.cs

using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EchoForge.App.Library;
using EchoForge.Audio.Windows.Playback;
using EchoForge.Contracts.Audio;
using EchoForge.Contracts.Library;
using EchoForge.Contracts.Playback;
using EchoForge.Contracts.Sessions;
using EchoForge.Infrastructure.Library;
using EchoForge.Infrastructure.Playback;
using EchoForge.Infrastructure.Sessions;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Summaries;
using EchoForge.Infrastructure.Storage;

List<string> failures = [];
List<string> notes = [];

void Check(bool condition, string what)
{
    Console.WriteLine((condition ? "  ok    " : "  FAIL  ") + what);
    if (!condition)
    {
        failures.Add(what);
    }
}

void Note(string what)
{
    Console.WriteLine("  note  " + what);
    notes.Add(what);
}

string root = Path.Combine(Path.GetTempPath(), "echoforge-smoke-library", Guid.NewGuid().ToString("n"));
Directory.CreateDirectory(root);

Console.WriteLine("EchoForge library smoke test");
Console.WriteLine("  synthetic session root: " + root);
Console.WriteLine();

try
{
    // -- a synthetic meeting -------------------------------------------------------------------

    const string SessionId = "01JSMOKELIBRARY";

    FileSessionStore sessions = new(root);
    sessions.Create(SessionId);
    SessionPaths paths = sessions.Resolve(SessionId);

    DateTimeOffset origin = new(2026, 8, 7, 10, 0, 0, TimeSpan.Zero);

    // Two epochs with a five-second pause between them, and both tracks, so playback has to place
    // audio absolutely rather than by counting.
    (SourceTrack Track, int Epoch, double Seconds, short Level, int Rate)[] plan =
    [
        (SourceTrack.Microphone, 1, 6.0, 6000, 48000),
        (SourceTrack.System, 1, 6.0, 4000, 44100),
        (SourceTrack.Microphone, 2, 6.0, 9000, 48000),
    ];

    Dictionary<SourceTrack, List<AudioChunkMetadata>> byTrack = [];
    Dictionary<(SourceTrack, int), double> cursor = [];
    Dictionary<int, double> epochLength = [];
    Dictionary<SourceTrack, CaptureFormat> formats = [];
    int index = 0;

    foreach ((SourceTrack track, int epoch, double seconds, short level, int rate) in plan)
    {
        index++;
        string relative = $"tracks/{track.ToString().ToLowerInvariant()}/chunks/{index:D6}.wav";
        string path = Path.Combine(paths.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        long frames = (long)Math.Round(seconds * rate);
        byte[] data = new byte[frames * 2];
        for (long frame = 0; frame < frames; frame++)
        {
            // A quiet tone rather than a constant, so a human running this hears something.
            short value = (short)(level * Math.Sin(2 * Math.PI * 220 * frame / rate));
            BitConverter.TryWriteBytes(data.AsSpan((int)frame * 2, 2), value);
        }

        byte[] header = new byte[44];
        "RIFF"u8.CopyTo(header.AsSpan(0));
        BitConverter.TryWriteBytes(header.AsSpan(4), (uint)(36 + data.Length));
        "WAVE"u8.CopyTo(header.AsSpan(8));
        "fmt "u8.CopyTo(header.AsSpan(12));
        BitConverter.TryWriteBytes(header.AsSpan(16), 16u);
        BitConverter.TryWriteBytes(header.AsSpan(20), (ushort)1);
        BitConverter.TryWriteBytes(header.AsSpan(22), (ushort)1);
        BitConverter.TryWriteBytes(header.AsSpan(24), (uint)rate);
        BitConverter.TryWriteBytes(header.AsSpan(28), (uint)(rate * 2));
        BitConverter.TryWriteBytes(header.AsSpan(32), (ushort)2);
        BitConverter.TryWriteBytes(header.AsSpan(34), (ushort)16);
        "data"u8.CopyTo(header.AsSpan(36));
        BitConverter.TryWriteBytes(header.AsSpan(40), (uint)data.Length);

        using (FileStream stream = File.Create(path))
        {
            stream.Write(header);
            stream.Write(data);
        }

        string digest;
        using (FileStream stream = File.OpenRead(path))
        {
            digest = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        double start = cursor.GetValueOrDefault((track, epoch));
        double length = (double)frames / rate;

        if (!byTrack.TryGetValue(track, out List<AudioChunkMetadata>? list))
        {
            list = [];
            byTrack[track] = list;
            formats[track] = new CaptureFormat(rate, 1, 16);
        }

        list.Add(new AudioChunkMetadata(
            index, relative, track, start, start + length, rate, 1, frames, digest, [], epoch));

        cursor[(track, epoch)] = start + length;
        epochLength[epoch] = Math.Max(epochLength.GetValueOrDefault(epoch), start + length);
    }

    List<SessionEpoch> epochs = [];
    double wall = 0;
    foreach (int epoch in epochLength.Keys.Order())
    {
        epochs.Add(new SessionEpoch(
            epoch, origin.AddSeconds(wall), origin.AddSeconds(wall + epochLength[epoch]), 0, 1, EpochEndReason.Paused));

        wall += epochLength[epoch] + 5;
    }

    sessions.WriteSnapshot(new SessionSnapshot(
        SessionId,
        SessionState.Recorded,
        origin,
        origin,
        origin.AddSeconds(wall),
        epochs,
        [
            .. byTrack.Keys.Order().Select(track => new SessionTrack(
                track, track.ToString().ToLowerInvariant(), track.ToString(), formats[track], byTrack[track]))
        ],
        Title: "Synthetic smoke meeting"));

    Console.WriteLine("Session");
    Check(sessions.ReadSnapshot(SessionId) is not null, "the synthetic session reads back");

    // -- playback, through the real device --------------------------------------------------------

    Console.WriteLine();
    Console.WriteLine("Playback");

    PlaybackPreparer preparer = new(sessions);
    PlaybackPreparation prepared = await preparer.PrepareAsync(SessionId);

    Check(prepared.Succeeded, "the aligned two-track derivative builds: " + (prepared.Message ?? "ok"));

    if (prepared.Succeeded)
    {
        PlaybackDerivativeRecord record = prepared.Record!;
        double expected = 6.0 + 5.0 + 6.0;

        Check(Math.Abs(record.DurationSeconds - expected) < 0.01,
            $"the pause is preserved: {record.DurationSeconds:F2}s, expected {expected:F2}s");

        Check(record.Channels == 2, "two channels, one per track");
        Check(record.For("microphone")!.HasAudio && record.For("system")!.HasAudio, "both tracks carry audio");

        // The same derivative a second time is reused rather than rebuilt.
        PlaybackPreparation again = await preparer.PrepareAsync(SessionId);
        Check(again.Succeeded && again.Record!.Sha256 == record.Sha256, "a valid derivative is reused");

        IPlaybackDevice device;
        bool deviceOpened;

        try
        {
            device = new NAudioPlaybackDevice();
            using PlaybackEngineProbe probe = new(prepared.AudioPath!, device);
            deviceOpened = probe.Opened;

            if (deviceOpened)
            {
                Check(true, "an audio output device opened");

                probe.Engine.Seek(11.5);
                Check(Math.Abs(probe.Engine.PositionSeconds - 11.5) <= 0.25,
                    $"seeking into the second epoch lands within 250 ms ({probe.Engine.PositionSeconds:F3}s)");

                probe.Engine.Play();
                Thread.Sleep(600);

                Check(probe.Engine.State == PlaybackState.Playing, "playback is running");
                Check(probe.Engine.PositionSeconds > 11.5, "the position advances while playing");

                probe.Engine.Pause();
                double held = probe.Engine.PositionSeconds;
                Thread.Sleep(200);
                Check(Math.Abs(probe.Engine.PositionSeconds - held) < 0.05, "pausing holds the position");

                probe.Engine.Stop();
                Check(probe.Engine.PositionSeconds < 0.01, "stop returns to the start");
            }
            else
            {
                Note("no audio output device on this machine, so device playback was not exercised");
            }
        }
        catch (Exception ex)
        {
            Check(false, "the audio device path threw: " + ex.GetType().Name + " " + ex.Message);
        }
    }

    // -- the library, indexed --------------------------------------------------------------------

    Console.WriteLine();
    Console.WriteLine("Library");

    FileTranscriptionStore transcripts = new(sessions);
    FileSummaryStore summaries = new(sessions);
    FileSpeakerAliasStore aliases = new(sessions);
    LibraryProjection projection = new(sessions, transcripts, summaries, aliases);

    using SqliteLibraryIndex libraryIndex = new(Path.Combine(root, "library.db"), projection);
    IndexHealth health = await libraryIndex.EnsureReadyAsync();

    Check(health.Usable, "the index opens");
    Check(libraryIndex.Meetings().Count == 1, "the synthetic meeting is listed");

    LibraryFilter thatDay = LibraryFilter.ForLocalDates(
        DateOnly.FromDateTime(origin.ToLocalTime().Date), DateOnly.FromDateTime(origin.ToLocalTime().Date));

    Check(libraryIndex.Meetings(thatDay).Count == 1, "the date range for that day includes it");

    LibraryFilter otherDay = LibraryFilter.ForLocalDates(new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 2));
    Check(libraryIndex.Meetings(otherDay).Count == 0, "a date range elsewhere excludes it");

    // -- the window, actually loaded --------------------------------------------------------------

    Console.WriteLine();
    Console.WriteLine("Window");

    Exception? windowFailure = null;
    List<string> windowChecks = [];

    Thread ui = new(() =>
    {
        try
        {
            EchoForge.App.App application = new();
            application.InitializeComponent();

            using SqliteLibraryIndex windowIndex = new(Path.Combine(root, "library.db"), projection);
            using LibraryViewModel library = new(
                windowIndex,
                projection,
                transcripts,
                summaries,
                aliases,
                new LibraryServices
                {
                    Playback = new PlaybackPreparer(sessions),
                    Devices = () => new NAudioPlaybackDevice(),
                    Deletion = new SessionDeletionService(
                        sessions,
                        root,
                        new CompositeDeletionAuthority(
                            new SessionStateDeletionAuthority(sessions),
                            new LeaseDeletionAuthority(new FileSessionLeaseProvider(sessions))),
                        new WindowsRecycleBin()),
                });

            library.InitializeAsync().GetAwaiter().GetResult();

            LibraryWindow window = new(library)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
            };

            windowChecks.Add("ok:the library window loads");

            window.Show();
            Pump();

            library.SelectedMeeting = library.Meetings.FirstOrDefault();
            Pump();

            List<DependencyObject> tree = [.. Descendants(window)];

            windowChecks.Add((tree.Any(node => node is DatePicker) ? "ok:" : "FAIL:") + "the date pickers are present");
            windowChecks.Add((tree.Any(node => node is Slider { Name: "Timeline" }) ? "ok:" : "FAIL:") + "the timeline is present");

            windowChecks.Add((tree.Any(node => node is Button b && b.Content is string t
                && t.Contains("Delete meeting", StringComparison.Ordinal)) ? "ok:" : "FAIL:") + "the delete action is present");

            windowChecks.Add((tree.Any(node => node is Button b && b.Content is string t
                && t.Contains("Transcribe again", StringComparison.Ordinal)) ? "ok:" : "FAIL:") + "the reprocess actions are present");

            window.Close();
            Pump();
        }
        catch (Exception ex)
        {
            windowFailure = ex;
        }
        finally
        {
            Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            Dispatcher.Run();
        }
    });

    ui.SetApartmentState(ApartmentState.STA);
    ui.Start();

    if (!ui.Join(TimeSpan.FromSeconds(90)))
    {
        Check(false, "the window thread finished");
    }

    foreach (string result in windowChecks)
    {
        Check(result.StartsWith("ok:", StringComparison.Ordinal), result[(result.IndexOf(':') + 1)..]);
    }

    if (windowFailure is not null)
    {
        Check(false, "composing the window threw: " + windowFailure.GetType().Name + " " + windowFailure.Message);
    }

    // -- deletion, on the synthetic meeting only ----------------------------------------------------

    Console.WriteLine();
    Console.WriteLine("Deletion");

    SessionDeletionService deletion = new(
        sessions,
        root,
        new CompositeDeletionAuthority(
            new SessionStateDeletionAuthority(sessions),
            new LeaseDeletionAuthority(new FileSessionLeaseProvider(sessions))),
        new WindowsRecycleBin(),
        id => libraryIndex.UpdateAsync(id));

    DeletionEligibility eligibility = deletion.Check(SessionId);
    Check(eligibility.Title == "Synthetic smoke meeting", "the confirmation names the meeting");

    if (!eligibility.Allowed)
    {
        Note("deletion is not available here: " + eligibility.Message);
    }
    else
    {
        DeletionResult deleted = await deletion.DeleteAsync(SessionId);

        Check(deleted.Deleted, "the synthetic meeting goes to the Recycle Bin: " + deleted.Message);
        Check(!Directory.Exists(paths.Root), "its folder is gone from the session root");
        Check(libraryIndex.Meetings().Count == 0, "it is gone from the library");

        await libraryIndex.RebuildAsync();
        Check(libraryIndex.Meetings().Count == 0, "a rebuild does not bring it back");

        Note("the recycled folder is in the Recycle Bin and can be removed from there");
    }
}
finally
{
    try
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
    catch (IOException)
    {
        Console.WriteLine();
        Console.WriteLine("  note  the temporary directory could not be removed: " + root);
    }
}

Console.WriteLine();
Console.WriteLine(failures.Count == 0 ? "  result  PASS" : "  result  FAIL");

foreach (string failure in failures)
{
    Console.WriteLine("    - " + failure);
}

return failures.Count == 0 ? 0 : 1;

static void Pump()
{
    for (int i = 0; i < 4; i++)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }
}

static IEnumerable<DependencyObject> Descendants(DependencyObject node)
{
    int count = VisualTreeHelper.GetChildrenCount(node);

    for (int i = 0; i < count; i++)
    {
        DependencyObject child = VisualTreeHelper.GetChild(node, i);
        yield return child;

        foreach (DependencyObject nested in Descendants(child))
        {
            yield return nested;
        }
    }
}

/// <summary>
/// Opens a transport over prepared audio and tidies up after itself, so the script never leaves
/// an audio endpoint claimed.
/// </summary>
file sealed class PlaybackEngineProbe : IDisposable
{
    public PlaybackEngineProbe(string audioPath, IPlaybackDevice device)
    {
        Engine = new EchoForge.Core.Playback.PlaybackEngine(
            WavPlaybackAudioSource.Open(audioPath), device);

        Opened = Engine.State != PlaybackState.Failed;
    }

    public EchoForge.Core.Playback.PlaybackEngine Engine { get; }

    public bool Opened { get; }

    public void Dispose() => Engine.Dispose();
}
