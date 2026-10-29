using SkyView.Models;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkyView.Services;

public interface IPreferencesStore
{
    AppPreferences Load();
    void Save(AppPreferences preferences);
}

/// <summary>
/// 偏好设置持久化：%LOCALAPPDATA%\SkyView\preferences.json（明文，不含敏感信息）。
/// 读写都不抛异常——偏好坏掉不应该让应用起不来。
/// </summary>
public sealed class PreferencesStore : IPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;

    /// <summary>默认存到 %LOCALAPPDATA%\SkyView\preferences.json；测试可传入临时路径。</summary>
    public PreferencesStore(string? filePath = null)
    {
        _filePath = filePath ?? AppPaths.Combine("preferences.json");
    }

    public AppPreferences Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new AppPreferences();

            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
                return new AppPreferences();

            return JsonSerializer.Deserialize<AppPreferences>(json, JsonOptions) ?? new AppPreferences();
        }
        catch (Exception)
        {
            // 文件损坏 → 回退默认值，不阻塞启动
            return new AppPreferences();
        }
    }

    public void Save(AppPreferences preferences)
    {
        try
        {
            var directory = AppPaths.EnsureDataDirectory();
            var temp = Path.Combine(directory, $"preferences.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, JsonSerializer.Serialize(preferences, JsonOptions));

            // File.Replace 失败（目标不存在）时退回 Move
            if (File.Exists(_filePath))
                File.Replace(temp, _filePath, null);
            else
                File.Move(temp, _filePath);
        }
        catch (Exception)
        {
            // 保存失败静默忽略：下次启动会回到默认值，但不影响当前会话
        }
    }
}
