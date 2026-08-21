using System.Windows;
using System.Windows.Input;

namespace SessionApp;

/// <summary>One row in the fork picker. Null <see cref="LeafUuid"/> = fork at the tip.</summary>
public sealed record ForkChoice(string Title, string Detail, string? LeafUuid);

/// <summary>
/// Modal picker for the F hotkey: fork at the tip (official --fork-session) or from
/// just before any earlier operator prompt (truncated-copy fork, see SessionForker).
/// </summary>
public partial class ForkDialog : Window
{
    public ForkChoice? Choice { get; private set; }

    public ForkDialog(string sessionName, IReadOnlyList<ForkChoice> choices)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        Title = $"Fork \"{sessionName}\"";
        List.ItemsSource = choices;
        List.SelectedIndex = 0;
        Loaded += (_, _) =>
        {
            List.Focus();
            if (List.SelectedItem is not null)
                ((FrameworkElement?)List.ItemContainerGenerator.ContainerFromIndex(0))?.Focus();
        };
    }

    private void Fork_Click(object sender, RoutedEventArgs e)
    {
        // Double-click on empty space below the rows lands here with no selection.
        if (e is MouseButtonEventArgs && List.SelectedItem is null) return;
        Choice = List.SelectedItem as ForkChoice;
        if (Choice is not null) DialogResult = true;
    }
}
