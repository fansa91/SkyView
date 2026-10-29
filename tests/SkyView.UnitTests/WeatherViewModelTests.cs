using Moq;
using Microsoft.Extensions.Configuration;
using SkyView.Models;
using SkyView.Services;
using SkyView.ViewModels;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace SkyView.UnitTests;

public class WeatherViewModelTests
{
    private static Mock<IWeatherService> CreateServiceMock(WeatherCurrent current)
    {
        var mock = new Mock<IWeatherService>();
        mock.Setup(s => s.GetCurrentWithFallbackAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(current);
        mock.Setup(s => s.GetForecastWithFallbackAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WeatherForecast>());
        return mock;
    }

    [Fact]
    public async Task RefreshAsync_ShouldSetCurrent_WhenServiceSucceeds()
    {
        // Arrange
        var mockService = CreateServiceMock(new WeatherCurrent
        {
            City = "北京",
            Temperature = 22.5,
            Condition = "晴",
            Source = "QWeather",
        });

        var vm = new WeatherViewModel(mockService.Object) { City = "北京" };

        // Act
        await vm.RefreshCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal("北京", vm.Current.City);
        Assert.Equal(22.5, vm.Current.Temperature);
        Assert.Equal("晴", vm.Current.Condition);
        Assert.Equal("QWeather", vm.Current.Source);
        Assert.False(vm.IsLoading);
        Assert.Equal(string.Empty, vm.ErrorMessage);
        Assert.True(vm.HasLoaded);
    }

    [Fact]
    public async Task RefreshAsync_ShouldSetError_WhenServiceThrows()
    {
        // Arrange
        var mockService = new Mock<IWeatherService>();
        mockService.Setup(s => s.GetCurrentWithFallbackAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("所有天气数据源均不可用"));

        var vm = new WeatherViewModel(mockService.Object) { City = "北京" };

        // Act
        await vm.RefreshCommand.ExecuteAsync(null);

        // Assert
        Assert.False(vm.IsLoading);
        Assert.NotEqual(string.Empty, vm.ErrorMessage);
        Assert.Contains("不可用", vm.ErrorMessage);
        Assert.True(vm.HasError);
        Assert.False(vm.HasLoaded);
    }

    [Fact]
    public async Task RefreshAsync_ShouldKeepCurrentWeather_WhenForecastFails()
    {
        // Arrange
        var mockService = CreateServiceMock(new WeatherCurrent { City = "北京", Temperature = 20, Condition = "多云" });
        mockService.Setup(s => s.GetForecastWithFallbackAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("预报不可用"));

        var vm = new WeatherViewModel(mockService.Object) { City = "北京" };

        // Act
        await vm.RefreshCommand.ExecuteAsync(null);

        // Assert：预报失败属于降级，不应污染当前天气与错误提示
        Assert.Equal(20, vm.Current.Temperature);
        Assert.Equal(string.Empty, vm.ErrorMessage);
        Assert.Empty(vm.Forecast);
        Assert.False(vm.HasForecast);
    }

    [Fact]
    public async Task RefreshAsync_ShouldFillForecast_WhenServiceReturnsData()
    {
        var mockService = CreateServiceMock(new WeatherCurrent { City = "北京", Temperature = 20 });
        mockService.Setup(s => s.GetForecastWithFallbackAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WeatherForecast>
            {
                new() { Date = System.DateTimeOffset.Now, TempHigh = 25, TempLow = 15, Condition = "晴" },
            });

        var vm = new WeatherViewModel(mockService.Object) { City = "北京" };

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Single(vm.Forecast);
        Assert.True(vm.HasForecast);
    }

    [Fact]
    public void Constructor_ShouldUseDefaultCityFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:DefaultCity"] = "杭州",
                ["App:ForecastDays"] = "5",
            })
            .Build();

        var vm = new WeatherViewModel(CreateServiceMock(new WeatherCurrent()).Object, config);

        Assert.Equal("杭州", vm.City);
    }

    [Fact]
    public async Task RefreshAsync_ShouldBeDisabled_WhileLoading()
    {
        var gate = new TaskCompletionSource<WeatherCurrent>();
        var mockService = new Mock<IWeatherService>();
        mockService.Setup(s => s.GetCurrentWithFallbackAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(gate.Task);

        var vm = new WeatherViewModel(mockService.Object) { City = "北京" };

        var running = vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.IsLoading);
        Assert.False(vm.RefreshCommand.CanExecute(null));

        gate.SetResult(new WeatherCurrent { City = "北京" });
        await running;

        Assert.False(vm.IsLoading);
        Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public void Constructor_ShouldStartInEmptyState_WithoutFakeWeather()
    {
        // Current 在构造时是默认实例（温度 0），若把 HasCurrent 当 true，
        // 界面会显示 0°C 这种假数据。
        var vm = new WeatherViewModel(CreateServiceMock(new WeatherCurrent()).Object);

        Assert.False(vm.HasCurrent);
        Assert.False(vm.HasError);
        Assert.True(vm.IsEmptyState);
    }

    [Fact]
    public async Task RefreshAsync_ShouldLeaveEmptyState_WhenSucceeds()
    {
        var vm = new WeatherViewModel(CreateServiceMock(new WeatherCurrent
        {
            City = "北京",
            Temperature = 21,
        }).Object);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.HasCurrent);
        Assert.False(vm.IsEmptyState);
    }

    [Fact]
    public async Task RefreshAsync_ShouldShowErrorInsteadOfEmptyHint_WhenServiceThrows()
    {
        var mockService = new Mock<IWeatherService>();
        mockService.Setup(s => s.GetCurrentWithFallbackAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("所有天气数据源均不可用"));

        var vm = new WeatherViewModel(mockService.Object);

        await vm.RefreshCommand.ExecuteAsync(null);

        // 有错误时不该再提示"还没有数据"，否则界面上会同时出现两条互相矛盾的文案
        Assert.False(vm.HasCurrent);
        Assert.True(vm.HasError);
        Assert.False(vm.IsEmptyState);
    }

    [Fact]
    public async Task RefreshAsync_ShouldKeepPreviousWeather_WhenLaterRefreshFails()
    {
        var shouldFail = false;
        var mockService = new Mock<IWeatherService>();
        mockService.Setup(s => s.GetCurrentWithFallbackAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => shouldFail
                ? throw new InvalidOperationException("数据源不可用")
                : new WeatherCurrent { City = "北京", Temperature = 18 });

        var vm = new WeatherViewModel(mockService.Object);

        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.HasCurrent);

        shouldFail = true;
        await vm.RefreshCommand.ExecuteAsync(null);

        // 刷新失败不清空已有数据，避免界面闪成空白
        Assert.True(vm.HasCurrent);
        Assert.Equal(18, vm.Current.Temperature);
        Assert.True(vm.HasError);
    }

    // ---------- 离线缓存 ----------

    [Fact]
    public void Constructor_ShouldRestoreFromCache_AndMarkAsCached()
    {
        var cachePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sv-cache-{System.Guid.NewGuid():N}.json");
        try
        {
            var cache = new WeatherCache(cachePath);
            cache.Save(new WeatherSnapshot
            {
                City = "上海",
                Current = new WeatherCurrent { City = "上海", Temperature = 19.5, Condition = "多云" },
                Forecast = [new WeatherForecast { TempHigh = 22, TempLow = 15, Condition = "多云" }],
                SavedAt = System.DateTimeOffset.Now,
            });

            var vm = new WeatherViewModel(CreateServiceMock(new WeatherCurrent()).Object, null, cache);

            // 有缓存时应该立刻有数据可显示，而不是空白
            Assert.True(vm.HasCurrent);
            Assert.True(vm.IsFromCache);
            Assert.Equal("上海", vm.City);
            Assert.Equal(19.5, vm.Current.Temperature);
            Assert.True(vm.HasForecast);
            Assert.Contains("缓存", vm.CacheHint);
        }
        finally
        {
            if (System.IO.File.Exists(cachePath))
                System.IO.File.Delete(cachePath);
        }
    }

    [Fact]
    public async Task RefreshAsync_ShouldClearCacheFlag_WhenSucceeds()
    {
        var cachePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sv-cache-{System.Guid.NewGuid():N}.json");
        try
        {
            var cache = new WeatherCache(cachePath);
            cache.Save(new WeatherSnapshot
            {
                City = "北京",
                Current = new WeatherCurrent { City = "北京", Temperature = 10 },
                SavedAt = System.DateTimeOffset.Now,
            });

            var vm = new WeatherViewModel(CreateServiceMock(new WeatherCurrent
            {
                City = "北京",
                Temperature = 26,
            }).Object, null, cache);

            Assert.True(vm.IsFromCache);

            await vm.RefreshCommand.ExecuteAsync(null);

            Assert.False(vm.IsFromCache);
            Assert.Equal(26, vm.Current.Temperature);
        }
        finally
        {
            if (System.IO.File.Exists(cachePath))
                System.IO.File.Delete(cachePath);
        }
    }

    // ---------- 温度单位 ----------

    [Fact]
    public async Task ApplyUnit_ShouldSwitchDisplayedTextToFahrenheit()
    {
        var vm = new WeatherViewModel(CreateServiceMock(new WeatherCurrent
        {
            City = "北京",
            Temperature = 22.5,
            FeelsLike = 22.5,
        }).Object);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("22.5°C", vm.TemperatureText);

        vm.ApplyUnit(true, persist: false);

        Assert.True(vm.UseFahrenheit);
        Assert.Equal("72.5°F", vm.TemperatureText);
        Assert.Equal("72.5°F", vm.FeelsLikeText);
        Assert.Equal("°F", vm.UnitSymbol);

        // 切回去
        vm.ApplyUnit(false, persist: false);
        Assert.Equal("22.5°C", vm.TemperatureText);
    }

    [Fact]
    public void HasLifeIndex_ShouldRespectShowLifeIndexPreference()
    {
        var vm = new WeatherViewModel(CreateServiceMock(new WeatherCurrent()).Object);

        vm.LifeIndex.Add(new WeatherLifeIndex { Name = "穿衣指数", Level = "舒适" });
        vm.ApplyPreferences(new AppPreferences { ShowLifeIndex = false });

        Assert.False(vm.HasLifeIndex);

        vm.ApplyPreferences(new AppPreferences { ShowLifeIndex = true });
        Assert.True(vm.HasLifeIndex);
    }
}
