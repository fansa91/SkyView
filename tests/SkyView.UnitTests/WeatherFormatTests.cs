using SkyView.Formatting;
using Xunit;

namespace SkyView.UnitTests;

public class WeatherFormatTests
{
    [Fact]
    public void Temperature_ShouldFormatWithOneDecimal()
    {
        Assert.Equal("22.5°C", WeatherFormat.Temperature(22.5));
        Assert.Equal("22°C", WeatherFormat.Temperature(22));
        Assert.Equal("-3°C", WeatherFormat.Temperature(-3));
    }

    [Fact]
    public void Number_ShouldUseProvidedFormat()
    {
        Assert.Equal("1013", WeatherFormat.Number(1013.4, "0"));
        Assert.Equal("72.4", WeatherFormat.Number(72.4, "F1"));
        Assert.Equal("72", WeatherFormat.Number(72.0, "0"));
    }

    [Fact]
    public void Number_ShouldFallBackToDefaultFormat_WhenFormatMissing()
    {
        Assert.Equal("22.5", WeatherFormat.Number(22.5, null));
    }

    [Fact]
    public void Date_ShouldRespectFormat()
    {
        // 用本地时间构造，避免测试依赖运行机器的时区
        var value = new System.DateTimeOffset(new System.DateTime(2026, 10, 28, 9, 5, 0, System.DateTimeKind.Local));

        Assert.Equal("10-28 09:05", WeatherFormat.Date(value, "MM-dd HH:mm"));
        Assert.Equal("10-28", WeatherFormat.Date(value, "MM-dd"));
    }

    [Theory]
    [InlineData("晴", "☀")]
    [InlineData("雨", "🌧")]
    [InlineData(null, "🌡")]
    [InlineData("不认识的天气", "🌡")]
    public void Glyph_ShouldMapKnownConditions(string? condition, string expected)
    {
        Assert.Equal(expected, WeatherFormat.Glyph(condition));
    }
}
