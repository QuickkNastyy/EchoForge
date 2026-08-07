#:project ../src/EchoForge.App/EchoForge.App.csproj
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property IsAotCompatible=false
#:property PublishTrimmed=false
#:property EnableTrimAnalyzer=false
#:property EnableAotAnalyzer=false

// The main window with its new navigation rail, loaded for real against the application resources.
//
// A missing StaticResource or a malformed template is not a compile error — it throws when the
// window loads. This constructs the real MainWindow, confirms it renders, and checks that the rail's
// three destinations (Record, All recordings, Setup) are present with accessibility names, and that
// the recording ribbon is in the tree. No recording, no audio device, no data context needed.
//
//   dotnet run scripts/smoke-mainwindow.cs

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EchoForge.App.Recording;

Console.WriteLine("EchoForge main-window rail smoke test");
Console.WriteLine();

Thread ui = new(() =>
{
    int failures = 0;
    void Check(bool condition, string what)
    {
        Console.WriteLine((condition ? "  ok    " : "  FAIL  ") + what);
        if (!condition) { failures++; }
    }

    try
    {
        EchoForge.App.App application = new();
        application.InitializeComponent();
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Constructing the window resolves every StaticResource and builds the logical tree — which
        // is all we need to check the rail. No Show(), so there is no expensive render pass to wait
        // on. A missing resource or malformed template throws right here.
        EchoForge.App.MainWindow window = new();

        List<DependencyObject> tree = [.. LogicalDescendants(window)];

        HashSet<string> navNames = tree
            .OfType<Button>()
            .Select(AutomationProperties.GetName)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet();

        Check(navNames.Contains("Record"), "the rail has a Record destination");
        Check(navNames.Contains("All recordings"), "the rail has an All recordings destination (not a footer button)");
        Check(navNames.Contains("Setup"), "the rail has a Setup destination");

        int railWithTooltips = tree.OfType<Button>()
            .Count(b => !string.IsNullOrEmpty(AutomationProperties.GetName(b)) && b.ToolTip is not null);
        Check(railWithTooltips >= 3, "the rail's icon buttons carry tooltips");

        Check(tree.OfType<Button>().Any(b => b.Content is "Start recording"), "the Start recording button is present");
        Check(tree.OfType<SpeechRibbon>().Any(), "the speech-density ribbon is on the recording screen");

        bool oldFooter = tree.OfType<Button>().Any(b => b.Content is "Meetings…" or "Setup…");
        Check(!oldFooter, "the old Meetings…/Setup… footer buttons are gone");

        // The compact recorder loads against the real resources and carries its two meters.
        EchoForge.App.CompactRecorderWindow compact = new();
        List<DependencyObject> compactTree = [.. LogicalDescendants(compact)];
        Check(compactTree.OfType<System.Windows.Controls.ProgressBar>().Count() >= 2, "the compact recorder has You and Remote meters");
        Check(compactTree.OfType<Button>().Any(b => b.ToolTip is "Stop recording"), "the compact recorder has a Stop button");
    }
    catch (Exception ex)
    {
        Console.WriteLine("  FAIL  the window threw: " + ex);
        failures++;
    }

    Console.WriteLine();
    Console.WriteLine(failures == 0 ? "  result  PASS" : "  result  FAIL");
    // Exit from the STA thread rather than unwinding the WPF dispatcher, which is what kept the
    // process alive in earlier attempts.
    Environment.Exit(failures == 0 ? 0 : 1);
});
ui.SetApartmentState(ApartmentState.STA);
ui.IsBackground = true;
ui.Start();
ui.Join(TimeSpan.FromSeconds(120));
Console.WriteLine("  FAIL  the window did not finish in time");
Environment.Exit(1);

static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
{
    Queue<DependencyObject> queue = new();
    queue.Enqueue(root);
    while (queue.Count > 0)
    {
        DependencyObject node = queue.Dequeue();
        yield return node;
        foreach (object child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject dependency)
            {
                queue.Enqueue(dependency);
            }
        }
    }
}
