using SkyView.Models;
using SkyView.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SkyView.Providers;

/// <summary>
/// Open-Meteo —— 免费、免 API Key 的天气数据源（<see href="https://open-meteo.com"/>）。
/// <para>
/// 本项目一直用它的 geocoding 接口做免密钥的「城市名 ⇄ 坐标」转换，
/// 这里把它升级为真正的天气数据源：即使其它付费 Key 都没配置（或都失效），
/// 应用也能拿到真实天气，不必退回纯演示模式。
/// </para>
/// <para>
/// 实现要点：城市名先走 <see cref="GeocodingService"/> 换成经纬度（与其它数据源共用缓存），
/// 再用经纬度调 forecast 接口；返回的 WMO <c>weather_code</c> 归一成中文短词，
/// 图标种类由 <see cref="WeatherIconSelector"/> 统一推导，避免再维护一份码表。
/// </para>
/// </summary>
public sealed class OpenMeteoProvider : WeatherProviderBase
{
    public OpenMeteoProvider(HttpClient http, ProviderConfig config, GeocodingService geocoder)
        : base(http, config)
    {
        _geocoder = geocoder;
    }

    private readonly GeocodingService _geocoder;

    public const string ProviderName = "OpenMeteo";

    // 基类要求实现，但本类直接构造完整 URL 给 GetJsonAsync，host 仅作占位
    protected override string DefaultHost => "api.open-meteo.com";

    private const string ForecastEndpoint = "https://api.open-meteo.com/v1/forecast";

    public override async Task<WeatherCurrent> GetCurrentAsync(string city, CancellationToken ct = default)
    {
        var point = await ResolveCityAsync(city, ct).ConfigureAwait(false);

        var url = string.Format(CultureInfo.InvariantCulture,
            "{0}?latitude={1:0.####}&longitude={2:0.####}" +
            "&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code," +
            "wind_speed_10m,wind_direction_10m,pressure_msl&timezone=auto",
            ForecastEndpoint, point.Latitude, point.Longitude);

        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);

        var root = doc.RootElement;
        if (!root.TryGetProperty("current", out var cur) || cur.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Open-Meteo 返回数据缺少 current 字段。");

        var temperature = GetDouble(cur, "temperature_2m");
        var code = GetInt(cur, "weather_code");

        return new WeatherCurrent
        {
            City = point.City,
            Temperature = temperature,
            FeelsLike = GetNullableDouble(cur, "apparent_temperature") ?? temperature,
            Humidity = GetInt(cur, "relative_humidity_2m"),
            Pressure = GetDouble(cur, "pressure_msl"),
            Condition = MapWmoCode(code),
            ObservedAt = DateTimeOffset.Now,
            Source = "Open-Meteo",
            WindSpeed = GetDouble(cur, "wind_speed_10m"),
            WindDirection = DescribeWindDirection(GetDouble(cur, "wind_direction_10m")),
        };
    }

    public override async Task<IEnumerable<WeatherForecast>> GetForecastAsync(string city, int days, CancellationToken ct = default)
    {
        var point = await ResolveCityAsync(city, ct).ConfigureAwait(false);

        var url = string.Format(CultureInfo.InvariantCulture,
            "{0}?latitude={1:0.####}&longitude={2:0.####}" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min" +
            "&timezone=auto&forecast_days={3}",
            ForecastEndpoint, point.Latitude, point.Longitude, days);

        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);

        var root = doc.RootElement;
        if (!root.TryGetProperty("daily", out var daily) || daily.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Open-Meteo 返回数据缺少 daily 字段。");

        if (!daily.TryGetProperty("time", out var times) || times.ValueKind != JsonValueKind.Array)
            return [];

        var codes = ReadDoubleArray(daily, "weather_code");
        var highs = ReadDoubleArray(daily, "temperature_2m_max");
        var lows = ReadDoubleArray(daily, "temperature_2m_min");

        var result = new List<WeatherForecast>();
        for (var i = 0; i < times.GetArrayLength(); i++)
        {
            if (i >= codes.Count || i >= highs.Count || i >= lows.Count)
                break;
            if (!DateTimeOffset.TryParse(GetStringAtIndex(times, i), out var date))
                continue;

            result.Add(new WeatherForecast
            {
                Date = date,
                TempHigh = highs[i],
                TempLow = lows[i],
                Condition = MapWmoCode((int)Math.Round(codes[i])),
            });
        }

        return result;
    }

