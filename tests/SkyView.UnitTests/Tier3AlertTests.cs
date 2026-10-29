using SkyView.Alerts;
using SkyView.Formatting;
using SkyView.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SkyView.UnitTests;

/// <summary>
/// tier-3 预警推导引擎测试（纯函数）。
/// <para>
/// 阈值行为钉死：降水概率 60/80 分界、高温 35/37 分界、寒潮 -10、阵风 62；
/// 以及三条结构性规则——同日同类去重保高级、按日期近→远排序、总量封顶 5 条。
/// </para>
/// </summary>
public class WeatherAlertEngineTests
{
    private static WeatherForecast Day(int offset, string condition, int pop, double high, double low)
        => new()
        {
            Date = DateTimeOffset.Now.Date.AddDays(offset),
            Condition = condition,
            PrecipitationProbability = pop,
            TempHigh = high,
            TempLow = low,
        };

    [Fact]
    public void NoTriggers_ShouldProduceNoAlerts()
    {
        var forecast = new[]
        {
            Day(0, "晴", 10, 24, 15),
            Day(1, "多云", 20, 26, 16),
            Day(2, "阴", 30, 22, 14),
        };

        Assert.Empty(WeatherAlertEngine.Derive(null, forecast));
    }

    [Fact]
    public void Rain_WithPop60_ShouldBeYellow()
    {
        var alerts = WeatherAlertEngine.Derive(null, [Day(1, "小雨", 60, 21, 13)]);

        var alert = Assert.Single(alerts);
        Assert.Equal("降雨", alert.Kind);
        Assert.Equal(2, alert.Level);
        Assert.Equal("黄色", alert.LevelText);
        Assert.Equal("明天", alert.DateText);
        Assert.Equal("降水概率 60%", alert.Reason);
    }

    [Fact]
    public void Rain_WithPop80_ShouldBeOrange()
    {
        var alerts = WeatherAlertEngine.Derive(null, [Day(0, "大雨", 85, 22, 18)]);

        var alert = Assert.Single(alerts);
        Assert.Equal("暴雨", alert.Kind); // "大雨" → 暴雨
        Assert.Equal(3, alert.Level);
        Assert.Equal("今天", alert.DateText);
    }

    [Fact]
    public void Thunderstorm_ShouldBeItsOwnKind()
    {
        var alerts = WeatherAlertEngine.Derive(null, [Day(2, "雷阵雨", 70, 25, 20)]);

        var alert = Assert.Single(alerts);
        Assert.Equal("雷暴", alert.Kind);
        Assert.Equal(2, alert.Level);
        Assert.Matches(@"^\d{2}-\d{2}$", alert.DateText); // 非今天/明天 → MM-dd 形态
    }

    [Fact]
    public void Snow_ShouldBeSnowKind()
    {
        var alerts = WeatherAlertEngine.Derive(null, [Day(3, "小雪", 65, 2, -3)]);

        var alert = Assert.Single(alerts);
        Assert.Equal("降雪", alert.Kind);
        Assert.Equal(2, alert.Level);
    }

    [Fact]
    public void Heat_35ShouldBeOrange_37ShouldBeRed()
    {
        var orange = WeatherAlertEngine.Derive(null, [Day(0, "晴", 10, 35, 24)]);
        var red = WeatherAlertEngine.Derive(null, [Day(0, "晴", 10, 38, 26)]);

        Assert.Equal(3, Assert.Single(orange).Level);
        Assert.Equal(4, Assert.Single(red).Level);
        Assert.Equal("最高 35°C", orange[0].Reason);
    }

    [Fact]
    public void Cold_BelowMinus10_ShouldAlert()
    {
        var alerts = WeatherAlertEngine.Derive(null, [Day(4, "晴", 5, -5, -12)]);

        var alert = Assert.Single(alerts);
        Assert.Equal("寒潮", alert.Kind);
        Assert.Equal("最低 -12°C", alert.Reason);
    }

