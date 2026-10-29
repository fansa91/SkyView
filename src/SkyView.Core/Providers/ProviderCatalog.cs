using SkyView.Models;

namespace SkyView.Providers;

/// <summary>
/// 内置数据源清单。appsettings.json 缺失或缺少 WeatherProviders 段时作为兜底，
/// 保证应用永远有一份可用的数据源定义（而不是直接崩或永远报"所有数据源不可用"）。
/// </summary>
public static class ProviderCatalog
{
    public static IReadOnlyList<ProviderConfig> CreateDefaults() =>
    [
        new ProviderConfig
        {
            Name = "QWeather",
            DisplayName = "和风天气",
            ApiHost = "devapi.qweather.com",
            Priority = 0,
            Enabled = true,
            TimeoutSeconds = 10,
        },
        new ProviderConfig
        {
            Name = "OpenWeatherMap",
            DisplayName = "OpenWeatherMap",
            ApiHost = "api.openweathermap.org",
            Priority = 1,
            Enabled = false,
            TimeoutSeconds = 10,
        },
        new ProviderConfig
        {
            Name = "Seniverse",
            DisplayName = "心知天气",
            ApiHost = "api.seniverse.com",
            Priority = 2,
            Enabled = false,
            TimeoutSeconds = 10,
        },
        new ProviderConfig
        {
            // 免费版每天 50 次调用，且必须先查 locationKey，所以默认关闭
            Name = "AccuWeather",
            DisplayName = "AccuWeather",
            ApiHost = "dataservice.accuweather.com",
            Priority = 3,
            Enabled = false,
            TimeoutSeconds = 10,
        },
        new ProviderConfig
        {
            // Dark Sky 的兼容实现，只接受经纬度（内部会用 Open-Meteo 换算）
            Name = "PirateWeather",
            DisplayName = "PirateWeather",
            ApiHost = "api.pirateweather.net",
            Priority = 4,
            Enabled = false,
            TimeoutSeconds = 10,
        },
        new ProviderConfig
        {
            // 免费、免 Key：即使其它付费源都没配/都失效，也能拿到真实天气（兜底）。
            // 优先级最低，排在降级链最后。
            Name = "OpenMeteo",
            DisplayName = "Open-Meteo",
            ApiHost = "api.open-meteo.com",
            Priority = 5,
            Enabled = true,
            TimeoutSeconds = 10,
        },
        new ProviderConfig
        {
            // 免费、免 Key、URL 直接接受城市名（不依赖 Open-Meteo geocoding）的独立兜底链路。
            // 排在 OpenMeteo 之后，作为第二道免密钥保底（参考 Breezy Weather 的免密钥源思路）。
            Name = "Wttr",
            DisplayName = "wttr.in",
            ApiHost = "wttr.in",
            Priority = 6,
            Enabled = true,
            TimeoutSeconds = 15,
        },
    ];
}
