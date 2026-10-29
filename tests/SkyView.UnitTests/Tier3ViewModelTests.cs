using Moq;
using SkyView.Alerts;
using SkyView.Models;
using SkyView.Services;
using SkyView.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SkyView.UnitTests;

/// <summary>
/// tier-3 在 ViewModel 层的派生测试：花粉文案 / 预警集合生成与联动。
/// <para>
/// 关键行为：
/// </para>
/// <list type="bullet">
/// <item>预警由本地引擎从 Current + Forecast 推导，刷新后自动生成、无触发时不建卡；</item>
/// <item>单位切换要重推导——Reason 里的高低温文本随 °C/°F 变；</item>
/// <item>花粉无数据（国内城市）时整行隐藏，有数据显示结论。</item>
/// </list>
/// </summary>
public class Tier3ViewModelTests
{
    private static WeatherViewModel VmWith(
        WeatherCurrent current,
        List<WeatherForecast>? forecast = null)
    {
        var mock = new Mock<IWeatherService>();
        mock.Setup(s => s.GetCurrentWithFallbackAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(current);
        mock.Setup(s => s.GetForecastWithFallbackAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(forecast ?? new List<WeatherForecast>());
        mock.Setup(s => s.GetHourlyWithFallbackAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WeatherHourly>());
        mock.Setup(s => s.GetLifeIndexWithFallbackAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WeatherLifeIndex>());

        return new WeatherViewModel(mock.Object) { City = "上海" };
    }

    private static WeatherForecast RainDay(int offset, int pop)
        => new()
        {
            Date = DateTimeOffset.Now.Date.AddDays(offset),
            Condition = "小雨",
            PrecipitationProbability = pop,
            TempHigh = 21,
            TempLow = 13,
        };

    [Fact]
    public async Task Alerts_ShouldBeDerivedAfterRefresh_WhenForecastTriggers()
    {
        var vm = VmWith(
            new WeatherCurrent { City = "上海", Temperature = 22, Condition = "多云" },
            new List<WeatherForecast> { RainDay(1, 80) });

        Assert.False(vm.HasAlerts); // 刷新前不预生成

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.HasAlerts);
        var alert = Assert.Single(vm.Alerts);
        Assert.Equal("降雨", alert.Kind);
        Assert.Equal(3, alert.Level); // pop 80 → 橙
        Assert.Equal("明天", alert.DateText);
    }

    [Fact]
    public async Task Alerts_ShouldBeEmpty_WhenNothingTriggers()
    {
        var vm = VmWith(
            new WeatherCurrent { City = "上海", Temperature = 22, Condition = "晴" },
            new List<WeatherForecast> { RainDay(1, 10) });

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(vm.HasAlerts);
        Assert.Empty(vm.Alerts);
    }

    [Fact]
    public async Task Alerts_ReasonShouldReconvert_WhenUnitSwitches()
    {
        var vm = VmWith(
            new WeatherCurrent { City = "上海", Temperature = 22, Condition = "晴" },
            new List<WeatherForecast>
            {
                new()
                {
                    Date = DateTimeOffset.Now.Date,
                    Condition = "晴",
                    PrecipitationProbability = 10,
                    TempHigh = 36,
                    TempLow = 24,
                },
            });

        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("最高 36°C", vm.Alerts.Single().Reason);

        vm.ApplyUnit(useFahrenheit: true, persist: false);

        Assert.Equal("最高 96.8°F", vm.Alerts.Single().Reason);
    }

    [Fact]
    public async Task Pollen_ShouldExposeText_WhenSourceProvidesIt()
    {
        var vm = VmWith(new WeatherCurrent
        {
            City = "上海",
            Temperature = 22,
            Condition = "晴",
            Pollen = new PollenInfo { Grass = 12.5, Tree = 45 },
        });
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.HasPollen);
        Assert.Equal("花粉：中（草 12.5 · 树 45 粒/m³）", vm.PollenText);
    }

    [Fact]
    public async Task Pollen_ShouldBeHidden_WhenSourceLacksIt()
    {
        // 国内城市：Air Quality API 五个花粉变量全 null → Provider 给 null
        var vm = VmWith(new WeatherCurrent { City = "北京", Temperature = 10, Condition = "晴" });
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(vm.HasPollen);
        Assert.Equal(string.Empty, vm.PollenText);
    }

    [Fact]
    public async Task PollenText_ShouldUsePeakOfBothKinds()
    {
        // 树花粉更高时按树分级（45 → 中），草低不拉低结论
        var vm = VmWith(new WeatherCurrent
        {
            City = "慕尼黑",
            Temperature = 12,
            Condition = "多云",
            Pollen = new PollenInfo { Grass = 3, Tree = 45 },
        });
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Contains("花粉：中", vm.PollenText);
    }
}
