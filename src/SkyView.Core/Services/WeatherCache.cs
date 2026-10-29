using SkyView.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkyView.Services;

/// <summary>一次完整的天气快照，用于离线回显。</summary>
public class WeatherSnapshot
{
    public string City { get; set; } = string.Empty;
    public WeatherCurrent? Current { get; set; }
    public List<WeatherForecast> Forecast { get; set; } = [];
    public List<WeatherHourly> Hourly { get; set; } = [];
    public List<WeatherLifeIndex> LifeIndex { get; set; } = [];
    public DateTimeOffset SavedAt { get; set; }

    [JsonIgnore]
    public bool HasAnyData => Current is not null || Forecast.Count > 0;

    /// <summary>快照距今多久，用于界面上标注"显示的是缓存数据"。</summary>
    public TimeSpan Age => DateTimeOffset.Now - SavedAt;
}

public interface IWeatherCache
{
    WeatherSnapshot? Load();
    void Save(WeatherSnapshot snapshot);
    void Clear();
}

/// <summary>
/// 上次成功的天气结果落盘（%LOCALAPPDATA%\SkyView\weather-cache.json）。
/// 目的：断网或重启后先把上次的数据显示出来，而不是一片空白。
/// </summary>
public sealed class WeatherCache : IWeatherCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;

    /// <summary>默认存到 %LOCALAPPDATA%\SkyView\weather-cache.json；测试可传入临时路径。</summary>
    public WeatherCache(string? filePath = null)
    {
        _filePath = filePath ?? AppPaths.Combine("weather-cache.json");
    }

    public WeatherSnapshot? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return null;

            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var snapshot = JsonSerializer.Deserialize<WeatherSnapshot>(json, JsonOptions);
            return snapshot?.HasAnyData == true ? snapshot : null;
        }
        catch (Exception)
        {
            // 缓存损坏就当没有，重新请求即可
            return null;
        }
    }

    public void Save(WeatherSnapshot snapshot)
    {
        try
        {
            var directory = AppPaths.EnsureDataDirectory();
            var temp = Path.Combine(directory, $"weather-cache.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));

            if (File.Exists(_filePath))
                File.Replace(temp, _filePath, null);
            else
                File.Move(temp, _filePath);
        }
        catch (Exception)
        {
            // 缓存写失败不影响主流程
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_filePath))
                File.Delete(_filePath);
        }
        catch (Exception)
        {
            // 忽略
        }
    }
}
