using System.Windows;
using SessionCore;

namespace SessionApp;

/// <summary>One line of the plan: a project about to get a host, or one passed over.</summary>
public sealed record StandbyLine(string Title, string Detail, bool Skipped);

/// <summary>
/// The plan the Standby button states before it opens anything — this dialog is what
/// <c>--yes</c> is on the command line.
///
/// The confirmation is not about anything at risk: everything standby touches is something it
/// just made, and nothing it does can lose work. It is about the hosts that are about to start
/// — a tab each in one Windows Terminal window — which is a thing to be told before it happens
/// rather than after.
/// </summary>
public partial class StandbyDialog : Window
{
    /// <summary>The line above the list: what will happen, and the trust caveat when it applies.</summary>
    public string Preamble { get; }

    public StandbyDialog(StandbyPlan plan, string preamble)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        Preamble = preamble;

        var lines = new List<StandbyLine>();
        foreach (var target in plan.Open)
            lines.Add(new StandbyLine(
                target.Project,
                $"{target.Folder}  ·  {TextUtil.RelativeAge(target.LastActive)}",
                false));
        foreach (var skip in plan.Skipped)
            lines.Add(new StandbyLine(skip.Project, $"skipped — {skip.Reason}", true));

        List.ItemsSource = lines;
        OpenBtn.Content = plan.Open.Count == 1
            ? "Open 1 host"
            : $"Open {plan.Open.Count} hosts";
    }

    private void Open_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
