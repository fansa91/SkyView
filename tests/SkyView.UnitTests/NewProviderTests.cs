using SkyView.Icons;
using SkyView.Models;
using SkyView.Providers;
using SkyView.Services;
using System.Net;
using System.Text;
using Xunit;

namespace SkyView.UnitTests;

/// <summary>
/// AccuWeather 与 PirateWeather 的解析测试。
/// <para>
/// 这两个数据源都有"看起来对、算出来错"的坑，所以重点放在**单位换算**和
/// **需要二次请求的间接字段**上：
/// </para>
/// <list type="bullet">
/// <item>PirateWeather 在 <c>units=si</c> 下湿度是 0-1 小数、风速是 m/s，
/// 少乘一次就是 45% 变 0%、12 km/h 变 3.4。</item>
/// <item>AccuWeather 必须先拿 locationKey 才能取天气，解析逻辑要覆盖两段请求。</item>
/// </list>
/// </summary>
public class NewProviderTests
{
    /// <summary>按请求 URL 的 host 路由到固定 JSON，避免真的联网。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _byHost;

        public StubHandler(Dictionary<string, string> byHost)
        {
            _byHost = byHost;
        }

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            Requests.Add(url.ToString());

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

    // ---------------- AccuWeather ----------------

    private const string AccuSearchJson = """
        [{"Version":1,"Key":"215854","Type":"City","LocalizedName":"上海","Country":{"LocalizedName":"中国"}}]
        """;

    private const string AccuCurrentJson = """
        [{
          "LocalObservationDateTime":"2026-10-29T08:00:00+08:00",
          "EpochTime":1793232000,
          "WeatherText":"晴",
          "WeatherIcon":1,
          "IsDayTime":true,
          "Temperature":{"Metric":{"Value":22.2,"Unit":"C"}},
          "RealFeelTemperature":{"Metric":{"Value":21.1,"Unit":"C"}},
          "RelativeHumidity":45,
          "Pressure":{"Metric":{"Value":1013,"Unit":"mb"}},
          "Wind":{"Direction":{"Degrees":180,"Localized":"南风"},"Speed":{"Metric":{"Value":12.4,"Unit":"km/h"}}}
        }]
        """;

    [Fact]
    public async Task AccuWeather_Current_ShouldParseMetricValuesAndNestedWind()
    {
        // 同 host 两段不同请求（先查 locationKey，再取天气），所以用按路径路由的 handler
        var routed = new PathRoutingHandler(new[]
        {
            ("/locations/v1/cities/search", AccuSearchJson),
            ("/currentconditions/v1/215854", AccuCurrentJson),
        });

        var provider = new AccuWeatherProvider(
            new HttpClient(routed),
            new ProviderConfig { Name = "AccuWeather", ApiKey = "test-key", ApiHost = "dataservice.accuweather.com" },
            new GeocodingService(new HttpClient(routed)));

        var current = await provider.GetCurrentAsync("上海");

        Assert.Equal(22.2, current.Temperature, 1);
        Assert.Equal(21.1, current.FeelsLike, 1);
        Assert.Equal(45, current.Humidity);
        Assert.Equal(1013, current.Pressure, 1);
        Assert.Equal("晴", current.Condition);
        Assert.Equal("AccuWeather", current.Source);
        Assert.Equal(12.4, current.WindSpeed, 1);
        Assert.Equal("南风", current.WindDirection);

        // locationKey 必须被缓存，否则免费额度会被秒耗尽
        await provider.GetCurrentAsync("上海");
        Assert.Single(routed.Requests, u => u.Contains("/locations/v1/cities/search"));
    }

