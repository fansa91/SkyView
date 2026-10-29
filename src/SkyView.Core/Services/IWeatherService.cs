using SkyView.Models;

namespace SkyView.Services;

public interface IWeatherService
{
    Task<WeatherCurrent> GetCurrentWithFallbackAsync(string city, CancellationToken ct = default);

    Task<IEnumerable<WeatherForecast>> GetForecastWithFallbackAsync(string city, int days, CancellationToken ct = default);

    /// <summary>逐小时预报。全部数据源都不支持时返回空集合（不抛异常，属可选能力）。</summary>
    Task<IEnumerable<WeatherHourly>> GetHourlyWithFallbackAsync(string city, int hours, CancellationToken ct = default);

    /// <summary>生活指数。全部数据源都不支持时返回空集合（不抛异常，属可选能力）。</summary>
    Task<IEnumerable<WeatherLifeIndex>> GetLifeIndexWithFallbackAsync(string city, CancellationToken ct = default);
}
