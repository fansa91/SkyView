using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SkyView.Services;

/// <summary>地理坐标 + 反查到的地名。</summary>
/// <param name="Latitude">纬度。</param>
/// <param name="Longitude">经度。</param>
/// <param name="City">规范化的城市名（优先使用数据源返回的本地名）。</param>
/// <param name="Region">省/州，可能为空。</param>
/// <param name="Country">国家，可能为空。</param>
public sealed record GeoPoint(
    double Latitude,
    double Longitude,
    string City,
    string Region = "",
    string Country = "");

/// <summary>
/// 城市名 ⇄ 经纬度的转换。
/// <para>
/// 为什么需要它：AccuWeather 和 PirateWeather 的接口都以经纬度为核心
/// （PirateWeather 甚至完全不接受城市名），而本应用对外统一用城市名。
/// </para>
/// <para>
/// 这里刻意选用 **Open-Meteo 的 geocoding 接口**：免费、无需 API Key，
/// 不绑定任何一家天气数据源的密钥。否则会出现"想用 PirateWeather 就必须先有
/// OpenWeatherMap 的 Key"这种荒谬的依赖。
/// </para>
/// <para>
/// 结果按城市名缓存：自动刷新每 30 分钟一次，没必要每次都重新解析。
/// 失败不缓存，断网恢复后能立刻重试。
/// </para>
/// </summary>
public sealed class GeocodingService
{
    private const string ForwardEndpoint = "https://geocoding-api.open-meteo.com/v1/search";
    private const string ReverseEndpoint = "https://api.bigdatacloud.net/data/reverse-geocode-client";

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, GeoPoint> _cache = new(StringComparer.OrdinalIgnoreCase);

    public GeocodingService(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// 城市名 → 坐标。解析不出来返回 <c>null</c>（不抛异常，由调用方决定怎么提示）。
    /// </summary>
    public async Task<GeoPoint?> ResolveAsync(string city, CancellationToken ct = default)
    {
        var key = city?.Trim() ?? string.Empty;
        if (key.Length == 0)
            return null;

        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var url = $"{ForwardEndpoint}?name={Uri.EscapeDataString(key)}&count=1&language=zh&format=json";

        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() == 0)
                return null;

            var first = results[0];
            if (!first.TryGetProperty("latitude", out var latEl) || !latEl.TryGetDouble(out var lat))
                return null;
            if (!first.TryGetProperty("longitude", out var lonEl) || !lonEl.TryGetDouble(out var lon))
                return null;

            var point = new GeoPoint(
                lat,
                lon,
                ReadString(first, "name", key),
                ReadString(first, "admin1"),
                ReadString(first, "country"));

            _cache[key] = point;
            return point;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 地理编码是"锦上添花"的一步，网络抖动时让它静默失败，
            // 由数据源给出"无法解析城市坐标"的明确错误，而不是在这里抛一堆底层异常。
            return null;
        }
    }

    /// <summary>
    /// 坐标 → 城市名（用于「定位当前城市」）。解析失败返回 <c>null</c>。
    /// </summary>
    public async Task<GeoPoint?> ReverseAsync(double latitude, double longitude, CancellationToken ct = default)
    {
        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}?latitude={1:0.####}&longitude={2:0.####}&localityLanguage=zh",
            ReverseEndpoint,
            latitude,
            longitude);

        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);

            var root = doc.RootElement;

            // 优先 city / locality，再退到 principalSubdivision（有些坐标只到省级）
            var name = ReadString(root, "city");
            if (name.Length == 0)
                name = ReadString(root, "locality");
            if (name.Length == 0)
                name = ReadString(root, "principalSubdivision");

            if (name.Length == 0)
                return null;

            return new GeoPoint(
                latitude,
                longitude,
                name,
                ReadString(root, "principalSubdivision"),
                ReadString(root, "countryName"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadString(JsonElement element, string property, string fallback = "")
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return fallback;

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    }
}
