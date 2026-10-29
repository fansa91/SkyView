using SkyView.Icons;
using SkyView.Models;
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
    /// UV 指数 → 中文等级（WHO/国标常用五档：低 / 中等 / 高 / 很高 / 极高）。
    /// </summary>
    public static string UvCategory(double uv) => uv switch
    {
        <= 2 => "低",
        <= 5 => "中等",
        <= 7 => "高",
        <= 10 => "很高",
        _ => "极高",
    };

    /// <summary>UV 等级对应的颜色档位（0-5），供界面选色（与 AqiLevel 同思路）。</summary>
    public static int UvLevel(double uv) => uv switch
    {
        <= 2 => 1,   // 低 → 绿
        <= 5 => 2,   // 中等 → 黄
        <= 7 => 3,   // 高 → 橙
        <= 10 => 4,  // 很高 → 红
        _ => 5,      // 极高 → 紫
    };

    /// <summary>
    /// 根据逐小时气压序列推断气压趋势，返回带箭头的短文案；数据不足时返回空串。
    /// <para>
    /// 取最近 <paramref name="sampleHours"/> 小时内**有气压值**的首尾两点算差值：
    /// 6 小时内变化 ≥2 hPa 记为「急」、1~2 记为普通变化、&lt;1 视为平稳。
    /// 纯函数、不依赖 UI 类型，方便单元测试。
    /// </para>
    /// </summary>
    public static string PressureTrend(IReadOnlyList<double?> pressures, int sampleHours = 6)
    {
        var points = new List<double>();
        foreach (var p in pressures)
        {
            if (p.HasValue && !double.IsNaN(p.Value))
                points.Add(p.Value);
            if (points.Count >= sampleHours)
                break;
        }

        if (points.Count < 2)
            return string.Empty;

        var delta = points[^1] - points[0];
        if (Math.Abs(delta) < 1)
            return "→ 平稳";
        return delta >= 2 ? "▲ 急升" : delta >= 1 ? "▲ 回升" : delta <= -2 ? "▼ 急降" : "▼ 下降";
    }

    /// <summary>
    /// 短临降雨预判 → 主页短文案（tier 2：分钟级降雨）。
    /// <para>
    /// 窗口内无雨但概率不低时附一句概率提示（阴而无雨也常见）；
    /// 已经开始下则说"正在下雨"。纯函数，边界文案由单测钉死。
    /// </para>
    /// </summary>
    public static string MinutelyRainSummary(MinutelyRainInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (!info.HasRain)
        {
            var tail = info.MaxProbability >= 30 ? $"（降水概率最高 {info.MaxProbability}%）" : string.Empty;
            return $"短临降雨：未来 {MinutelyRainInfo.WindowHours} 小时无雨{tail}";
        }

        if (info.MinutesUntilRain <= 0)
            return $"短临降雨：正在下雨，预计持续约 {info.RainDurationMinutes} 分钟";

        return $"短临降雨：约 {info.MinutesUntilRain} 分钟后开始下雨，持续约 {info.RainDurationMinutes} 分钟";
    }

    /// <summary>
    /// 花粉浓度（grains/m³）→ 中文等级。四级（低/中/高/极高），
    /// 阈值取常用花粉量级：≤20 低、≤50 中、≤100 高、其余极高。
    /// </summary>
    public static string PollenCategory(double grains) => grains switch
    {
        <= 20 => "低",
        <= 50 => "中",
        <= 100 => "高",
        _ => "极高",
    };

    /// <summary>花粉等级对应的颜色档位（1-4），供界面选色（与 UvLevel 同思路）。</summary>
    public static int PollenLevel(double grains) => grains switch
    {
        <= 20 => 1,
        <= 50 => 2,
        <= 100 => 3,
        _ => 4,
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
