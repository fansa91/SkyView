using SkyView.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SkyView.Providers;

/// <summary>
/// wttr.in —— 免费、免 API Key 的通用天气源（<see href="https://wttr.in"/>），
/// 参考 Breezy Weather 的免密钥源思路补充。
/// <para>
/// 与 Open-Meteo 的关键区别：wttr.in 的 URL 直接接受**城市名**（如 <c>/Beijing?format=j1</c>），
/// 内部自己做地理编码，因此本 Provider **不依赖 <see cref="GeocodingService"/>**，
/// 即使 Open-Meteo geocoding 不可用也能独立取数，是又一条独立的免密钥兜底链路。
/// </para>
/// <para>
/// 返回 <c>format=j1</c> 的 JSON：顶层 <c>current_condition[0]</c>（当前天气）+ <c>weather[]</c>
/// （未来 3 天，每天 8 条 3 小时粒度的 <c>hourly</c> 与 <c>astronomy</c>）。
/// 天气描述用 <see cref="WeatherProviderBase.MapCondition"/> 归一成中文，图标由
/// <see cref="SkyView.Icons.WeatherIconSelector"/> 统一推导，避免再维护一份码表。
/// </para>
/// </summary>
public sealed class WttrProvider : WeatherProviderBase
{
    public WttrProvider(HttpClient http, ProviderConfig config)
        : base(http, config)
    {
    }

    public const string ProviderName = "Wttr";

    // 本类直接构造完整 URL 给 GetJsonAsync，host 仅作占位（与其它 Provider 的 DefaultHost 语义一致）
    protected override string DefaultHost => "wttr.in";

    private const string J1Endpoint = "https://wttr.in/";