    [Fact]
    public void Gusts_62Kmh_ShouldTriggerWindAlert()
    {
        var current = new WeatherCurrent { WindGusts = 65 };
        var alerts = WeatherAlertEngine.Derive(current, [Day(0, "晴", 10, 24, 15)]);

        var alert = Assert.Single(alerts);
        Assert.Equal("大风", alert.Kind);
        Assert.Equal("现在", alert.DateText);
        Assert.Equal("阵风 65 km/h", alert.Reason);
    }

    [Fact]
    public void Gusts_BelowThreshold_ShouldNotTrigger()
    {
        var current = new WeatherCurrent { WindGusts = 61.9 };
        Assert.Empty(WeatherAlertEngine.Derive(current, [Day(0, "晴", 10, 24, 15)]));
    }

    [Fact]
    public void HeatReason_ShouldFollowUnit()
    {
        var forecast = new[] { Day(0, "晴", 10, 36, 24) };

        Assert.Equal("最高 36°C", WeatherAlertEngine.Derive(null, forecast)[0].Reason);
        // 36°C → 96.8°F：单位切换后重推导，Reason 必须跟着变
        Assert.Equal("最高 96.8°F", WeatherAlertEngine.Derive(null, forecast, useFahrenheit: true)[0].Reason);
    }

    [Fact]
    public void ShouldCapAtFive_AndOrderByDate()
    {
        // 8 天全部触发降雨（概率 70）→ 只保留最近的 5 条
        var forecast = Enumerable.Range(0, 8)
            .Select(i => Day(i, "小雨", 70, 21, 13))
            .ToList();

        var alerts = WeatherAlertEngine.Derive(null, forecast);

        Assert.Equal(5, alerts.Count);
        Assert.Equal(alerts.Min(a => a.Date), alerts[0].Date);
        Assert.True(alerts.Select(a => a.Date).SequenceEqual(alerts.Select(a => a.Date).OrderBy(d => d)));
    }

    [Fact]
    public void SameDaySameKind_ShouldKeepHighestLevel()
    {
        // 同一天既有高温又有降雨：两类都留；同类不会重复（单日单 condition 本就一条，
        // 这里验证分组去重逻辑不会把不同 Level 的同 Kind 重复计入）
        var forecast = new[] { Day(0, "大雨", 85, 36, 24) };

        var alerts = WeatherAlertEngine.Derive(null, forecast);

        Assert.Equal(2, alerts.Count); // 暴雨 + 高温
        Assert.Contains(alerts, a => a.Kind == "暴雨" && a.Level == 3);
        Assert.Contains(alerts, a => a.Kind == "高温" && a.Level == 3);
    }

    [Fact]
    public void LevelText_ShouldMatchDomesticColors()
    {
        Assert.Equal("蓝色", WeatherAlertEngine.LevelText(1));
        Assert.Equal("黄色", WeatherAlertEngine.LevelText(2));
        Assert.Equal("橙色", WeatherAlertEngine.LevelText(3));
        Assert.Equal("红色", WeatherAlertEngine.LevelText(4));
        Assert.Equal("蓝色", WeatherAlertEngine.LevelText(0)); // 越界回落
    }
}

/// <summary>
/// tier-3 花粉分级纯函数测试（四级：低/中/高/极高）。
/// </summary>
public class Tier3FormatTests
{
    [Theory]
    [InlineData(0, "低")]
    [InlineData(20, "低")]
    [InlineData(20.1, "中")]
    [InlineData(50, "中")]
    [InlineData(50.1, "高")]
    [InlineData(100, "高")]
    [InlineData(100.1, "极高")]
    public void PollenCategory_Boundaries(double grains, string expected)
        => Assert.Equal(expected, WeatherFormat.PollenCategory(grains));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(20, 1)]
    [InlineData(21, 2)]
    [InlineData(50, 2)]
    [InlineData(51, 3)]
    [InlineData(100, 3)]
    [InlineData(101, 4)]
    public void PollenLevel_Boundaries(double grains, int expected)
        => Assert.Equal(expected, WeatherFormat.PollenLevel(grains));
}