    [Fact]
    public async Task AccuWeather_WithoutApiKey_ShouldThrowClearError()
    {
        var provider = new AccuWeatherProvider(
            Client(new StubHandler(new())),
            new ProviderConfig { Name = "AccuWeather", ApiKey = "" },
            new GeocodingService(Client(new StubHandler(new()))));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetCurrentAsync("上海"));
        Assert.Contains("API Key", ex.Message);
    }

    // ---------------- PirateWeather ----------------

    private const string PirateCurrentJson = """
        {
          "latitude":31.2222,"longitude":121.4581,"timezone":"Asia/Shanghai",
          "currently":{
            "time":1793232000,
            "summary":"晴",
            "icon":"clear-day",
            "temperature":22.5,
            "apparentTemperature":21.0,
            "humidity":0.45,
            "pressure":1013.2,
            "windSpeed":3.4,
            "windBearing":180
          }
        }
        """;

    private const string PirateHourlyJson = """
        {
          "hourly":{"data":[
            {"time":1793232000,"summary":"多云","temperature":22.0,"precipProbability":0.2},
            {"time":1793235600,"summary":"小雨","temperature":21.0,"precipProbability":0.8}
          ]}
        }
        """;

    private const string PirateDailyJson = """
        {
          "daily":{"data":[
            {"time":1793232000,"summary":"晴","temperatureHigh":25.4,"temperatureLow":15.1,
             "precipProbability":0.1,"sunriseTime":1793206800,"sunsetTime":1793247600}
          ]}
        }
        """;

    private static PirateWeatherProvider PirateProvider(StubHandler handler) => new(
        Client(handler),
        new ProviderConfig { Name = "PirateWeather", ApiKey = "test-key", ApiHost = "api.pirateweather.net" },
        new GeocodingService(Client(handler)));

    [Fact]
    public async Task PirateWeather_Current_ShouldConvertSiUnits()
    {
        var handler = new StubHandler(new()
        {
            ["geocoding-api.open-meteo.com"] = GeocodingJson,
            ["api.pirateweather.net"] = PirateCurrentJson,
        });

        var current = await PirateProvider(handler).GetCurrentAsync("上海");

        Assert.Equal(22.5, current.Temperature, 1);
        Assert.Equal(21.0, current.FeelsLike, 1);

        // 0.45 → 45%，漏乘 100 会变成 0
        Assert.Equal(45, current.Humidity);

        // 3.4 m/s → 12.24 km/h，漏乘 3.6 会变成 3
        Assert.Equal(12.24, current.WindSpeed, 2);
        Assert.Equal("南风", current.WindDirection);
        Assert.Equal("晴", current.Condition);
        Assert.Equal("PirateWeather", current.Source);
    }

    [Fact]
    public async Task PirateWeather_Hourly_ShouldScalePrecipitationToPercent()
    {
        var handler = new StubHandler(new()
        {
            ["geocoding-api.open-meteo.com"] = GeocodingJson,
            ["api.pirateweather.net"] = PirateHourlyJson,
        });

        var hourly = (await PirateProvider(handler).GetHourlyAsync("上海", 12)).ToList();

        Assert.Equal(2, hourly.Count);
        Assert.Equal(20, hourly[0].PrecipitationProbability);
        Assert.Equal(80, hourly[1].PrecipitationProbability);

        // 「小雨」会被 MapCondition 归一成「雨」，强度信息由降水概率补回
        // （WeatherIconSelector.Select(condition, precipitationProbability)）
        Assert.Equal("雨", hourly[1].Condition);
        Assert.Equal(WeatherIconKind.HeavyRain, hourly[1].IconKind);
    }

    [Fact]
    public async Task PirateWeather_Daily_ShouldMapHighLowAndSunTimes()
    {
        var handler = new StubHandler(new()
        {
            ["geocoding-api.open-meteo.com"] = GeocodingJson,
            ["api.pirateweather.net"] = PirateDailyJson,
        });

        var daily = (await PirateProvider(handler).GetForecastAsync("上海", 3)).ToList();

        Assert.Single(daily);
        Assert.Equal(25.4, daily[0].TempHigh, 1);
        Assert.Equal(15.1, daily[0].TempLow, 1);
        Assert.Equal(10, daily[0].PrecipitationProbability);
        Assert.Matches(@"^\d{2}:\d{2}$", daily[0].Sunrise);
        Assert.Matches(@"^\d{2}:\d{2}$", daily[0].Sunset);
    }

    [Fact]
    public async Task PirateWeather_UnknownCity_ShouldFailWithReadableMessage()
    {
        // 地理编码返回空 results：应该抛"无法解析坐标"，而不是 NullReferenceException
        var handler = new StubHandler(new()
        {
            ["geocoding-api.open-meteo.com"] = """{"generationtime_ms":0.1}""",
            ["api.pirateweather.net"] = PirateCurrentJson,
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PirateProvider(handler).GetCurrentAsync("不存在的地方"));

        Assert.Contains("坐标", ex.Message);
    }

    /// <summary>按 URL 路径关键字路由，用于同一 host 上有多段请求的场景。</summary>
    private sealed class PathRoutingHandler : HttpMessageHandler
    {
        private readonly (string Fragment, string Body)[] _routes;

        public PathRoutingHandler((string, string)[] routes)
        {
            _routes = routes;
        }

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);

            foreach (var (fragment, body) in _routes)
            {
                if (url.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
