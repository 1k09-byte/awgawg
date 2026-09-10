using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace stellarisKIT.Converters;

/// <summary>
/// Null → indeterminate ProgressBar (download percent unknown / resolving),
/// non-null → determinate at the bound percent. Maps double? → bool for
/// ProgressBar.IsIndeterminate.
/// </summary>
public sealed class NullToIndeterminateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is null;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
