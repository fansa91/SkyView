using System;
using System.Collections.Generic;

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

    /// <summary>
    /// 功能 → 数据源名称的分配（<see cref="WeatherFeature"/> 的 Id 为键）。
    /// 空字典或某个功能没有条目 = 「自动」：按优先级依次尝试，成功即用。
    /// 指定的源失败时会自动回退到链上的下一个；源被停用/不存在时等同自动。
    /// </summary>
    public Dictionary<string, string> FeatureSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取某个功能指派的源；没指派（或指派为空串）时返回 null。</summary>
    public string? SourceFor(string feature)
    {
        if (string.IsNullOrEmpty(feature))
            return null;
        return FeatureSources.TryGetValue(feature, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name.Trim()
            : null;
    }

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
