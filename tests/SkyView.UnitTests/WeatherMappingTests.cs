using SkyView.Providers;
using SkyView.Services;
using Xunit;

namespace SkyView.UnitTests;

public class WeatherMappingTests
{
    [Theory]
    [InlineData("晴", "晴")]
    [InlineData("sunny", "晴")]
    [InlineData("Clear", "晴")]
    [InlineData("小雨", "雨")]
    [InlineData("中雨", "雨")]
    [InlineData("light rain", "雨")]
    [InlineData("partly cloudy", "多云")]
    [InlineData("多云", "多云")]
    [InlineData("阴", "阴")]
    [InlineData("雷阵雨", "雷阵雨")]
    [InlineData("小雪", "雪")]
    [InlineData("雾", "雾霾")]
    [InlineData("浮尘", "沙尘")]
    public void MapCondition_ShouldNormalizeKnownDescriptions(string source, string expected)
    {
        Assert.Equal(expected, WeatherProviderBase.MapCondition(source));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MapCondition_ShouldReturnUnknown_ForEmptyInput(string? source)
    {
        Assert.Equal("未知", WeatherProviderBase.MapCondition(source));
    }

    [Fact]
    public void MapCondition_ShouldKeepOriginalText_WhenUnrecognized()
    {
        Assert.Equal("冰粒", WeatherProviderBase.MapCondition("冰粒"));
    }
}
