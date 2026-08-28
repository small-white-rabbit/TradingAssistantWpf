using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using StockReviewWpf.Services;

namespace StockReviewWpf.Controls;

/// <summary>
/// RichTextBox 富文本绑定附加属性：弥补 WPF RichTextBox.Document 无法直接 DataBinding 的缺陷。
/// 在 DataTemplate 中用 controls:RichTextBehavior.Content="{Binding Summary}" 绑定内容，
/// 属性变化时自动调用 RichTextUtil.LoadInto 渲染（兼容 HTML/RTF/纯文本）。
/// 可选 LineHeight 附加属性：对齐纸张横线（BlockLineHeight）。
/// 用法：
/// &lt;RichTextBox controls:RichTextBehavior.Content="{Binding Summary}"
///              controls:RichTextBehavior.LineHeight="28"
///              IsReadOnly="True" .../&gt;
/// </summary>
public static class RichTextBehavior
{
    public static readonly DependencyProperty ContentProperty =
        DependencyProperty.RegisterAttached("Content", typeof(string), typeof(RichTextBehavior),
            new PropertyMetadata(null, OnContentChanged));

    public static string? GetContent(DependencyObject obj) => (string?)obj.GetValue(ContentProperty);
    public static void SetContent(DependencyObject obj, string? value) => obj.SetValue(ContentProperty, value);

    public static readonly DependencyProperty LineHeightProperty =
        DependencyProperty.RegisterAttached("LineHeight", typeof(double), typeof(RichTextBehavior),
            new PropertyMetadata(double.NaN, OnLineHeightChanged));

    public static double GetLineHeight(DependencyObject obj) => (double)obj.GetValue(LineHeightProperty);
    public static void SetLineHeight(DependencyObject obj, double value) => obj.SetValue(LineHeightProperty, value);

    /// <summary>列表卡连续空行压缩上限：用户连敲多个空行（&lt;p&gt;&lt;br&gt;&lt;/p&gt;）在高度受限的
    /// 列表卡里会挤占正文，设为 N 后任意连续空段序列最多保留 N 行、多余删除。0=不压缩（详情默认）。</summary>
    public static readonly DependencyProperty MaxBlankRowsProperty =
        DependencyProperty.RegisterAttached("MaxBlankRows", typeof(int), typeof(RichTextBehavior),
            new PropertyMetadata(0, OnMaxBlankRowsChanged));

    public static int GetMaxBlankRows(DependencyObject obj) => (int)obj.GetValue(MaxBlankRowsProperty);
    public static void SetMaxBlankRows(DependencyObject obj, int value) => obj.SetValue(MaxBlankRowsProperty, value);

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox rtb) return;
        var content = e.NewValue as string ?? "";
        RichTextUtil.LoadInto(rtb, content);
        ApplyLineHeight(rtb);
        CollapseBlankRows(rtb);
    }

    private static void OnLineHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox rtb) return;
        ApplyLineHeight(rtb);
    }

    private static void OnMaxBlankRowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox rtb) return;
        CollapseBlankRows(rtb);
    }

    /// <summary>对齐纸张横线：给所有段落（含列表项内段落）设统一行高 + BlockLineHeight，
    /// 并清零段间距/内边距——WPF 段落默认有外边距，短行内容会被撑出“正文+空白行”的
    /// 双倍行高观感（如 2 行内容显示为 4 行），清零后每段恰好一行、贴合横线。
    /// 显式空行 &lt;p&gt;&lt;br&gt;&lt;/p&gt; 仍渲染为一条 28px 的 LineBreak（保留用户敲的空行）。</summary>
    private static void ApplyLineHeight(RichTextBox rtb)
    {
        var lh = GetLineHeight(rtb);
        if (double.IsNaN(lh) || lh <= 0) return;
        var zero = new Thickness(0);
        foreach (var block in rtb.Document.Blocks)
            ApplyToBlock(block, lh, zero);
    }

    private static void ApplyToBlock(Block block, double lh, Thickness zero)
    {
        block.LineHeight = lh;
        block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        block.Margin = zero;
        block.Padding = zero;
        switch (block)
        {
            case List list:
                foreach (var li in list.ListItems)
                    foreach (var b in li.Blocks)
                        ApplyToBlock(b, lh, zero);
                break;
            case Section sec:
                foreach (var b in sec.Blocks)
                    ApplyToBlock(b, lh, zero);
                break;
        }
    }

    /// <summary>折叠连续空段至不超过 MaxBlankRows 行。空段=无内联，或仅含 LineBreak/空白 Run。
    /// 两阶段：先标记后删除，避免遍历中改集合。List/Section 内部递归处理。</summary>
    private static void CollapseBlankRows(RichTextBox rtb)
    {
        var max = GetMaxBlankRows(rtb);
        if (max <= 0) return;
        CollapseBlankBlocks(rtb.Document.Blocks, max);
    }

    // 两层折叠，缺一不可：
    // 1) 段内：单个段落里连续的"空白内联"（LineBreak/空白 Run）折叠至 ≤max。
    //    处理 &lt;p&gt;&lt;br&gt;&lt;br&gt;&lt;/p&gt; 这类"一段多换行"——按段计数会
    //    把整段算 1 个空段（streak=1≤max 保留），但它视觉是 max+1 行，压缩不到。
    // 2) 段间：连续空段（整段仅含空白内联）保留至多 max 段、多余删除。
    //    处理 &lt;p&gt;&lt;br&gt;&lt;/p&gt;&lt;p&gt;&lt;br&gt;&lt;/p&gt; 这类"多段空行"。
    // 注意：上限若设为 2，则"恰好 2 个连续空段"会被全部保留→输出仍 2 行→对最常见的
    // 2 空行场景零效果。要让 2 行被压缩，上限必须 <2（即 1）。列表卡用 1。
    private static void CollapseBlankBlocks(BlockCollection blocks, int max)
    {
        var remove = new List<Block>();
        var streak = 0;
        foreach (var block in blocks)
        {
            if (block is Paragraph p)
            {
                TrimBlankInlines(p, max);
                if (IsBlankParagraph(p))
                {
                    streak++;
                    if (streak > max) remove.Add(p);
                    continue;
                }
                streak = 0;
                continue;
            }
            streak = 0;
            if (block is List list)
            {
                foreach (var li in list.ListItems)
                    CollapseBlankBlocks(li.Blocks, max);
            }
            else if (block is Section sec)
            {
                CollapseBlankBlocks(sec.Blocks, max);
            }
        }
        foreach (var b in remove)
            blocks.Remove(b);
    }

    // 段内连续空白内联折叠至 ≤max。两阶段：先收集后删除，避免遍历中改集合。
    private static void TrimBlankInlines(Paragraph p, int max)
    {
        if (p.Inlines.Count == 0) return;
        var remove = new List<Inline>();
        var streak = 0;
        foreach (var inline in p.Inlines)
        {
            if (IsBlankInline(inline))
            {
                streak++;
                if (streak > max) remove.Add(inline);
                continue;
            }
            streak = 0;
        }
        foreach (var il in remove)
            p.Inlines.Remove(il);
    }

    private static bool IsBlankParagraph(Paragraph p)
    {
        if (p.Inlines.Count == 0) return true;
        foreach (var inline in p.Inlines)
            if (!IsBlankInline(inline)) return false;
        return true;
    }

    private static bool IsBlankInline(Inline inline)
    {
        if (inline is LineBreak) return true;
        if (inline is Run r && string.IsNullOrWhiteSpace(r.Text)) return true;
        return false;
    }
}
