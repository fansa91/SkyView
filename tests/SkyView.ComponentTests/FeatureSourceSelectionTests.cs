using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SkyView.Models;
using SkyView.Providers;
using SkyView.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SkyView.ComponentTests;

/// <summary>
/// 按功能分配数据源（参考 Breezy Weather 的 per-feature source 模型）：
/// 指派的源排在该功能尝试链最前面；失败回落自动链；未指派 = 自动。
/// </summary>
public class FeatureSourceSelectionTests
{
    private static WeatherProviderFactory CreateFactory() =>
        new(new ConfigurationBuilder().Build(), Mock.Of<ISecureConfigStore>());

    private static WeatherService CreateService(
        WeatherProviderFactory factory,
        IPreferencesStore? preferences = null) =>
        new(factory, new MemoryCache(new MemoryCacheOptions()), NullLogger<WeatherService>.Instance, null, preferences);

    /// <summary>能返回当前/预报/逐小时/生活指数的假数据源，按名字打标。</summary>
    private static Mock<IWeatherProvider> WorkingProvider(string name)
    {
        var mock = new Mock<IWeatherProvider>();
        mock.Setup(p => p.Name).Returns(name);
        mock.Setup(p => p.GetCurrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WeatherCurrent { City = "北京", Temperature = 20, Source = name });
        mock.Setup(p => p.GetForecastAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WeatherForecast> { new() { Condition = "晴", TempHigh = 30, TempLow = 20 } });
        mock.Setup(p => p.GetHourlyAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WeatherHourly> { new() { Time = DateTimeOffset.Now.AddHours(1), Temperature = 21, Condition = "晴" } });
        mock.Setup(p => p.GetLifeIndexAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WeatherLifeIndex> { new() { Name = "运动", Level = "适宜" } });
        return mock;
    }

    private static Mock<IWeatherProvider> FailingProvider(string name)
    {
        var mock = new Mock<IWeatherProvider>();
        mock.Setup(p => p.Name).Returns(name);
        mock.Setup(p => p.GetCurrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        mock.Setup(p => p.GetForecastAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        mock.Setup(p => p.GetHourlyAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        return mock;
    }

    /// <summary>把分配写进一个临时 preferences.json，返回 store。</summary>
    private static IPreferencesStore PreferencesWith(params (string Feature, string Source)[] assignments)
    {
        var path = Path.Combine(Path.GetTempPath(), $"skyview-prefs-{Guid.NewGuid():N}.json");
        var store = new PreferencesStore(path);
        var prefs = new AppPreferences();
        foreach (var (feature, source) in assignments)
            prefs.FeatureSources[feature] = source;
        store.Save(prefs);
        return store;
    }

    [Fact]
    public async Task AssignedSource_ShouldServeOnlyItsFeature_OthersStayAuto()
    {
        var factory = CreateFactory();
        // 链上顺序：Alpha 在前（自动时会服务所有功能），Beta 在后
        factory.ReplaceProviders([WorkingProvider("Alpha").Object, WorkingProvider("Beta").Object]);

        // 只把「预报」指派给 Beta
        var service = CreateService(factory, PreferencesWith((WeatherFeature.Forecast, "Beta")));

        var current = await service.GetCurrentWithFallbackAsync("北京");
        var forecast = (await service.GetForecastWithFallbackAsync("北京", 3)).ToList();

        // 当前天气没有指派 → 仍走链首 Alpha；预报被指派 → Beta
        Assert.Equal("Alpha", current.Source);
        Assert.Equal("Beta", forecast[0].Source);
    }

    [Fact]
    public async Task AssignedSource_ShouldServeHourlyAndLifeIndex_WhenAssigned()
    {
        var factory = CreateFactory();
        factory.ReplaceProviders([WorkingProvider("Alpha").Object, WorkingProvider("Beta").Object]);

        var service = CreateService(factory, PreferencesWith(
            (WeatherFeature.Hourly, "Beta"),
            (WeatherFeature.LifeIndex, "Beta")));

        var hourly = (await service.GetHourlyWithFallbackAsync("北京", 12)).ToList();
        var life = (await service.GetLifeIndexWithFallbackAsync("北京")).ToList();

        Assert.Equal("Beta", hourly[0].Source);
        Assert.Equal("运动", life[0].Name);
    }

    [Fact]
    public async Task AssignedSourceFails_ShouldFallBackToPriorityChain()
    {
        var factory = CreateFactory();
        // 指派的 Broken 在最前面，但它抛异常 → 应回落到 Alpha
        factory.ReplaceProviders([FailingProvider("Broken").Object, WorkingProvider("Alpha").Object]);

        var service = CreateService(factory, PreferencesWith((WeatherFeature.Current, "Broken")));

        var current = await service.GetCurrentWithFallbackAsync("北京");

        Assert.Equal("Alpha", current.Source);
    }

    [Fact]
    public async Task AssignedSourceNotEnabled_ShouldBehaveLikeAuto()
    {
        var factory = CreateFactory();
        factory.ReplaceProviders([WorkingProvider("Alpha").Object, WorkingProvider("Beta").Object]);

        // 指派了一个不在启用列表里的源（被停用/拼错）→ 等同自动
        var service = CreateService(factory, PreferencesWith((WeatherFeature.Current, "Ghost")));

        var current = await service.GetCurrentWithFallbackAsync("北京");

        Assert.Equal("Alpha", current.Source);
    }

    [Fact]
    public async Task NoAssignment_ShouldKeepAutoBehavior()
    {
        var factory = CreateFactory();
        factory.ReplaceProviders([WorkingProvider("Alpha").Object, WorkingProvider("Beta").Object]);

        var service = CreateService(factory, PreferencesWith());

        var current = await service.GetCurrentWithFallbackAsync("北京");
        var forecast = (await service.GetForecastWithFallbackAsync("北京", 3)).ToList();

        Assert.Equal("Alpha", current.Source);
        Assert.Equal("Alpha", forecast[0].Source);
    }

    [Fact]
    public void FeatureSources_ShouldRoundTripThroughPreferencesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"skyview-prefs-{Guid.NewGuid():N}.json");
        var store = new PreferencesStore(path);

        store.Save(new AppPreferences
        {
            FeatureSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [WeatherFeature.Current] = "OpenMeteo",
                [WeatherFeature.Forecast] = "Wttr",
            },
        });

        var loaded = store.Load();

        Assert.Equal("OpenMeteo", loaded.SourceFor(WeatherFeature.Current));
        Assert.Equal("Wttr", loaded.SourceFor(WeatherFeature.Forecast));
        Assert.Null(loaded.SourceFor(WeatherFeature.Hourly)); // 没指派 → null（自动）
    }

