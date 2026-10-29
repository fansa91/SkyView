using SkyView.Icons;
using Xunit;

namespace SkyView.UnitTests;

public class WeatherIconSelectorTests
{
    // ---------- 上游 MapCondition 会产出的那些短词 ----------

    [Theory]
    [InlineData("晴", WeatherIconKind.Clear)]
    [InlineData("多云", WeatherIconKind.PartlyCloudy)]
    [InlineData("阴", WeatherIconKind.Cloudy)]
    [InlineData("雨", WeatherIconKind.Rain)]
    [InlineData("雷阵雨", WeatherIconKind.Thunderstorm)]
    [InlineData("雪", WeatherIconKind.Snow)]
    [InlineData("雾霾", WeatherIconKind.Fog)]
    [InlineData("沙尘", WeatherIconKind.Dust)]
    public void Select_ShouldMapNormalizedConditions(string condition, WeatherIconKind expected)
    {
        Assert.Equal(expected, WeatherIconSelector.Select(condition));
    }

    // ---------- 优先级：雷、雪、暴雨必须抢在通用「雨」前面 ----------

    [Theory]
    [InlineData("雷阵雨", WeatherIconKind.Thunderstorm)]
    [InlineData("雷阵雨伴有冰雹", WeatherIconKind.Thunderstorm)]
    [InlineData("强雷暴", WeatherIconKind.Thunderstorm)]
    public void Select_ShouldPreferThunderOverRain(string condition, WeatherIconKind expected)
    {
        Assert.Equal(expected, WeatherIconSelector.Select(condition));
    }

    [Theory]
    [InlineData("雨夹雪", WeatherIconKind.Snow)]
    [InlineData("阵雪", WeatherIconKind.Snow)]
    [InlineData("大雪", WeatherIconKind.Snow)]
    public void Select_ShouldPreferSnowOverRain(string condition, WeatherIconKind expected)
    {
        Assert.Equal(expected, WeatherIconSelector.Select(condition));
    }

    [Theory]
    [InlineData("小雨", WeatherIconKind.LightRain)]
    [InlineData("毛毛雨", WeatherIconKind.LightRain)]
    [InlineData("大雨", WeatherIconKind.HeavyRain)]
    [InlineData("暴雨", WeatherIconKind.HeavyRain)]
    [InlineData("特大暴雨", WeatherIconKind.HeavyRain)]
    public void Select_ShouldKeepRainIntensity(string condition, WeatherIconKind expected)
    {
        Assert.Equal(expected, WeatherIconSelector.Select(condition));
    }

    // ---------- 英文与大小写 ----------

    [Theory]
    [InlineData("sunny", WeatherIconKind.Clear)]
    [InlineData("CLEAR", WeatherIconKind.Clear)]
    [InlineData("Partly Cloudy", WeatherIconKind.PartlyCloudy)]
    [InlineData("overcast", WeatherIconKind.Cloudy)]
    [InlineData("light rain", WeatherIconKind.LightRain)]
    [InlineData("heavy rain", WeatherIconKind.HeavyRain)]
    [InlineData("Thunderstorm", WeatherIconKind.Thunderstorm)]
    [InlineData("blizzard", WeatherIconKind.Snow)]
    [InlineData("haze", WeatherIconKind.Fog)]
    [InlineData("sand", WeatherIconKind.Dust)]
    [InlineData("gale", WeatherIconKind.Wind)]
    public void Select_ShouldHandleEnglish(string condition, WeatherIconKind expected)
    {
        Assert.Equal(expected, WeatherIconSelector.Select(condition));
    }

    [Fact]
    public void Select_ShouldHandleChineseWind()
    {
        Assert.Equal(WeatherIconKind.Wind, WeatherIconSelector.Select("大风"));
    }

    // ---------- 兜底 ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("冰粒")]
    [InlineData("zzz")]
    public void Select_ShouldFallBackToUnknown(string? condition)
    {
        Assert.Equal(WeatherIconKind.Unknown, WeatherIconSelector.Select(condition));
    }

    // ---------- 降水概率补强度 ----------

    [Theory]
    [InlineData(0, WeatherIconKind.Rain)]
    [InlineData(69, WeatherIconKind.Rain)]
    [InlineData(70, WeatherIconKind.HeavyRain)]
    [InlineData(95, WeatherIconKind.HeavyRain)]
    public void Select_WithProbability_ShouldUpgradePlainRainOnly(int probability, WeatherIconKind expected)
    {
        Assert.Equal(expected, WeatherIconSelector.Select("雨", probability));
    }

    [Fact]
    public void Select_WithProbability_ShouldNotAffectNonRainKinds()
    {
        Assert.Equal(WeatherIconKind.Clear, WeatherIconSelector.Select("晴", 100));
        Assert.Equal(WeatherIconKind.Snow, WeatherIconSelector.Select("雪", 100));
        Assert.Equal(WeatherIconKind.LightRain, WeatherIconSelector.Select("小雨", 100));
    }

    // ---------- Describe ----------

    [Theory]
    [InlineData(WeatherIconKind.Clear, "晴")]
    [InlineData(WeatherIconKind.HeavyRain, "大雨")]
    [InlineData(WeatherIconKind.Unknown, "未知")]
    public void Describe_ShouldReturnChineseName(WeatherIconKind kind, string expected)
    {
        Assert.Equal(expected, WeatherIconSelector.Describe(kind));
    }
}
