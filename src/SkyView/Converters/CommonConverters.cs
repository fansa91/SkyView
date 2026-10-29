using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using SkyView.Formatting;
using System;

namespace SkyView.Converters;

/// <summary>bool → Visibility。参数为 "Invert" 时取反（true → Collapsed）。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>
/// 非 bool 类型（string / 集合 / 可空对象）→ Visibility：
/// null、空字符串、空集合为 Collapsed。参数 "Invert" 取反。
/// </summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isEmpty = value switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            System.Collections.ICollection c => c.Count == 0,
            _ => false,
        };

        if (parameter is string p && p.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            isEmpty = !isEmpty;

        return isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>数值格式化。ConverterParameter 传格式串，例如 "0"、"F1"。</summary>
public sealed class NumberFormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        double d => WeatherFormat.Number(d, parameter as string),
        float f => WeatherFormat.Number(f, parameter as string),
        decimal m => WeatherFormat.Number((double)m, parameter as string),
        int i => WeatherFormat.Number(i, parameter as string),
        _ => value?.ToString() ?? string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => double.TryParse(value?.ToString(), out var d) ? d : 0d;
}

/// <summary>时间格式化。ConverterParameter 传格式串，默认 "MM-dd HH:mm"。</summary>
public sealed class DateTimeFormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        DateTimeOffset dto => WeatherFormat.Date(dto, parameter as string),
        DateTime dt => WeatherFormat.Date(dt, parameter as string),
        _ => string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
