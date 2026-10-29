namespace SkyView.Models;

/// <summary>
/// 本地推导的恶劣天气预警。
/// <para>
/// 各数据源都没有免 Key 的官方预警接口（和风天气有，但三把付费 Key 全部失效），
/// 这里从 daily/current 数据按公开阈值推导，与「气压趋势」「短临降雨」同属本地计算，
/// 不引入新请求、不依赖新数据源。
/// </para>
/// </summary>
public sealed class WeatherAlert
{
    /// <summary>预警类型，如「降雨」「雷暴」「高温」「大风」。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>等级 1-4：蓝 / 黄 / 橙 / 红（对齐国内预警色系）。</summary>
    public int Level { get; set; }

    /// <summary>等级文本，如「黄色」。</summary>
    public string LevelText { get; set; } = string.Empty;

    /// <summary>日期文本：「今天」/「明天」/「MM-dd」/「现在」。</summary>
    public string DateText { get; set; } = string.Empty;

    /// <summary>触发原因，如「降水概率 80%」「最高 36°C」。</summary>
    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset Date { get; set; }
}
