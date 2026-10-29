using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using SkyView.Icons;
using SkyView.Motion;
using System.Collections.Generic;

namespace SkyView.Controls;

/// <summary>
/// 天气图标。一套 24×24 矢量图层，按 <see cref="Kind"/> 组合出对应画面，并在
/// <see cref="IsAnimated"/> 打开时播放环境动效（太阳转、云飘、雨落…）。
///
/// 用法：
/// <code>
/// &lt;controls:WeatherIcon Kind="Clear" Width="64" Height="64" /&gt;
/// </code>
/// 列表里的图标把 <c>IsAnimated</c> 设成 False —— 一屏二十几个图标同时跑
/// Storyboard 不只是耗电，视觉上也会变成一锅粥。
/// </summary>
public sealed partial class WeatherIcon : UserControl
{
    /// <summary>大雨/雷雨的云：偏冷的板岩灰。不要用纯蓝——那看着像色块而不是云。</summary>
    private static readonly SolidColorBrush CloudDarkBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0x54, 0x6E, 0x7A));
    private static readonly SolidColorBrush DustBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 0xD7, 0xB9, 0x8E));

    private readonly List<Storyboard> _running = [];
    private readonly SolidColorBrush _cloudBrush;
    private readonly SolidColorBrush _cloudBackBrush;
    private readonly SolidColorBrush _fogBrush;

    public WeatherIcon()
    {
        InitializeComponent();

        // 记住设计态颜色，切换 Kind 时要能还原（雷雨会把云刷深、沙尘会把雾线刷成土黄）
        _cloudBrush = (SolidColorBrush)CloudLayer.Children[0].GetValue(Shape_FillProperty());
        _cloudBackBrush = (SolidColorBrush)CloudBackLayer.Children[0].GetValue(Shape_FillProperty());
        _fogBrush = (SolidColorBrush)Fog1.Fill;

        Loaded += (_, _) => Apply();
        Unloaded += (_, _) => StopAnimations();
    }

    // ---------- 依赖属性 ----------

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(WeatherIconKind),
        typeof(WeatherIcon),
        new PropertyMetadata(WeatherIconKind.Unknown, OnKindChanged));

    public static readonly DependencyProperty IsAnimatedProperty = DependencyProperty.Register(
        nameof(IsAnimated),
        typeof(bool),
        typeof(WeatherIcon),
        new PropertyMetadata(true, OnKindChanged));

    public WeatherIconKind Kind
    {
        get => (WeatherIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>是否播放环境动效。列表项建议关掉。</summary>
    public bool IsAnimated
    {
        get => (bool)GetValue(IsAnimatedProperty);
        set => SetValue(IsAnimatedProperty, value);
    }

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((WeatherIcon)d).Apply();

    // ---------- 无障碍 ----------

    /// <summary>
    /// UserControl 默认没有 AutomationPeer，不加这个在 UIA 树里根本看不到，
    /// 屏幕阅读器读不到、pywinauto 也断言不到。同时给个中文名。
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer()
        => new FrameworkElementAutomationPeer(this);

    // ---------- 组装 ----------

    private void Apply()
    {
        if (Root is null)  // 构造期间 DP 先于 InitializeComponent 赋值时的保护
            return;

        // 先全收起来，再按 kind 打开需要的图层
        SunHost.Visibility = Visibility.Collapsed;
        CloudBackHost.Visibility = Visibility.Collapsed;
        CloudHost.Visibility = Visibility.Collapsed;
        PrecipLayer.Visibility = Visibility.Collapsed;
        BoltShape.Visibility = Visibility.Collapsed;
        FogLayer.Visibility = Visibility.Collapsed;
        WindLayer.Visibility = Visibility.Collapsed;

        foreach (var drop in new[] { Drop1, Drop2, Drop3, Drop4, Drop5 })
            drop.Visibility = Visibility.Collapsed;
        foreach (var flake in new[] { Flake1, Flake2, Flake3 })
            flake.Visibility = Visibility.Collapsed;

        // 复位到设计态配色
        foreach (var shape in CloudLayer.Children)
            shape.SetValue(Shape_FillProperty(), _cloudBrush);
        foreach (var shape in CloudBackLayer.Children)
            shape.SetValue(Shape_FillProperty(), _cloudBackBrush);
        Fog1.Fill = Fog2.Fill = Fog3.Fill = Fog4.Fill = _fogBrush;

        ResetHostTransforms();

        var animations = ConfigureLayers(Kind);

        AutomationProperties.SetName(this, $"天气图标：{WeatherIconSelector.Describe(Kind)}");

        StopAnimations();
        if (IsAnimated && IsLoaded)
            StartAnimations(animations);
    }

    /// <summary>按 kind 摆图层，返回需要播放的动效名。</summary>
    private string[] ConfigureLayers(WeatherIconKind kind)
    {
        switch (kind)
        {
            case WeatherIconKind.Clear:
                SunHost.Visibility = Visibility.Visible;
                return ["SunSpin"];

            case WeatherIconKind.PartlyCloudy:
                // 太阳缩小挪到右上角，云压在左下方
                SunHost.Visibility = Visibility.Visible;
                SunHostTransform.ScaleX = SunHostTransform.ScaleY = 0.70;
                SunHostTransform.TranslateX = 4.6;
                SunHostTransform.TranslateY = -4.6;

                CloudHost.Visibility = Visibility.Visible;
                CloudHostTransform.ScaleX = CloudHostTransform.ScaleY = 0.88;
                CloudHostTransform.TranslateX = -1.4;
                CloudHostTransform.TranslateY = 2.2;
                return ["SunSpin", "CloudDrift"];

            case WeatherIconKind.Cloudy:
                CloudBackHost.Visibility = Visibility.Visible;
                CloudBackHostTransform.ScaleX = CloudBackHostTransform.ScaleY = 0.82;
                CloudBackHostTransform.TranslateX = 3.0;
                CloudBackHostTransform.TranslateY = -2.8;

                CloudHost.Visibility = Visibility.Visible;
                return ["CloudDrift", "CloudBackDrift"];

            case WeatherIconKind.LightRain:
                CloudHost.Visibility = Visibility.Visible;
                ShowDrops(1, 3);
                return ["CloudDrift", "RainFall"];

            case WeatherIconKind.Rain:
                CloudHost.Visibility = Visibility.Visible;
                ShowDrops(0, 2, 4);
                return ["CloudDrift", "RainFall"];

            case WeatherIconKind.HeavyRain:
            case WeatherIconKind.Thunderstorm:
                CloudHost.Visibility = Visibility.Visible;
                TintCloud(CloudDarkBrush);
                ShowDrops(0, 1, 2, 3, 4);
                if (kind == WeatherIconKind.Thunderstorm)
                {
                    BoltShape.Visibility = Visibility.Visible;
                    return ["CloudDrift", "RainFall", "BoltFlash"];
                }
                return ["CloudDrift", "RainFall"];

            case WeatherIconKind.Snow:
                CloudHost.Visibility = Visibility.Visible;
                // 雪花和雨滴共用 PrecipLayer，这里必须把它一起打开，
                // 否则只显示一朵云（子元素可见但父容器 Collapsed）。
                PrecipLayer.Visibility = Visibility.Visible;
                Flake1.Visibility = Flake2.Visibility = Flake3.Visibility = Visibility.Visible;
                return ["CloudDrift", "SnowFall"];

            case WeatherIconKind.Fog:
                FogLayer.Visibility = Visibility.Visible;
                return ["FogDrift"];

            case WeatherIconKind.Dust:
                FogLayer.Visibility = Visibility.Visible;
                Fog1.Fill = Fog2.Fill = Fog3.Fill = Fog4.Fill = DustBrush;
                return ["FogDrift"];

            case WeatherIconKind.Wind:
                WindLayer.Visibility = Visibility.Visible;
                return ["WindBlow"];

            default:  // Unknown：一朵中性云，不空着
                CloudHost.Visibility = Visibility.Visible;
                return ["CloudDrift"];
        }
    }

    /// <summary>
    /// 显示指定的几个雨滴。传下标而不是数量，因为 5 个雨滴是等距排布的，
    /// 取「偶数位」或「首中尾」才是对称的，取前 N 个会一边重一边轻。
    /// </summary>
    private void ShowDrops(params int[] indices)
    {
        PrecipLayer.Visibility = Visibility.Visible;
        var drops = new[] { Drop1, Drop2, Drop3, Drop4, Drop5 };
        foreach (var index in indices)
        {
            if (index >= 0 && index < drops.Length)
                drops[index].Visibility = Visibility.Visible;
        }
    }

    private void TintCloud(SolidColorBrush brush)
    {
        foreach (var shape in CloudLayer.Children)
            shape.SetValue(Shape_FillProperty(), brush);
    }

    private void ResetHostTransforms()
    {
        SunHostTransform.ScaleX = SunHostTransform.ScaleY = 1;
        SunHostTransform.TranslateX = SunHostTransform.TranslateY = 0;
        CloudHostTransform.ScaleX = CloudHostTransform.ScaleY = 1;
        CloudHostTransform.TranslateX = CloudHostTransform.TranslateY = 0;
        CloudBackHostTransform.ScaleX = CloudBackHostTransform.ScaleY = 1;
        CloudBackHostTransform.TranslateX = CloudBackHostTransform.TranslateY = 0;
    }

    // ---------- 动效 ----------

    private void StartAnimations(IEnumerable<string> names)
    {
        if (!MotionLibrary.AnimationsEnabled)
            return;

        foreach (var name in names)
        {
            if (Resources.TryGetValue(name, out var resource) && resource is Storyboard storyboard)
            {
                try
                {
                    storyboard.Begin();
                    _running.Add(storyboard);
                }
                catch
                {
                    // 动画失败绝不能影响主流程；静态图标本身是可用的
                }
            }
        }
    }

    private void StopAnimations()
    {
        foreach (var storyboard in _running)
        {
            try
            {
                storyboard.Stop();
            }
            catch
            {
                // 同上
            }
        }

        _running.Clear();
    }

    /// <summary>取 Shape.Fill 的依赖属性对象，避免为 Ellipse / Rectangle 各写一份。</summary>
    private static DependencyProperty Shape_FillProperty()
        => Microsoft.UI.Xaml.Shapes.Shape.FillProperty;
}
