using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using SkyView.Formatting;
using SkyView.Motion;
using SkyView.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI;

namespace SkyView.Views;

public sealed partial class MainPage : Page
{
    public WeatherViewModel ViewModel { get; }

    private DispatcherTimer? _autoRefreshTimer;

    public MainPage()
    {
        InitializeComponent();

        ViewModel = App.Services.GetRequiredService<WeatherViewModel>();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 首次进入自动拉一次天气（之前页面完全依赖用户手动点按钮，进来是一片空白）。
    /// 已有数据时不重复请求；上次失败则在返回本页时重试。
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ConfigureAutoRefresh();

        if (App.MainWindow is not null)
            App.MainWindow.Activated += MainWindow_Activated;

        if (ViewModel.HasLoaded || ViewModel.IsLoading)
        {
            RenderTrendChart();
            QueueEntranceAnimation();
            return;
        }

        await TryRefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _autoRefreshTimer?.Stop();
        _autoRefreshTimer = null;

        if (App.MainWindow is not null)
            App.MainWindow.Activated -= MainWindow_Activated;
    }

    /// <summary>从设置页返回时重新读取偏好（单位、自动刷新间隔可能变了）。</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        ConfigureAutoRefresh();
        RenderTrendChart();
    }

    private async Task TryRefreshAsync()
    {
        try
        {
            await ViewModel.RefreshCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = ex.Message;
        }
        finally
        {
            RenderTrendChart();
            QueueEntranceAnimation();
        }
    }

    // ---------- 入场动效 ----------

    private bool _entrancePlayed;

    /// <summary>
    /// 数据首次出现时播一次入场动效。刷新不再重播 —— 每 30 分钟自动刷新时
    /// 卡片整体弹一下会很烦人。
    ///
    /// 延后一帧再放：卡片显隐是绑定 Visibility 的，同一帧里读到的还是旧值。
    /// </summary>
    private void QueueEntranceAnimation()
    {
        if (_entrancePlayed)
            return;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, PlayEntrance);
    }

    private void PlayEntrance()
    {
        if (_entrancePlayed || !ViewModel.HasCurrent)
            return;

        _entrancePlayed = true;

        MotionLibrary.PlayPopIn(CurrentIcon, 60);
        MotionLibrary.PlayEntrance(CurrentWeatherCard, 90);

        // 后面的卡片错峰出现，先给 120ms 起步，每张递增 70ms
        var delay = 190;
        foreach (var card in new FrameworkElement[] { HourlyCard, TrendCard, ForecastCard, LifeIndexCard })
        {
            if (card.Visibility != Visibility.Visible)
                continue;

            MotionLibrary.PlayEntrance(card, delay);
            delay += 70;
        }
    }

    // ---------- 自动刷新 ----------

    private void ConfigureAutoRefresh()
    {
        var minutes = ViewModel.Preferences.NormalizedAutoRefreshMinutes;

        _autoRefreshTimer?.Stop();
        _autoRefreshTimer = null;

        if (minutes <= 0)
            return;

        _autoRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(minutes),
        };
        _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
        _autoRefreshTimer.Start();
    }

    private async void AutoRefreshTimer_Tick(object? sender, object e)
    {
        if (ViewModel.IsLoading)
            return;

        await TryRefreshAsync();
    }

    /// <summary>
    /// 窗口重新获得焦点时，如果数据已经超过自动刷新间隔就刷新一次。
    /// 从其他应用切回来时不用手动点按钮。
    /// </summary>
    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
            return;

        var minutes = ViewModel.Preferences.NormalizedAutoRefreshMinutes;
        if (minutes <= 0 || ViewModel.IsLoading || !ViewModel.HasCurrent)
            return;

        var age = DateTimeOffset.Now - (ViewModel.LastUpdated ?? DateTimeOffset.MinValue);
        if (age < TimeSpan.FromMinutes(minutes))
            return;

        await TryRefreshAsync();
    }

    // ---------- 温度趋势图 ----------

    /// <summary>
    /// 手绘折线图。没用图表库：只有高低两条曲线，直接用 Polyline 画最省依赖；
    /// 放在 Viewbox 里自适应缩放，不需要处理尺寸变化。
    /// </summary>
    private void RenderTrendChart()
    {
        if (TrendCanvas is null)
            return;

        TrendCanvas.Children.Clear();

        var items = ViewModel.Forecast;
        if (items.Count == 0)
            return;

        const double width = 640;
        const double height = 170;
        const double left = 46;
        const double right = 26;
        const double top = 26;
        const double bottom = 46;

        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;

        var highs = items
            .Select(f => WeatherFormat.ConvertTemperature(f.TempHigh, ViewModel.UseFahrenheit))
            .ToList();
        var lows = items
            .Select(f => WeatherFormat.ConvertTemperature(f.TempLow, ViewModel.UseFahrenheit))
            .ToList();

        var all = highs.Concat(lows).ToList();
        var min = all.Min();
        var max = all.Max();

        // 全平的数据会让 range = 0，除零；补一点余量
        if (max - min < 1)
        {
            max += 1;
            min -= 1;
        }

        var range = max - min;

        double X(int index) => items.Count == 1
            ? left + plotWidth / 2
            : left + plotWidth * index / (items.Count - 1);

        double Y(double value) => top + plotHeight * (1 - (value - min) / range);

        var gridBrush = new SolidColorBrush(Color.FromArgb(0x28, 0x80, 0x80, 0x80));
        var labelBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A));
        var highBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xE5, 0x39, 0x35));
        var lowBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x43, 0xA0, 0x47));

        // 横向网格线（3 条）
        for (var i = 0; i <= 3; i++)
        {
            var y = top + plotHeight * i / 3.0;
            TrendCanvas.Children.Add(new Line
            {
                X1 = left,
                X2 = left + plotWidth,
                Y1 = y,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 1,
            });
        }

        DrawSeries(highs, highBrush);
        DrawSeries(lows, lowBrush);

        // 折线 + 数据点 + 数值标注
        void DrawSeries(List<double> values, SolidColorBrush brush)
        {
            var polyline = new Polyline
            {
                Stroke = brush,
                StrokeThickness = 2.5,
                StrokeLineJoin = PenLineJoin.Round,
            };

            for (var i = 0; i < values.Count; i++)
                polyline.Points.Add(new Windows.Foundation.Point(X(i), Y(values[i])));

            TrendCanvas.Children.Add(polyline);

            for (var i = 0; i < values.Count; i++)
            {
                var x = X(i);
                var y = Y(values[i]);

                var dot = new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = brush,
                };
                Canvas.SetLeft(dot, x - 3.5);
                Canvas.SetTop(dot, y - 3.5);
                TrendCanvas.Children.Add(dot);

                var valueText = new TextBlock
                {
                    Text = values[i].ToString("0"),
                    FontSize = 11,
                    Foreground = brush,
                };
                Canvas.SetLeft(valueText, x - 8);
                Canvas.SetTop(valueText, y - 20);
                TrendCanvas.Children.Add(valueText);
            }
        }

        // 底部日期标签
        for (var i = 0; i < items.Count; i++)
        {
            var label = new TextBlock
            {
                Text = items[i].Date.ToLocalTime().ToString("MM-dd"),
                FontSize = 11,
                Foreground = labelBrush,
            };
            Canvas.SetLeft(label, X(i) - 16);
            Canvas.SetTop(label, height - bottom + 14);
            TrendCanvas.Children.Add(label);
        }

        // 纵轴上下限
        var maxLabel = new TextBlock { Text = max.ToString("0"), FontSize = 10, Foreground = labelBrush };
        Canvas.SetLeft(maxLabel, 6);
        Canvas.SetTop(maxLabel, top - 8);
        TrendCanvas.Children.Add(maxLabel);

        var minLabel = new TextBlock { Text = min.ToString("0"), FontSize = 10, Foreground = labelBrush };
        Canvas.SetLeft(minLabel, 6);
        Canvas.SetTop(minLabel, height - bottom - 8);
        TrendCanvas.Children.Add(minLabel);
    }
}
