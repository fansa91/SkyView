using SkyView.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SkyView.Providers;

/// <summary>
/// 心知天气（Seniverse）。
/// 文档：https://seniverse.yuque.com/books/share/ded3e1f0-9d8f-4a49-9f26-c4b3a09d5a4d
/// </summary>
public sealed class SeniverseProvider : WeatherProviderBase
{
    public SeniverseProvider(HttpClient http, ProviderConfig config) : base(http, config) { }

    protected override string DefaultHost => "api.seniverse.com";

    private string Key => Uri.EscapeDataString(Config.ApiKey);

    private string LocationParam(string city) => Uri.EscapeDataString(city);

    public override async Task<WeatherCurrent> GetCurrentAsync(string city, CancellationToken ct = default)
    {
        EnsureApiKey();

        using var doc = await GetJsonAsync(
            BuildUrl($"/v3/weather/now.json?key={Key}&location={LocationParam(city)}&language=zh-Hans&unit=c"),
            ct).ConfigureAwait(false);

        var now = GetFirstResult(doc.RootElement, "results", "now", city);
        var temperature = GetDouble(now, "temperature");

        return new WeatherCurrent
        {
            City = city,
            Temperature = temperature,
            // 心知没有体感温度，退化成实况温度
            FeelsLike = temperature,
            Humidity = GetInt(now, "humidity"),
            Pressure = GetDouble(now, "pressure"),
            Condition = MapCondition(GetString(now, "text")),
            IconCode = GetString(now, "code"),
            WindDirection = GetString(now, "wind_direction"),
            WindSpeed = GetDouble(now, "wind_speed"),
            ObservedAt = GetTime(now, "last_update"),
            Source = "Seniverse",
        };
    }

    public override async Task<IEnumerable<WeatherForecast>> GetForecastAsync(string city, int days, CancellationToken ct = default)
    {
        EnsureApiKey();

        var span = Math.Clamp(days, 1, 15);
        using var doc = await GetJsonAsync(
            BuildUrl($"/v3/weather/daily.json?key={Key}&location={LocationParam(city)}&language=zh-Hans&unit=c&start=0&days={span}"),
            ct).ConfigureAwait(false);

        var daily = GetFirstResult(doc.RootElement, "results", "daily", city);

        var list = new List<WeatherForecast>();
        if (daily.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var d in daily.EnumerateArray().Take(span))
        {
            list.Add(new WeatherForecast
            {
                Date = GetTime(d, "date"),
                TempHigh = GetDouble(d, "high"),
                TempLow = GetDouble(d, "low"),
                Condition = MapCondition(GetString(d, "text_day")),
                IconCode = GetString(d, "code_day"),
                PrecipitationProbability = GetInt(d, "precip"),
            });
        }

        return list;
    }

    /// <summary>逐小时预报。需要订阅版才开放此接口，失败时由上层静默降级。</summary>
    public override async Task<IEnumerable<WeatherHourly>> GetHourlyAsync(string city, int hours, CancellationToken ct = default)
    {
        EnsureApiKey();

        var span = Math.Clamp(hours, 1, 24);
        using var doc = await GetJsonAsync(
            BuildUrl($"/v3/weather/hourly.json?key={Key}&location={LocationParam(city)}&language=zh-Hans&unit=c&start=0&hours={span}"),
            ct).ConfigureAwait(false);

        var hourly = GetFirstResult(doc.RootElement, "results", "hourly", city);
        if (hourly.ValueKind != JsonValueKind.Array)
            return [];

        return hourly.EnumerateArray()
            .Take(span)
            .Select(h => new WeatherHourly
            {
                Time = GetTime(h, "time"),
                Temperature = GetDouble(h, "temperature"),
                Condition = MapCondition(GetString(h, "text")),
                IconCode = GetString(h, "code"),
                // 心知给的是降水量（mm）而不是概率，这里不硬凑成百分比
                PrecipitationProbability = 0,
            })
            .ToList();
    }

    /// <summary>生活指数（/v3/life/suggestion.json）。按建议类型返回条目。</summary>
    public override async Task<IEnumerable<WeatherLifeIndex>> GetLifeIndexAsync(string city, CancellationToken ct = default)
    {
        EnsureApiKey();

        using var doc = await GetJsonAsync(
            BuildUrl($"/v3/life/suggestion.json?key={Key}&location={LocationParam(city)}&language=zh-Hans"),
            ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array ||
            results.GetArrayLength() == 0)
        {
            return [];
        }

        var suggestion = results[0].TryGetProperty("suggestion", out var s) ? s : default;
        if (suggestion.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<WeatherLifeIndex>();
        foreach (var item in suggestion.EnumerateArray())
        {
            var type = GetString(item, "type");
            var brief = GetString(item, "brief");
            var details = GetString(item, "details");

            if (string.IsNullOrWhiteSpace(brief) && string.IsNullOrWhiteSpace(details))
                continue;

            list.Add(new WeatherLifeIndex
            {
                Name = TranslateSuggestionType(type),
                Level = brief,
                Text = details,
            });
        }

        return list;
    }

    /// <summary>把心知的英文建议类型转成中文名称。</summary>
    private static string TranslateSuggestionType(string type) => type switch
    {
        "dressing" => "穿衣指数",
        "uv" => "紫外线",
        "car_washing" => "洗车指数",
        "travel" => "旅游指数",
        "flu" => "感冒指数",
        "sport" => "运动指数",
        "comfort" => "舒适度",
        "umbrella" => "雨伞指数",
        "air" => "空气扩散",
        "allergy" => "过敏指数",
        "beer" => "啤酒指数",
        "makeup" => "化妆指数",
        "sunshine" => "晾晒指数",
        "drying" => "干燥指数",
        "traffic" => "交通指数",
        "sunscreen" => "防晒指数",
        _ => string.IsNullOrWhiteSpace(type) ? "生活指数" : type,
    };

    private static JsonElement GetFirstResult(JsonElement root, string resultsProperty, string childProperty, string city)
    {
        if (!root.TryGetProperty(resultsProperty, out var results) ||
            results.ValueKind != JsonValueKind.Array ||
            results.GetArrayLength() == 0)
        {
            var statusText = GetString(root, "status_text");
            throw new InvalidOperationException(
                statusText.Length > 0
                    ? $"心知天气请求失败：{statusText}（城市：{city}）"
                    : $"心知天气返回数据异常：城市 {city} 没有结果。");
        }

        var first = results[0];
        if (!first.TryGetProperty(childProperty, out var child))
            throw new InvalidOperationException($"心知天气返回数据缺少 {childProperty} 字段。");

        return child;
    }
}
