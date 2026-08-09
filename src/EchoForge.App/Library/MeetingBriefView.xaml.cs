using System.Windows;
using System.Windows.Controls;
using EchoForge.Contracts.Library;

namespace EchoForge.App.Library;

/// <summary>
/// The meeting brief.
///
/// <para>
/// The only thing it does is raise <see cref="EvidenceRequested"/> when somebody asks where a
/// claim came from. Following the citation — opening the exact revision it names, scrolling the
/// transcript, cueing the audio — belongs to the page that owns both panes.
/// </para>
/// </summary>
public partial class MeetingBriefView : System.Windows.Controls.UserControl
{
    public MeetingBriefView() => InitializeComponent();

    /// <summary>Raised with the first citation of whatever the reader asked about.</summary>
    public event EventHandler<EvidenceLocation>? EvidenceRequested;

    private void OnShowEvidence(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<EvidenceLocation> evidence = (sender as FrameworkElement)?.Tag switch
        {
            PlanStepRow step => step.Evidence,
            SummaryLine line => line.Evidence,
            _ => [],
        };

        if (evidence.Count > 0)
        {
            EvidenceRequested?.Invoke(this, evidence[0]);
        }
    }
}