    public override async Task<IEnumerable<WeatherHourly>> GetHourlyAsync(string city, int hours, CancellationToken ct = default)
    {
        var point = await ResolveCityAsync(city, ct).ConfigureAwait(false);

        // 取两天，客户端再按"从当前时刻起"截断，避免跨日边界丢数据
        var url = string.Format(CultureInfo.InvariantCulture,
            "{0}?latitude={1:0.####}&longitude={2:0.####}" +
            "&hourly=temperature_2m,weather_code,precipitation_probability" +
            "&timezone=auto&forecast_days=2",
            ForecastEndpoint, point.Latitude, point.Longitude);

        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);

        var root = doc.RootElement;
        if (!root.TryGetProperty("hourly", out var hourly) || hourly.ValueKind != JsonValueKind.Object)
            return [];

        if (!hourly.TryGetProperty("time", out var times) || times.ValueKind != JsonValueKind.Array)
            return [];

        var temps = ReadDoubleArray(hourly, "temperature_2m");
        var codes = ReadDoubleArray(hourly, "weather_code");
        var pops = ReadDoubleArray(hourly, "precipitation_probability");

        var now = DateTimeOffset.Now;
        var result = new List<WeatherHourly>();

        for (var i = 0; i < times.GetArrayLength(); i++)
        {
            if (!DateTimeOffset.TryParse(GetStringAtIndex(times, i), out var t))
                continue;
            if (t < now)
                continue;
            if (result.Count >= hours)
                break;
            if (i >= temps.Count || i >= codes.Count)
                break;

            result.Add(new WeatherHourly
            {
                Time = t,
                Temperature = temps[i],
                Condition = MapWmoCode((int)Math.Round(codes[i])),
                PrecipitationProbability = i < pops.Count ? (int)Math.Round(pops[i]) : 0,
            });
        }

        return result;
    }

    private async Task<GeoPoint> ResolveCityAsync(string city, CancellationToken ct)
    {
        var point = await _geocoder.ResolveAsync(city, ct).ConfigureAwait(false);
        if (point is null)
            throw new InvalidOperationException($"Open-Meteo 无法解析「{city}」的坐标，请确认城市名（可尝试英文名，如 Shanghai）。");
        return point;
    }

    private static List<double> ReadDoubleArray(JsonElement container, string property)
    {
        var list = new List<double>();
        if (!container.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in arr.EnumerateArray())
        {
            var value = item.ValueKind switch
            {
                JsonValueKind.Number => item.TryGetDouble(out var d) ? d : double.NaN,
                JsonValueKind.String => double.TryParse(item.GetString(), out var d2) ? d2 : double.NaN,
                _ => double.NaN,
            };
            list.Add(value);
        }

        return list;
    }

    private static string GetStringAtIndex(JsonElement array, int index)
    {
        if (array.ValueKind != JsonValueKind.Array || index >= array.GetArrayLength())
            return string.Empty;

        var item = array[index];
        return item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : string.Empty;
    }

    /// <summary>WMO weather code → 中文天气短词，图标种类由 WeatherIconSelector 据短词推导。</summary>
    private static readonly Dictionary<int, string> WmoConditions = new()
    {
        [0] = "晴",
        [1] = "晴",
        [2] = "多云",
        [3] = "阴",
        [45] = "雾霾", [48] = "雾霾",
        [51] = "小雨", [53] = "小雨", [55] = "小雨",
        [56] = "小雨", [57] = "小雨",
        [61] = "小雨", [63] = "雨", [65] = "大雨",
        [66] = "雨", [67] = "雨",
        [71] = "小雪", [73] = "雪", [75] = "大雪",
        [77] = "雪",
        [80] = "小雨", [81] = "阵雨", [82] = "大雨",
        [85] = "雪", [86] = "雪",
        [95] = "雷阵雨", [96] = "雷阵雨", [99] = "雷阵雨",
    };

    private static string MapWmoCode(int code) =>
        WmoConditions.TryGetValue(code, out var condition) ? condition : "未知";

    private static readonly string[] Directions =
        ["北风", "东北风", "东风", "东南风", "南风", "西南风", "西风", "西北风"];

    private static string DescribeWindDirection(double degrees)
    {
        if (degrees is < 0 or > 360)
            return string.Empty;
        var index = (int)Math.Round(degrees / 45.0) % 8;
        return Directions[index];
    }
}
