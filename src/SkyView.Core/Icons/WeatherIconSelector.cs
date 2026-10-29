using System;

namespace SkyView.Icons;

/// <summary>
/// 把归一化后的天气描述映射成 <see cref="WeatherIconKind"/>。
///
/// 为什么只按文字匹配：
/// 各数据源的 <c>IconCode</c> 语义完全不同（和风是 "100"，OpenWeatherMap 是 "01d"，
/// 心知是 "0"），拿它做通用映射只会把三个源的语义混在一起。文字描述则已经被
/// <c>WeatherProviderBase.MapCondition</c> 归成中文短词，是唯一稳定的输入。
///
/// 规则表是**有序**的：先匹配到的赢。顺序不是随便排的，几个必须靠前的：
/// - 雷：<c>雷阵雨</c> 里含「雨」，雷要排在雨前面
/// - 雪：<c>雨夹雪</c> 里含「雨」，画成雪比画成雨更贴切
/// - 暴雨/大雨：要排在小雨和通用「雨」前面，否则强度信息全丢
/// </summary>
public static class WeatherIconSelector
{
    /// <summary>有序规则表：按优先级从高到低。</summary>
    private static readonly (WeatherIconKind Kind, string[] Keywords)[] Rules =
    [
        (WeatherIconKind.Thunderstorm, ["雷", "thunder", "tstorm", "lightning"]),
        (WeatherIconKind.Snow, ["雪", "snow", "sleet", "blizzard", "flurr"]),
        (WeatherIconKind.HeavyRain, ["暴雨", "大雨", "强降雨", "强降水", "heavy rain", "torrential", "downpour"]),
        (WeatherIconKind.LightRain, ["小雨", "毛毛雨", "细雨", "微雨", "drizzle", "light rain", "shower"]),
        (WeatherIconKind.Rain, ["雨", "rain", "wet"]),
        (WeatherIconKind.Dust, ["沙", "尘", "dust", "sand"]),
        (WeatherIconKind.Fog, ["雾", "霾", "fog", "mist", "haze", "smoke"]),
        (WeatherIconKind.Wind, ["风", "wind", "gale", "gust"]),
        (WeatherIconKind.Cloudy, ["阴", "overcast"]),
        (WeatherIconKind.PartlyCloudy, ["云", "cloud"]),
        (WeatherIconKind.Clear, ["晴", "clear", "sun", "fair"]),
    ];

    /// <summary>
    /// 根据天气描述选图标。空值或无法识别时返回 <see cref="WeatherIconKind.Unknown"/>。
    /// </summary>
    public static WeatherIconKind Select(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return WeatherIconKind.Unknown;

        var text = condition.Trim();

        foreach (var (kind, keywords) in Rules)
        {
            foreach (var keyword in keywords)
            {
                if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return kind;
            }
        }

        return WeatherIconKind.Unknown;
    }

    /// <summary>
    /// 带降水概率的重载。
    ///
    /// 上游 <c>MapCondition</c> 会把「小雨 / 中雨 / 大雨」一律压成「雨」，
    /// 文字里的强度信息就没了。降水概率是个还活着的信号：概率很高时画大雨图标，
    /// 至少不会出现「明明是暴雨却挂着小雨图标」的观感。
    /// </summary>
    public static WeatherIconKind Select(string? condition, int precipitationProbability)
    {
        var kind = Select(condition);

        if (kind == WeatherIconKind.Rain && precipitationProbability >= 70)
            return WeatherIconKind.HeavyRain;

        return kind;
    }

    /// <summary>图标的中文名，用于无障碍朗读与测试断言。</summary>
    public static string Describe(WeatherIconKind kind) => kind switch
    {
        WeatherIconKind.Clear => "晴",
        WeatherIconKind.PartlyCloudy => "多云",
        WeatherIconKind.Cloudy => "阴",
        WeatherIconKind.LightRain => "小雨",
        WeatherIconKind.Rain => "雨",
        WeatherIconKind.HeavyRain => "大雨",
        WeatherIconKind.Thunderstorm => "雷阵雨",
        WeatherIconKind.Snow => "雪",
        WeatherIconKind.Fog => "雾霾",
        WeatherIconKind.Dust => "沙尘",
        WeatherIconKind.Wind => "大风",
        _ => "未知",
    };
}
