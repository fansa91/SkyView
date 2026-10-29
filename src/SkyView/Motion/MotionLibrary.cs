using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;

namespace SkyView.Motion;

/// <summary>
/// 轻量动效库：入场淡入上浮、图标弹出、错峰播放。
///
/// 只用 WinUI 自带的 Storyboard + 组合变换，不引第三方动画包：
/// 这是自包含发布的应用，多一个包就多一份启动开销和版本冲突面。
///
/// 所有方法都会先看系统的「减少动画」设置（设置 → 辅助功能 → 视觉效果 → 动画效果）。
/// 关掉动画的用户不少是前庭功能敏感，硬播动画会让人不适，这一点不能省。
/// </summary>
public static class MotionLibrary
{
    private static readonly Lazy<bool> SystemAnimationFlag = new(ReadSystemAnimationFlag);

    /// <summary>系统是否允许播放动画。</summary>
    public static bool AnimationsEnabled => SystemAnimationFlag.Value;

    private static bool ReadSystemAnimationFlag()
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        }
        catch
        {
            // 读不到系统设置时按允许处理：动画本身无害，缺动画反而像坏了
            return true;
        }
    }

    /// <summary>
    /// 淡入 + 向上浮动入场。用于卡片出现。
    /// </summary>
    /// <param name="element">目标元素，建议先设 RenderTransformOrigin="0.5,0.5"。</param>
    /// <param name="delayMs">延迟毫秒，用于多张卡片错峰。</param>
    /// <param name="offsetY">起始下移量（像素）。</param>
    public static void PlayEntrance(UIElement element, int delayMs = 0, double offsetY = 12)
    {
        if (element is null)
            return;

        if (!AnimationsEnabled)
        {
            element.Opacity = 1;
            return;
        }

        var transform = EnsureTransform(element);
        transform.TranslateX = 0;
        transform.TranslateY = offsetY;
        element.Opacity = 0;

        var duration = TimeSpan.FromMilliseconds(340);

        var fade = new DoubleAnimation
        {
            To = 1,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var rise = new DoubleAnimation
        {
            To = 0,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(rise, transform);
        Storyboard.SetTargetProperty(rise, "TranslateY");

        var storyboard = new Storyboard { BeginTime = TimeSpan.FromMilliseconds(Math.Max(0, delayMs)) };
        storyboard.Children.Add(fade);
        storyboard.Children.Add(rise);
        storyboard.Begin();
    }

    /// <summary>
    /// 从 88% 放大到 100%，用于主图标出现。
    /// </summary>
    public static void PlayPopIn(UIElement element, int delayMs = 0)
    {
        if (element is null)
            return;

        if (!AnimationsEnabled)
        {
            element.Opacity = 1;
            return;
        }

        var transform = EnsureTransform(element);
        transform.ScaleX = 0.88;
        transform.ScaleY = 0.88;
        element.Opacity = 0;

        var duration = TimeSpan.FromMilliseconds(420);
        var easing = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };

        var scaleX = new DoubleAnimation { To = 1, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(scaleX, transform);
        Storyboard.SetTargetProperty(scaleX, "ScaleX");

        var scaleY = new DoubleAnimation { To = 1, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(scaleY, transform);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");

        var fade = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(220) };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard { BeginTime = TimeSpan.FromMilliseconds(Math.Max(0, delayMs)) };
        storyboard.Children.Add(scaleX);
        storyboard.Children.Add(scaleY);
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }

    /// <summary>取（必要时创建）元素上的组合变换。</summary>
    private static CompositeTransform EnsureTransform(UIElement element)
    {
        if (element.RenderTransform is CompositeTransform existing)
            return existing;

        var transform = new CompositeTransform();
        element.RenderTransform = transform;
        element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        return transform;
    }
}
