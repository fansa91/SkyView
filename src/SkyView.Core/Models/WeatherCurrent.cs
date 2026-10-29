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

    public bool HasAirQuality => Aqi > 0;

    public bool HasWind => WindSpeed > 0;

    public bool HasSunTimes => !string.IsNullOrEmpty(Sunrise) && !string.IsNullOrEmpty(Sunset);

    /// <summary>供界面直接绑定的图标种类，分类规则见 <see cref="WeatherIconSelector"/>。</summary>
    public WeatherIconKind IconKind => WeatherIconSelector.Select(Condition);
}
