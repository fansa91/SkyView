using SkyView.Models;
using SkyView.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SkyView.Providers;

/// <summary>
/// PirateWeather —— Dark Sky 的兼容实现（Dark Sky 已于 2023 年停止服务）。
/// 文档：https://docs.pirateweather.net/
/// <para>
/// 特点：**只接受经纬度**，没有地理编码接口，所以先走 <see cref="GeocodingService"/>
/// 把城市名换成坐标。响应结构沿用 Dark Sky 的 <c>currently / hourly / daily</c> 三段式。
/// </para>
/// <para>
/// <c>units=si</c> 下温度是 °C、风速是 m/s、气压是 hPa、湿度是 0-1 的小数，
/// 单位换算是这个 Provider 里最容易写错的地方（尤其是湿度要 ×100、风速要 ×3.6）。
/// </para>
/// </summary>
public sealed class PirateWeatherProvider : WeatherProviderBase
{
    private readonly GeocodingService _geocoder;

    public PirateWeatherProvider(HttpClient http, ProviderConfig config, GeocodingService geocoder)
        : base(http, config)
    {
        _geocoder = geocoder;
    }

    protected override string DefaultHost => "api.pirateweather.net";

    private async Task<GeoPoint> RequirePointAsync(string city, CancellationToken ct)
    {
        var point = await _geocoder.ResolveAsync(city, ct).ConfigureAwait(false);
        return point ?? throw new InvalidOperationException(
            $"PirateWeather 无法解析「{city}」的坐标（它只接受经纬度）。请检查城市名，或改用拼音/英文名。");
    }

    private async Task<JsonDocument> GetForecastAsync(GeoPoint point, string exclude, CancellationToken ct)
    {
        EnsureApiKey();

        var lat = point.Latitude.ToString("0.####", CultureInfo.InvariantCulture);
        var lon = point.Longitude.ToString("0.####", CultureInfo.InvariantCulture);
        var key = Uri.EscapeDataString(Config.ApiKey);

        var url = BuildUrl($"/forecast/{key}/{lat},{lon}?units=si&lang=zh&exclude={exclude}");
        return await GetJsonAsync(url, ct).ConfigureAwait(false);
    }

    public override async Task<WeatherCurrent> GetCurrentAsync(string city, CancellationToken ct = default)
    {
        var point = await RequirePointAsync(city, ct).ConfigureAwait(false);

        using var doc = await GetForecastAsync(point, "minutely,hourly,daily,alerts", ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("currently", out var currently))
            throw new InvalidOperationException("PirateWeather 返回数据缺少 currently 字段。");

        var temperature = GetDouble(currently, "temperature");

        return new WeatherCurrent
        {
            City = city,
            Temperature = temperature,
            FeelsLike = GetNullableDouble(currently, "apparentTemperature") ?? temperature,
            // si 单位下 humidity 是 0-1 的小数，界面和模型都用百分比
            Humidity = (int)Math.Round(GetDouble(currently, "humidity") * 100),
            Pressure = GetDouble(currently, "pressure"),
            Condition = MapCondition(PickCondition(currently)),
            IconCode = GetString(currently, "icon"),
            ObservedAt = ReadUnix(currently, "time") ?? DateTimeOffset.Now,
            Source = "PirateWeather",
            // si 单位下风速是 m/s，换算成 km/h 与其他数据源对齐
            WindSpeed = GetDouble(currently, "windSpeed") * 3.6,
            WindDirection = DescribeWindDirection(GetDouble(currently, "windBearing")),
            Sunrise = string.Empty,
            Sunset = string.Empty,
        };
    }

    public override async Task<IEnumerable<WeatherForecast>> GetForecastAsync(string city, int days, CancellationToken ct = default)
    {
        var point = await RequirePointAsync(city, ct).ConfigureAwait(false);

        using var doc = await GetForecastAsync(point, "minutely,hourly,alerts", ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("daily", out var daily)
            || !daily.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("PirateWeather 返回数据缺少 daily.data 字段。");

        var result = new List<WeatherForecast>();

        foreach (var day in data.EnumerateArray().Take(Math.Clamp(days, 1, 8)))
        {
            result.Add(new WeatherForecast
            {
                Date = ReadUnix(day, "time") ?? DateTimeOffset.Now,
                TempHigh = GetDouble(day, "temperatureHigh"),
                TempLow = GetDouble(day, "temperatureLow"),
                Condition = MapCondition(PickCondition(day)),
                IconCode = GetString(day, "icon"),
                PrecipitationProbability = (int)Math.Round(GetDouble(day, "precipProbability") * 100),
                Sunrise = FormatClockFromUnix(day, "sunriseTime"),
                Sunset = FormatClockFromUnix(day, "sunsetTime"),
            });
        }

        return result;
    }

    public override async Task<IEnumerable<WeatherHourly>> GetHourlyAsync(string city, int hours, CancellationToken ct = default)
    {
        var point = await RequirePointAsync(city, ct).ConfigureAwait(false);

        using var doc = await GetForecastAsync(point, "minutely,daily,alerts", ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("hourly", out var hourly)
            || !hourly.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
            return [];

        return data.EnumerateArray()
            .Take(Math.Clamp(hours, 1, 48))
            .Select(item => new WeatherHourly
            {
                Time = ReadUnix(item, "time") ?? DateTimeOffset.Now,
                Temperature = GetDouble(item, "temperature"),
                Condition = MapCondition(PickCondition(item)),
                IconCode = GetString(item, "icon"),
                PrecipitationProbability = (int)Math.Round(GetDouble(item, "precipProbability") * 100),
            })
            .ToList();
    }

    /// <summary>
    /// 优先用 <c>summary</c>（<c>lang=zh</c> 时是中文，例如「晴」），
    /// 为空才退回 <c>icon</c> 的英文短码（clear-day / partly-cloudy-night 等）。
    /// </summary>
    private static string PickCondition(JsonElement element)
    {
        var summary = GetString(element, "summary");
        return !string.IsNullOrWhiteSpace(summary) ? summary : GetString(element, "icon");
    }

    private static DateTimeOffset? ReadUnix(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || !value.TryGetInt64(out var unix))
            return null;

        return DateTimeOffset.FromUnixTimeSeconds(unix);
    }

    private static string FormatClockFromUnix(JsonElement element, string property)
        => ReadUnix(element, property)?.ToLocalTime().ToString("HH:mm") ?? string.Empty;

    private static string DescribeWindDirection(double degrees)
    {
        if (Math.Abs(degrees) < 0.001)
            return string.Empty;

        string[] names = ["北风", "东北风", "东风", "东南风", "南风", "西南风", "西风", "西北风"];
        var index = (int)Math.Round(((degrees % 360) + 360) % 360 / 45.0) % 8;
        return names[index];
    }
}
