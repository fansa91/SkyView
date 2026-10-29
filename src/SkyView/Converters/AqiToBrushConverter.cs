using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using SkyView.Formatting;
using System;
using Windows.UI;

namespace SkyView.Converters;

/// <summary>
/// AQI 数值 → 前景/背景色。
/// 颜色按国标六级配色：优(绿) / 良(黄) / 轻度(橙) / 中度(红) / 重度(紫) / 严重(褐红)。
/// 参数 "Background" 时返回浅色背景，否则返回实色。
/// </summary>
public sealed class AqiToBrushConverter : IValueConverter
{
    private static readonly Color[] Palette =
    [
        Colors.Gray,                      // 0 无数据
        Color.FromArgb(255, 0x4C, 0xAF, 0x50), // 优
        Color.FromArgb(255, 0xD4, 0xC0, 0x00), // 良
        Color.FromArgb(255, 0xFF, 0x98, 0x00), // 轻度污染
        Color.FromArgb(255, 0xE5, 0x39, 0x35), // 中度污染
        Color.FromArgb(255, 0x8E, 0x24, 0xAA), // 重度污染
        Color.FromArgb(255, 0x7C, 0x0C, 0x1F), // 严重污染
    ];

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var aqi = value switch
        {
            int i => i,
            double d => (int)Math.Round(d),
            _ => 0,
        };

        var level = WeatherFormat.AqiLevel(aqi);
        if (level < 0 || level >= Palette.Length)
            level = 0;

        var color = Palette[level];

        // 背景用低透明度版本，避免整块卡片被高饱和色淹没
        if (parameter is string p && p.Equals("Background", StringComparison.OrdinalIgnoreCase))
            color = Color.FromArgb(0x24, color.R, color.G, color.B);

        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
