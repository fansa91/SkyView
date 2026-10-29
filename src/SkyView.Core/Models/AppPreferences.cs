namespace SkyView.Models;

/// <summary>
/// 用户偏好（非敏感，明文保存）。
/// 敏感的 API Key 走 <c>SecureConfigStore</c> 的 DPAPI 加密，不放在这里。
/// </summary>
public class AppPreferences
{
    /// <summary>使用华氏度显示。默认 false（摄氏度）。</summary>
    public bool UseFahrenheit { get; set; }

    /// <summary>自动刷新间隔（分钟）。0 表示关闭自动刷新。</summary>
    public int AutoRefreshMinutes { get; set; } = 30;

    /// <summary>逐小时预报显示的小时数。</summary>
    public int HourlyCount { get; set; } = 12;

    /// <summary>是否显示生活指数。</summary>
    public bool ShowLifeIndex { get; set; } = true;

    /// <summary>把自动刷新间隔收敛到合理范围，避免配置里写 0 以外的负数或过大值。</summary>
    public int NormalizedAutoRefreshMinutes => AutoRefreshMinutes switch
    {
        <= 0 => 0,
        < 5 => 5,
        > 240 => 240,
        _ => AutoRefreshMinutes,
    };

    public int NormalizedHourlyCount => HourlyCount switch
    {
        < 4 => 4,
        > 24 => 24,
        _ => HourlyCount,
    };
}
