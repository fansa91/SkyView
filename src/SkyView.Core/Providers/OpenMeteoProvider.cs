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
            "wind_speed_10m,wind_direction_10m,pressure_msl,uv_index,precipitation," +
            "cloud_cover,dew_point_2m,wind_gusts_10m" +
            // tier 2：分钟级降雨（15 分钟粒度，供「未来 2 小时会不会下雨」短临预判）
            "&minutely_15=precipitation,precipitation_probability&timezone=auto",
            ForecastEndpoint, point.Latitude, point.Longitude);

        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);

        var root = doc.RootElement;
        if (!root.TryGetProperty("current", out var cur) || cur.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Open-Meteo 返回数据缺少 current 字段。");

        var temperature = GetDouble(cur, "temperature_2m");
        var code = GetInt(cur, "weather_code");

        var current = new WeatherCurrent
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
            UvIndex = GetNullableDouble(cur, "uv_index"),
            Precipitation = GetNullableDouble(cur, "precipitation"),
            CloudCover = GetNullableDouble(cur, "cloud_cover"),
            DewPoint = GetNullableDouble(cur, "dew_point_2m"),
            WindGusts = GetNullableDouble(cur, "wind_gusts_10m"),
            MinutelyRain = BuildMinutelyRain(root),
        };

        // 花粉是加分项：单独一次小请求，超时/失败都吞掉，绝不拖垮当前天气
        current.Pollen = await TryLoadPollenAsync(point, ct).ConfigureAwait(false);
        return current;
    }

    private const string AirQualityEndpoint = "https://air-quality-api.open-meteo.com/v1/air-quality";

    /// <summary>
    /// 拉取花粉浓度（免 Key 的 Air Quality API，5 个花粉变量）。
    /// <para>
    /// 该接口花粉数据主要覆盖欧洲：国内城市五个变量全返回 null，此时整体返回 null
    /// （界面隐藏整行），与"有数据但浓度为 0"是两回事。3 秒超时——花粉再重要
    /// 也不值得让用户多等当前天气。
    /// </para>
    /// </summary>
    private async Task<PollenInfo?> TryLoadPollenAsync(GeoPoint point, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            var url = string.Format(CultureInfo.InvariantCulture,
                "{0}?latitude={1:0.####}&longitude={2:0.####}" +
                "&current=grass_pollen,birch_pollen,ragweed_pollen,alder_pollen,olive_pollen&timezone=auto",
                AirQualityEndpoint, point.Latitude, point.Longitude);

            using var doc = await GetJsonAsync(url, cts.Token).ConfigureAwait(false);
            var root = doc.RootElement;
            if (!root.TryGetProperty("current", out var cur) || cur.ValueKind != JsonValueKind.Object)
                return null;

            var grass = GetNullableDouble(cur, "grass_pollen");
            var treeValues = new[]
            {
                GetNullableDouble(cur, "birch_pollen"),
                GetNullableDouble(cur, "ragweed_pollen"),
                GetNullableDouble(cur, "alder_pollen"),
                GetNullableDouble(cur, "olive_pollen"),
            }.Where(v => v.HasValue).ToList();

            // 接口可用但该地区无花粉数据（国内城市）：null，界面整行隐藏
            if (!grass.HasValue && treeValues.Count == 0)
                return null;

            return new PollenInfo
            {
                Grass = grass,
                Tree = treeValues.Count > 0 ? treeValues.Max() : null,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // 花粉接口超时/被限流：不影响当前天气
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 从 <c>minutely_15</c> 序列算「未来 2 小时会不会下雨」。
    /// <para>
    /// 只收「从当前时刻起、落在窗口内」的槽位（已过去的整格跳过）；
    /// 首个降水量 ≥ 阈值的槽即降雨开始时刻，向后累计连续降雨槽数得持续时间。
    /// 返回 null = 接口没给分钟级数据（旧版/被裁剪的响应），与「有数据但无雨」是两回事。
    /// </para>
    /// </summary>
    private static MinutelyRainInfo? BuildMinutelyRain(JsonElement root)
    {
        if (!root.TryGetProperty("minutely_15", out var m) || m.ValueKind != JsonValueKind.Object)
            return null;
        if (!m.TryGetProperty("time", out var times) || times.ValueKind != JsonValueKind.Array || times.GetArrayLength() == 0)
            return null;

        var precip = ReadNullableDoubleArray(m, "precipitation");
        var prob = ReadNullableDoubleArray(m, "precipitation_probability");

        var now = DateTimeOffset.Now;
        var windowMinutes = MinutelyRainInfo.WindowHours * 60;
        var windowSlots = windowMinutes / MinutelyRainInfo.SlotMinutes;

        var slots = new List<(double mm, int prob, int minutesAhead)>();
        for (var i = 0; i < times.GetArrayLength() && slots.Count < windowSlots; i++)
        {
            if (!DateTimeOffset.TryParse(GetStringAtIndex(times, i), out var t))
                continue;
            var ahead = (t - now).TotalMinutes;
            if (ahead < -MinutelyRainInfo.SlotMinutes)
                continue; // 早就过去的整格
            if (ahead > windowMinutes)
                break; // time 升序，之后的格都出窗

            slots.Add((
                i < precip.Count ? precip[i] ?? 0 : 0,
                i < prob.Count ? (int)Math.Round(prob[i] ?? 0) : 0,
                // 距现在多少分钟，按 5 分钟取整——15 分钟粒度对着一刻钟报太粗，
                // "约 25 分钟后" 比 "约 30 分钟后"更贴近真实开始点
                (int)Math.Round(Math.Max(ahead, 0) / 5.0) * 5));
        }

        if (slots.Count == 0)
            return null;

        var maxProb = slots.Max(s => s.prob);

        var firstRain = -1;
        for (var i = 0; i < slots.Count; i++)
        {
            if (slots[i].mm >= MinutelyRainInfo.RainThresholdMm)
            {
                firstRain = i;
                break;
            }
        }

        if (firstRain < 0)
            return new MinutelyRainInfo { MinutesUntilRain = -1, RainDurationMinutes = 0, MaxProbability = maxProb };

        // 从首个降雨槽起累计连续降雨槽数；被窗口截断时持续时间就是窗口内观测到的长度
        var duration = 0;
        for (var i = firstRain; i < slots.Count; i++)
        {
            if (slots[i].mm < MinutelyRainInfo.RainThresholdMm)
                break;
            duration++;
        }

        return new MinutelyRainInfo
        {
            MinutesUntilRain = slots[firstRain].minutesAhead,
            RainDurationMinutes = duration * MinutelyRainInfo.SlotMinutes,
            MaxProbability = maxProb,
        };
    }

    public override async Task<IEnumerable<WeatherForecast>> GetForecastAsync(string city, int days, CancellationToken ct = default)
    {
        var point = await ResolveCityAsync(city, ct).ConfigureAwait(false);

        var url = string.Format(CultureInfo.InvariantCulture,
            "{0}?latitude={1:0.####}&longitude={2:0.####}" +
            // tier 2：预报拉长到 15 天（Open-Meteo 上限）；顺带取降水概率和日出日落，
            // 概率参与图标推导，日出日落填补模型里一直空着的两个字段
            "&daily=weather_code,temperature_2m_max,temperature_2m_min," +
            "precipitation_probability_max,sunrise,sunset" +
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
        var pops = ReadDoubleArray(daily, "precipitation_probability_max");
        var sunrises = ReadStringArray(daily, "sunrise");
        var sunsets = ReadStringArray(daily, "sunset");

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
                PrecipitationProbability = i < pops.Count ? (int)Math.Round(pops[i]) : 0,
                Sunrise = i < sunrises.Count ? ClockOf(sunrises[i]) : string.Empty,
                Sunset = i < sunsets.Count ? ClockOf(sunsets[i]) : string.Empty,
            });
        }

        return result;
    }

    public override async Task<IEnumerable<WeatherHourly>> GetHourlyAsync(string city, int hours, CancellationToken ct = default)
    {
        var point = await ResolveCityAsync(city, ct).ConfigureAwait(false);

        // 取两天，客户端再按"从当前时刻起"截断，避免跨日边界丢数据
        // pressure_msl 供主页算「气压趋势」；cloud_cover / precipitation 等当前值已由 GetCurrentAsync 提供
        var url = string.Format(CultureInfo.InvariantCulture,
            "{0}?latitude={1:0.####}&longitude={2:0.####}" +
            "&hourly=temperature_2m,weather_code,precipitation_probability,pressure_msl" +
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
        var pressures = ReadNullableDoubleArray(hourly, "pressure_msl");

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
                Pressure = i < pressures.Count ? pressures[i] : null,
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

    /// <summary>可空 double 数组版：缺项/解析失败为 null，与 <see cref="ReadDoubleArray"/> 的 NaN 语义区分开。</summary>
    private static List<double?> ReadNullableDoubleArray(JsonElement container, string property)
    {
        var list = new List<double?>();
        if (!container.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in arr.EnumerateArray())
        {
            list.Add(item.ValueKind switch
            {
                JsonValueKind.Number => item.TryGetDouble(out var d) ? d : null,
                JsonValueKind.String => double.TryParse(item.GetString(), out var d2) ? d2 : null,
                _ => null,
            });
        }

        return list;
    }

    /// <summary>字符串数组版：缺整个数组返回空列表，缺项为空串（与 GetStringAtIndex 的越界语义一致）。</summary>
    private static List<string> ReadStringArray(JsonElement container, string property)
    {
        var list = new List<string>();
        if (!container.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in arr.EnumerateArray())
            list.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : string.Empty);

        return list;
    }

    /// <summary>
    /// 取 Open-Meteo 本地时间串（形如 <c>2026-10-29T06:12</c>，已是地点本地时区、无偏移后缀）
    /// 的 HH:mm 部分。不能再 Parse 成 DateTimeOffset——带本地时区语义的串一旦转换会整体偏移。
    /// </summary>
    private static string ClockOf(string iso)
    {
        if (string.IsNullOrEmpty(iso))
            return string.Empty;

        var span = iso.AsSpan();
        var t = span.IndexOf('T');
        if (t < 0 || span.Length < t + 6)
            return string.Empty;

        return span.Slice(t + 1, 5).ToString();
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
