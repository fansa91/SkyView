using SkyView.Formatting;
using SkyView.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyView.Alerts;

/// <summary>
/// 恶劣天气预警的本地推导引擎（纯函数，不依赖 UI 与网络）。
/// <para>
/// 阈值取国内常用量级：降水概率 ≥60 黄 / ≥80 橙；日最高 ≥35°C 橙 / ≥37°C 红；
/// 日最低 ≤ -10°C 寒潮；当前阵风 ≥62 km/h（8 级）大风。
/// 同一天同一类型只保留等级最高的一条；总量封顶 5 条，按日期近→远排序。
/// </para>
/// </summary>
public static class WeatherAlertEngine
{
    /// <summary>大风预警的阵风阈值 km/h（8 级风 ≈ 62 km/h）。</summary>
    public const double GustAlertKmh = 62;

    /// <summary>高温橙色档阈值 °C。</summary>
    public const double HeatOrangeC = 35;

    /// <summary>高温红色档阈值 °C。</summary>
    public const double HeatRedC = 37;

    /// <summary>低温（寒潮）阈值 °C。</summary>
    public const double ColdC = -10;

    /// <summary>降水概率黄色档。</summary>
    public const int PopYellow = 60;

    /// <summary>降水概率橙色档。</summary>
    public const int PopOrange = 80;

    /// <summary>最多展示几条（15 天预报可能条条都触阈，取最近的才有行动价值）。</summary>
    public const int MaxAlerts = 5;

    public static List<WeatherAlert> Derive(
        WeatherCurrent? current,
        IEnumerable<WeatherForecast> forecast,
        bool useFahrenheit = false)
    {
        var alerts = new List<WeatherAlert>();
        if (forecast is null)
            return alerts;

        var today = DateTimeOffset.Now.Date;

        foreach (var day in forecast)
        {
            var dateText = day.Date.Date == today ? "今天"
                : day.Date.Date == today.AddDays(1) ? "明天"
                : day.Date.ToString("MM-dd");

            // ---- 降雨 / 降雪 / 雷暴：降水概率 + 天气描述共同定级 ----
            var pop = day.PrecipitationProbability;
            if (pop >= PopYellow && (day.Condition.Contains("雨") || day.Condition.Contains("雪")))
            {
                var snowy = day.Condition.Contains("雪");
                var kind = day.Condition switch
                {
                    var c when c.Contains("雷") => "雷暴",
                    var c when snowy => "降雪",
                    var c when c.Contains("大") => "暴雨",
                    _ => "降雨",
                };
                var level = pop >= PopOrange ? 3 : 2;
                alerts.Add(new WeatherAlert
                {
                    Kind = kind,
                    Level = level,
                    LevelText = LevelText(level),
                    Date = day.Date,
                    DateText = dateText,
                    Reason = $"降水概率 {pop}%",
                });
            }

            // ---- 高温 / 寒潮 ----
            if (day.TempHigh >= HeatOrangeC)
            {
                var level = day.TempHigh >= HeatRedC ? 4 : 3;
                alerts.Add(new WeatherAlert
                {
                    Kind = "高温",
                    Level = level,
                    LevelText = LevelText(level),
                    Date = day.Date,
                    DateText = dateText,
                    Reason = $"最高 {WeatherFormat.Temperature(day.TempHigh, useFahrenheit)}",
                });
            }

            if (day.TempLow <= ColdC)
            {
                alerts.Add(new WeatherAlert
                {
                    Kind = "寒潮",
                    Level = 3,
                    LevelText = LevelText(3),
                    Date = day.Date,
                    DateText = dateText,
                    Reason = $"最低 {WeatherFormat.Temperature(day.TempLow, useFahrenheit)}",
                });
            }
        }

        // ---- 大风：hourly 不带风速，只能看 current 的阵风 ----
        if (current is { HasWindGusts: true } && current.WindGusts!.Value >= GustAlertKmh)
        {
            alerts.Add(new WeatherAlert
            {
                Kind = "大风",
                Level = 3,
                LevelText = LevelText(3),
                Date = DateTimeOffset.Now,
                DateText = "现在",
                Reason = $"阵风 {current.WindGusts.Value:0.#} km/h",
            });
        }

        return alerts
            .GroupBy(a => (a.Date.Date, a.Kind))
            .Select(g => g.OrderByDescending(a => a.Level).First())
            .OrderBy(a => a.Date)
            .ThenByDescending(a => a.Level)
            .Take(MaxAlerts)
            .ToList();
    }

    /// <summary>等级 → 中文（蓝 / 黄 / 橙 / 红）。</summary>
    public static string LevelText(int level) => level switch
    {
        4 => "红色",
        3 => "橙色",
        2 => "黄色",
        _ => "蓝色",
    };
}
