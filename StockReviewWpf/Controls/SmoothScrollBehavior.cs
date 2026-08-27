using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace StockReviewWpf.Controls;

/// <summary>
/// 平滑滚动附加属性：给 ScrollViewer 的鼠标滚轮滚动添加惯性动画，
/// 模拟网页/触控滑动效果（逐帧插值，而非 WPF 默认的逐行跳跃）。
/// 用法：在 ScrollViewer 上设 controls:SmoothScrollBehavior.IsEnabled="True"
/// </summary>
public static class SmoothScrollBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(SmoothScrollBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly Dictionary<ScrollViewer, ScrollAnimState> _states = new();
    private static bool _isRendering;

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if ((bool)e.NewValue)
        {
            sv.PreviewMouseWheel -= OnPreviewMouseWheel;
            sv.PreviewMouseWheel += OnPreviewMouseWheel;
            sv.PanningMode = PanningMode.VerticalOnly;
            // 注意：不能设 sv.CanContentScroll = false —— 那会关闭 VirtualizingStackPanel
            // 的虚拟化，导致列表首帧全量实例化（页面打开卡顿数秒的主因）。
            // 像素级平滑滚动改由面板 ScrollUnit=Pixel 实现，虚拟化保持开启。
            sv.Loaded -= OnSvLoaded;
            sv.Loaded += OnSvLoaded;
            ApplyPixelScrollUnit(sv);
            sv.Unloaded += OnUnloaded;
        }
        else
        {
            sv.PreviewMouseWheel -= OnPreviewMouseWheel;
            sv.Loaded -= OnSvLoaded;
            _states.Remove(sv);
        }
    }

    private static void OnSvLoaded(object? sender, RoutedEventArgs e) => ApplyPixelScrollUnit((ScrollViewer)sender!);

    /// <summary>
    /// 把 ScrollViewer 可视树内虚拟化列表切到像素滚动单元：
    /// ScrollUnit=Pixel 时 VerticalOffset 以像素计（平滑滚动的插值目标），
    /// 同时虚拟化不受影响（仅渲染可视区条目）。
    /// 注意：VSP 实际从 ItemsControl 上读取 ScrollUnit 附加属性——只设在面板实例上无效，
    /// 必须设在 ItemsControl 上（并同步设面板 + 强制重新测量兜底）。
    /// </summary>
    private static void ApplyPixelScrollUnit(ScrollViewer sv)
    {
        var panel = FindDescendant<VirtualizingPanel>(sv);
        if (panel == null) return;
        panel.SetValue(VirtualizingPanel.ScrollUnitProperty, ScrollUnit.Pixel);
        var owner = FindAncestor<ItemsControl>(panel);
        if (owner != null && (ScrollUnit)owner.GetValue(VirtualizingPanel.ScrollUnitProperty) != ScrollUnit.Pixel)
        {
            owner.SetValue(VirtualizingPanel.ScrollUnitProperty, ScrollUnit.Pixel);
            panel.InvalidateMeasure();
        }
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        var cur = start;
        while (cur is not null)
        {
            cur = VisualTreeHelper.GetParent(cur);
            if (cur is T t) return t;
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer sv)
            _states.Remove(sv);
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        e.Handled = true;

        if (!_states.TryGetValue(sv, out var state))
        {
            state = new ScrollAnimState();
            _states[sv] = state;
        }

        var delta = e.Delta > 0 ? -72 : 72;
        var newTarget = Math.Max(0, Math.Min(sv.ScrollableHeight, state.TargetOffset + delta));

        if ((newTarget > state.TargetOffset && delta < 0) || (newTarget < state.TargetOffset && delta > 0))
            state.CurrentOffset = sv.VerticalOffset;

        state.TargetOffset = newTarget;
        state.StartOffset = state.CurrentOffset;
        state.StartTime = Environment.TickCount64;

        if (!state.IsAnimating)
        {
            state.IsAnimating = true;
            StartRendering();
        }
    }

    private static void StartRendering()
    {
        if (_isRendering) return;
        _isRendering = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private static void StopRendering()
    {
        if (!_isRendering) return;
        var any = false;
        foreach (var s in _states.Values)
            if (s.IsAnimating) { any = true; break; }
        if (!any)
        {
            _isRendering = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        var toRemove = new List<ScrollViewer>();

        foreach (var kv in _states)
        {
            var sv = kv.Key;
            var st = kv.Value;
            if (!st.IsAnimating) continue;

            var elapsed = now - st.StartTime;
            var progress = Math.Min(1.0, elapsed / 150.0);
            var eased = 1 - Math.Pow(1 - progress, 3);
            st.CurrentOffset = st.StartOffset + (st.TargetOffset - st.StartOffset) * eased;

            try { sv.ScrollToVerticalOffset(st.CurrentOffset); }
            catch { toRemove.Add(sv); continue; }

            if (progress >= 1.0)
                st.IsAnimating = false;
        }

        foreach (var sv in toRemove)
            _states.Remove(sv);

        StopRendering();
    }

    private class ScrollAnimState
    {
        public double CurrentOffset;
        public double TargetOffset;
        public double StartOffset;
        public long StartTime;
        public bool IsAnimating;
    }
}
