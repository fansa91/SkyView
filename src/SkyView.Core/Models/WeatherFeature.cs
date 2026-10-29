namespace SkyView.Models;

/// <summary>
/// 可按功能单独分配数据源的功能项——参考 Breezy Weather 的 per-feature source 模型：
/// 每个功能（当前天气 / 预报 / 逐小时 / 生活指数）都可以指定用哪个数据源，
/// 不指定时走「按优先级自动降级」链；指定的源失败时同样回退到链上的下一个。
/// </summary>
public static class WeatherFeature
{
    public const string Current = "Current";
    public const string Forecast = "Forecast";
    public const string Hourly = "Hourly";
    public const string LifeIndex = "LifeIndex";

    /// <summary>全部可分配功能（设置页按这个顺序渲染，服务层按 Id 取分配）。</summary>
    public static readonly IReadOnlyList<FeatureDescriptor> All =
    [
        new(Current, "当前天气"),
        new(Forecast, "天气预报"),
        new(Hourly, "逐小时预报"),
        new(LifeIndex, "生活指数"),
    ];

    /// <summary>功能 Id + 界面显示名。</summary>
    public sealed record FeatureDescriptor(string Id, string DisplayName);
}
