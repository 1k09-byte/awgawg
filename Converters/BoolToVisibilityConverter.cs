using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace stellarisKIT.Converters;

/// <summary>bool → Visibility (Visibility.Collapsed when false), with an optional "invert" parameter.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool result = value is bool b && b;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
            result = !result;
        return result ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility v && v == Visibility.Visible;
}
