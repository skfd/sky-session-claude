using System.Windows;

namespace SessionApp;

/// <summary>
/// The one window a clicked <c>skysession://</c> link ever shows.
///
/// Two jobs, because they are the same window with a different button: asking before
/// <c>new</c> starts an agent in a folder, and saying what happened when nothing else will.
/// A verb whose success and whose failure look identical is a verb nobody trusts twice, and
/// most of these clicks happen with the app's own window nowhere in sight.
///
/// <c>Topmost</c> on purpose: the click came from a browser, which is what the operator is
/// looking at, and a confirmation that opens behind it is a confirmation that never happened.
/// </summary>
public partial class LinkDialog : Window
{
    private LinkDialog() => InitializeComponent();

    /// <summary>Ask before doing something. True when the operator said yes.</summary>
    public static bool Confirm(string headline, string detail, string accept)
    {
        var dialog = new LinkDialog();
        dialog.Headline.Text = headline;
        dialog.Detail.Text = detail;
        dialog.Accept.Content = accept;
        dialog.Reject.Content = "Cancel";
        return dialog.ShowDialog() == true;
    }

    /// <summary>Say what happened, with nothing to decide.</summary>
    public static void Notice(string headline, string detail)
    {
        var dialog = new LinkDialog();
        dialog.Headline.Text = headline;
        dialog.Detail.Text = detail;
        dialog.Accept.Content = "OK";

        // Nothing to decline when there is nothing being asked, and a lone Cancel next to a
        // lone OK reads as a choice that matters.
        dialog.Reject.Visibility = Visibility.Collapsed;
        dialog.ShowDialog();
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
