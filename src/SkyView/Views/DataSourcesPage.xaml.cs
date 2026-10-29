using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SkyView.Models;
using SkyView.ViewModels;

namespace SkyView.Views;

/// <summary>
/// 数据源页：数据源列表（Key / 启用 / 优先级 / 测试连接）+ 功能数据源分配。
/// 与设置页共享同一个 <see cref="SettingsViewModel"/> 单例，两处看到的是同一份状态。
/// </summary>
public sealed partial class DataSourcesPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public DataSourcesPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
    }

    /// <summary>
    /// PasswordBox.Password 不是依赖属性，无法用 x:Bind 双向同步，
    /// 这里手动把用户输入写回对应的数据项（随后由 ViewModel 防抖自动保存）。
    /// </summary>
    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && box.DataContext is ProviderConfigItem item)
            item.ApiKeyInput = box.Password;
    }

    /// <summary>用当前输入（含刚键入、可能尚未落盘的 Key）测一次连通性。</summary>
    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        if (element.DataContext is not ProviderConfigItem item)
            return;

        await ViewModel.TestConnectionCommand.ExecuteAsync(item);
    }
}
