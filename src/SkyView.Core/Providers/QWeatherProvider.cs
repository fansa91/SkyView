using SkyView.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SkyView.Providers;

/// <summary>
/// 和风天气（QWeather）。
/// 文档：https://dev.qweather.com/docs/api/
/// 注意：公共开发域名 devapi.qweather.com 已停止新用户使用，
/// 需要在「设置」页把 ApiHost 填成控制台分配的专属 Host（形如 xxx.re.qweatherapi.com）。
/// </summary>
public sealed class QWeatherProvider : WeatherProviderBase
{
    public QWeatherProvider(HttpClient http, ProviderConfig config) : base(http, config) { }

    protected override string DefaultHost => "devapi.qweather.com";

    private string Key => Uri.EscapeDataString(Config.ApiKey);

    public override async Task<WeatherCurrent> GetCurrentAsync(string city, CancellationToken ct = default)
    {
        EnsureApiKey();
        var locationId = await ResolveLocationIdAsync(city, ct).ConfigureAwait(false);

        using var doc = await GetJsonAsync(
            BuildUrl($"/v7/weather/now?location={Uri.EscapeDataString(locationId)}&key={Key}&lang=zh"),
            ct).ConfigureAwait(false);

        EnsureCodeOk(doc.RootElement);

        if (!doc.RootElement.TryGetProperty("now", out var now))
            throw new InvalidOperationException("和风天气返回数据缺少 now 字段。");

        var temperature = GetDouble(now, "temp");

        return new WeatherCurrent
        {
            City = city,
            Temperature = temperature,
            // 老版本接口没有 feelsLike，退化成实况温度而不是留 0
            FeelsLike = GetNullableDouble(now, "feelsLike") ?? temperature,
            Humidity = GetInt(now, "humidity"),
            Pressure = GetDouble(now, "pressure"),
            Condition = MapCondition(GetString(now, "text")),
            IconCode = GetString(now, "icon"),
            WindDirection = GetString(now, "windDir"),
            WindSpeed = GetDouble(now, "windSpeed"),
            ObservedAt = GetTime(now, "obsTime"),
            Source = "QWeather",
        };
    }

