namespace SkyView.Models;

/// <summary>
/// 花粉浓度（grains/m³），数据来自 Open-Meteo Air Quality API。
/// <para>
/// 该接口的花粉变量主要覆盖欧洲——国内城市（北京/上海实测）五个变量全部返回 null，
/// 此时整个对象为 null，界面隐藏整行；有数据时 0 是合法读数（无花粉），按「低」显示。
/// </para>
/// </summary>
public sealed class PollenInfo
{
    /// <summary>草类花粉浓度；null = 该变量无数据。</summary>
    public double? Grass { get; init; }

    /// <summary>树木类花粉浓度（桦/豚草/桤/橄榄取最大值）；null = 无数据。</summary>
    public double? Tree { get; init; }

    /// <summary>任一变量有值就算有数据（0 是合法读数，不是未知）。</summary>
    public bool HasData => Grass.HasValue || Tree.HasValue;

    /// <summary>两类中的较高值，用于分级；无数据时为 0。</summary>
    public double Peak => Math.Max(Grass ?? 0, Tree ?? 0);
}
