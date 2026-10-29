using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SkyView.Models;
using SkyView.Providers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SkyView.Services;

/// <summary>
/// 多数据源聚合服务：按优先级依次尝试，成功即返回并写入缓存，失败则回落到下一个源。
/// </summary>
public sealed class WeatherService : IWeatherService
{
    private readonly WeatherProviderFactory _factory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<WeatherService> _logger;
    private readonly TimeSpan _cacheDuration;

    public WeatherService(
        WeatherProviderFactory factory,
        IMemoryCache cache,
        ILogger<WeatherService> logger,
        IConfiguration? config = null)
    {
        _factory = factory;
        _cache = cache;
        _logger = logger;

        var minutes = config?.GetValue("App:CacheMinutes", 10) ?? 10;
        _cacheDuration = TimeSpan.FromMinutes(minutes > 0 ? minutes : 10);
    }

    public async Task<WeatherCurrent> GetCurrentWithFallbackAsync(string city, CancellationToken ct = default)
    {
        var city2 = (city ?? string.Empty).Trim();
        if (city2.Length == 0)
            throw new ArgumentException("城市不能为空。", nameof(city));

        var cacheKey = $"current:{city2.ToLowerInvariant()}";
        if (_cache.TryGetValue<WeatherCurrent>(cacheKey, out var cached) && cached is not null)
            return cached;

        var errors = new List<string>();

        foreach (var provider in _factory.GetProviders())
        {
            try
            {
                var result = await provider.GetCurrentAsync(city2, ct).ConfigureAwait(false);
                if (result is null)
                    throw new InvalidOperationException("数据源返回了空结果。");

                result.Source = provider.Name;

                // 空气质量是加分项：单独请求，失败不影响主流程
                await TryFillAirQualityAsync(provider, city2, result, ct).ConfigureAwait(false);

                _cache.Set(cacheKey, result, _cacheDuration);
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add(Describe(provider.Name, ex));
                _logger.LogWarning(ex, "天气数据源 {Provider} 请求失败，尝试下一个源", provider.Name);
            }
        }

        throw AllProvidersFailed(errors);
    }

    public async Task<IEnumerable<WeatherForecast>> GetForecastWithFallbackAsync(string city, int days, CancellationToken ct = default)
    {
        var city2 = (city ?? string.Empty).Trim();
        if (city2.Length == 0)
            throw new ArgumentException("城市不能为空。", nameof(city));

        var span = Math.Clamp(days, 1, 15);
        var cacheKey = $"forecast:{city2.ToLowerInvariant()}:{span}";
        if (_cache.TryGetValue<List<WeatherForecast>>(cacheKey, out var cached) && cached is not null)
            return cached;

        var errors = new List<string>();

        foreach (var provider in _factory.GetProviders())
        {
            try
            {
                var result = await provider.GetForecastAsync(city2, span, ct).ConfigureAwait(false);
                var list = result?.ToList() ?? [];
                if (list.Count == 0)
                    throw new InvalidOperationException("数据源没有返回预报数据。");

                _cache.Set(cacheKey, list, _cacheDuration);
                return list;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add(Describe(provider.Name, ex));
                _logger.LogWarning(ex, "天气数据源 {Provider} 预报请求失败，尝试下一个源", provider.Name);
            }
        }

        throw AllProvidersFailed(errors);
    }

    public async Task<IEnumerable<WeatherHourly>> GetHourlyWithFallbackAsync(string city, int hours, CancellationToken ct = default)
    {
        var city2 = (city ?? string.Empty).Trim();
        if (city2.Length == 0)
            return [];

        var span = Math.Clamp(hours, 1, 48);
        var cacheKey = $"hourly:{city2.ToLowerInvariant()}:{span}";
        if (_cache.TryGetValue<List<WeatherHourly>>(cacheKey, out var cached) && cached is not null)
            return cached;

        foreach (var provider in _factory.GetProviders())
        {
            try
            {
                var result = await provider.GetHourlyAsync(city2, span, ct).ConfigureAwait(false);
                var list = result?.ToList() ?? [];
                if (list.Count == 0)
                    continue;

                // 保证按时间升序，图上画出来才是从左到右
                list.Sort((a, b) => a.Time.CompareTo(b.Time));
                _cache.Set(cacheKey, list, _cacheDuration);
                return list;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "数据源 {Provider} 不支持或请求逐小时预报失败", provider.Name);
            }
        }

        return [];
    }

    public async Task<IEnumerable<WeatherLifeIndex>> GetLifeIndexWithFallbackAsync(string city, CancellationToken ct = default)
    {
        var city2 = (city ?? string.Empty).Trim();
        if (city2.Length == 0)
            return [];

        var cacheKey = $"life:{city2.ToLowerInvariant()}";
        if (_cache.TryGetValue<List<WeatherLifeIndex>>(cacheKey, out var cached) && cached is not null)
            return cached;

        foreach (var provider in _factory.GetProviders())
        {
            try
            {
                var result = await provider.GetLifeIndexAsync(city2, ct).ConfigureAwait(false);
                var list = result?.ToList() ?? [];
                if (list.Count == 0)
                    continue;

                _cache.Set(cacheKey, list, _cacheDuration);
                return list;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "数据源 {Provider} 不支持或请求生活指数失败", provider.Name);
            }
        }

        return [];
    }

    /// <summary>把空气质量合并进当前天气。任何失败都静默忽略——它只是加分项。</summary>
    private async Task TryFillAirQualityAsync(
        IWeatherProvider provider,
        string city,
        WeatherCurrent target,
        CancellationToken ct)
    {
        try
        {
            var air = await provider.GetAirQualityAsync(city, ct).ConfigureAwait(false);
            if (air is null || air.Aqi <= 0)
                return;

            target.Aqi = air.Aqi;
            target.AqiCategory = air.Category;
            target.PrimaryPollutant = air.PrimaryPollutant;
            target.Pm25 = air.Pm25;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "数据源 {Provider} 空气质量获取失败，忽略", provider.Name);
        }
    }

    private InvalidOperationException AllProvidersFailed(List<string> errors)
    {
        if (_factory.GetProviders().Count == 0)
        {
            return new InvalidOperationException(
                "没有启用任何天气数据源。请前往「设置」页至少启用一个数据源并填写 API Key。");
        }

        var detail = errors.Count > 0 ? string.Join("；", errors) : "未知原因";
        return new InvalidOperationException(
            $"所有天气数据源均不可用（{detail}）。请检查「设置」页中的 API Key 与网络连接。");
    }

    private static string Describe(string providerName, Exception ex)
    {
        var message = ex.Message;
        if (message.Length > 120)
            message = message[..120] + "…";
        return $"{providerName}：{message}";
    }
}
