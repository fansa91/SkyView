using SkyView.Models;
using SkyView.Providers;
using System.Net;
using System.Text;
using Xunit;

namespace SkyView.UnitTests;

/// <summary>
/// wttr.in 数据源的解析测试（免 Key，URL 直接接受城市名）。
/// <para>
/// wttr.in 的 j1 JSON 全用**字符串**承载数值（"temp_C":"18"），且天气描述是英文
/// （"Sunny"/"Moderate rain"），要经 <c>MapCondition</c> 归一成中文；16 方位风向
/// （"WNW"）要映射成中文。这些都是"看着对、容易算错"的点，故用 stub 固定响应验证。
/// </para>
/// </summary>
public class WttrProviderTests
{
    /// <summary>真实形状的 wttr.in j1 响应：当前 + 2 天，每天 8 条 3 小时粒度 hourly。</summary>
    private const string WttrJson = """
        {
          "current_condition": [{
            "temp_C": "18", "FeelsLikeC": "11", "humidity": "12", "pressure": "1017",
            "windspeedKmph": "19", "winddir16Point": "WNW",
            "weatherDesc": [{"value": "Sunny"}]
          }],
          "weather": [
            {
              "date": "2026-11-05", "maxtempC": "20", "mintempC": "8",
              "astronomy": [{"sunrise": "06:39 AM", "sunset": "05:17 PM"}],
              "hourly": [
                {"time": "0",    "tempC": "5",  "chanceofrain": "0", "weatherDesc": [{"value": "Clear"}]},
                {"time": "300",  "tempC": "6",  "chanceofrain": "5", "weatherDesc": [{"value": "Clear"}]},
                {"time": "600",  "tempC": "8",  "chanceofrain": "10","weatherDesc": [{"value": "Sunny"}]},
                {"time": "900",  "tempC": "12", "chanceofrain": "20","weatherDesc": [{"value": "Sunny"}]},
                {"time": "1200", "tempC": "16", "chanceofrain": "60","weatherDesc": [{"value": "Moderate rain"}]},
                {"time": "1500", "tempC": "15", "chanceofrain": "40","weatherDesc": [{"value": "Cloudy"}]},
                {"time": "1800", "tempC": "11", "chanceofrain": "15","weatherDesc": [{"value": "Cloudy"}]},
                {"time": "2100", "tempC": "7",  "chanceofrain": "5", "weatherDesc": [{"value": "Clear"}]}
              ]
            },
            {
              "date": "2026-11-06", "maxtempC": "19", "mintempC": "9",
              "astronomy": [{"sunrise": "06:40 AM", "sunset": "05:16 PM"}],
              "hourly": [
                {"time": "0",    "tempC": "9",  "chanceofrain": "10","weatherDesc": [{"value": "Cloudy"}]},
                {"time": "300",  "tempC": "10", "chanceofrain": "10","weatherDesc": [{"value": "Cloudy"}]},
                {"time": "600",  "tempC": "12", "chanceofrain": "20","weatherDesc": [{"value": "Cloudy"}]},
                {"time": "900",  "tempC": "14", "chanceofrain": "30","weatherDesc": [{"value": "Cloudy"}]},
                {"time": "1200", "tempC": "15", "chanceofrain": "40","weatherDesc": [{"value": "Sunny"}]},
                {"time": "1500", "tempC": "13", "chanceofrain": "20","weatherDesc": [{"value": "Sunny"}]},
                {"time": "1800", "tempC": "11", "chanceofrain": "10","weatherDesc": [{"value": "Cloudy"}]},
                {"time": "2100", "tempC": "9",  "chanceofrain": "5", "weatherDesc": [{"value": "Cloudy"}]}
              ]
            }
          ]
        }
        """;

    private static WttrProvider Wttr(string body) => new(
        new HttpClient(new WttrJsonHandler(body)),
        new ProviderConfig { Name = "Wttr", ApiHost = "wttr.in", TimeoutSeconds = 10 });

