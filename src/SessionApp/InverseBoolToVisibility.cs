using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SessionApp;

/// <summary>
/// <see cref="BooleanToVisibilityConverter"/> the other way round, so one flag can drive two
/// sets of controls: the filters that belong to the project list appear when it is showing,
/// and the ones that only mean something for sessions appear when it is not.
/// </summary>
public sealed class InverseBoolToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}
