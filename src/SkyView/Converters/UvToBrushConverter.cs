using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using SkyView.Formatting;
using System;
using Windows.UI;

namespace SkyView.Converters;

/// <summary>
/// UV 指数 → 前景/背景色。五档配色（WHO 常用分级）：
/// 低(绿) / 中等(黄) / 高(橙) / 很高(红) / 极高(紫)。
/// 参数 "Background" 时返回低透明度背景，否则返回实色——与 AqiToBrushConverter 同约定。
/// </summary>
public sealed class UvToBrushConverter : IValueConverter
{
    private static readonly Color[] Palette =
    [
        Colors.Gray,                             // 0 无数据
        Color.FromArgb(255, 0x4C, 0xAF, 0x50),   // 低
        Color.FromArgb(255, 0xD4, 0xC0, 0x00),   // 中等
        Color.FromArgb(255, 0xFF, 0x98, 0x00),   // 高
        Color.FromArgb(255, 0xE5, 0x39, 0x35),   // 很高
        Color.FromArgb(255, 0x8E, 0x24, 0xAA),   // 极高
    ];

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var uv = value switch
        {
            double d => d,
            int i => i,
            _ => double.NaN,
        };

        var level = double.IsNaN(uv) ? 0 : WeatherFormat.UvLevel(uv);
        if (level < 0 || level >= Palette.Length)
            level = 0;

        var color = Palette[level];

        if (parameter is string p && p.Equals("Background", StringComparison.OrdinalIgnoreCase))
            color = Color.FromArgb(0x24, color.R, color.G, color.B);

        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
