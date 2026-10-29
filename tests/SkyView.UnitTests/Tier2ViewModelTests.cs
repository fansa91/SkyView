using Moq;
using SkyView.Models;
using SkyView.Services;
using SkyView.ViewModels;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace SkyView.UnitTests;

/// <summary>
/// tier-2 分钟级降雨在 ViewModel 层的派生测试。
/// <para>
/// 关键语义：<see cref="WeatherCurrent.MinutelyRain"/> 为 null = 数据源不提供（wttr.in 等），
/// 整行隐藏；非 null 且无雨 = 有数据的结论，显示"未来 2 小时无雨"。
/// 把前者显示成"无数据"或把后者藏掉，都是把两种状态混为一谈。
/// </para>
/// </summary>
public class Tier2ViewModelTests
{
    private static WeatherViewModel VmWith(WeatherCurrent current)
    {
        var mock = new Mock<IWeatherService>();
        mock.Setup(s => s.GetCurrentWithFallbackAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(current);
        mock.Setup(s => s.GetForecastWithFallbackAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WeatherForecast>());
        mock.Setup(s => s.GetHourlyWithFallbackAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WeatherHourly>());
        mock.Setup(s => s.GetLifeIndexWithFallbackAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WeatherLifeIndex>());

        return new WeatherViewModel(mock.Object) { City = "上海" };
    }

    [Fact]
    public async Task MinutelyRain_ShouldExposeSummary_WhenSourceProvidesIt()
    {
        var vm = VmWith(new WeatherCurrent
        {
            City = "上海",
            Temperature = 24,
            Condition = "多云",
            MinutelyRain = new MinutelyRainInfo
            {
                MinutesUntilRain = 45,
                RainDurationMinutes = 60,
                MaxProbability = 80,
            },
        });
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.HasMinutelyRain);
        Assert.Equal("短临降雨：约 45 分钟后开始下雨，持续约 60 分钟", vm.MinutelyRainText);
    }

    [Fact]
    public async Task MinutelyRain_ShouldShowNoRainConclusion_WhenDataPresent()
    {
        var vm = VmWith(new WeatherCurrent
        {
            City = "上海",
            Temperature = 24,
            Condition = "晴",
            MinutelyRain = new MinutelyRainInfo
            {
                MinutesUntilRain = -1,
                RainDurationMinutes = 0,
                MaxProbability = 20,
            },
        });
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.HasMinutelyRain);
        Assert.Equal("短临降雨：未来 2 小时无雨", vm.MinutelyRainText);
    }

    [Fact]
    public async Task MinutelyRain_ShouldBeHidden_WhenSourceLacksIt()
    {
        var vm = VmWith(new WeatherCurrent { City = "上海", Temperature = 24, Condition = "晴" });
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(vm.HasMinutelyRain);
        Assert.Equal(string.Empty, vm.MinutelyRainText);
    }

    [Fact]
    public async Task ForecastTitle_ShouldReflectActualDayCount()
    {
        // 15 天预报时标题必须是「未来 15 天」，不能停留在写死的「未来几天」
        var days = new List<WeatherForecast>();
        for (var i = 0; i < 15; i++)
            days.Add(new WeatherForecast { Date = DateTimeOffset.Now.Date.AddDays(i) });

        var mock = new Mock<IWeatherService>();
        mock.Setup(s => s.GetCurrentWithFallbackAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WeatherCurrent { City = "上海", Temperature = 24 });
        mock.Setup(s => s.GetForecastWithFallbackAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(days);
        mock.Setup(s => s.GetHourlyWithFallbackAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WeatherHourly>());
        mock.Setup(s => s.GetLifeIndexWithFallbackAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WeatherLifeIndex>());

        var vm = new WeatherViewModel(mock.Object) { City = "上海" };
        Assert.Equal("未来几天", vm.ForecastTitleText); // 刷新前

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(15, vm.Forecast.Count);
        Assert.Equal("未来 15 天", vm.ForecastTitleText);
    }
}
