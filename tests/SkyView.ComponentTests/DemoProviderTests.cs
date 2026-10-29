using SkyView.Models;
using SkyView.Providers;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace SkyView.ComponentTests;

/// <summary>
/// 演示数据源要覆盖全部可选能力（逐小时 / 空气质量 / 生活指数），
/// 否则界面上的区块在演示模式下不渲染，UI 自动化测试就无从断言。
/// </summary>
public class DemoProviderTests
{
    private static DemoProvider Create() =>
        new(new HttpClient(), new ProviderConfig { Name = "Demo", Enabled = true });

    [Fact]
    public async Task ShouldReturnCurrentWeather()
    {
        var current = await Create().GetCurrentAsync("北京");

        Assert.Equal("北京", current.City);
        Assert.True(current.Temperature > 0);
        Assert.True(current.HasAirQuality);
        Assert.True(current.HasWind);
        Assert.True(current.HasSunTimes);
    }

    [Fact]
    public async Task ShouldReturnForecast()
    {
        var forecast = (await Create().GetForecastAsync("北京", 5)).ToList();

        Assert.Equal(5, forecast.Count);
        Assert.All(forecast, f => Assert.False(string.IsNullOrEmpty(f.Condition)));
    }

    [Fact]
    public async Task ShouldReturnHourly_ThroughProviderInterface()
    {
        // 关键点：这些方法是接口的默认实现，必须确认改写的版本真的被调用到
        IWeatherProvider provider = Create();

        var hourly = (await provider.GetHourlyAsync("北京", 12)).ToList();

        Assert.Equal(12, hourly.Count);
        Assert.All(hourly, h => Assert.False(string.IsNullOrEmpty(h.TimeText)));
    }

    [Fact]
    public async Task ShouldReturnAirQuality_ThroughProviderInterface()
    {
        IWeatherProvider provider = Create();

        var air = await provider.GetAirQualityAsync("北京");

        Assert.NotNull(air);
        Assert.True(air!.Aqi > 0);
        Assert.False(string.IsNullOrEmpty(air.Category));
    }

    [Fact]
    public async Task ShouldReturnLifeIndex_ThroughProviderInterface()
    {
        IWeatherProvider provider = Create();

        var items = (await provider.GetLifeIndexAsync("北京")).ToList();

        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.False(string.IsNullOrEmpty(i.Name)));
    }

    [Fact]
    public async Task TestConnection_ShouldSucceed()
    {
        // TestConnectionAsync 只有接口上的默认实现，必须通过接口调用
        IWeatherProvider provider = Create();

        var result = await provider.TestConnectionAsync("北京");

        Assert.True(result.Success, result.Message);
        Assert.Contains("连接成功", result.Message);
    }
}
