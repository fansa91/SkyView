using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SkyView.Services;

/// <summary>城市列表持久化（%LOCALAPPDATA%\SkyView\cities.json，纯文本，非敏感信息）。</summary>
public sealed class CityStore
{
    private static readonly string[] Fallback = ["北京", "上海", "广州", "深圳"];

    private readonly object _gate = new();
    private readonly string _filePath;

    public CityStore()
    {
        _filePath = AppPaths.Combine("cities.json");
    }

    public IReadOnlyList<string> GetCities()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_filePath))
                    return Fallback.ToList();

                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_filePath));
                return list is { Count: > 0 } ? list : Fallback.ToList();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return Fallback.ToList();
            }
        }
    }

    public void Save(IEnumerable<string> cities)
    {
        var normalized = cities
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_gate)
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(_filePath, JsonSerializer.Serialize(normalized));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 写失败忽略：城市列表属于非关键数据
            }
        }
    }
}
