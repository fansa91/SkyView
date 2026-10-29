namespace SkyView.Models;

/// <summary>
/// 设置页「功能数据源分配」下拉框的一个选项。
/// <see cref="Name"/> 为空串表示「自动（按优先级）」——与 <see cref="AppPreferences.SourceFor"/>
/// 对未指派功能返回 null 的语义对应。
/// </summary>
public class FeatureSourceOption
{
    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
}
