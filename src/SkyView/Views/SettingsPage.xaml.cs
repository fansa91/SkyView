using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SkyView.Diagnostics;
using SkyView.ViewModels;
using System;
using System.Diagnostics;
using System.IO;

namespace SkyView.Views;

/// <summary>
/// 设置页：显示与刷新偏好。所有修改由 <see cref="SettingsViewModel"/> 实时保存，
/// 本页不再需要「保存」按钮；数据源相关设置见 <see cref="DataSourcesPage"/>。
/// </summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    /// <summary>设置页底部显示的版本号（构建时从 git tag 注入，格式 <c>v1.4.0</c>）。</summary>
    public string VersionText { get; } = $"v{AppInfo.Version}";

    public SettingsPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
    }

    /// <summary>
    /// 打开日志所在目录。有 crash.log 时在资源管理器里直接选中它，
    /// 没有就只打开目录 —— 用户不用再去翻 %LOCALAPPDATA%。
    /// </summary>
    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = Path.GetDirectoryName(CrashLog.FilePath)!;
            Directory.CreateDirectory(folder);

            if (File.Exists(CrashLog.FilePath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{CrashLog.FilePath}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"打开日志目录失败：{ex.Message}（路径：{CrashLog.FilePath}）";
        }
    }
}
