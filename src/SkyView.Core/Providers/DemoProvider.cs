using SkyView.Models;

namespace SkyView.Providers;

/// <summary>
/// 演示数据源：返回固定的、结构完整的假数据，不发任何网络请求。
/// <para>
/// 用途有两个：
/// 1. 没有 API Key 时也能演示完整界面（趋势图、逐小时、生活指数、AQI 都要有数据才渲染）；
/// 2. UI 自动化测试需要稳定的、可预期的数据，否则断言无从下手。
/// </para>
/// <para>
/// 只会通过 <c>App:DemoMode=true</c> 或环境变量 <c>SKYVIEW_DEMO=1</c> 启用，
/// 默认不存在，不会污染真实使用。
/// </para>
/// </summary>
public sealed class DemoProvider : WeatherProviderBase
{
    public const string DemoName = "Demo";

    public DemoProvider(HttpClient http, ProviderConfig config) : base(http, config) { }

    protected override string DefaultHost => "demo.invalid";

    public override Task<WeatherCurrent> GetCurrentAsync(string city, CancellationToken ct = default) => Task.FromResult(new WeatherCurrent
    {
        City = string.IsNullOrWhiteSpace(city) ? "北京" : city,
        Temperature = 22.5,
        FeelsLike = 24.1,
        Humidity = 45,
        Pressure = 1013,
        Condition = "晴",
        IconCode = "100",
        ObservedAt = DateTimeOffset.Now,
        Source = DemoName,
        WindDirection = "东南风",
        WindSpeed = 12.4,
        Sunrise = "06:12",
        Sunset = "17:48",
        Aqi = 62,
        AqiCategory = "良",
        PrimaryPollutant = "PM2.5",
        Pm25 = 38,
        UvIndex = 5.2,
        Precipitation = 0.4,
        CloudCover = 45,
        DewPoint = 14.6,
        WindGusts = 21.8,
        // tier 2 演示值：45 分钟后开始下雨、持续 1 小时（与 tier-1 一样，
        // 演示模式要把每个功能都演出来，否则文档截图里功能"不存在"）
        MinutelyRain = new MinutelyRainInfo
        {
            MinutesUntilRain = 45,
            RainDurationMinutes = 60,
            MaxProbability = 80,
        },
        // tier 3 演示值：花粉中等（草 12.5 / 树 45 粒/m³）
        Pollen = new PollenInfo { Grass = 12.5, Tree = 45 },
    });

    public override Task<IEnumerable<WeatherForecast>> GetForecastAsync(string city, int days, CancellationToken ct = default)
    {
        var span = Math.Clamp(days, 1, 15);
        var start = DateTimeOffset.Now.Date;
        double[] highs = [24, 26, 21, 19, 23, 25, 27];
        double[] lows = [14, 16, 13, 11, 12, 15, 17];
        string[] conditions = ["晴", "多云", "雨", "阴", "多云", "晴", "晴"];

        var list = new List<WeatherForecast>();
        for (var i = 0; i < span; i++)
        {
            list.Add(new WeatherForecast
            {
                Date = new DateTimeOffset(start.AddDays(i)),
                TempHigh = highs[i % highs.Length],
                TempLow = lows[i % lows.Length],
                Condition = conditions[i % conditions.Length],
                Sunrise = "06:12",
                Sunset = "17:48",
                PrecipitationProbability = conditions[i % conditions.Length] == "雨" ? 80 : 10,
            });
        }

        return Task.FromResult<IEnumerable<WeatherForecast>>(list);
    }

    public override Task<IEnumerable<WeatherHourly>> GetHourlyAsync(string city, int hours, CancellationToken ct = default)
    {
        var span = Math.Clamp(hours, 1, 24);
        var now = DateTimeOffset.Now;
        // 从下一个整点开始，避免"现在"这一格时间不整
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset).AddHours(1);

        double[] temps = [22, 23, 24, 25, 26, 25, 24, 22, 20, 19, 18, 17];
        string[] conditions = ["晴", "晴", "多云", "多云", "阴", "雨", "雨", "多云", "晴", "晴", "多云", "阴"];
        // 气压缓升后转降，用来演示「气压趋势」箭头
        double[] pressures = [1012.0, 1012.4, 1012.9, 1013.5, 1014.1, 1013.8, 1013.2, 1012.9, 1012.6, 1012.4, 1012.1, 1011.8];

        var list = new List<WeatherHourly>();
        for (var i = 0; i < span; i++)
        {
            var index = i % temps.Length;
            list.Add(new WeatherHourly
            {
                Time = start.AddHours(i),
                Temperature = temps[index],
                Condition = conditions[index],
                PrecipitationProbability = conditions[index] == "雨" ? 70 : (index % 3 == 0 ? 10 : 0),
                Pressure = pressures[index],
            });
        }

        return Task.FromResult<IEnumerable<WeatherHourly>>(list);
    }

    public override Task<AirQuality?> GetAirQualityAsync(string city, CancellationToken ct = default) => Task.FromResult<AirQuality?>(new AirQuality
    {
        Aqi = 62,
        Category = "良",
        PrimaryPollutant = "PM2.5",
        Pm25 = 38,
    });

    public override Task<IEnumerable<WeatherLifeIndex>> GetLifeIndexAsync(string city, CancellationToken ct = default)
    {
        var list = new List<WeatherLifeIndex>
        {
            new() { Name = "穿衣指数", Level = "舒适", Text = "建议着薄外套、开衫牛仔衫裤等服装。" },
            new() { Name = "紫外线", Level = "强", Text = "外出请涂抹防晒霜，戴帽子或太阳镜。" },
            new() { Name = "运动指数", Level = "适宜", Text = "天气较好，适合户外运动，注意补水。" },
            new() { Name = "洗车指数", Level = "较适宜", Text = "未来一天无雨，风力较小，适合洗车。" },
        };

        return Task.FromResult<IEnumerable<WeatherLifeIndex>>(list);
    }
}
