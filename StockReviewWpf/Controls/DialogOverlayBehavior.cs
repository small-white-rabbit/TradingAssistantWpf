using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace StockReviewWpf.Controls;

/// <summary>
/// 弹窗遮罩通用行为（附加属性）：
/// 1. ESC 关闭——遮罩可见时按 ESC，把 CloseOnEsc 指定的 VM 布尔属性置 false；
///    多个弹窗叠加时按注册逆序（XAML 后声明者在上层）只关最上面一个。
/// 2. 滚轮转发——光标在弹窗任意位置（含遮罩、内边距区）滚动时，
///    统一转发给弹窗内第一个 ScrollViewer，实现"鼠标在哪都能滚弹窗"。
/// 用法：ui:DialogOverlayBehavior.CloseOnEsc="IsAddDialogVisible"
/// </summary>
public static class DialogOverlayBehavior
{
    public static readonly DependencyProperty CloseOnEscProperty =
        DependencyProperty.RegisterAttached("CloseOnEsc", typeof(string), typeof(DialogOverlayBehavior),
            new PropertyMetadata(null, OnCloseOnEscChanged));

    public static string GetCloseOnEsc(DependencyObject obj) => (string?)obj.GetValue(CloseOnEscProperty) ?? "";

    public static void SetCloseOnEsc(DependencyObject obj, string value) => obj.SetValue(CloseOnEscProperty, value);

    // 每窗口的遮罩注册表（注册顺序 = XAML 声明顺序 ≈ 叠放层级，后者在上）
    private static readonly DependencyProperty RegistryProperty =
        DependencyProperty.RegisterAttached("Registry", typeof(List<FrameworkElement>), typeof(DialogOverlayBehavior),
            new PropertyMetadata(null));

    private static void OnCloseOnEscChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;

        el.PreviewMouseWheel -= OnPreviewMouseWheel;
        el.Loaded -= OnOverlayLoaded;
        el.Unloaded -= OnOverlayUnloaded;

        if (string.IsNullOrEmpty(e.NewValue as string)) return;

        el.PreviewMouseWheel += OnPreviewMouseWheel;
        el.Loaded += OnOverlayLoaded;
        el.Unloaded += OnOverlayUnloaded;
        if (el.IsLoaded) OnOverlayLoaded(el, new RoutedEventArgs());
    }

    // ===== 注册/注销（随页面加载/卸载动态维护，页面切换不残留） =====
    private static void OnOverlayLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        var window = Window.GetWindow(el);
        if (window == null) return;

        var reg = (List<FrameworkElement>?)window.GetValue(RegistryProperty);
        if (reg == null)
        {
            reg = new List<FrameworkElement>();
            window.SetValue(RegistryProperty, reg);
            window.PreviewKeyDown += OnWindowPreviewKeyDown;
        }
        if (!reg.Contains(el)) reg.Add(el);
    }

    private static void OnOverlayUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        var window = Window.GetWindow(el);
        if (window?.GetValue(RegistryProperty) is not List<FrameworkElement> reg) return;

        reg.Remove(el);
        if (reg.Count == 0)
        {
            window.PreviewKeyDown -= OnWindowPreviewKeyDown;
            window.SetValue(RegistryProperty, null);
        }
    }

    // ===== ESC 关闭：只关注册表里最上层的可见弹窗 =====
    private static void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (sender is not Window window) return;
        if (window.GetValue(RegistryProperty) is not List<FrameworkElement> reg) return;

        for (var i = reg.Count - 1; i >= 0; i--)
        {
            var el = reg[i];
            if (!el.IsVisible) continue;
            CloseOverlay(el);
            e.Handled = true;
            return;
        }
    }

    private static void CloseOverlay(FrameworkElement el)
    {
        var propName = GetCloseOnEsc(el);
        if (propName.Length > 0 && el.DataContext is { } dc)
        {
            var prop = dc.GetType().GetProperty(propName);
            if (prop?.PropertyType == typeof(bool) && prop.CanWrite && (bool)prop.GetValue(dc)!)
            {
                prop.SetValue(dc, false);
                return;
            }
        }
        // 兜底：VM 属性不可写时直接收起遮罩
        el.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
    }

    // ===== 滚轮转发：光标不在可滚内容上时，手动滚弹窗内的 ScrollViewer =====
    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0) return;
        if (sender is not FrameworkElement el) return;

        var sv = FindDescendant<ScrollViewer>(el);
        if (sv == null) return;

        // 事件源自 ScrollViewer 内部（光标在可滚内容上）→ 交给默认处理，避免双重滚动
        if (e.OriginalSource is DependencyObject src && IsDescendantOf(src, sv)) return;

        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private static bool IsDescendantOf(DependencyObject node, DependencyObject ancestor)
    {
        var cur = node;
        while (cur is not null)
        {
            if (ReferenceEquals(cur, ancestor)) return true;
            cur = VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur);
        }
        return false;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var found = FindDescendant<T>(child);
            if (found != null) return found;
        }
        return null;
    }
}