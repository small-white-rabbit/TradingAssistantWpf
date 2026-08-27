using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ScottPlot.WPF;

namespace StockReviewWpf.Controls;

/// <summary>
/// ScottPlot 图表宿主入场动画（ScottPlot 本身是 SkiaSharp Canvas 渲染，无法对内部元素做 WPF 动画，
/// 所以动画全部施加在 WpfPlot 宿主 FrameworkElement 上）。
///
/// 关键设计：
///   1. 动画前先 Reset 宿主的 transform/opacity，防止上一次动画中断留下脏状态（解决"有时候空白"）
///   2. RotateTransform 绕 RenderTransformOrigin(0.5,0.5) 正中心旋转，不会飞出区域遮挡周围卡片
///   3. DispatcherPriority.Background 确保 ScottPlot 已 Render 完第一帧
///   4. _animating 标记防止同一控件被重复触发（tab 快速切换场景）
/// </summary>
public static class ChartAnimations
{
    private static readonly CubicEase EaseOut = new() { EasingMode = EasingMode.EaseOut };

    // Attached property：标记当前是否正在做入场动画
    private static readonly DependencyProperty IsAnimatingProperty =
        DependencyProperty.RegisterAttached("IsAnimating", typeof(bool), typeof(ChartAnimations), new PropertyMetadata(false));
    private static bool GetIsAnimating(DependencyObject d) => (bool)d.GetValue(IsAnimatingProperty);
    private static void SetIsAnimating(DependencyObject d, bool v) => d.SetValue(IsAnimatingProperty, v);

    private static void ResetTransform(FrameworkElement host)
    {
        host.Opacity = 1;
        host.RenderTransformOrigin = new Point(0.5, 0.5);
        host.RenderTransform = Transform.Identity;
    }

    private static void BeginSafely(Storyboard sb, FrameworkElement target)
    {
        sb.Completed += (_, _) => 
        target.Dispatcher.BeginInvoke(new Action(() =>
        {
            try { sb.Stop(target); } catch { }
            sb.Begin(target);
        }), DispatcherPriority.Background);
    }

    /// <summary>通用 fade + scale(0.94→1) 入场，用于柱状图、折线图。</summary>
    public static void HostFadeIn(WpfPlot plot, double delayMs = 0)
    {
        var host = (FrameworkElement)plot;
        // IsAnimating skip removed - always reset on every call
        // SetIsAnimating skip removed

        ResetTransform(host);
        host.Opacity = 0;
        host.RenderTransform = new ScaleTransform(0.94, 0.94);

        var sb = new Storyboard { BeginTime = TimeSpan.FromMilliseconds(delayMs) };
        var a1 = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = EaseOut };
        var a2 = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(350)) { EasingFunction = EaseOut };
        Storyboard.SetTarget(a1, host); Storyboard.SetTargetProperty(a1, new PropertyPath("Opacity"));
        Storyboard.SetTarget(a2, host); Storyboard.SetTargetProperty(a2, new PropertyPath("RenderTransform.(ScaleTransform.ScaleX)"));
        sb.Children.Add(a1); sb.Children.Add(a2);

        BeginSafely(sb, host);
    }

    /// <summary>
    /// 饼图专属入场：Scale(0.85→1) + Rotate(-30→0)，绕正中心旋转。
    /// 等 Loaded 后再构建 TransformGroup（需要 ActualWidth/ActualHeight 确定渲染区域）。
    /// </summary>
    public static void AnimatePieChart(WpfPlot plot)
    {
        var host = (FrameworkElement)plot;
        // IsAnimating skip removed - always reset on every call
        // SetIsAnimating skip removed

        ResetTransform(host);
        host.Opacity = 0;

        void DoAnimate(object? sender, RoutedEventArgs e)
        {
            host.Loaded -= DoAnimate;

            // RotateTransform 默认 CenterX/CenterY = (0,0)（左上角）。
            // 要绕正中心旋转，必须显式设置 CenterX/Y。
            // TransformGroup 内的每个 Transform 独立执行，不能依赖 RenderTransformOrigin 自动居中。
            var cx = host.ActualWidth > 0 ? host.ActualWidth / 2 : 150;
            var cy = host.ActualHeight > 0 ? host.ActualHeight / 2 : 150;

            var scaleT = new ScaleTransform(0.85, 0.85, cx, cy);
            var rotT = new RotateTransform(-30, cx, cy);
            var group = new TransformGroup();
            group.Children.Add(scaleT);
            group.Children.Add(rotT);
            host.RenderTransform = group;

            var sb = new Storyboard();
            var a1 = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)) { EasingFunction = EaseOut };
            var a2 = new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(500)) { EasingFunction = EaseOut };
            var a3 = new DoubleAnimation(-30, 0, TimeSpan.FromMilliseconds(550)) { EasingFunction = EaseOut };
            Storyboard.SetTarget(a1, host); Storyboard.SetTargetProperty(a1, new PropertyPath("Opacity"));
            Storyboard.SetTarget(a2, scaleT); Storyboard.SetTargetProperty(a2, new PropertyPath("ScaleX"));
            Storyboard.SetTarget(a3, rotT); Storyboard.SetTargetProperty(a3, new PropertyPath("Angle"));
            sb.Children.Add(a1); sb.Children.Add(a2); sb.Children.Add(a3);

            BeginSafely(sb, host);
        }

        if (host.IsLoaded && host.ActualWidth > 0)
            DoAnimate(null, new RoutedEventArgs());
        else
            host.Loaded += DoAnimate;
    }

    /// <summary>柱状图：fade + scale 入场</summary>
    public static void AnimateBarChart(WpfPlot plot) => HostFadeIn(plot);

    /// <summary>折线图：fade + scale 入场</summary>
    public static void AnimateLineChart(WpfPlot plot) => HostFadeIn(plot);

    /// <summary>Tab 切换批量 animate，stagger 60ms 错峰</summary>
    public static void AnimateTabCharts(params WpfPlot[] plots)
    {
        for (int i = 0; i < plots.Length; i++)
            HostFadeIn(plots[i], i * 60);
    }

    /// <summary>通用 UserControl slideInTop + fade 页面过渡</summary>
    public static void SlideInTop(FrameworkElement element, double delayMs = 0)
    {
        if (GetIsAnimating(element)) return;
        SetIsAnimating(element, true);

        ResetTransform(element);
        element.Opacity = 0;
        var transform = new TranslateTransform(0, -16);
        element.RenderTransform = transform;

        var sb = new Storyboard { BeginTime = TimeSpan.FromMilliseconds(delayMs) };
        var a1 = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)) { EasingFunction = EaseOut };
        var a2 = new DoubleAnimation(-16, 0, TimeSpan.FromMilliseconds(400)) { EasingFunction = EaseOut };
        Storyboard.SetTarget(a1, element); Storyboard.SetTargetProperty(a1, new PropertyPath("Opacity"));
        Storyboard.SetTarget(a2, transform); Storyboard.SetTargetProperty(a2, new PropertyPath("Y"));
        sb.Children.Add(a1); sb.Children.Add(a2);

        BeginSafely(sb, element);
    }
}