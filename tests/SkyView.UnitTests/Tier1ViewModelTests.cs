using Moq;
using SkyView.Models;
using SkyView.Services;
using SkyView.ViewModels;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace SkyView.UnitTests;

/// <summary>
/// tier-1 三件套（UV 徽标 / 详细指标行 / 气压趋势）在 ViewModel 层的派生与联动测试。
/// <para>
/// 两个关键行为：
/// </para>
/// <list type="bullet">
/// <item>数据源不提供的字段 → 派生文本为空串、Has* 为 false（界面自动隐藏，不显示"0 mm"假数据）；</item>
/// <item>露点是温度 → 单位切换后文本必须重算（2026-10-29 主页大字温度踩过
/// "x:Bind + 静态转换器不重求值"的坑，这里钉死通知链）。</item>
/// </list>
/// </summary>
public class Tier1ViewModelTests
{
    private static WeatherViewModel VmWith(WeatherCurrent current, List<WeatherHourly>? hourly = null)
    {
        var mock = new Mock<IWeatherService>();
        mock.Setup(s => s.GetCurrentWithFallbackAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(current);
        mock.Setup(s => s.GetForecastWithFallbackAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WeatherForecast>());
        mock.Setup(s => s.GetHourlyWithFallbackAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(hourly ?? new List<WeatherHourly>());
        mock.Setup(s => s.GetLifeIndexWithFallbackAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WeatherLifeIndex>());

        return new WeatherViewModel(mock.Object) { City = "上海" };
    }

    private static WeatherCurrent Full() => new()
    {
        City = "上海",
        Temperature = 24.6,
        Condition = "多云",
        Source = "OpenMeteo",
        Pressure = 1016.4,
        UvIndex = 3.6,
        Precipitation = 0.4,
        CloudCover = 42,
        DewPoint = 15.2,
        WindGusts = 24.7,
    };

    [Fact]
    public async Task UvBadge_ShouldExposeTextAndLevel_WhenSourceProvidesUv()
    {
        var vm = VmWith(Full());
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.HasUvIndex);
        Assert.Equal("UV 4 · 中等", vm.UvIndexText);
        Assert.Equal(2, vm.UvLevel); // 3.6 → 中等档
    }

    [Fact]
    public async Task DetailMetrics_ShouldExposeAllTexts_WhenSourceProvidesThem()
    {
        var vm = VmWith(Full());
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.HasDetailMetrics);
        Assert.True(vm.HasPrecipitationMetric);
        Assert.Equal("0.4 mm", vm.PrecipitationText);
        Assert.True(vm.HasCloudCoverMetric);
        Assert.Equal("42 %", vm.CloudCoverText);
        Assert.True(vm.HasDewPointMetric);
        Assert.Equal("15.2°C", vm.DewPointText);
        Assert.True(vm.HasWindGustsMetric);
        Assert.Equal("24.7 km/h", vm.WindGustsText);
    }

    [Fact]
    public async Task DetailMetrics_ShouldAllBeHidden_WhenSourceLacksThem()
    {
        var vm = VmWith(new WeatherCurrent { City = "上海", Temperature = 20, Condition = "晴" });
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(vm.HasDetailMetrics);
        Assert.False(vm.HasUvIndex);
        Assert.Equal(string.Empty, vm.UvIndexText);
        Assert.Equal(string.Empty, vm.PrecipitationText);
        Assert.Equal(string.Empty, vm.DewPointText);
        Assert.Equal(string.Empty, vm.WindGustsText);
        Assert.Equal(0, vm.UvLevel);
    }

    [Fact]
    public async Task DewPointText_ShouldReconvert_WhenUnitSwitches()
    {
        var vm = VmWith(Full());
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("15.2°C", vm.DewPointText);

        vm.ApplyUnit(useFahrenheit: true, persist: false);

        // 15.2°C → 59.4°F（温度类文本必须随单位重算，不能停留旧单位）
        Assert.Equal("59.4°F", vm.DewPointText);
        Assert.Equal("0.4 mm", vm.PrecipitationText); // 降水量无单位概念，不受影响
    }

    [Fact]
    public async Task PressureTrend_ShouldComputeFromHourly_AfterRefresh()
    {
        var hourly = new List<WeatherHourly>
        {
            new() { Pressure = 1012.0 },
            new() { Pressure = 1013.2 },
            new() { Pressure = 1013.5 },
        };
        var vm = VmWith(Full(), hourly);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.HasPressureTrend);
        Assert.Equal("▲ 回升", vm.PressureTrendText);
    }

    [Fact]
    public async Task PressureTrend_ShouldBeHidden_WhenHourlyHasNoPressure()
    {
        var hourly = new List<WeatherHourly>
        {
            new() { Pressure = null },
            new() { Pressure = null },
        };
        var vm = VmWith(Full(), hourly);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(vm.HasPressureTrend);
        Assert.Equal(string.Empty, vm.PressureTrendText);
    }
}
