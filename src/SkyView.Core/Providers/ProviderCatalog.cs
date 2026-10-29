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
    ];
}
