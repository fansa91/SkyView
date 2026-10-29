using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using SkyView.Alerts;
using System;
using Windows.UI;

namespace SkyView.Converters;

/// <summary>
/// 预警等级 → 前景/背景色。四级对齐国内预警色系：蓝 / 黄 / 橙 / 红。
/// 参数 "Background" 时返回低透明度背景，否则返回实色——与 UvToBrushConverter 同约定。
/// </summary>
public sealed class AlertLevelToBrushConverter : IValueConverter
{
    private static readonly Color[] Palette =
    [
        Colors.Gray,                             // 0 无数据
        Color.FromArgb(255, 0x1E, 0x88, 0xE5),   // 蓝
        Color.FromArgb(255, 0xF9, 0xA8, 0x25),   // 黄
        Color.FromArgb(255, 0xFB, 0x8C, 0x00),   // 橙
        Color.FromArgb(255, 0xE5, 0x39, 0x35),   // 红
    ];

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var level = value switch
        {
            int i => i,
            double d => (int)d,
            _ => 0,
        };

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
