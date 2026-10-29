using SkyView.Icons;
using System;
using System.Globalization;

namespace SkyView.Formatting;

/// <summary>
/// 纯展示格式化逻辑（不依赖 UI 框架），便于单元测试。
/// WinUI 的 IValueConverter 只是它的一层薄封装。
/// </summary>
public static class WeatherFormat
{
    public const string DefaultNumberFormat = "0.#";
    public const string DefaultDateFormat = "MM-dd HH:mm";

    /// <summary>摄氏度符号。</summary>
    public const string Celsius = "°C";

    /// <summary>华氏度符号。</summary>
    public const string Fahrenheit = "°F";

    /// <summary>摄氏度转华氏度。</summary>
    public static double ToFahrenheit(double celsius) => celsius * 9d / 5d + 32d;

    /// <summary>按单位设置格式化温度，默认摄氏度。</summary>
    public static string Temperature(double celsius, bool useFahrenheit = false)
    {
        var value = useFahrenheit ? ToFahrenheit(celsius) : celsius;
        var symbol = useFahrenheit ? Fahrenheit : Celsius;
        return value.ToString(DefaultNumberFormat, CultureInfo.CurrentCulture) + symbol;
    }

    /// <summary>只转换数值、不带单位（用于图表数据点、列表里的高低温）。</summary>
    public static double ConvertTemperature(double celsius, bool useFahrenheit = false)
        => useFahrenheit ? ToFahrenheit(celsius) : celsius;

    public static string Number(double value, string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            format = DefaultNumberFormat;

        return value.ToString(format, CultureInfo.CurrentCulture);
    }

    public static string Date(DateTimeOffset value, string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            format = DefaultDateFormat;

        return value.ToLocalTime().ToString(format, CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// 把空气质量指数映射成中文等级。数据源本身给了等级时优先用数据源的，
    /// 这个只作为兜底（例如 OWM 只给 1-5 等级、需要自己算）。
    /// </summary>
    public static string AqiCategory(int aqi) => aqi switch
    {
        <= 0 => "—",
        <= 50 => "优",
        <= 100 => "良",
        <= 150 => "轻度污染",
        <= 200 => "中度污染",
        <= 300 => "重度污染",
        _ => "严重污染",
    };

    /// <summary>
    /// AQI 对应的颜色档位（0-5），供界面选色。
    /// 用档位而不是直接返回颜色，是为了让格式化层不依赖 UI 类型。
    /// </summary>
    public static int AqiLevel(int aqi) => aqi switch
    {
        <= 0 => 0,
        <= 50 => 1,
        <= 100 => 2,
        <= 150 => 3,
        <= 200 => 4,
        <= 300 => 5,
        _ => 6,
    };

    /// <summary>
    /// 按天气状态挑一个 emoji。只用于没有 XAML 图标可用的纯文本场合（如通知、日志）；
    /// 界面上的主视觉走 <c>SkyView.Controls.WeatherIcon</c> 那套矢量图标。
    ///
    /// 分类逻辑统一委托给 <see cref="WeatherIconSelector"/>，避免这里和图标控件各维护一张表。
    /// </summary>
    public static string Glyph(string? condition) => WeatherIconSelector.Select(condition) switch
    {
        WeatherIconKind.Clear => "☀",
        WeatherIconKind.PartlyCloudy => "⛅",
        WeatherIconKind.Cloudy => "☁",
        WeatherIconKind.LightRain or WeatherIconKind.Rain or WeatherIconKind.HeavyRain => "🌧",
        WeatherIconKind.Thunderstorm => "⛈",
        WeatherIconKind.Snow => "❄",
        WeatherIconKind.Fog => "🌫",
        WeatherIconKind.Dust or WeatherIconKind.Wind => "🌪",
        _ => "🌡",
    };

    /// <summary>把时间跨度说成人话，用于"数据来自 N 分钟前"。</summary>
    public static string DescribeAge(TimeSpan age)
    {
        if (age.TotalSeconds < 60)
            return "刚刚";

        if (age.TotalMinutes < 60)
            return $"{(int)age.TotalMinutes} 分钟前";

        if (age.TotalHours < 24)
            return $"{(int)age.TotalHours} 小时前";

        return $"{(int)age.TotalDays} 天前";
    }
}
