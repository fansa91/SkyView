using SkyView.Icons;

namespace SkyView.Models;

public class WeatherForecast
{
    public DateTimeOffset Date { get; set; }
    public double TempHigh { get; set; }
    public double TempLow { get; set; }
    public string Condition { get; set; } = string.Empty;
    public string IconCode { get; set; } = string.Empty;

    /// <summary>当天日出 HH:mm，空表示数据源未提供。</summary>
    public string Sunrise { get; set; } = string.Empty;

    /// <summary>当天日落 HH:mm，空表示数据源未提供。</summary>
    public string Sunset { get; set; } = string.Empty;

    /// <summary>降水概率百分比（0-100），0 表示未知。</summary>
    public int PrecipitationProbability { get; set; }

    /// <summary>预报图标。</summary>
    public WeatherIconKind IconKind => WeatherIconSelector.Select(Condition, PrecipitationProbability);
}