    [Fact]
    public async Task ChangingAssignedSource_ShouldBypassFreshCache()
    {
        var factory = CreateFactory();
        factory.ReplaceProviders([WorkingProvider("Alpha").Object, WorkingProvider("Beta").Object]);

        var path = Path.Combine(Path.GetTempPath(), $"skyview-prefs-{Guid.NewGuid():N}.json");
        var store = new PreferencesStore(path);
        var service = CreateService(factory, store);

        // 第一轮：自动 → Alpha 服务预报并写入缓存（TTL 10 分钟内有效）
        var first = (await service.GetForecastWithFallbackAsync("北京", 3)).ToList();
        Assert.Equal("Alpha", first[0].Source);

        // 缓存仍然新鲜，但用户刚把预报指派改成了 Beta → 必须换源，不能吃旧缓存
        store.Save(new AppPreferences { FeatureSources = { [WeatherFeature.Forecast] = "Beta" } });

        var second = (await service.GetForecastWithFallbackAsync("北京", 3)).ToList();
        Assert.Equal("Beta", second[0].Source);
    }

    [Fact]
    public async Task ChangingEnabledChain_ShouldBypassFreshCache()
    {
        var factory = CreateFactory();
        factory.ReplaceProviders([WorkingProvider("Alpha").Object, WorkingProvider("Beta").Object]);

        var service = CreateService(factory, PreferencesWith());

        var first = await service.GetCurrentWithFallbackAsync("北京");
        Assert.Equal("Alpha", first.Source);

        // Alpha 被停用（启用链变化）→ 缓存键随之变化，不能吃到 Alpha 的旧缓存
        factory.ReplaceProviders([WorkingProvider("Beta").Object]);

        var second = await service.GetCurrentWithFallbackAsync("北京");
        Assert.Equal("Beta", second.Source);
    }
}
