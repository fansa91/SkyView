using SkyView.Models;
using SkyView.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SkyView.Providers;

/// <summary>
/// AccuWeather。
/// 文档：https://developer.accuweather.com/documentation
/// <para>
/// 与其他数据源最大的不同：**所有天气接口都以 locationKey 为参数，不接受城市名**。
/// 所以每次请求前要先做一次地理位置查询，再把 Key 缓存起来 ——
/// AccuWeather 免费版每天只有 50 次调用，不缓存的话刷两次就耗尽配额。
/// </para>
/// </summary>
public sealed class AccuWeatherProvider : WeatherProviderBase
{
    private readonly GeocodingService _geocoder;

    /// <summary>城市名 → locationKey。免费额度太少，必须缓存。</summary>
    private readonly ConcurrentDictionary<string, string> _locationKeys = new(StringComparer.OrdinalIgnoreCase);

    public AccuWeatherProvider(HttpClient http, ProviderConfig config, GeocodingService geocoder)
        : base(http, config)
    {
        _geocoder = geocoder;
    }

    protected override string DefaultHost => "dataservice.accuweather.com";

    private string Key => Uri.EscapeDataString(Config.ApiKey);

    public override async Task<WeatherCurrent> GetCurrentAsync(string city, CancellationToken ct = default)
    {
        EnsureApiKey();
        var locationKey = await ResolveLocationKeyAsync(city, ct).ConfigureAwait(false);

        using var doc = await GetJsonAsync(
            BuildUrl($"/currentconditions/v1/{locationKey}?apikey={Key}&language=zh-cn&details=true"),
            ct).ConfigureAwait(false);

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            throw new InvalidOperationException($"AccuWeather 没有返回 {city} 的实时天气。");

        var item = root[0];
        var temperature = ReadMetric(item, "Temperature");

        return new WeatherCurrent
        {
            City = city,
            Temperature = temperature,
            FeelsLike = ReadMetricNullable(item, "RealFeelTemperature") ?? temperature,
            Humidity = GetInt(item, "RelativeHumidity"),
            Pressure = ReadMetric(item, "Pressure"),
            Condition = MapCondition(GetString(item, "WeatherText")),
            IconCode = GetString(item, "WeatherIcon"),
            ObservedAt = item.TryGetProperty("EpochTime", out var epoch) && epoch.TryGetInt64(out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : DateTimeOffset.Now,
            Source = "AccuWeather",
            WindSpeed = ReadNestedMetric(item, "Wind", "Speed"),
            WindDirection = item.TryGetProperty("Wind", out var wind)
                && wind.TryGetProperty("Direction", out var dir)
                    ? GetString(dir, "Localized")
                    : string.Empty,
            // 实时天气接口不含日出日落（在日预报里），这里留空由界面隐藏，不伪造时间
            Sunrise = string.Empty,
            Sunset = string.Empty,
        };
    }

    public override async Task<IEnumerable<WeatherForecast>> GetForecastAsync(string city, int days, CancellationToken ct = default)
    {
        EnsureApiKey();
        var locationKey = await ResolveLocationKeyAsync(city, ct).ConfigureAwait(false);

        // 免费版只有 1/5/10/15 天档位，取最接近的 5 天档
        var span = days <= 1 ? "1day" : days <= 5 ? "5day" : "10day";

        using var doc = await GetJsonAsync(
            BuildUrl($"/forecasts/v1/daily/{span}/{locationKey}?apikey={Key}&language=zh-cn&metric=true"),
            ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("DailyForecasts", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("AccuWeather 返回数据缺少 DailyForecasts 字段。");

        var result = new List<WeatherForecast>();

        foreach (var day in list.EnumerateArray().Take(days))
        {
            var dayPart = day.TryGetProperty("Day", out var d) ? d : default;
            var sun = day.TryGetProperty("Sun", out var s) ? s : default;

            result.Add(new WeatherForecast
            {
                Date = GetTime(day, "Date"),
                TempHigh = ReadMetric(day, "Temperature", "Maximum"),
                TempLow = ReadMetric(day, "Temperature", "Minimum"),
                Condition = MapCondition(GetString(dayPart, "IconPhrase")),
                IconCode = GetString(dayPart, "Icon"),
                PrecipitationProbability = GetInt(dayPart, "PrecipitationProbability"),
                Sunrise = FormatClock(GetString(sun, "Rise")),
                Sunset = FormatClock(GetString(sun, "Set")),
            });
        }

        return result;
    }

    public override async Task<IEnumerable<WeatherHourly>> GetHourlyAsync(string city, int hours, CancellationToken ct = default)
    {
        EnsureApiKey();
        var locationKey = await ResolveLocationKeyAsync(city, ct).ConfigureAwait(false);

        var span = hours <= 12 ? "12hour" : "24hour";

        using var doc = await GetJsonAsync(
            BuildUrl($"/forecasts/v1/hourly/{span}/{locationKey}?apikey={Key}&language=zh-cn&metric=true"),
            ct).ConfigureAwait(false);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        return doc.RootElement.EnumerateArray()
            .Take(Math.Clamp(hours, 1, 24))
            .Select(item => new WeatherHourly
            {
                Time = item.TryGetProperty("EpochDateTime", out var epoch) && epoch.TryGetInt64(out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix)
                    : GetTime(item, "DateTime"),
                Temperature = ReadMetric(item, "Temperature"),
                Condition = MapCondition(GetString(item, "IconPhrase")),
                IconCode = GetString(item, "Icon"),
                PrecipitationProbability = GetInt(item, "PrecipitationProbability"),
            })
            .ToList();
    }

    /// <summary>
    /// locationKey 解析。先用城市名直接查，查不到再用 geocoding 拿坐标按坐标查。
    /// <para>
    /// 两步都必要：AccuWeather 的中文城市搜索对"上海"这类写法经常返回空数组，
    /// 而按经纬度反查（geoposition）几乎总能命中。
    /// </para>
    /// </summary>
    private async Task<string> ResolveLocationKeyAsync(string city, CancellationToken ct)
    {
        var cacheKey = city.Trim();
        if (_locationKeys.TryGetValue(cacheKey, out var cached))
            return cached;

        var byName = await TrySearchByNameAsync(cacheKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(byName))
        {
            _locationKeys[cacheKey] = byName;
            return byName;
        }

        var point = await _geocoder.ResolveAsync(cacheKey, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"AccuWeather 无法解析「{cacheKey}」的位置。请确认城市名，或改用英文名（如 Shanghai）。");

        var lat = point.Latitude.ToString("0.####", CultureInfo.InvariantCulture);
        var lon = point.Longitude.ToString("0.####", CultureInfo.InvariantCulture);

        using var doc = await GetJsonAsync(
            BuildUrl($"/locations/v1/cities/geoposition/search?apikey={Key}&q={lat},{lon}&language=zh-cn"),
            ct).ConfigureAwait(false);

        var key = GetString(doc.RootElement, "Key");
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException($"AccuWeather 按坐标（{lat},{lon}）没有找到对应城市。");

        _locationKeys[cacheKey] = key;
        return key;
    }

    private async Task<string> TrySearchByNameAsync(string city, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync(
                BuildUrl($"/locations/v1/cities/search?apikey={Key}&q={Uri.EscapeDataString(city)}&language=zh-cn&details=false"),
                ct).ConfigureAwait(false);

            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return string.Empty;

            return GetString(doc.RootElement[0], "Key");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 名字查不到属于常见情况（中文名尤其），吞掉异常走坐标兜底即可
            return string.Empty;
        }
    }

    /// <summary>读 <c>{"Temperature":{"Metric":{"Value":22.2}}}</c> 这种结构里的数值。</summary>
    private static double ReadMetric(JsonElement element, string property, string? child = null)
        => ReadMetricNullable(element, property, child) ?? 0;

    private static double? ReadMetricNullable(JsonElement element, string property, string? child = null)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var node))
            return null;

        if (child is not null)
        {
            if (!node.TryGetProperty(child, out node))
                return null;
        }

        if (!node.TryGetProperty("Metric", out var metric))
            return null;

        return GetNullableDouble(metric, "Value");
    }

    private static double ReadNestedMetric(JsonElement element, string property, string child)
        => ReadMetricNullable(element, property, child) ?? 0;
}
