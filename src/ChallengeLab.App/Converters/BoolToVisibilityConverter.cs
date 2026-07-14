using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ChallengeLab.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>If true (or ConverterParameter = "invert"), null → Visible, non-null → Collapsed.</summary>
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var invert = Invert ||
                     string.Equals(parameter?.ToString(), "invert", StringComparison.OrdinalIgnoreCase);
        var isNull = value is null;
        var show = invert ? isNull : !isNull;
        return show ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
