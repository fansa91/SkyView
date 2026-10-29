using SkyView.Formatting;
using Xunit;

namespace SkyView.UnitTests;

public class TemperatureUnitTests
{
    [Fact]
    public void Temperature_ShouldDefaultToCelsius()
    {
        Assert.Equal("22.5°C", WeatherFormat.Temperature(22.5));
    }

    [Fact]
    public void Temperature_ShouldConvertToFahrenheit()
    {
        // 22.5°C = 72.5°F
        Assert.Equal("72.5°F", WeatherFormat.Temperature(22.5, useFahrenheit: true));
    }

    [Theory]
    [InlineData(0, 32)]
    [InlineData(100, 212)]
    [InlineData(-40, -40)]
    public void ToFahrenheit_ShouldMatchKnownPoints(double celsius, double expected)
    {
        Assert.Equal(expected, WeatherFormat.ToFahrenheit(celsius), 3);
    }

    [Fact]
    public void ConvertTemperature_ShouldRespectUnit()
    {
        Assert.Equal(20, WeatherFormat.ConvertTemperature(20, useFahrenheit: false));
        Assert.Equal(68, WeatherFormat.ConvertTemperature(20, useFahrenheit: true), 3);
    }

    [Theory]
    [InlineData(0, "—")]
    [InlineData(35, "优")]
    [InlineData(75, "良")]
    [InlineData(125, "轻度污染")]
    [InlineData(175, "中度污染")]
    [InlineData(250, "重度污染")]
    [InlineData(400, "严重污染")]
    public void AqiCategory_ShouldMapToChineseLevel(int aqi, string expected)
    {
        Assert.Equal(expected, WeatherFormat.AqiCategory(aqi));
    }

    [Fact]
    public void AqiLevel_ShouldIncreaseWithPollution()
    {
        Assert.Equal(0, WeatherFormat.AqiLevel(0));
        Assert.Equal(1, WeatherFormat.AqiLevel(30));
        Assert.Equal(2, WeatherFormat.AqiLevel(90));
        Assert.Equal(6, WeatherFormat.AqiLevel(500));

        // 单调不减
        var previous = -1;
        foreach (var aqi in new[] { 0, 50, 100, 150, 200, 300, 500 })
        {
            var level = WeatherFormat.AqiLevel(aqi);
            Assert.True(level >= previous);
            previous = level;
        }
    }

    [Theory]
    [InlineData(10, "刚刚")]
    [InlineData(300, "5 分钟前")]
    [InlineData(7200, "2 小时前")]
    [InlineData(172800, "2 天前")]
    public void DescribeAge_ShouldProduceReadableText(int seconds, string expected)
    {
        Assert.Equal(expected, WeatherFormat.DescribeAge(System.TimeSpan.FromSeconds(seconds)));
    }
}
