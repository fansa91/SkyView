using SkyView.Icons;
using System;

namespace SkyView.Theming;

/// <summary>
/// 一组背景配色。颜色用 <c>#RRGGBB</c> 字符串表示，
/// 因为 <c>SkyView.Core</c> 是纯 .NET 库（net10.0），不引用 Windows.UI，
/// 否则单元测试就得在带 WinUI 的宿主里跑（实测会直接崩测试进程）。
/// </summary>
/// <param name="Name">配色名，用于无障碍描述与调试。</param>
/// <param name="TopColor">渐变起点色 <c>#RRGGBB</c>。</param>
/// <param name="BottomColor">渐变终点色 <c>#RRGGBB</c>。</param>
public readonly record struct WeatherPalette(string Name, string TopColor, string BottomColor);

/// <summary>
/// 天气主题背景：按天气种类给出整页背景的渐变配色。
/// <para>
/// 设计原则：**只做氛围，不抢内容**。色值都是中低饱和度的天光色，
/// 并且界面上以低透明度叠加在原有页面背景之上 ——
/// 卡片本身是不透明的，所以无论浅色/深色主题，文字可读性都不受影响。
/// </para>
/// <para>
/// 归组方式：小雨与中雨共用一组（"下不下雨"比"下多大"更影响天色，
/// 相邻城市的界面不该因为小雨/中雨来回跳色）；强降雨单独深一档，
/// 用来体现"天更暗"。其余各自成组。
/// </para>
/// </summary>
public static class WeatherTheme
{
    private static readonly WeatherPalette Default = new("默认", "#8FA3B5", "#7A8A99");

    public static WeatherPalette For(WeatherIconKind kind) => kind switch
    {
        // 晴天：天空在上（蓝），地平线在下（暖）。反过来会把米黄糊在标题区，
        // 像纸张发霉，实测很难看。
        WeatherIconKind.Clear => new("晴天", "#79C9F5", "#FFCE73"),
        WeatherIconKind.PartlyCloudy => new("多云", "#96CDF5", "#6EAFD7"),
        WeatherIconKind.Cloudy => new("阴天", "#96A0AF", "#69767F"),

        WeatherIconKind.LightRain => new("小雨", "#6B9BC4", "#3F5F85"),
        WeatherIconKind.Rain => new("降雨", "#6B9BC4", "#3F5F85"),
        WeatherIconKind.HeavyRain => new("强降雨", "#5A7FA8", "#2F4A6B"),

        WeatherIconKind.Thunderstorm => new("雷雨", "#5A558C", "#282550"),
        WeatherIconKind.Snow => new("降雪", "#C8E6FA", "#8CB4D7"),
        WeatherIconKind.Fog => new("雾霾", "#AFB4B9", "#7D848C"),
        WeatherIconKind.Dust => new("沙尘", "#D7B482", "#A07850"),
        WeatherIconKind.Wind => new("大风", "#A0CDC3", "#5F9691"),

        _ => Default,
    };
}
