using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SkyView.Services;

// SkyView 是 Windows 桌面应用。Core 项目刻意使用非平台限定的 net10.0（让测试项目保持轻量），
// DPAPI 仅 Windows 可用，因此对本文件的平台检查做豁免。
#pragma warning disable CA1416

/// <summary>
/// 基于 Windows DPAPI（CurrentUser 作用域）的加密配置存储。
/// <para>
/// 实现要点：
/// 1. 使用 <see cref="ProtectedData"/>（同步 API）。WinRT 的 DataProtectionProvider
///    是异步的，在 UI 线程上同步 Wait() 有死锁风险。
/// 2. 密文以 Base64 写入 %LOCALAPPDATA%\SkyView\settings.dat
///    （未打包应用不能用 ApplicationData.Current，会抛异常）。
/// 3. 内存中缓存整份配置，写入时整体落盘（临时文件 + 替换，避免半截文件）。
/// </para>
/// </summary>
public sealed class SecureConfigStore : ISecureConfigStore
{
    // 额外熵：即使别的程序拿到密文，也无法用同一个 DPAPI 作用域直接解密
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SkyView.Config.v1");

    private readonly object _gate = new();
    private readonly string _filePath;
    private Dictionary<string, string>? _cache;

    public SecureConfigStore()
    {
        _filePath = AppPaths.Combine("settings.dat");
    }

    public T? Get<T>(string key) where T : class
    {
        lock (_gate)
        {
            var data = EnsureLoaded();
            if (!data.TryGetValue(key, out var encoded) || string.IsNullOrEmpty(encoded))
                return default;

            try
            {
                var encrypted = Convert.FromBase64String(encoded);
                var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<T>(plain);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
            {
                // 密文损坏，或换了用户/机器导致 DPAPI 无法解密：丢弃这条记录，回落到默认配置
                data.Remove(key);
                Save(data);
                return default;
            }
        }
    }

    public void Set<T>(string key, T value) where T : class
    {
        lock (_gate)
        {
            var data = EnsureLoaded();
            var plain = JsonSerializer.SerializeToUtf8Bytes(value);
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            data[key] = Convert.ToBase64String(encrypted);
            Save(data);
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            var data = EnsureLoaded();
            if (data.Remove(key))
                Save(data);
        }
    }

    private Dictionary<string, string> EnsureLoaded() => _cache ??= Load();

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void Save(Dictionary<string, string> data)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data));

            if (File.Exists(_filePath))
                File.Replace(temp, _filePath, null);
            else
                File.Move(temp, _filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 落盘失败不应让整个保存流程崩掉；内存缓存保留，下次保存会重试
            _cache = data;
        }
    }
}
