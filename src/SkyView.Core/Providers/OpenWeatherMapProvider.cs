using SkyView.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SkyView.Providers;

/// <summary>
/// OpenWeatherMap。
/// 文档：https://openweathermap.org/current 、https://openweathermap.org/forecast5
/// 免费版预报为 5 天 / 3 小时粒度，这里按天聚合出最高最低温。
/// </summary>
public sealed class OpenWeatherMapProvider : WeatherProviderBase
{
    public OpenWeatherMapProvider(HttpClient http, ProviderConfig config) : base(http, config) { }

    protected override string DefaultHost => "api.openweathermap.org";

    private string Key => Uri.EscapeDataString(Config.ApiKey);

    public override async Task<WeatherCurrent> GetCurrentAsync(string city, CancellationToken ct = default)
    {
        EnsureApiKey();

        using var doc = await GetJsonAsync(
            BuildUrl($"/data/2.5/weather?q={Uri.EscapeDataString(city)}&appid={Key}&units=metric&lang=zh_cn"),
            ct).ConfigureAwait(false);

        var root = doc.RootElement;
        if (!root.TryGetProperty("main", out var main))
            throw new InvalidOperationException($"OpenWeatherMap 返回数据异常：{GetString(root, "message", "缺少 main 字段")}");

        var weather = root.TryGetProperty("weather", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0
            ? arr[0]
            : default;

        var sys = root.TryGetProperty("sys", out var s) ? s : default;
        var wind = root.TryGetProperty("wind", out var w) ? w : default;

        var temperature = GetDouble(main, "temp");

        return new WeatherCurrent
        {
            City = city,
            Temperature = temperature,
            FeelsLike = GetNullableDouble(main, "feels_like") ?? temperature,
            Humidity = GetInt(main, "humidity"),
            Pressure = GetDouble(main, "pressure"),
            Condition = MapCondition(GetString(weather, "description")),
            IconCode = GetString(weather, "icon"),
            ObservedAt = root.TryGetProperty("dt", out var dt) && dt.TryGetInt64(out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : DateTimeOffset.Now,
            Source = "OpenWeatherMap",
            // OWM 的 units=metric 下风速是 m/s，这里换算成 km/h 与其他源对齐
            WindSpeed = GetDouble(wind, "speed") * 3.6,
            WindDirection = DescribeWindDirection(GetDouble(wind, "deg")),
            Sunrise = FormatClockFromUnix(sys, "sunrise"),
            Sunset = FormatClockFromUnix(sys, "sunset"),
        };
    }

    public override async Task<IEnumerable<WeatherForecast>> GetForecastAsync(string city, int days, CancellationToken ct = default)
    {
        EnsureApiKey();

        using var doc = await GetJsonAsync(
            BuildUrl($"/data/2.5/forecast?q={Uri.EscapeDataString(city)}&appid={Key}&units=metric&lang=zh_cn"),
            ct).ConfigureAwait(false);

        var root = doc.RootElement;
        if (root.TryGetProperty("cod", out var cod) && cod.ValueKind == JsonValueKind.String && cod.GetString() != "200")
            throw new InvalidOperationException($"OpenWeatherMap 预报请求失败：{GetString(root, "message")}");

        if (!root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("OpenWeatherMap 返回数据缺少 list 字段。");

        var today = DateTimeOffset.Now.Date;
        var result = new List<WeatherForecast>();

        var byDay = list.EnumerateArray()
            .Where(item => item.TryGetProperty("dt", out var t) && t.TryGetInt64(out _))
            .Select(item => new
            {
                Time = DateTimeOffset.FromUnixTimeSeconds(item.GetProperty("dt").GetInt64()).ToLocalTime(),
                Main = item.TryGetProperty("main", out var m) ? m : default,
                Weather = item.TryGetProperty("weather", out var w) && w.ValueKind == JsonValueKind.Array && w.GetArrayLength() > 0
                    ? w[0]
                    : default,
                Pop = GetDouble(item, "pop"),
            })
            .Where(x => x.Main.ValueKind == JsonValueKind.Object)
            .Where(x => x.Time.Date >= today)
            .GroupBy(x => x.Time.Date)
            .OrderBy(g => g.Key)
            .Take(days);

        foreach (var day in byDay)
        {
            // 取最接近当地时间正午的那条 3 小时预报作为"白天天气"
            var noon = day.OrderBy(x => Math.Abs((x.Time.Hour - 12) * 60 + x.Time.Minute)).First();

            result.Add(new WeatherForecast
            {
                Date = new DateTimeOffset(day.Key),
                TempHigh = day.Max(x => GetDouble(x.Main, "temp_max")),
                TempLow = day.Min(x => GetDouble(x.Main, "temp_min")),
                Condition = MapCondition(GetString(noon.Weather, "description")),
                IconCode = GetString(noon.Weather, "icon"),
                PrecipitationProbability = (int)Math.Round(day.Max(x => x.Pop) * 100),
            });
        }

        return result;
    }

    /// <summary>
    /// 逐小时预报。OWM 免费版的 /forecast 是 3 小时粒度，
    /// 这里按 3 小时返回并在注释里说明——不假装能做到真正的逐小时。
    /// </summary>
    public override async Task<IEnumerable<WeatherHourly>> GetHourlyAsync(string city, int hours, CancellationToken ct = default)
    {
        EnsureApiKey();

        using var doc = await GetJsonAsync(
            BuildUrl($"/data/2.5/forecast?q={Uri.EscapeDataString(city)}&appid={Key}&units=metric&lang=zh_cn"),
            ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];

        var wanted = Math.Max(1, (int)Math.Ceiling(Math.Clamp(hours, 1, 48) / 3.0));

        return list.EnumerateArray()
            .Where(item => item.TryGetProperty("dt", out var t) && t.TryGetInt64(out _))
            .Take(wanted)
            .Select(item =>
            {
                var weather = item.TryGetProperty("weather", out var w) && w.ValueKind == JsonValueKind.Array && w.GetArrayLength() > 0
                    ? w[0]
                    : default;

                return new WeatherHourly
                {
                    Time = DateTimeOffset.FromUnixTimeSeconds(item.GetProperty("dt").GetInt64()).ToLocalTime(),
                    Temperature = GetDouble(item.TryGetProperty("main", out var m) ? m : default, "temp"),
                    Condition = MapCondition(GetString(weather, "description")),
                    IconCode = GetString(weather, "icon"),
                    PrecipitationProbability = (int)Math.Round(GetDouble(item, "pop") * 100),
                };
            })
            .ToList();
    }

    /// <summary>
    /// 空气质量。OWM 的 air_pollution 接口只接受经纬度，
    /// 所以先用 GeoAPI 把城市名转成坐标（两次请求，失败则返回 null 不影响主流程）。
    /// </summary>
    public override async Task<AirQuality?> GetAirQualityAsync(string city, CancellationToken ct = default)
    {
        EnsureApiKey();

        using var geoDoc = await GetJsonAsync(
            BuildUrl($"/geo/1.0/direct?q={Uri.EscapeDataString(city)}&limit=1&appid={Key}"),
            ct).ConfigureAwait(false);

        if (geoDoc.RootElement.ValueKind != JsonValueKind.Array || geoDoc.RootElement.GetArrayLength() == 0)
            return null;

        var place = geoDoc.RootElement[0];
        var lat = GetDouble(place, "lat");
        var lon = GetDouble(place, "lon");
        if (lat == 0 && lon == 0)
            return null;

        using var doc = await GetJsonAsync(
            BuildUrl($"/data/2.5/air_pollution?lat={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}&lon={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}&appid={Key}"),
            ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            return null;

        var first = list[0];
        var level = GetInt(first.TryGetProperty("main", out var m) ? m : default, "aqi");
        if (level <= 0)
            return null;

        // OWM 给的是 1-5 的等级，不是 AQI 数值，这里映射成代表性的 AQI 便于统一配色
        var (aqi, category) = level switch
        {
            1 => (25, "优"),
            2 => (75, "良"),
            3 => (125, "轻度污染"),
            4 => (175, "中度污染"),
            _ => (250, "重度污染"),
        };

        var components = first.TryGetProperty("components", out var c) ? c : default;

        return new AirQuality
        {
            Aqi = aqi,
            Category = category,
            PrimaryPollutant = "PM2.5",
            Pm25 = GetDouble(components, "pm2_5"),
        };
    }

    private static string FormatClockFromUnix(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var v) || !v.TryGetInt64(out var unix))
            return string.Empty;

        return DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("HH:mm");
    }

    private static string DescribeWindDirection(double degrees)
    {
        if (degrees <= 0 && degrees >= 0 && Math.Abs(degrees) < 0.001)
            return string.Empty;

        string[] names = ["北风", "东北风", "东风", "东南风", "南风", "西南风", "西风", "西北风"];
        var index = (int)Math.Round(((degrees % 360) + 360) % 360 / 45.0) % 8;
        return names[index];
    }
}