    public override async Task<IEnumerable<WeatherForecast>> GetForecastAsync(string city, int days, CancellationToken ct = default)
    {
        EnsureApiKey();
        var locationId = await ResolveLocationIdAsync(city, ct).ConfigureAwait(false);

        // 和风只提供固定档位：3d / 7d / 10d / 15d / 30d
        var span = days switch
        {
            <= 3 => "3d",
            <= 7 => "7d",
            <= 10 => "10d",
            <= 15 => "15d",
            _ => "30d",
        };

        using var doc = await GetJsonAsync(
            BuildUrl($"/v7/weather/{span}?location={Uri.EscapeDataString(locationId)}&key={Key}&lang=zh"),
            ct).ConfigureAwait(false);

        EnsureCodeOk(doc.RootElement);

        if (!doc.RootElement.TryGetProperty("daily", out var daily) || daily.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("和风天气返回数据缺少 daily 字段。");

        var list = new List<WeatherForecast>();
        foreach (var d in daily.EnumerateArray().Take(days))
        {
            list.Add(new WeatherForecast
            {
                Date = GetTime(d, "fxDate"),
                TempHigh = GetDouble(d, "tempMax"),
                TempLow = GetDouble(d, "tempMin"),
                Condition = MapCondition(GetString(d, "textDay")),
                IconCode = GetString(d, "iconDay"),
                Sunrise = FormatClock(GetString(d, "sunrise")),
                Sunset = FormatClock(GetString(d, "sunset")),
            });
        }

        return list;
    }

    /// <summary>逐小时预报（24 小时固定档位，免费版可用）。</summary>
    public override async Task<IEnumerable<WeatherHourly>> GetHourlyAsync(string city, int hours, CancellationToken ct = default)
    {
        EnsureApiKey();
        var locationId = await ResolveLocationIdAsync(city, ct).ConfigureAwait(false);

        using var doc = await GetJsonAsync(
            BuildUrl($"/v7/weather/24h?location={Uri.EscapeDataString(locationId)}&key={Key}&lang=zh"),
            ct).ConfigureAwait(false);

        EnsureCodeOk(doc.RootElement);

        if (!doc.RootElement.TryGetProperty("hourly", out var hourly) || hourly.ValueKind != JsonValueKind.Array)
            return [];

        return hourly.EnumerateArray()
            .Take(Math.Clamp(hours, 1, 24))
            .Select(h => new WeatherHourly
            {
                Time = GetTime(h, "fxTime"),
                Temperature = GetDouble(h, "temp"),
                Condition = MapCondition(GetString(h, "text")),
                IconCode = GetString(h, "icon"),
                PrecipitationProbability = GetInt(h, "pop"),
            })
            .ToList();
    }

    /// <summary>空气质量（/v7/air/now）。新版本字段是 pm2p5，老版本是 pm25，两个都试。</summary>
    public override async Task<AirQuality?> GetAirQualityAsync(string city, CancellationToken ct = default)
    {
        EnsureApiKey();
        var locationId = await ResolveLocationIdAsync(city, ct).ConfigureAwait(false);

        using var doc = await GetJsonAsync(
            BuildUrl($"/v7/air/now?location={Uri.EscapeDataString(locationId)}&key={Key}&lang=zh"),
            ct).ConfigureAwait(false);

        EnsureCodeOk(doc.RootElement);

        if (!doc.RootElement.TryGetProperty("now", out var now))
            return null;

        var aqi = GetInt(now, "aqi");
        if (aqi <= 0)
            return null;

        return new AirQuality
        {
            Aqi = aqi,
            Category = GetString(now, "category"),
            PrimaryPollutant = GetString(now, "primary"),
            Pm25 = GetNullableDouble(now, "pm2p5") ?? GetNullableDouble(now, "pm25") ?? 0,
        };
    }

    /// <summary>生活指数（type=0 表示取全部类型）。免费版可用，但按天更新，失败不影响主流程。</summary>
    public override async Task<IEnumerable<WeatherLifeIndex>> GetLifeIndexAsync(string city, CancellationToken ct = default)
    {
        EnsureApiKey();
        var locationId = await ResolveLocationIdAsync(city, ct).ConfigureAwait(false);

        using var doc = await GetJsonAsync(
            BuildUrl($"/v7/indices/1d?location={Uri.EscapeDataString(locationId)}&key={Key}&type=0&lang=zh"),
            ct).ConfigureAwait(false);

        EnsureCodeOk(doc.RootElement);

        if (!doc.RootElement.TryGetProperty("daily", out var daily) || daily.ValueKind != JsonValueKind.Array)
            return [];

        return daily.EnumerateArray()
            .Select(d => new WeatherLifeIndex
            {
                Name = GetString(d, "name"),
                Level = GetString(d, "category"),
                Text = GetString(d, "text"),
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .ToList();
    }

    /// <summary>
    /// 和风天气的天气接口接受 LocationID，也接受 "经度,纬度"。这里先用 GeoAPI 把城市名换成 ID，
    /// 官方推荐缓存城市 ID（免费版有调用配额）。
    /// </summary>
    private async Task<string> ResolveLocationIdAsync(string city, CancellationToken ct)
    {
        var trimmed = city.Trim();
        if (trimmed.Length > 0 && trimmed.All(char.IsDigit))
            return trimmed;

        var escaped = Uri.EscapeDataString(trimmed);

        try
        {
            using var geo = await GetJsonAsync(BuildUrl($"/geo/v2/city/lookup?location={escaped}&number=1&key={Key}"), ct)
                .ConfigureAwait(false);
            return ExtractLocationId(geo.RootElement, trimmed);
        }
        catch (HttpRequestException)
        {
            // 老账号的 GeoAPI 仍在独立域名上
            using var geo = await GetJsonAsync(
                $"https://geoapi.qweather.com/v2/city/lookup?location={escaped}&number=1&key={Key}", ct).ConfigureAwait(false);
            return ExtractLocationId(geo.RootElement, trimmed);
        }
    }

    private static string ExtractLocationId(JsonElement root, string city)
    {
        var code = GetString(root, "code");
        if (code.Length > 0 && code != "200")
            throw new InvalidOperationException($"和风天气城市查询失败（code={code}）：{city}");

        if (!root.TryGetProperty("location", out var locs) || locs.ValueKind != JsonValueKind.Array || locs.GetArrayLength() == 0)
            throw new InvalidOperationException($"和风天气找不到城市：{city}");

        var id = GetString(locs[0], "id");
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException($"和风天气返回的城市 ID 为空：{city}");

        return id;
    }

    private static void EnsureCodeOk(JsonElement root)
    {
        var value = GetString(root, "code");
        if (value.Length == 0 || value == "200")
            return;
        throw new InvalidOperationException(value switch
        {
            "401" => "和风天气认证失败：API Key 无效或与 ApiHost 不匹配。",
            "402" => "和风天气配额已用完，或当前订阅不含该接口。",
            "403" => "和风天气拒绝访问：请确认 ApiHost 是否为控制台分配的专属 Host。",
            "404" => "和风天气找不到请求的城市或接口。",
            _ => $"和风天气返回错误 code={value}。",
        });
    }
}
