using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace StockReviewWpf.Controls;

/// <summary>
/// 杂志排版流式面板：复刻原版 .insight-magazine 的 CSS Grid 布局——
/// 列宽 repeat(auto-fill, minmax(MinSlotWidth, 1fr))，间距 Gap，
/// 且每 3 张卡片的第 1 张（索引 0/3/6…）跨 2 列（magazine-large），
/// 同行卡片等高（对齐 CSS Grid 的 align-items: stretch 默认行为）。
/// </summary>
public class MagazinePanel : Panel
{
    public static readonly DependencyProperty MinSlotWidthProperty =
        DependencyProperty.Register(nameof(MinSlotWidth), typeof(double), typeof(MagazinePanel),
            new FrameworkPropertyMetadata(300.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty =
        DependencyProperty.Register(nameof(Gap), typeof(double), typeof(MagazinePanel),
            new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>单列最小宽度（对应 CSS minmax(300px, 1fr)）</summary>
    public double MinSlotWidth { get => (double)GetValue(MinSlotWidthProperty); set => SetValue(MinSlotWidthProperty, value); }

    /// <summary>列间距/行间距（对应 CSS gap: 16px）</summary>
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    // 测量阶段缓存的排列矩形（child → 最终 rect，同行等高）
    private readonly Dictionary<UIElement, Rect> _layout = new();

    protected override Size MeasureOverride(Size availableSize)
    {
        _layout.Clear();
        var gap = Gap;
        var availW = double.IsInfinity(availableSize.Width) ? 0 : Math.Max(0, availableSize.Width);

        // auto-fill：容纳的列数 = floor((可用宽 + gap) / (minSlot + gap))
        var cols = Math.Max(1, (int)Math.Floor((availW + gap) / (MinSlotWidth + gap)));
        var slot = cols == 1 ? availW : (availW - (cols - 1) * gap) / cols;
        if (slot < 0) slot = 0;

        // 行分配：贪心装箱，跨 2 列的卡片放不下则换行
        var rows = new List<List<(UIElement el, int col, int span)>>();
        var current = new List<(UIElement el, int col, int span)>();
        var col = 0;
        var index = 0;

        foreach (UIElement child in InternalChildren)
        {
            // 每组 3 张的第 1 张跨 2 列；单列布局时退化为 1 列
            var span = cols >= 2 && index % 3 == 0 ? 2 : 1;
            if (col + span > cols && col > 0)
            {
                rows.Add(current);
                current = new List<(UIElement, int, int)>();
                col = 0;
            }

            var w = slot * span + gap * (span - 1);
            child.Measure(new Size(w, double.PositiveInfinity));
            current.Add((child, col, span));
            col += span;
            index++;

            if (col >= cols)
            {
                rows.Add(current);
                current = new List<(UIElement, int, int)>();
                col = 0;
            }
        }
        if (current.Count > 0) rows.Add(current);

        // 同行卡片等高（CSS Grid stretch），按行累计 y
        var y = 0.0;
        foreach (var row in rows)
        {
            var h = 0.0;
            foreach (var (el, _, _) in row) h = Math.Max(h, el.DesiredSize.Height);
            foreach (var (el, c, span) in row)
                _layout[el] = new Rect(c * (slot + gap), y, slot * span + gap * (span - 1), h);
            y += h + gap;
        }

        return new Size(availW, Math.Max(0, y - gap));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            if (_layout.TryGetValue(child, out var rect))
                child.Arrange(rect);
            else
                child.Arrange(new Rect(0, 0, 0, 0));
        }
        return finalSize;
    }
}
