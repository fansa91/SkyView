using SkyView.Icons;

namespace SkyView.Models;

/// <summary>逐小时预报（未来 N 小时）。</summary>
public class WeatherHourly
{
    public DateTimeOffset Time { get; set; }
    public double Temperature { get; set; }
    public string Condition { get; set; } = string.Empty;
    public string IconCode { get; set; } = string.Empty;

    /// <summary>降水概率百分比（0-100）。0 表示未知或无降水。</summary>
    public int PrecipitationProbability { get; set; }

    /// <summary>实际提供这条逐小时预报的数据源名称（按功能分配后可能逐项不同），空表示未知。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>用于界面显示的时段文本，如 14:00。</summary>
    public string TimeText => Time.ToString("HH:mm");

    /// <summary>当前小时标记为「现在」，方便界面强调。</summary>
    public bool IsNow => Time.LocalDateTime.Hour == DateTimeOffset.Now.Hour
                         && Time.LocalDateTime.Date == DateTimeOffset.Now.Date;

    /// <summary>有降水概率时才在界面上显示，避免每格都挂一个 0%。</summary>
    public bool HasPrecipitation => PrecipitationProbability > 0;

    /// <summary>逐小时图标。带降水概率，文字被上游归并成「雨」时还能反映强度。</summary>
    public WeatherIconKind IconKind => WeatherIconSelector.Select(Condition, PrecipitationProbability);
}
