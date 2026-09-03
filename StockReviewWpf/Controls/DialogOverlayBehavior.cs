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

    // ===== 滚轮转发：全屏滚轮联动弹窗内容 =====
    // 两种场景：
    // 1. 光标不在任何可滚内容上（遮罩空白/标题区）→ 手动滚动弹窗内第一个可滚动的 ScrollViewer；
    // 2. 光标在嵌套 ScrollViewer 内且该层已滚到边界 → 接管并滚动外层（弹窗级），
    //    解决"内层滚完吞掉滚轮、外层不联动"的 WPF 嵌套滚动经典问题。
    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0) return;
        if (sender is not FrameworkElement el) return;

        // 弹窗内全部 ScrollViewer（视觉树先序：外层在前，内层在后）
        var viewers = new List<System.Windows.Controls.ScrollViewer>();
        FindDescendants(el, viewers);
        if (viewers.Count == 0) return;

        // 光标所在的 ScrollViewer（从最内层向外逐个检查是否到边界）
        if (e.OriginalSource is DependencyObject src)
        {
            // 从最内层（视觉树最深的包含者）向外：IsDescendantOf 对嵌套链上所有层都成立，
            // 按 viewers 逆序（内层优先）找到第一个未到边界的层交给默认处理
            for (var i = viewers.Count - 1; i >= 0; i--)
            {
                if (!IsDescendantOf(src, viewers[i])) continue;
                if (CanScroll(viewers[i], e.Delta)) return; // 该层还能滚 → 交给默认处理
                // 该层到边界 → 找更外层可同向滚动的
                for (var j = i - 1; j >= 0; j--)
                {
                    if (!CanScroll(viewers[j], e.Delta)) continue;
                    viewers[j].ScrollToVerticalOffset(viewers[j].VerticalOffset - e.Delta);
                    e.Handled = true;
                    return;
                }
                return; // 所有层都到边界
            }
        }

        // 光标不在可滚内容上：滚动第一个可滚动的 ScrollViewer
        foreach (var sv in viewers)
        {
            if (!CanScroll(sv, e.Delta)) continue;
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
            e.Handled = true;
            return;
        }
    }

    /// <summary>该 ScrollViewer 是否还能按滚轮方向滚动（上滚未到顶 / 下滚未到底）。</summary>
    private static bool CanScroll(System.Windows.Controls.ScrollViewer sv, double delta) =>
        sv.ScrollableHeight > 0 && (delta > 0 ? sv.VerticalOffset > 0 : sv.VerticalOffset < sv.ScrollableHeight - 0.5);

    private static void FindDescendants<T>(DependencyObject parent, List<T> found) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) found.Add(t);
            FindDescendants(child, found);
        }
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
}