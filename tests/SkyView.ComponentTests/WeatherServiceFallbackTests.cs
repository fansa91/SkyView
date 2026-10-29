using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SkyView.Models;
using SkyView.Providers;
using SkyView.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SkyView.ComponentTests;

public class WeatherServiceFallbackTests
{
    private static WeatherProviderFactory CreateFactory() =>
        new(new ConfigurationBuilder().Build(), Mock.Of<ISecureConfigStore>());

    private static WeatherService CreateService(WeatherProviderFactory factory) =>
        new(factory, new MemoryCache(new MemoryCacheOptions()), NullLogger<WeatherService>.Instance);

    private static Mock<IWeatherProvider> FailingProvider(string name)
    {
        var mock = new Mock<IWeatherProvider>();
        mock.Setup(p => p.Name).Returns(name);
        mock.Setup(p => p.GetCurrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        mock.Setup(p => p.GetForecastAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        return mock;
    }

    [Fact]
    public async Task GetCurrentWithFallbackAsync_ShouldReturnFirstSuccessfulProvider()
    {
        // Arrange
        var factory = CreateFactory();

        var successProvider = new Mock<IWeatherProvider>();
        successProvider.Setup(p => p.Name).Returns("SuccessProvider");
        successProvider.Setup(p => p.GetCurrentAsync("北京", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WeatherCurrent { City = "北京", Temperature = 25, Source = "SuccessProvider" });

        factory.ReplaceProviders([FailingProvider("FailingProvider").Object, successProvider.Object]);

        var service = CreateService(factory);

        // Act
        var result = await service.GetCurrentWithFallbackAsync("北京");

        // Assert
        Assert.Equal("SuccessProvider", result.Source);
        Assert.Equal(25, result.Temperature);
    }

    [Fact]
    public async Task GetCurrentWithFallbackAsync_ShouldThrowHelpfulMessage_WhenNoProviderEnabled()
    {
        var factory = CreateFactory();
        factory.ReplaceProviders([]);

        var service = CreateService(factory);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetCurrentWithFallbackAsync("北京"));

        Assert.Contains("没有启用任何天气数据源", ex.Message);
    }

    [Fact]
    public async Task GetCurrentWithFallbackAsync_ShouldIncludeProviderErrors_WhenAllFail()
    {
        var factory = CreateFactory();
        factory.ReplaceProviders([FailingProvider("ProviderA").Object, FailingProvider("ProviderB").Object]);

        var service = CreateService(factory);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetCurrentWithFallbackAsync("北京"));

        Assert.Contains("所有天气数据源均不可用", ex.Message);
        Assert.Contains("ProviderA", ex.Message);
        Assert.Contains("ProviderB", ex.Message);
    }

    [Fact]
    public async Task GetCurrentWithFallbackAsync_ShouldServeSecondCallFromCache()
    {
        var factory = CreateFactory();

        var provider = new Mock<IWeatherProvider>();
        provider.Setup(p => p.Name).Returns("Cached");
        provider.Setup(p => p.GetCurrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WeatherCurrent { City = "北京", Temperature = 18 });

        factory.ReplaceProviders([provider.Object]);
        var service = CreateService(factory);

        await service.GetCurrentWithFallbackAsync("北京");
        await service.GetCurrentWithFallbackAsync("北京");

        provider.Verify(p => p.GetCurrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetForecastWithFallbackAsync_ShouldFallbackToNextProvider()
    {
        var factory = CreateFactory();

        var good = new Mock<IWeatherProvider>();
        good.Setup(p => p.Name).Returns("Good");
        good.Setup(p => p.GetForecastAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WeatherForecast> { new() { Condition = "晴", TempHigh = 30, TempLow = 20 } });

        factory.ReplaceProviders([FailingProvider("Broken").Object, good.Object]);
        var service = CreateService(factory);

        var forecast = await service.GetForecastWithFallbackAsync("北京", 3);

        var list = new List<WeatherForecast>(forecast);
        Assert.Single(list);
        Assert.Equal("晴", list[0].Condition);
    }

    [Fact]
    public async Task GetCurrentWithFallbackAsync_ShouldRejectEmptyCity()
    {
        var service = CreateService(CreateFactory());

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetCurrentWithFallbackAsync("   "));
    }
}