    private sealed class WttrJsonHandler : HttpMessageHandler
    {
        private readonly string _body;
        public WttrJsonHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = _body;
            var status = HttpStatusCode.OK;
            if (string.IsNullOrEmpty(body))
            {
                status = HttpStatusCode.NotFound;
                body = """{"detail":"Not Found"}""";
            }
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task Wttr_Current_ShouldParseAllFields()
    {
        var current = await Wttr(WttrJson).GetCurrentAsync("北京");

        // 数值都是字符串，验证能正确解析
        Assert.Equal(18, current.Temperature, 1);
        Assert.Equal(11, current.FeelsLike, 1);
        Assert.Equal(12, current.Humidity);
        Assert.Equal(1017, current.Pressure, 1);
        Assert.Equal(19, current.WindSpeed, 1);

        // "Sunny" → 晴；16 方位 WNW → 西北偏西风
        Assert.Equal("晴", current.Condition);
        Assert.Equal("西北偏西风", current.WindDirection);
        Assert.Equal("wttr.in", current.Source);
        Assert.Equal("北京", current.City);
    }

    [Fact]
    public async Task Wttr_Forecast_ShouldMapHighLowMiddayDescAndSunTimes()
    {
        var daily = (await Wttr(WttrJson).GetForecastAsync("北京", 3)).ToList();

        Assert.Equal(2, daily.Count);
        Assert.Equal(20, daily[0].TempHigh, 1);
        Assert.Equal(8, daily[0].TempLow, 1);

        // 当天代表天气应取"中午前后"那条（time=1200 的 Moderate rain），不是 00:00 的 Sunny
        Assert.Equal("雨", daily[0].Condition);

        // wttr.in 的日出日落是 "06:39 AM" / "05:17 PM"，FormatClock 要归一成 HH:mm
        Assert.Matches(@"^\d{2}:\d{2}$", daily[0].Sunrise);
        Assert.Matches(@"^\d{2}:\d{2}$", daily[0].Sunset);
    }

    [Fact]
    public async Task Wttr_Hourly_FutureDates_ShouldReturnAllInOrder()
    {
        // 夹具日期是未来（2026-11-05），所有条目都应保留；2 天 × 8 = 16 条
        var hourly = (await Wttr(WttrJson).GetHourlyAsync("北京", 24)).ToList();

        Assert.Equal(16, hourly.Count);
        Assert.Equal(0, hourly[0].Time.Hour);
        Assert.Equal(5, hourly[0].Temperature, 1);
        Assert.Equal("晴", hourly[0].Condition); // "Clear" → 晴

        // 时间升序
        for (var i = 1; i < hourly.Count; i++)
            Assert.True(hourly[i - 1].Time < hourly[i].Time, "逐小时应按时间升序");

        // 中午那条降水概率 60%、描述 Moderate rain → 雨
        var noon = hourly.Single(h => h.Time.Hour == 12 && h.Time.Day == 5);
        Assert.Equal(60, noon.PrecipitationProbability);
        Assert.Equal("雨", noon.Condition);
    }

    [Fact]
    public async Task Wttr_Hourly_PastDates_ShouldBeFilteredOut()
    {
        // 同结构但日期在过去（2020）→ 全部早于"现在"，应返回空
        var pastJson = WttrJson.Replace("2026-11-05", "2020-01-05").Replace("2026-11-06", "2020-01-06");
        var hourly = (await Wttr(pastJson).GetHourlyAsync("北京", 24)).ToList();

        Assert.Empty(hourly);
    }

    [Fact]
    public async Task Wttr_MissingCurrent_ShouldThrowReadableError()
    {
        var bad = """{"weather":[]}""";
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Wttr(bad).GetCurrentAsync("北京"));
    }

    [Fact]
    public async Task Wttr_BuildUrl_ShouldEncodeCityName()
    {
        var handler = new RecordingHandler(WttrJson);
        var provider = new WttrProvider(new HttpClient(handler), new ProviderConfig { Name = "Wttr" });

        await provider.GetCurrentAsync("上 海");

        // 空格必须转成 %20（URL 里裸空格非法），且必须请求 j1 格式
        var url = handler.LastUrl ?? string.Empty;
        Assert.Contains("%20", url);
        Assert.DoesNotContain(" ", url);
        Assert.Contains("format=j1", url);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _body;
        public RecordingHandler(string body) => _body = body;

        /// <summary>记 <c>AbsoluteUri</c> 而不是 <c>ToString()</c>：后者会把 %20 反转义回裸空格，断言就失去意义。</summary>
        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri?.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
