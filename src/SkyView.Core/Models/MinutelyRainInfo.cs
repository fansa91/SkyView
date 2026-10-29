namespace SkyView.Models;

/// <summary>
/// 短临降雨预判（未来 2 小时、15 分钟粒度），由 Provider 从分钟级序列本地计算，UI 只消费结论。
/// <para>
/// 两种"没有雨"的语义必须分清：
/// <see cref="WeatherCurrent.MinutelyRain"/> 为 null = 数据源不提供分钟级数据（wttr.in 等），界面整行隐藏；
/// 非 null 且 <see cref="HasRain"/> 为 false = 有数据、窗口内无雨，界面显示"未来 2 小时无雨"。
/// </para>
/// </summary>
public sealed class MinutelyRainInfo
{
    /// <summary>预判窗口长度（小时）。分钟级降雨只看短临，拉长窗口没有预报价值。</summary>
    public const int WindowHours = 2;

    /// <summary>采样粒度（分钟）。Open-Meteo 的 minutely_15 即 15 分钟一格。</summary>
    public const int SlotMinutes = 15;

    /// <summary>判定"正在下雨"的单槽降水量阈值 mm：低于此视为微量，不当成雨。</summary>
    public const double RainThresholdMm = 0.1;

    /// <summary>窗口内开始下雨还需多少分钟（Provider 侧已按 5 分钟取整）；无雨时无意义。</summary>
    public int MinutesUntilRain { get; init; }

    /// <summary>预计持续分钟数（按整槽累计，上限 = 窗口长度）；无雨时为 0。</summary>
    public int RainDurationMinutes { get; init; }

    /// <summary>窗口内最大降水概率百分比（0-100）。无雨时也可能不低（阴而无雨）。</summary>
    public int MaxProbability { get; init; }

    /// <summary>窗口内是否有可感知的降雨。Provider 用 <see cref="MinutesUntilRain"/> = -1 表示无雨。</summary>
    public bool HasRain => MinutesUntilRain >= 0 && RainDurationMinutes > 0;
}
