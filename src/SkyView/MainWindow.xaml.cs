using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SkyView.Formatting;
using SkyView.Shell;
using SkyView.ViewModels;
using SkyView.Views;
using System;
using System.ComponentModel;
using System.IO;
using Windows.Graphics;

namespace SkyView;

public sealed partial class MainWindow : Window
{
    private TrayIcon? _tray;
    private readonly WeatherViewModel _weather;
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();

        Title = "SkyView";
        TryResize(1000, 720);
        TrySetIcon();

        _weather = App.Services.GetRequiredService<WeatherViewModel>();
        _weather.PropertyChanged += Weather_PropertyChanged;

        ContentFrame.Navigated += ContentFrame_Navigated;

        // 默认导航到天气主页
        ContentFrame.Navigate(typeof(MainPage));
        RootNavigation.SelectedItem = HomeNavItem;

        // 关闭按钮不是退出，而是收进托盘（见 OnAppWindowClosing）
        AppWindow.Closing += OnAppWindowClosing;

        InitTray();
    }

    private void TryResize(int width, int height)
    {
        try
        {
            AppWindow.Resize(new SizeInt32(width, height));
        }
        catch (Exception)
        {
            // 某些环境（如无窗口管理器）不允许调整尺寸，忽略即可
        }
    }

    /// <summary>标题栏与任务栏使用应用自己的图标，而不是系统默认的空白窗口图标。</summary>
    private void TrySetIcon()
    {
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "SkyView.ico");
            if (File.Exists(icon))
                AppWindow.SetIcon(icon);
        }
        catch (Exception)
        {
            // 图标缺失不该影响启动
        }
    }

    // ---------------- 托盘 ----------------

    private void InitTray()
    {
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "SkyView.ico");
            _tray = new TrayIcon(icon);

            _tray.ShowRequested += ShowFromTray;
            _tray.RefreshRequested += RefreshFromTray;
            _tray.ExitRequested += ExitApplication;

            UpdateTrayTooltip();
        }
        catch (Exception ex)
        {
            // 托盘失败不能让主窗口起不来：记录后继续，用户仍可正常使用窗口
            Diagnostics.CrashLog.Write("MainWindow.InitTray", ex);
            _tray = null;
        }
    }

    private void Weather_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WeatherViewModel.Current) or nameof(WeatherViewModel.LastUpdated)
            or nameof(WeatherViewModel.UseFahrenheit))
        {
            UpdateTrayTooltip();
        }
    }

    /// <summary>托盘悬停提示：城市 + 温度 + 天气。上限 127 字符（NOTIFYICONDATA.szTip）。</summary>
    private void UpdateTrayTooltip()
    {
        if (_tray is null)
            return;

        if (!_weather.HasCurrent)
        {
            _tray.SetTooltip("SkyView");
            return;
        }

        var current = _weather.Current;
        var temperature = WeatherFormat.ConvertTemperature(current.Temperature, _weather.UseFahrenheit);
        var unit = _weather.UseFahrenheit ? "°F" : "°C";

        _tray.SetTooltip($"{current.City} {temperature:0.#}{unit} {current.Condition}".Trim());
    }

    private void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
    }

    private async void RefreshFromTray()
    {
        try
        {
            await _weather.RefreshCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            Diagnostics.CrashLog.Write("MainWindow.RefreshFromTray", ex);
        }
    }

    private void ExitApplication()
    {
        _exiting = true;
        _tray?.Dispose();
        _tray = null;
        Close();
    }

    /// <summary>
    /// 点窗口的 × 时收进托盘，而不是退出进程 —— 这是「系统托盘」这一档功能的核心行为。
    /// 真正的退出走托盘菜单的「退出」，那条路径会把 <see cref="_exiting"/> 置 true。
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting)
        {
            _tray?.Dispose();
            _tray = null;
            return;
        }

        args.Cancel = true;
        AppWindow.Hide();
    }

    /// <summary>
    /// 页面代码里也可能直接 Frame.Navigate（例如城市列表点选后跳回首页），
    /// 这里让导航栏选中项跟随实际页面，避免高亮错位。
    /// </summary>
    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        var item = e.SourcePageType == typeof(MainPage) ? HomeNavItem
            : e.SourcePageType == typeof(CityListPage) ? CityNavItem
            : e.SourcePageType == typeof(DataSourcesPage) ? DataSourcesNavItem
            : e.SourcePageType == typeof(SettingsPage) ? SettingsNavItem
            : null;

        if (item is not null && !ReferenceEquals(RootNavigation.SelectedItem, item))
            RootNavigation.SelectedItem = item;
    }

    private void RootNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
            return;

        var pageType = item.Tag?.ToString() switch
        {
            "Home" => typeof(MainPage),
            "City" => typeof(CityListPage),
            "DataSources" => typeof(DataSourcesPage),
            "Settings" => typeof(SettingsPage),
            _ => null,
        };

        // 避免重复导航到同一页面（每次导航都会重建页面并触发一次自动刷新）
        if (pageType is null || ContentFrame.Content?.GetType() == pageType)
            return;

        ContentFrame.Navigate(pageType);
    }
}
