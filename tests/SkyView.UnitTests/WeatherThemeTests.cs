using SkyView.Icons;
using SkyView.Theming;
using Xunit;

namespace SkyView.UnitTests;

public class WeatherThemeTests
{
    private static readonly WeatherIconKind[] AllKinds = Enum.GetValues<WeatherIconKind>();

    [Fact]
    public void EveryKind_ShouldReturnValidHexColors()
    {
        foreach (var kind in AllKinds)
        {
            var palette = WeatherTheme.For(kind);

            Assert.False(string.IsNullOrWhiteSpace(palette.Name), $"{kind} 缺少配色名");
            Assert.Matches("^#[0-9A-Fa-f]{6}$", palette.TopColor);
            Assert.Matches("^#[0-9A-Fa-f]{6}$", palette.BottomColor);
        }
    }

    [Fact]
    public void LightRainAndRain_ShouldShareTheSamePalette()
    {
        // 小雨和中雨不该改变天色 —— 否则相邻时段背景会来回跳色
        var light = WeatherTheme.For(WeatherIconKind.LightRain);
        var rain = WeatherTheme.For(WeatherIconKind.Rain);

        Assert.Equal(light.TopColor, rain.TopColor);
        Assert.Equal(light.BottomColor, rain.BottomColor);
    }

    [Fact]
    public void HeavyRain_ShouldBeDarkerThanPlainRain()
    {
        // 暴雨单独深一档，用来体现"天更暗"
        static int Luma(string hex)
        {
            var r = Convert.ToInt32(hex.Substring(1, 2), 16);
            var g = Convert.ToInt32(hex.Substring(3, 2), 16);
            var b = Convert.ToInt32(hex.Substring(5, 2), 16);
            return (r * 299 + g * 587 + b * 114) / 1000;
        }

        var rain = WeatherTheme.For(WeatherIconKind.Rain);
        var heavy = WeatherTheme.For(WeatherIconKind.HeavyRain);

        Assert.True(Luma(heavy.TopColor) < Luma(rain.TopColor));
        Assert.True(Luma(heavy.BottomColor) < Luma(rain.BottomColor));
    }

    [Fact]
    public void DifferentWeatherFamilies_ShouldNotSharePalette()
    {
        // 晴 / 雨 / 雪 / 雷必须有明显区别，否则"天气主题背景"这个功能就白做了
        var palettes = new[]
        {
            WeatherTheme.For(WeatherIconKind.Clear),
            WeatherTheme.For(WeatherIconKind.Rain),
            WeatherTheme.For(WeatherIconKind.Snow),
            WeatherTheme.For(WeatherIconKind.Thunderstorm),
        };

        Assert.Equal(palettes.Length, palettes.Distinct().Count());
    }

    [Fact]
    public void Unknown_ShouldFallBackToDefault()
    {
        var unknown = WeatherTheme.For(WeatherIconKind.Unknown);
        var undocumented = WeatherTheme.For((WeatherIconKind)999);

        Assert.Equal(unknown, undocumented);
        Assert.Matches("^#[0-9A-Fa-f]{6}$", unknown.TopColor);
    }
}
