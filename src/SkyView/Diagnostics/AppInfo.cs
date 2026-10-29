using System;
using System.Reflection;

namespace SkyView.Diagnostics;

/// <summary>
/// 应用版本信息。版本号在构建时由 <c>SkyView.csproj</c> 的
/// <c>ResolveVersionFromGitTag</c> 目标从 git tag 注入，这里只负责读出来展示。
/// </summary>
public static class AppInfo
{
    /// <summary>产品名（不含版本）。</summary>
    public const string ProductName = "SkyView";

    /// <summary>
    /// 语义化版本，例如 <c>1.4.0</c>。取不到时退回程序集版本，再退回 <c>0.0.0</c>。
    /// </summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>带与不带版本都能用的显示名。</summary>
    public static string DisplayName => $"{ProductName} {Version}";

    private static string ResolveVersion()
    {
        try
        {
            var assembly = typeof(AppInfo).Assembly;

            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                // SDK 在启用 SourceLink / CI 构建时会追加 "+<commit sha>"，
                // 界面上只显示版本号本身，commit 留给关于页或日志。
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            var version = assembly.GetName().Version;
            return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch
        {
            return "0.0.0";
        }
    }
}
