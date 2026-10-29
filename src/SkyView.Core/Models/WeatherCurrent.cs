using SkyView.Icons;

namespace SkyView.Models;

/// <summary>实时天气。字段以"可直接展示"为目标，各数据源的原始结构在 Provider 里归一。</summary>
public class WeatherCurrent
{
    public string City { get; set; } = string.Empty;
    public double Temperature { get; set; }
    public int Humidity { get; set; }
    public double Pressure { get; set; }
    public string Condition { get; set; } = string.Empty;
    public string IconCode { get; set; } = string.Empty;
    public DateTimeOffset ObservedAt { get; set; }
    public string Source { get; set; } = string.Empty;

    /// <summary>体感温度。数据源不提供时由 Provider 填成 Temperature。</summary>
    public double FeelsLike { get; set; }

    /// <summary>风向文本，如「东南风」。没有时为空。</summary>
    public string WindDirection { get; set; } = string.Empty;

    /// <summary>风速 km/h。0 表示未知。</summary>
    public double WindSpeed { get; set; }

    /// <summary>日出时间 HH:mm。空字符串表示数据源未提供。</summary>
    public string Sunrise { get; set; } = string.Empty;

    /// <summary>日落时间 HH:mm。空字符串表示数据源未提供。</summary>
    public string Sunset { get; set; } = string.Empty;

    /// <summary>空气质量指数。0 表示未获取到。</summary>
    public int Aqi { get; set; }

    /// <summary>空气质量等级文本，如「优」「良」「轻度污染」。</summary>
    public string AqiCategory { get; set; } = string.Empty;

    /// <summary>首要污染物，如「PM2.5」。</summary>
    public string PrimaryPollutant { get; set; } = string.Empty;

    /// <summary>PM2.5 浓度 μg/m³。0 表示未知。</summary>
    public double Pm25 { get; set; }

    /// <summary>UV 指数。null 表示数据源未提供（注意 0 是合法值——夜间 UV 就是 0）。</summary>
    public double? UvIndex { get; set; }

    /// <summary>实际降水量 mm。null 表示数据源未提供。</summary>
    public double? Precipitation { get; set; }

    /// <summary>云量百分比（0-100）。null 表示数据源未提供。</summary>
    public double? CloudCover { get; set; }

    /// <summary>露点温度 °C（随单位设置换算展示）。null 表示数据源未提供。</summary>
    public double? DewPoint { get; set; }

    /// <summary>阵风风速 km/h。null 表示数据源未提供。</summary>
    public double? WindGusts { get; set; }

    /// <summary>
    /// 短临降雨预判（未来 2 小时分钟级）。null 表示数据源未提供分钟级降雨数据
    /// （wttr.in 等只有逐小时）；非 null 即「有数据」，无雨时界面显示"未来 2 小时无雨"。
    /// </summary>
    public MinutelyRainInfo? MinutelyRain { get; set; }

    /// <summary>
    /// 花粉浓度（tier 3）。null 表示数据源不提供花粉数据——Open-Meteo 的
    /// Air Quality API 花粉变量主要覆盖欧洲，国内城市全部返回 null（界面隐藏整行）。
    /// </summary>
    public PollenInfo? Pollen { get; set; }

    public bool HasAirQuality => Aqi > 0;

    public bool HasWind => WindSpeed > 0;

    public bool HasSunTimes => !string.IsNullOrEmpty(Sunrise) && !string.IsNullOrEmpty(Sunset);

    public bool HasUvIndex => UvIndex.HasValue;

    public bool HasPrecipitation => Precipitation.HasValue;

    public bool HasCloudCover => CloudCover.HasValue;

    public bool HasDewPoint => DewPoint.HasValue;

    public bool HasWindGusts => WindGusts.HasValue;

    /// <summary>详细指标行（降水 / 云量 / 露点 / 阵风）是否有任何一项可显示。</summary>
    public bool HasDetailMetrics => HasPrecipitation || HasCloudCover || HasDewPoint || HasWindGusts;

    /// <summary>数据源是否提供分钟级降雨预判（有数据才显示，无雨也是一种结论）。</summary>
    public bool HasMinutelyRain => MinutelyRain is not null;

    /// <summary>数据源是否提供花粉数据（国内城市通常不提供，整行隐藏）。</summary>
    public bool HasPollen => Pollen is { HasData: true };

    /// <summary>供界面直接绑定的图标种类，分类规则见 <see cref="WeatherIconSelector"/>。</summary>
    public WeatherIconKind IconKind => WeatherIconSelector.Select(Condition);
}
