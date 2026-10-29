using System.Text.Json.Serialization;
using System;

namespace SkyView.Models;

public class ProviderConfig
{
    /// <summary>数据源内部标识（QWeather / OpenWeatherMap / Seniverse）。</summary>
    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// API 主机地址。和风天气自 2024 年起不再使用公共开发域名，
    /// 需要填控制台分配的专属 Host（例如 abc1234xyz.re.qweatherapi.com）。
    /// </summary>
    public string ApiHost { get; set; } = string.Empty;

    public int Priority { get; set; }

    public bool Enabled { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// 是否需要 API Key。按数据源名字推导，而不是配置字段——
    /// 旧版 appsettings.json / settings.dat 里没有这一项，反序列化只能拿到默认值，
    /// 按名字推导才不会把免 Key 源误判成"必须填 Key"。
    /// </summary>
    [JsonIgnore]
    public bool RequiresApiKey => !IsKeyless(Name);

    /// <summary>免 Key 数据源名单（与 <c>WeatherProviderFactory.CreateProvider</c> 的分支对应）。</summary>
    public static bool IsKeyless(string? name) =>
        string.Equals(name, "OpenMeteo", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Wttr", StringComparison.OrdinalIgnoreCase);
}
