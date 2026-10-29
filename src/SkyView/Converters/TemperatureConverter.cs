using Microsoft.UI.Xaml.Data;
using SkyView.Formatting;
using SkyView.Services;
using System;

namespace SkyView.Converters;

/// <summary>
/// 温度格式化：22.5 → "22.5°C" / "72.5°F"。
/// 单位从 <see cref="UnitSettings"/> 读，因为 IValueConverter 无法接收额外的绑定参数。
/// 切换单位后需要重建列表集合，DataTemplate 里的绑定才会重新求值。
/// </summary>
public sealed class TemperatureConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var useFahrenheit = UnitSettings.UseFahrenheit;

        return value switch
        {
            double d => WeatherFormat.Temperature(d, useFahrenheit),
            float f => WeatherFormat.Temperature(f, useFahrenheit),
            int i => WeatherFormat.Temperature(i, useFahrenheit),
            _ => string.Empty,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        // 只用于单向展示；保持不变即可（WinUI 里没有 WPF 的 Binding.DoNothing）
        return value ?? string.Empty;
    }
}
