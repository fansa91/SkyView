using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace SkyView.UITests;

/// <summary>
/// 冒烟级 UI 测试：验证构建产物与随包配置文件是否齐全。
/// （真实的界面交互测试需要图形桌面，交给 tools/check_ui_layout.py 在交互式会话里跑。）
/// </summary>
public class AppLaunchTests
{
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SkyView.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static string? FindAppExe()
    {
        var root = FindRepoRoot();
        if (root is null)
            return null;

        var srcDir = Path.Combine(root, "src");
        if (!Directory.Exists(srcDir))
            return null;

        return Directory.EnumerateFiles(srcDir, "SkyView.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    [Fact]
    public void AppExe_ShouldExist_InBuildOutput()
    {
        var exe = FindAppExe();
        Assert.True(exe is not null, "未找到 SkyView.exe，请先执行 dotnet build src/SkyView/SkyView.csproj");
    }

    [Fact]
    public void AppDll_ShouldExist_NextToExe()
    {
        var exe = FindAppExe();
        Assert.True(exe is not null, "未找到 SkyView.exe");

        var dll = Path.Combine(Path.GetDirectoryName(exe!)!, "SkyView.dll");
        Assert.True(File.Exists(dll), $"程序集不存在：{dll}");
    }

    [Fact]
    public void AppSettings_ShouldBeCopiedNextToExe_WithProviderSection()
    {
        var exe = FindAppExe();
        Assert.True(exe is not null, "未找到 SkyView.exe");

        var settingsPath = Path.Combine(Path.GetDirectoryName(exe!)!, "appsettings.json");
        Assert.True(File.Exists(settingsPath), $"appsettings.json 未随构建输出复制：{settingsPath}");

        using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.True(doc.RootElement.TryGetProperty("WeatherProviders", out var providers), "缺少 WeatherProviders 配置段");
        Assert.Equal(JsonValueKind.Array, providers.ValueKind);
        Assert.True(providers.GetArrayLength() >= 1, "至少需要配置一个天气数据源");
    }

    [Fact]
    public void BuildOutput_ShouldContainMicrosoftUiXaml_ForFrameworkDependentMode()
    {
        var exe = FindAppExe();
        Assert.True(exe is not null, "未找到 SkyView.exe");

        var dir = Path.GetDirectoryName(exe!)!;
        var hasWinUI = File.Exists(Path.Combine(dir, "Microsoft.WinUI.dll")) ||
                       File.Exists(Path.Combine(dir, "Microsoft.UI.Xaml.dll")) ||
                       Directory.EnumerateFiles(dir, "Microsoft*.dll").Any(f => Path.GetFileName(f).Contains("WinUI", StringComparison.OrdinalIgnoreCase));

        Assert.True(hasWinUI, $"输出目录缺少 WinUI 依赖：{dir}");
    }
}
