using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace StockReviewWpf.Controls;

/// <summary>
/// 弹窗入场动画（attached behavior）：
/// 在遮罩 Border 上设 controls:DialogAnimation.Enable="True"，
/// Visibility 变为 Visible 时——遮罩 180ms 淡入，第一个子面板 12px 上移 + 220ms 淡入（cubic ease-out）。
/// 只动 Opacity/Translate（合成友好），不触碰 Effect/布局。
/// </summary>
public static class DialogAnimation
{
    public static readonly DependencyProperty EnableProperty =
        DependencyProperty.RegisterAttached("Enable", typeof(bool), typeof(DialogAnimation),
            new PropertyMetadata(false, OnEnableChanged));

    public static bool GetEnable(DependencyObject obj) => (bool)obj.GetValue(EnableProperty);
    public static void SetEnable(DependencyObject obj, bool value) => obj.SetValue(EnableProperty, value);

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement overlay) return;
        if ((bool)e.NewValue)
            overlay.IsVisibleChanged += Overlay_IsVisibleChanged;
        else
            overlay.IsVisibleChanged -= Overlay_IsVisibleChanged;
    }

    private static void Overlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Border overlay || !overlay.IsVisible) return;

        // 遮罩淡入（含半透明背景一起渐显）
        overlay.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(180)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        // 面板上移入场：取第一个子元素（DialogPanel 主体）
        if (overlay.Child is not FrameworkElement panel) return;
        if (panel.RenderTransform is not TranslateTransform || panel.RenderTransform.IsFrozen)
            panel.RenderTransform = new TranslateTransform();
        panel.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        ((TranslateTransform)panel.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(12, 0, new Duration(TimeSpan.FromMilliseconds(220)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }
}
