using SkyView.Models;
using SkyView.Services;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace SkyView.UnitTests;

public class PreferencesStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"skyview-prefs-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    [Fact]
    public void Load_ShouldReturnDefaults_WhenFileMissing()
    {
        var prefs = new PreferencesStore(_path).Load();

        Assert.False(prefs.UseFahrenheit);
        Assert.Equal(30, prefs.AutoRefreshMinutes);
        Assert.Equal(12, prefs.HourlyCount);
        Assert.True(prefs.ShowLifeIndex);
    }

    [Fact]
    public void SaveThenLoad_ShouldRoundTrip()
    {
        var store = new PreferencesStore(_path);

        store.Save(new AppPreferences
        {
            UseFahrenheit = true,
            AutoRefreshMinutes = 45,
            HourlyCount = 8,
            ShowLifeIndex = false,
        });

        var loaded = store.Load();

        Assert.True(loaded.UseFahrenheit);
        Assert.Equal(45, loaded.AutoRefreshMinutes);
        Assert.Equal(8, loaded.HourlyCount);
        Assert.False(loaded.ShowLifeIndex);
    }

    [Fact]
    public void Load_ShouldFallBackToDefaults_WhenFileCorrupt()
    {
        File.WriteAllText(_path, "{ this is not json ]");

        var prefs = new PreferencesStore(_path).Load();

        // 坏文件不应该让应用起不来
        Assert.False(prefs.UseFahrenheit);
        Assert.Equal(30, prefs.AutoRefreshMinutes);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(3, 5)]
    [InlineData(30, 30)]
    [InlineData(999, 240)]
    public void AutoRefreshMinutes_ShouldBeClamped(int input, int expected)
    {
        Assert.Equal(expected, new AppPreferences { AutoRefreshMinutes = input }.NormalizedAutoRefreshMinutes);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(12, 12)]
    [InlineData(100, 24)]
    public void HourlyCount_ShouldBeClamped(int input, int expected)
    {
        Assert.Equal(expected, new AppPreferences { HourlyCount = input }.NormalizedHourlyCount);
    }
}

public class WeatherCacheTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"skyview-cache-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static WeatherSnapshot CreateSnapshot() => new()
    {
        City = "北京",
        Current = new WeatherCurrent
        {
            City = "北京",
            Temperature = 21.5,
            Condition = "晴",
            Humidity = 40,
            Aqi = 55,
            AqiCategory = "良",
            Sunrise = "06:12",
            Sunset = "17:48",
        },
        Forecast = new List<WeatherForecast>
        {
            new() { Date = DateTimeOffset.Now.Date, TempHigh = 25, TempLow = 15, Condition = "晴" },
        },
        Hourly = new List<WeatherHourly>
        {
            new() { Time = DateTimeOffset.Now, Temperature = 21, Condition = "晴" },
        },
        LifeIndex = new List<WeatherLifeIndex>
        {
            new() { Name = "穿衣指数", Level = "舒适" },
        },
        SavedAt = DateTimeOffset.Now,
    };

    [Fact]
    public void Load_ShouldReturnNull_WhenNoCache()
    {
        Assert.Null(new WeatherCache(_path).Load());
    }

    [Fact]
    public void SaveThenLoad_ShouldPreserveAllSections()
    {
        var cache = new WeatherCache(_path);
        cache.Save(CreateSnapshot());

        var loaded = cache.Load();

        Assert.NotNull(loaded);
        Assert.Equal("北京", loaded!.City);
        Assert.Equal(21.5, loaded.Current!.Temperature);
        Assert.Equal(55, loaded.Current.Aqi);
        Assert.Equal("06:12", loaded.Current.Sunrise);
        Assert.Single(loaded.Forecast);
        Assert.Single(loaded.Hourly);
        Assert.Single(loaded.LifeIndex);
        Assert.True(loaded.HasAnyData);
    }

    [Fact]
    public void Load_ShouldReturnNull_WhenFileCorrupt()
    {
        File.WriteAllText(_path, "not json at all");

        Assert.Null(new WeatherCache(_path).Load());
    }

    [Fact]
    public void Clear_ShouldRemoveCacheFile()
    {
        var cache = new WeatherCache(_path);
        cache.Save(CreateSnapshot());
        Assert.True(File.Exists(_path));

        cache.Clear();

        Assert.False(File.Exists(_path));
        Assert.Null(cache.Load());
    }

    [Fact]
    public void Snapshot_ShouldReportAge()
    {
        var snapshot = new WeatherSnapshot { SavedAt = DateTimeOffset.Now.AddMinutes(-10) };

        Assert.True(snapshot.Age.TotalMinutes >= 9.5);
    }
}
