using System;
using System.IO;

namespace SkyView.Services;

/// <summary>
/// 应用数据目录。
/// <para>
/// 注意：SkyView 以「未打包（unpackaged）」方式部署，此时
/// <c>Windows.Storage.ApplicationData.Current</c> 会因缺少包标识而抛异常，
/// 因此本地数据统一放在 %LOCALAPPDATA%\SkyView 下。
/// </para>
/// </summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SkyView");

    public static string EnsureDataDirectory()
    {
        Directory.CreateDirectory(DataDirectory);
        return DataDirectory;
    }

    public static string Combine(string fileName) => Path.Combine(EnsureDataDirectory(), fileName);
}
