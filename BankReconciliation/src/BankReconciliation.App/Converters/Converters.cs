using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BankReconciliation.Core.Models;

namespace BankReconciliation.App.Converters;

/// <summary>Maps a <see cref="MatchStatus"/> to the theme brush key used to
/// color its grid row / status pill. Returns the DynamicResource-resolved
/// brush directly (needs a live Window to resolve resources against, passed
/// as the converter parameter is not used here — instead each usage site
/// looks the brush up from Application.Current.Resources, which always has
/// the current theme merged in).</summary>
public sealed class MatchStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            MatchStatus.MatchedExact or MatchStatus.MatchedDateTolerant => "SuccessBackgroundBrush",
            MatchStatus.MatchedCombination => "InfoBackgroundBrush",
            MatchStatus.ManualReview or MatchStatus.PossibleDuplicate => "WarningBackgroundBrush",
            MatchStatus.NoMatch => "DangerBackgroundBrush",
            _ => "PanelBackgroundBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class MatchStatusToForegroundBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            MatchStatus.MatchedExact or MatchStatus.MatchedDateTolerant => "SuccessBrush",
            MatchStatus.MatchedCombination => "InfoBrush",
            MatchStatus.ManualReview or MatchStatus.PossibleDuplicate => "WarningBrush",
            MatchStatus.NoMatch => "DangerBrush",
            _ => "TextSecondaryBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Black;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        if (parameter as string == "Invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || (value is string s && string.IsNullOrEmpty(s)) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => !(value is bool v && v);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool v && v);
}

public sealed class PercentToWidthConverter : IMultiValueConverter
{
    /// <summary>values[0] = percent (0-100), values[1] = available width.</summary>
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double percent || values[1] is not double width) return 0.0;
        return Math.Max(0, width * Math.Clamp(percent, 0, 100) / 100.0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
