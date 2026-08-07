#:project ../src/EchoForge.App/EchoForge.App.csproj
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property IsAotCompatible=false
#:property PublishTrimmed=false
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false

// The speech-density ribbon, rendered for real to a bitmap and inspected.
//
// The unit suite covers the activity history. What it cannot check is that the control actually
// draws — that both lanes appear in their own colours, that a lost track shows red, and that a
// three-hour history renders quickly without throwing. This renders the SpeechRibbon offscreen and
// counts coloured pixels. Everything is synthetic.
//
//   dotnet run scripts/smoke-recording-ui.cs

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EchoForge.App.Recording;

int failures = 0;
void Check(bool condition, string what)
{
    Console.WriteLine((condition ? "  ok    " : "  FAIL  ") + what);
    if (!condition) { failures++; }
}

Console.WriteLine("EchoForge recording-UI smoke test");
Console.WriteLine();

Exception? failure = null;

Thread ui = new(() =>
{
    try
    {
        // A ribbon with You loud early, Remote loud late.
        SpeechActivityHistory history = new(1.0);
        for (int t = 0; t < 60; t++)
        {
            history.Add(t, t < 30 ? 0.9 : 0.05, true, t >= 30 ? 0.9 : 0.05, true);
        }

        (int you, int remote, int red) = RenderCounts(history, 1.0);
        Check(you > 50, $"the You lane draws in amber ({you} px)");
        Check(remote > 50, $"the Remote lane draws in teal ({remote} px)");

        // A dropped microphone: inactive You samples must flatline in red.
        SpeechActivityHistory dropped = new(1.0);
        for (int t = 0; t < 40; t++)
        {
            dropped.Add(t, 0.0, t >= 20 == false, 0.8, true); // You goes inactive from t=20
        }
        (_, _, int redPx) = RenderCounts(dropped, 1.0);
        Check(redPx > 10, $"a lost track flatlines in red ({redPx} px)");

        // Empty history renders without throwing.
        (int e1, int e2, int e3) = RenderCounts(new SpeechActivityHistory(), 1.0);
        Check(e1 == 0 && e2 == 0, "an empty ribbon renders with no lane bars");

        // A three-hour history renders quickly and stays bounded.
        SpeechActivityHistory big = new(1.0);
        for (int i = 0; i < 3 * 3600 * 5; i++) { big.Add(i / 5.0, 0.6, true, 0.6, true); }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        (int b1, int b2, _) = RenderCounts(big, 1.0);
        clock.Stop();
        Check(big.Count <= SpeechActivityHistory.Capacity, $"the long recording stays bounded ({big.Count} buckets)");
        Check(b1 > 50 && b2 > 50, "the long recording still draws both lanes");
        Check(clock.ElapsedMilliseconds < 1500, $"the long recording renders quickly ({clock.ElapsedMilliseconds} ms)");
    }
    catch (Exception ex)
    {
        failure = ex;
    }
    finally
    {
        Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        Dispatcher.Run();
    }
});
ui.SetApartmentState(ApartmentState.STA);
ui.Start();
if (!ui.Join(TimeSpan.FromSeconds(60)))
{
    Console.WriteLine("  FAIL  the render did not finish in time");
    Environment.Exit(1);
}
if (failure is not null)
{
    Console.WriteLine("  FAIL  the ribbon threw: " + failure);
    Environment.Exit(1);
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "  result  PASS" : "  result  FAIL");
Environment.Exit(failures == 0 ? 0 : 1);

// Renders the ribbon to a bitmap and counts amber (You), teal (Remote) and red (dead) pixels.
static (int You, int Remote, int Red) RenderCounts(SpeechActivityHistory history, double playhead)
{
    SpeechRibbon ribbon = new()
    {
        History = history,
        PlayheadFraction = playhead,
        Width = 700,
        Height = 96,
    };
    Size size = new(700, 96);
    ribbon.Measure(size);
    ribbon.Arrange(new Rect(size));
    ribbon.UpdateLayout();

    RenderTargetBitmap bmp = new(700, 96, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(ribbon);

    int stride = 700 * 4;
    byte[] px = new byte[stride * 96];
    bmp.CopyPixels(px, stride, 0);

    int you = 0, remote = 0, red = 0;
    for (int i = 0; i < px.Length; i += 4)
    {
        int b = px[i], g = px[i + 1], r = px[i + 2];
        if (Near(r, g, b, 0xF2, 0xA9, 0x3B)) { you++; }
        else if (Near(r, g, b, 0x3F, 0xBF, 0xC7)) { remote++; }
        else if (Near(r, g, b, 0xE2, 0x45, 0x3D)) { red++; }
    }
    return (you, remote, red);
}

static bool Near(int r, int g, int b, int tr, int tg, int tb)
{
    return Math.Abs(r - tr) < 40 && Math.Abs(g - tg) < 40 && Math.Abs(b - tb) < 40;
}
