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
}