    public override async Task<WeatherCurrent> GetCurrentAsync(string city, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(BuildJ1Url(city), ct).ConfigureAwait(false);

        var root = doc.RootElement;
        if (!root.TryGetProperty("current_condition", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            throw new InvalidOperationException("wttr.in 返回数据缺少 current_condition。");

        var cur = arr[0];

        var temperature = GetDouble(cur, "temp_C");
        var feels = GetDouble(cur, "FeelsLikeC");
        var humidity = GetInt(cur, "humidity");
        var pressure = GetDouble(cur, "pressure");
        var windKmph = GetDouble(cur, "windspeedKmph");
        var condition = MapCondition(ReadDesc(cur));

        // 露点 / 阵风 wttr.in 只放在 hourly 里，取"当天最接近当前时刻"的那条
        double? dewPoint = null;
        double? gusts = null;
        var nearest = FindNearestHourly(root);
        if (nearest is { } h)
        {
            dewPoint = GetNullableDouble(h, "DewPointC");
            gusts = GetNullableDouble(h, "WindGustKmph");
        }

        return new WeatherCurrent
        {
            City = city,
            Temperature = temperature,
            FeelsLike = feels,
            Humidity = humidity,
            Pressure = pressure,
            Condition = condition,
            ObservedAt = DateTimeOffset.Now,
            Source = "wttr.in",
            WindSpeed = windKmph,
            WindDirection = MapWindPoint(GetString(cur, "winddir16Point")),
            // wttr.in 免费可得的三项：current 自带 uv_index，露点/阵风取最近 hourly 条
            UvIndex = GetNullableDouble(cur, "uvIndex"),
            DewPoint = dewPoint,
            WindGusts = gusts,
            Precipitation = GetNullableDouble(cur, "precipMM"),
            CloudCover = GetNullableDouble(cur, "cloudcover"),
        };
    }

    /// <summary>找 weather[0].hourly 里 time 最接近当前时刻的那条（wttr.in 只有 hourly 有露点/阵风）。</summary>
    private static JsonElement? FindNearestHourly(JsonElement root)
    {
        if (!root.TryGetProperty("weather", out var days) || days.ValueKind != JsonValueKind.Array || days.GetArrayLength() == 0)
            return null;
        if (!days[0].TryGetProperty("hourly", out var hourly) || hourly.ValueKind != JsonValueKind.Array)
            return null;

        var nowHour = DateTimeOffset.Now.Hour;
        JsonElement? best = null;
        var bestDiff = int.MaxValue;
        foreach (var h in hourly.EnumerateArray())
        {
            if (!TryParseHour(GetString(h, "time"), out var hour))
                continue;
            var diff = Math.Abs(hour - nowHour);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = h;
            }
        }

        return best;
    }

    public override async Task<IEnumerable<WeatherForecast>> GetForecastAsync(string city, int days, CancellationToken ct = default)
    {
        var wanted = Math.Clamp(days, 1, 3); // wttr.in j1 只有 3 天

        using var doc = await GetJsonAsync(BuildJ1Url(city), ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("weather", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("wttr.in 返回数据缺少 weather。");

        var result = new List<WeatherForecast>();
        foreach (var day in arr.EnumerateArray())
        {
            if (result.Count >= wanted)
                break;
            if (!day.TryGetProperty("date", out var dateEl))
                continue;
            if (!DateTimeOffset.TryParse(GetRaw(dateEl), CultureInfo.InvariantCulture, out var date))
                continue;

            var high = GetDouble(day, "maxtempC");
            var low = GetDouble(day, "mintempC");

            // 当天典型天气：取中午前后那条 hourly 的描述（8 条每 3 小时，第 4 条约 09:00）
            var condition = MapCondition(PickMiddayDesc(day));

            var sunrise = string.Empty;
            var sunset = string.Empty;
            if (day.TryGetProperty("astronomy", out var astro) && astro.ValueKind == JsonValueKind.Array && astro.GetArrayLength() > 0)
            {
                var a0 = astro[0];
                sunrise = FormatClock(GetString(a0, "sunrise"));
                sunset = FormatClock(GetString(a0, "sunset"));
            }

            result.Add(new WeatherForecast
            {
                Date = date,
                TempHigh = high,
                TempLow = low,
                Condition = condition,
                Sunrise = sunrise,
                Sunset = sunset,
            });
        }

        return result;
    }

    public override async Task<IEnumerable<WeatherHourly>> GetHourlyAsync(string city, int hours, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(BuildJ1Url(city), ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("weather", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        var now = DateTimeOffset.Now;
        var result = new List<WeatherHourly>();

        // 遍历 3 天 × 8 条（每 3 小时），按时间过滤"从当前时刻起"，取前 hours 条
        foreach (var day in arr.EnumerateArray())
        {
            if (!day.TryGetProperty("date", out var dateEl))
                continue;
            if (!DateTimeOffset.TryParse(GetRaw(dateEl), CultureInfo.InvariantCulture, out var dayStart))
                continue;
            if (!day.TryGetProperty("hourly", out var hourly) || hourly.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var h in hourly.EnumerateArray())
            {
                if (result.Count >= hours)
                    break;

                if (!TryParseHour(GetString(h, "time"), out var hour))
                    continue;

                var time = dayStart.Date + TimeSpan.FromHours(hour);
                if (time < now)
                    continue;

                result.Add(new WeatherHourly
                {
                    Time = time,
                    Temperature = GetDouble(h, "tempC"),
                    Condition = MapCondition(ReadDesc(h)),
                    PrecipitationProbability = GetInt(h, "chanceofrain"),
                    Pressure = GetNullableDouble(h, "pressure"),
                });
            }
        }

        return result;
    }

    // ---------- 内部 helper ----------

    private string BuildJ1Url(string city)
    {
        // wttr.in 接受城市名/经纬度直接拼进路径；中文城市名也支持（如 /北京）
        var clean = (city ?? string.Empty).Trim().Replace(" ", "%20");
        if (clean.Length == 0)
            clean = "Beijing";
        return $"{J1Endpoint}{clean}?format=j1";
    }

    /// <summary>wttr.in 的天气描述在 <c>weatherDesc[0].value</c>，统一取出再交给 MapCondition。</summary>
    private static string ReadDesc(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return string.Empty;
        if (!element.TryGetProperty("weatherDesc", out var desc) || desc.ValueKind != JsonValueKind.Array || desc.GetArrayLength() == 0)
            return string.Empty;
        var first = desc[0];
        return first.ValueKind == JsonValueKind.Object ? GetString(first, "value") : first.GetString() ?? string.Empty;
    }

    /// <summary>取一天里"中午前后"那条 hourly 的天气描述，作为当天代表天气。</summary>
    private static string PickMiddayDesc(JsonElement day)
    {
        if (day.ValueKind != JsonValueKind.Object || !day.TryGetProperty("hourly", out var hourly) || hourly.ValueKind != JsonValueKind.Array)
            return string.Empty;

        JsonElement best = default;
        var bestDiff = int.MaxValue;
        foreach (var h in hourly.EnumerateArray())
        {
            if (!TryParseHour(GetString(h, "time"), out var hour))
                continue;
            var diff = Math.Abs(hour - 12);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = h;
            }
        }

        return ReadDesc(best);
    }

    /// <summary>wttr.in 的 <c>time</c> 形如 "0"/"300"/"1200"/"2100"（= 小时 × 100，也可能 "000"/"030"）。</summary>
    private static bool TryParseHour(string raw, out int hour)
    {
        hour = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return false;
        // wttr.in 的 time 形如 "0" / "300" / "1200"（0, 300, 600... 即小时×100；也可能直接 "0".."23"）
        hour = (v >= 0 && v <= 23) ? v : (v >= 100 && v <= 2300 ? v / 100 : -1);
        return hour >= 0 && hour <= 23;
    }

    /// <summary>16 方位风向（wttr.in 的 winddir16Point）→ 中文。</summary>
    private static readonly Dictionary<string, string> WindPoints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["N"] = "北风", ["NNE"] = "东北偏北风", ["NE"] = "东北风", ["ENE"] = "东偏东北风",
        ["E"] = "东风", ["ESE"] = "东南偏东风", ["SE"] = "东南风", ["SSE"] = "南偏东南风",
        ["S"] = "南风", ["SSW"] = "西南偏南风", ["SW"] = "西南风", ["WSW"] = "西偏西南风",
        ["W"] = "西风", ["WNW"] = "西北偏西风", ["NW"] = "西北风", ["NNW"] = "北偏西北风",
    };

    private static string MapWindPoint(string point16)
    {
        if (string.IsNullOrWhiteSpace(point16))
            return string.Empty;
        return WindPoints.TryGetValue(point16.Trim(), out var cn) ? cn : point16.Trim();
    }

    /// <summary>读取 JsonElement 文本（字符串原样取，非字符串则空）。</summary>
    private static string GetRaw(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : string.Empty;
    }
}
