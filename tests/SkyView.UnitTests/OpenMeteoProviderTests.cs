using SkyView.Formatting;
using SkyView.Models;
using SkyView.Providers;
using SkyView.Services;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SkyView.UnitTests;

/// <summary>
/// Open-Meteo 免 Key 源的解析测试。
/// <para>
/// 重点盯两类"看起来对、算出来错"的风险：
/// </para>
/// <list type="bullet">
/// <item>新增的 tier-1 字段（uv_index / precipitation / cloud_cover / dew_point_2m / wind_gusts_10m）
/// 必须按可空 double 解析——0 是合法值（夜间 UV=0），缺字段才是 null，
/// 用"0 表示未知"会把真实数据吞掉。</item>
/// <item>hourly 的 pressure_msl 用于主页「气压趋势」，数组与 time 数组等长对齐，
/// 任何一侧越界都不能崩。</item>
/// </list>
/// </summary>
public class OpenMeteoProviderTests
{
    /// <summary>按 host 路由到固定 JSON，避免真的联网。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _byHost;

        public StubHandler(Dictionary<string, string> byHost)
        {
            _byHost = byHost;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            if (!_byHost.TryGetValue(url.Host, out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static HttpClient Client(StubHandler handler) => new(handler) { Timeout = TimeSpan.FromSeconds(5) };

    private const string GeocodingJson = """
        {"results":[{"id":1796236,"name":"上海","latitude":31.2222,"longitude":121.4581,"country":"中国","admin1":"上海市"}]}
        """;

    private const string CurrentJson = """
        {
          "latitude": 31.25, "longitude": 121.45,
          "current": {
            "time": "2026-10-29T14:00",
            "temperature_2m": 24.6,
            "relative_humidity_2m": 58,
            "apparent_temperature": 25.1,
            "weather_code": 2,
            "wind_speed_10m": 11.3,
            "wind_direction_10m": 135,
            "pressure_msl": 1016.4,
            "uv_index": 3.6,
            "precipitation": 0,
            "cloud_cover": 42,
            "dew_point_2m": 15.2,
            "wind_gusts_10m": 24.7
          }
        }
        """;

    /// <summary>hourly 的 time 必须落在"未来"，否则被 GetHourlyAsync 的"从当前时刻起"过滤掉。</summary>
    private static string HourlyJson()
    {
        var base0 = DateTimeOffset.Now.AddHours(1);
        string T(int offset) => base0.AddHours(offset).ToString("yyyy-MM-ddTHH:00");
        return $$"""
            {
              "latitude": 31.25, "longitude": 121.45,
              "hourly": {
                "time": ["{{T(0)}}", "{{T(1)}}", "{{T(2)}}", "{{T(3)}}"],
                "temperature_2m": [23.8, 24.6, 25.1, 24.2],
                "weather_code": [2, 2, 3, 61],
                "precipitation_probability": [8, 10, 14, 40],
                "pressure_msl": [1015.2, 1015.9, 1016.4, 1016.1]
              }
            }
            """;
    }

    private static OpenMeteoProvider Provider(StubHandler handler) => new(
        Client(handler),
        new ProviderConfig { Name = "OpenMeteo" },
        new GeocodingService(Client(handler)));

    private static StubHandler Stub(string forecastJson) => new(new()
    {
        ["geocoding-api.open-meteo.com"] = GeocodingJson,
        ["api.open-meteo.com"] = forecastJson,
    });

    /// <summary>花粉 stub（Air Quality API 独立 host）。</summary>
    private const string PollenJson = """
        {
          "latitude": 31.25, "longitude": 121.45,
          "current": {
            "time": "2026-10-29T17:00",
            "grass_pollen": 12.5,
            "birch_pollen": 3.0,
            "ragweed_pollen": 45.0,
            "alder_pollen": 8.0,
            "olive_pollen": null
          }
        }
        """;

    /// <summary>国内城市实测形态：五个花粉变量全部为 null。</summary>
    private const string PollenAllNullJson = """
        {
          "latitude": 39.90, "longitude": 116.40,
          "current": {
            "time": "2026-10-29T17:00",
            "grass_pollen": null,
            "birch_pollen": null,
            "ragweed_pollen": null,
            "alder_pollen": null,
            "olive_pollen": null
          }
        }
        """;

    private static StubHandler StubWithPollen(string forecastJson, string pollenJson) => new(new()
    {
        ["geocoding-api.open-meteo.com"] = GeocodingJson,
        ["api.open-meteo.com"] = forecastJson,
        ["air-quality-api.open-meteo.com"] = pollenJson,
    });

    /// <summary>daily stub：days 天，含 tier-2 新增的降水概率与日出日落。</summary>
    private static string DailyJson(int days)
    {
        var start = DateTimeOffset.Now.Date;
        var times = new List<string>();
        var codes = new List<int>();
        var highs = new List<double>();
        var lows = new List<double>();
        var pops = new List<int>();
        var sunrises = new List<string>();
        var sunsets = new List<string>();
        for (var i = 0; i < days; i++)
        {
            var d = start.AddDays(i);
            var day = d.ToString("yyyy-MM-dd");
            times.Add(day);
            codes.Add(i % 3 == 2 ? 61 : 2); // 第 3/6/9… 天小雨
            highs.Add(20 + i % 5);
            lows.Add(10 + i % 4);
            pops.Add(i % 3 == 2 ? 80 : 10);
            sunrises.Add($"{day}T06:12");
            sunsets.Add($"{day}T17:48");
        }

        return JsonSerializer.Serialize(new
        {
            daily = new Dictionary<string, object>
            {
                ["time"] = times,
                ["weather_code"] = codes,
                ["temperature_2m_max"] = highs,
                ["temperature_2m_min"] = lows,
                ["precipitation_probability_max"] = pops,
                ["sunrise"] = sunrises,
                ["sunset"] = sunsets,
            },
        });
    }

    /// <summary>
    /// current + minutely_15 stub。槽位时间必须动态生成——从「当前 15 分钟整点槽」起排，
    /// 写死时间会被"从当前时刻起"的过滤逻辑整体丢掉（hourly 测试踩过这个坑）。
    /// </summary>
    private static string CurrentWithMinutelyJson(double[] precip, int[] prob)
    {
        var now = DateTimeOffset.Now;
        var slotTicks = TimeSpan.FromMinutes(MinutelyRainInfo.SlotMinutes).Ticks;
        var slot0 = new DateTimeOffset(now.Ticks - now.Ticks % slotTicks, now.Offset);
        var times = new List<string>();
        for (var i = 0; i < precip.Length; i++)
            times.Add(slot0.AddMinutes(MinutelyRainInfo.SlotMinutes * i).ToString("yyyy-MM-ddTHH:mm"));

        return JsonSerializer.Serialize(new
        {
            current = new Dictionary<string, object>
            {
                ["time"] = "2026-10-29T14:00",
                ["temperature_2m"] = 24.6,
                ["relative_humidity_2m"] = 58,
                ["apparent_temperature"] = 25.1,
                ["weather_code"] = 2,
                ["wind_speed_10m"] = 11.3,
                ["wind_direction_10m"] = 135,
                ["pressure_msl"] = 1016.4,
            },
            minutely_15 = new Dictionary<string, object>
            {
                ["time"] = times,
                ["precipitation"] = precip,
                ["precipitation_probability"] = prob,
            },
        });
    }

    [Fact]
    public async Task Current_ShouldParseTier1FieldsAsNullable()
    {
        var current = await Provider(Stub(CurrentJson)).GetCurrentAsync("上海");

        // 新字段按可空解析：0（真实无降水）与 null（源不提供）必须能区分
        Assert.Equal(3.6, current.UvIndex!.Value, 1);
        Assert.Equal(0, current.Precipitation!.Value, 1);
        Assert.True(current.HasPrecipitation, "precipitation=0 是真实值，不能当成未知");
        Assert.Equal(42, current.CloudCover!.Value);
        Assert.Equal(15.2, current.DewPoint!.Value, 1);
        Assert.Equal(24.7, current.WindGusts!.Value, 1);
        Assert.True(current.HasDetailMetrics);
    }

    [Fact]
    public async Task Current_WithoutNewFields_ShouldLeaveThemNull()
    {
        // 旧版接口/裁剪过的响应：新字段缺失时必须为 null（而不是 0），否则界面会显示"降水 0 mm"这种假数据
        var current = await Provider(Stub("""
            {"current":{"temperature_2m":20.0,"relative_humidity_2m":40,"apparent_temperature":19.5,
                        "weather_code":0,"wind_speed_10m":5.0,"wind_direction_10m":0,"pressure_msl":1012.0}}
            """)).GetCurrentAsync("上海");

        Assert.False(current.UvIndex.HasValue);
        Assert.False(current.Precipitation.HasValue);
        Assert.False(current.CloudCover.HasValue);
        Assert.False(current.DewPoint.HasValue);
        Assert.False(current.WindGusts.HasValue);
        Assert.False(current.HasUvIndex);
        Assert.False(current.HasDetailMetrics);
    }

    [Fact]
    public async Task Hourly_ShouldParsePressureAlignedWithTime()
    {
        var hourly = (await Provider(Stub(HourlyJson())).GetHourlyAsync("上海", 24)).ToList();

        Assert.Equal(4, hourly.Count);
        Assert.All(hourly, h => Assert.True(h.Pressure.HasValue));
        // 与 time 数组等长对齐：第 3 条（15:00）对应 1016.4
        Assert.Equal(1016.4, hourly[2].Pressure!.Value, 1);
    }

    [Fact]
    public async Task Hourly_WithoutPressure_ShouldNotCrash()
    {
        var oneFuture = DateTimeOffset.Now.AddHours(1).ToString("yyyy-MM-ddTHH:00");
        var json = "{\"hourly\":{\"time\":[\"" + oneFuture + "\"],\"temperature_2m\":[24.0]," +
                   "\"weather_code\":[2],\"precipitation_probability\":[10]}}";
        var hourly = (await Provider(Stub(json)).GetHourlyAsync("上海", 24)).ToList();

        Assert.Single(hourly);
        Assert.False(hourly[0].Pressure.HasValue);
    }

    [Fact]
    public async Task Forecast_ShouldParseFifteenDays_WithProbabilityAndSunTimes()
    {
        // tier 2：预报拉长到 15 天，daily 顺带解析降水概率与日出日落
        var forecast = (await Provider(Stub(DailyJson(15))).GetForecastAsync("上海", 15)).ToList();

        Assert.Equal(15, forecast.Count);
        Assert.Equal(80, forecast[2].PrecipitationProbability); // stub 第 3 天小雨 80%
        Assert.Equal(10, forecast[0].PrecipitationProbability);
        Assert.Equal("06:12", forecast[0].Sunrise);
        Assert.Equal("17:48", forecast[0].Sunset);
        Assert.All(forecast, f => Assert.False(string.IsNullOrEmpty(f.Sunrise)));
        // 日期逐天递增、无重复
        Assert.Equal(forecast[0].Date.AddDays(14), forecast[^1].Date);
    }

    [Fact]
    public async Task Forecast_WithoutNewDailyFields_ShouldNotCrash()
    {
        // 旧版接口只给三要素：概率回落 0（未知）、日出日落为空串，不能崩
        var json = """
            {"daily":{"time":["2026-10-29","2026-10-30"],
                      "weather_code":[2,61],
                      "temperature_2m_max":[24.0,21.0],
                      "temperature_2m_min":[15.0,13.0]}}
            """;
        var forecast = (await Provider(Stub(json)).GetForecastAsync("上海", 15)).ToList();

        Assert.Equal(2, forecast.Count);
        Assert.Equal(0, forecast[0].PrecipitationProbability);
        Assert.Equal(string.Empty, forecast[0].Sunrise);
        Assert.Equal(string.Empty, forecast[0].Sunset);
    }

    [Fact]
    public async Task Current_ShouldComputeMinutelyRain_FromFifteenMinuteSlots()
    {
        // 第 2 格（0.2mm）开始下雨，连续 3 格共 45 分钟
        var current = await Provider(Stub(CurrentWithMinutelyJson(
            [0, 0.2, 1.4, 2.1, 0, 0, 0, 0],
            [10, 40, 80, 90, 20, 10, 5, 5]))).GetCurrentAsync("上海");

        var rain = current.MinutelyRain;
        Assert.NotNull(rain);
        Assert.True(current.HasMinutelyRain);
        Assert.True(rain.HasRain);
        // 首个降雨槽距现在 0~15 分钟（按 5 分钟取整后的落点）
        Assert.InRange(rain.MinutesUntilRain, 0, 15);
        Assert.Equal(45, rain.RainDurationMinutes); // 3 个连续降雨槽
        Assert.Equal(90, rain.MaxProbability);
    }

    [Fact]
    public async Task Current_MinutelyNoRain_ShouldSaySoInsteadOfNull()
    {
        // 有分钟级数据但窗口内无雨：这是"结论"，要与"无数据"区分开
        var current = await Provider(Stub(CurrentWithMinutelyJson(
            [0, 0, 0, 0, 0, 0, 0, 0],
            [5, 20, 30, 10, 10, 5, 5, 5]))).GetCurrentAsync("上海");

        var rain = current.MinutelyRain;
        Assert.NotNull(rain);
        Assert.False(rain.HasRain);
        Assert.Equal(30, rain.MaxProbability);
        Assert.Equal("短临降雨：未来 2 小时无雨（降水概率最高 30%）",
            WeatherFormat.MinutelyRainSummary(rain));
    }

    [Fact]
    public async Task Current_WithoutMinutely_ShouldLeaveRainNull()
    {
        // 接口没给 minutely_15（wttr.in 同理）：null，界面整行隐藏
        var current = await Provider(Stub(CurrentJson)).GetCurrentAsync("上海");

        Assert.Null(current.MinutelyRain);
        Assert.False(current.HasMinutelyRain);
    }

    [Fact]
    public async Task Current_WithEmptyMinutely_ShouldLeaveRainNull()
    {
        // 给了 minutely_15 但数组为空：同样按"无数据"处理，不显示"未来 2 小时无雨"
        var json = """{"current":{"temperature_2m":24.6,"relative_humidity_2m":58,"apparent_temperature":25.1,"weather_code":2,"wind_speed_10m":11.3,"wind_direction_10m":135,"pressure_msl":1016.4},"minutely_15":{"time":[]}}""";
        var current = await Provider(Stub(json)).GetCurrentAsync("上海");

        Assert.Null(current.MinutelyRain);
    }

    [Fact]
    public async Task Current_ShouldParsePollen_FromAirQualityApi()
    {
        // tier 3：花粉走独立的 Air Quality API，树类取四种最大值
        var current = await Provider(StubWithPollen(CurrentJson, PollenJson)).GetCurrentAsync("上海");

        var pollen = current.Pollen;
        Assert.NotNull(pollen);
        Assert.True(current.HasPollen);
        Assert.Equal(12.5, pollen.Grass!.Value, 1);
        Assert.Equal(45.0, pollen.Tree!.Value, 1); // max(3, 45, 8, null)
        Assert.Equal(45.0, pollen.Peak, 1);
    }

    [Fact]
    public async Task Current_PollenAllNull_ShouldLeavePollenNull()
    {
        // 国内城市实测：五个变量全 null → null（整行隐藏），而不是"0 粒/m³"假数据
        var current = await Provider(StubWithPollen(CurrentJson, PollenAllNullJson)).GetCurrentAsync("北京");

        Assert.Null(current.Pollen);
        Assert.False(current.HasPollen);
    }

    [Fact]
    public async Task Current_PollenApiFailure_ShouldNotBreakCurrent()
    {
        // 花粉接口 404 / 超时都不能影响当前天气（catch 吞掉返回 null）
        var current = await Provider(Stub(CurrentJson)).GetCurrentAsync("上海");

        Assert.Null(current.Pollen);
        Assert.Equal(24.6, current.Temperature, 1);
        Assert.True(current.HasUvIndex); // 主链路数据完好
    }
}

/// <summary>
/// tier-1 新增展示逻辑的纯函数测试：UV 分级 / 气压趋势。
/// 这些是 UI 文案的唯一定义处，边界值钉死，避免"改一版UI文案悄悄漂移"。
/// </summary>
public class Tier1FormatTests
{
    [Theory]
    [InlineData(0, "低")]      // 夜间 UV=0 也是合法值
    [InlineData(2, "低")]
    [InlineData(2.5, "中等")]
    [InlineData(5, "中等")]
    [InlineData(5.1, "高")]
    [InlineData(7, "高")]
    [InlineData(7.5, "很高")]
    [InlineData(10, "很高")]
    [InlineData(10.5, "极高")]
    [InlineData(15, "极高")]
    public void UvCategory_Boundaries(double uv, string expected)
        => Assert.Equal(expected, WeatherFormat.UvCategory(uv));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(5, 2)]
    [InlineData(6, 3)]
    [InlineData(7, 3)]
    [InlineData(8, 4)]
    [InlineData(10, 4)]
    [InlineData(11, 5)]
    public void UvLevel_Boundaries(double uv, int expected)
        => Assert.Equal(expected, WeatherFormat.UvLevel(uv));

    [Fact]
    public void PressureTrend_Steady_WhenChangeBelowThreshold()
        => Assert.Equal("→ 平稳", WeatherFormat.PressureTrend([1013.0, 1013.4, 1013.6]));

    [Fact]
    public void PressureTrend_Rising()
        => Assert.Equal("▲ 回升", WeatherFormat.PressureTrend([1012.0, 1013.2, 1013.5]));

    [Fact]
    public void PressureTrend_SharpRising()
        => Assert.Equal("▲ 急升", WeatherFormat.PressureTrend([1010.0, 1012.0, 1013.5]));

    [Fact]
    public void PressureTrend_Falling()
        => Assert.Equal("▼ 下降", WeatherFormat.PressureTrend([1014.0, 1013.0, 1012.6]));

    [Fact]
    public void PressureTrend_SharpFalling()
        => Assert.Equal("▼ 急降", WeatherFormat.PressureTrend([1016.0, 1013.5, 1012.5]));

    [Fact]
    public void PressureTrend_NotEnoughData_ShouldBeEmpty()
    {
        Assert.Equal(string.Empty, WeatherFormat.PressureTrend([]));
        Assert.Equal(string.Empty, WeatherFormat.PressureTrend([1013.0]));
        Assert.Equal(string.Empty, WeatherFormat.PressureTrend([null, null]));
    }

    [Fact]
    public void PressureTrend_ShouldSkipNulls()
    {
        // null 跳过而非当中断：前 6 个有效点里首尾仍取到 1012.0 / 1013.5
        Assert.Equal("▲ 回升", WeatherFormat.PressureTrend([null, 1012.0, null, 1013.2, 1013.5]));
    }
}

/// <summary>
/// tier-2 短临降雨文案的纯函数测试。三种结局（无雨 / 将要下 / 正在下）各钉一句，
/// 概率提示只在 ≥30% 时出现，避免文案漂移。
/// </summary>
public class Tier2FormatTests
{
    [Fact]
    public void MinutelyRainSummary_NoRain()
        => Assert.Equal("短临降雨：未来 2 小时无雨",
            WeatherFormat.MinutelyRainSummary(
                new MinutelyRainInfo { MinutesUntilRain = -1, RainDurationMinutes = 0 }));

    [Fact]
    public void MinutelyRainSummary_NoRain_WithProbabilityHint()
        => Assert.Equal("短临降雨：未来 2 小时无雨（降水概率最高 65%）",
            WeatherFormat.MinutelyRainSummary(
                new MinutelyRainInfo { MinutesUntilRain = -1, RainDurationMinutes = 0, MaxProbability = 65 }));

    [Fact]
    public void MinutelyRainSummary_RainStartingLater()
        => Assert.Equal("短临降雨：约 45 分钟后开始下雨，持续约 60 分钟",
            WeatherFormat.MinutelyRainSummary(
                new MinutelyRainInfo { MinutesUntilRain = 45, RainDurationMinutes = 60, MaxProbability = 80 }));

    [Fact]
    public void MinutelyRainSummary_RainingNow()
        => Assert.Equal("短临降雨：正在下雨，预计持续约 30 分钟",
            WeatherFormat.MinutelyRainSummary(
                new MinutelyRainInfo { MinutesUntilRain = 0, RainDurationMinutes = 30 }));

    [Fact]
    public void MinutelyRainSummary_Null_ShouldThrow()
        => Assert.Throws<ArgumentNullException>(() => WeatherFormat.MinutelyRainSummary(null!));
}
