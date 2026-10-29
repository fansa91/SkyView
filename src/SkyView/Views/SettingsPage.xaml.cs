using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SkyView.Diagnostics;
using SkyView.Models;
using SkyView.ViewModels;
using System;
using System.Diagnostics;
using System.IO;

namespace SkyView.Views;

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
    /// PasswordBox.Password 不是依赖属性，无法用 x:Bind 双向同步，
    /// 这里手动把用户输入写回对应的数据项。
    /// </summary>
    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && box.DataContext is ProviderConfigItem item)
            item.ApiKeyInput = box.Password;
    }

    /// <summary>用当前输入（含尚未保存的 Key）测一次连通性。</summary>
    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        if (element.DataContext is not ProviderConfigItem item)
            return;

        await ViewModel.TestConnectionCommand.ExecuteAsync(item);
    }

    /// <summary>
    /// 保存：先写数据源配置，再把偏好同步给 WeatherViewModel 立即生效
    /// （否则用户改了温度单位要重启才看得到效果）。
    /// </summary>
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SaveCommand.Execute(null);

        var weather = App.Services.GetRequiredService<WeatherViewModel>();
        weather.ApplyPreferences(ViewModel.BuildPreferences());
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
