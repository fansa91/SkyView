using SkyView.Models;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SkyView.Providers;

public abstract class WeatherProviderBase : IWeatherProvider
{
    protected readonly HttpClient Http;
    protected readonly ProviderConfig Config;

    protected WeatherProviderBase(HttpClient http, ProviderConfig config)
    {
        Http = http;
        Config = config;
        Name = config.Name;
    }

    public string Name { get; }

    public abstract Task<WeatherCurrent> GetCurrentAsync(string city, CancellationToken ct = default);

    public abstract Task<IEnumerable<WeatherForecast>> GetForecastAsync(string city, int days, CancellationToken ct = default);

    /// <summary>
    /// 逐小时预报。默认返回空集合，数据源不支持时无需实现。
    /// <para>
    /// 注意：这里必须是基类的 <c>virtual</c> 成员，**不能只依赖接口的默认实现** ——
    /// 接口映射是在声明接口的那个类型（本类）上计算的，派生类新写的同名方法
    /// 不会参与映射，结果就是调用时永远走接口的默认实现（返回空集合）。
    /// </para>
    /// </summary>
    public virtual Task<IEnumerable<WeatherHourly>> GetHourlyAsync(string city, int hours, CancellationToken ct = default)
        => Task.FromResult<IEnumerable<WeatherHourly>>([]);

    /// <summary>空气质量。默认 null，表示该数据源不提供。</summary>
    public virtual Task<AirQuality?> GetAirQualityAsync(string city, CancellationToken ct = default)
        => Task.FromResult<AirQuality?>(null);

    /// <summary>生活指数。默认空集合。</summary>
    public virtual Task<IEnumerable<WeatherLifeIndex>> GetLifeIndexAsync(string city, CancellationToken ct = default)
        => Task.FromResult<IEnumerable<WeatherLifeIndex>>([]);

    /// <summary>
    /// 把各数据源五花八门的天气描述归一成中文短词。
    /// 采用"包含"匹配，而不是整串精确相等 —— 否则 "小雨"、"partly cloudy"、"晴转多云"
    /// 这些常见返回值全都会落到"未知"。
    /// </summary>
    internal static string MapCondition(string? sourceCondition)
    {
        if (string.IsNullOrWhiteSpace(sourceCondition))
            return "未知";

        var s = sourceCondition.Trim();
        var lower = s.ToLowerInvariant();

        if (Has(s, lower, "雷", "thunder", "tstorm"))
            return "雷阵雨";
        if (Has(s, lower, "雪", "snow", "sleet", "blizzard"))
            return "雪";
        if (Has(s, lower, "雨", "rain", "drizzle", "shower"))
            return "雨";
        if (Has(s, lower, "雾", "霾", "fog", "mist", "haze", "smoke"))
            return "雾霾";
        if (Has(s, lower, "沙", "尘", "dust", "sand"))
            return "沙尘";
        if (Has(s, lower, "阴", "overcast"))
            return "阴";
        if (Has(s, lower, "云", "cloud"))
            return "多云";
        if (Has(s, lower, "晴", "clear", "sun"))
            return "晴";

        // 无法归类时保留原始描述，比显示"未知"更有信息量
        return s;
    }

    private static bool Has(string source, string lowerSource, params string[] keywords)
    {
        foreach (var k in keywords)
        {
            if (source.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                lowerSource.Contains(k, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    protected void EnsureApiKey()
    {
        if (string.IsNullOrWhiteSpace(Config.ApiKey))
            throw new InvalidOperationException($"{DisplayNameSafe()} API Key 未配置，请在「设置」页填写。");
    }

    protected string DisplayNameSafe() =>
        string.IsNullOrWhiteSpace(Config.DisplayName) ? Config.Name : Config.DisplayName;

    /// <summary>拼接 API 地址：ApiHost 可带或不带 scheme。</summary>
    protected string BuildUrl(string pathAndQuery)
    {
        var host = string.IsNullOrWhiteSpace(Config.ApiHost) ? DefaultHost : Config.ApiHost.Trim();
        if (!host.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            host = "https://" + host.TrimEnd('/');
        return host.TrimEnd('/') + pathAndQuery;
    }

    /// <summary>各数据源的默认主机，配置里留空时使用。</summary>
    protected abstract string DefaultHost { get; }

    /// <summary>带超时的 GET，并把非 2xx 响应转成包含响应片段的异常，便于定位问题。</summary>
    protected async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        var timeout = Config.TimeoutSeconds > 0 ? Config.TimeoutSeconds : 10;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        using var response = await Http.GetAsync(url, cts.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var snippet = body.Length > 200 ? body[..200] : body;
            throw new HttpRequestException(
                $"{DisplayNameSafe()} 请求失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase} - {snippet}");
        }

        return JsonDocument.Parse(body);
    }

    protected static string GetString(JsonElement element, string property, string fallback = "")
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var v))
            return fallback;

        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
    }

    /// <summary>部分数据源把数值以字符串返回（如 "23"），这里统一容错解析。</summary>
    protected static double GetDouble(JsonElement element, string property, double fallback = 0)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var v))
            return fallback;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out var d) ? d : fallback,
            JsonValueKind.String => double.TryParse(v.GetString(), out var d2) ? d2 : fallback,
            _ => fallback,
        };
    }

    protected static int GetInt(JsonElement element, string property, int fallback = 0)
    {
        var d = GetDouble(element, property, double.NaN);
        return double.IsNaN(d) ? fallback : (int)Math.Round(d);
    }

    protected static DateTimeOffset GetTime(JsonElement element, string property)
    {
        var raw = GetString(element, property);
        return DateTimeOffset.TryParse(raw, out var t) ? t : DateTimeOffset.Now;
    }

    /// <summary>
    /// 把日出/日落这类时间值统一成 "HH:mm"。
    /// 各数据源格式不一：有的给 "06:12"，有的给 "2024-01-01T06:12+08:00"。
    /// 解析不出来时返回空字符串（界面据此隐藏该字段），不要伪造时间。
    /// </summary>
    protected static string FormatClock(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        if (TimeOnly.TryParse(raw, out var time))
            return time.ToString("HH:mm");

        if (DateTimeOffset.TryParse(raw, out var dto))
            return dto.ToString("HH:mm");

        return string.Empty;
    }

    /// <summary>把 JSON 数值/字符串统一读成可空 double，区分"确实没有"和"值为 0"。</summary>
    protected static double? GetNullableDouble(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var v))
            return null;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out var d) ? d : null,
            JsonValueKind.String => double.TryParse(v.GetString(), out var d2) ? d2 : null,
            _ => null,
        };
    }
}
