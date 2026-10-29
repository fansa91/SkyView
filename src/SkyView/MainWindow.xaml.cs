using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SkyView.Views;
using System;
using Windows.Graphics;

namespace SkyView;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Title = "SkyView";
        TryResize(1000, 720);

        ContentFrame.Navigated += ContentFrame_Navigated;

        // 默认导航到天气主页
        ContentFrame.Navigate(typeof(MainPage));
        RootNavigation.SelectedItem = HomeNavItem;
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

    /// <summary>
    /// 页面代码里也可能直接 Frame.Navigate（例如城市列表点选后跳回首页），
    /// 这里让导航栏选中项跟随实际页面，避免高亮错位。
    /// </summary>
    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        var item = e.SourcePageType == typeof(MainPage) ? HomeNavItem
            : e.SourcePageType == typeof(CityListPage) ? CityNavItem
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
            "Settings" => typeof(SettingsPage),
            _ => null,
        };

        // 避免重复导航到同一页面（每次导航都会重建页面并触发一次自动刷新）
        if (pageType is null || ContentFrame.Content?.GetType() == pageType)
            return;

        ContentFrame.Navigate(pageType);
    }
}
