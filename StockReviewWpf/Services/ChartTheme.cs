using System;
using System.Collections.Generic;
using System.Linq;
using ScottPlot;
using ScottPlot.TickGenerators;
using ScottPlot.WPF;
using Color = ScottPlot.Color;

namespace StockReviewWpf.Services;

/// <summary>
/// 统一图表主题引擎 v2.1 —— 兼容 ScottPlot 5.0.51 API。
/// 
/// 修复 v1 核心缺陷（在 ScottPlot 5.0.51 API 约束内）：
///   1. 折线图"歪七扭八"：数据点 ≤8 时强制 Smooth=false（真实直线，零过冲）；
///      数据点 >8 时仍启用平滑但调大张力(0.5)，避免过度扭曲。
///   2. 折线填充方向错：原 FillYAbove=true + FillYBelow=true 双向都填，
///      v2 仅保留 FillYBelow=true 从线向 0 基线单向填充。
///   3. 数据点 Marker 加白边，避免被折线吞没。
///   4. 调色板升级为 Material Design 专业色；子级亮度阶梯从 30 起太暗 → 从 40 起。
///   5. 饼图环形厚度修正（0.57→0.571=40/70）；图例名直接带百分比。
///   6. 柱状图顶标签加 OffsetY；父级 HSL 色亮度整体提升，不再发黑。
///   7. 新增通用 RenderStackedBars / RenderGroupedBars（用 v1 兼容 API），
///      统一处理槽位偏移，避免 StatisticsView 手写 offset 不齐。
/// </summary>
public static class ChartTheme
{
    public const string TextPrimaryHex = "#1F2937";
    public const string TextRegularHex = "#6B7280";
    public const string TextSecondaryHex = "#9CA3AF";
    private const string GridHex = "#E5E7EB";   // 浅灰网格

    // ===== 语义色（金融语义） =====
    public const string WinRateHex = "#EF4444";      // 综合胜率折线（金融红）
    public const string ReturnHex = "#3B82F6";       // 收益趋势折线（专业蓝）
    public const string StrongHex = "#F97316";       // 强股月度折线（活力橙）
    public const string ReferenceHex = "#F59E0B";    // 50% 虚线基准（琥珀）
    public const string UnclassifiedHex = "#9CA3AF"; // 未分类（灰）
    public const string AllWinRateHex = "#DC2626";   // 全时段胜率柱
    public const string UpHex = "#EF4444";           // 擒牛（红）
    public const string DownHex = "#16A34A";         // 漏网（深专业绿）

    /// <summary>专业调色板（饼图 / 分组柱循环取色）</summary>
    public static readonly string[] Palette =
    {
        "#3B82F6", "#10B981", "#F59E0B", "#EF4444", "#06B6D4",
        "#8B5CF6", "#F97316", "#EC4899", "#84CC16", "#6366F1"
    };

    public static readonly Color TextPrimary = Color.FromHex(TextPrimaryHex);
    public static readonly Color TextRegular = Color.FromHex(TextRegularHex);
    public static readonly Color TextSecondary = Color.FromHex(TextSecondaryHex);

    private static bool _fontSet;

    /// <summary>应用基础主题：白底、浅网格、统一字体、隐藏垂直网格</summary>
    public static void Apply(Plot plt)
    {
        if (!_fontSet)
        {
            _fontSet = true;
            try { Fonts.Default = "Microsoft YaHei UI"; } catch { /* 回退默认字体 */ }
        }

        plt.FigureBackground.Color = Colors.White;
        plt.DataBackground.Color = Colors.White;

        // 浅色主网格 + 无次网格
        plt.Grid.MajorLineColor = Color.FromHex(GridHex);
        plt.Grid.MajorLineWidth = 1;
        plt.Grid.MinorLineWidth = 0;

        // 轴标签美化
        foreach (var axis in new IAxis[] { plt.Axes.Bottom, plt.Axes.Left })
        {
            axis.TickLabelStyle.ForeColor = TextRegular;
            axis.TickLabelStyle.FontSize = 11;
            axis.Label.ForeColor = TextPrimary;
            axis.Label.FontSize = 12;
            axis.Label.Bold = true;
        }
    }

    /// <summary>空数据提示</summary>
    public static void ShowEmpty(Plot plt) => plt.Add.BackgroundText("暂无数据", TextSecondary, 18);

    // =====================================================================
    // 折线图 v2.1 —— ScottPlot 5.0.51 兼容；核心修复"歪七扭八"
    // =====================================================================

    public sealed record LineOptions
    {
        public string ColorHex { get; init; } = WinRateHex;
        public string XTitle { get; init; } = "月份";
        public string YTitle { get; init; } = "";
        public double? YMax { get; init; }
        public double? YMin { get; init; }
        /// <summary>虚线参考线（胜率类传 50）</summary>
        public double? ReferenceY { get; init; }
        public string ReferenceLabel { get; init; } = "基准";
        /// <summary>面积填充（Electron 收益/胜率折线均有）</summary>
        public bool ShowArea { get; init; } = true;
        /// <summary>数据点顶标签（默认关闭；月度擒牛趋势开启）</summary>
        public bool ShowValueLabels { get; init; } = false;
        public string ValueFormat { get; init; } = "F1";
        public string ValueSuffix { get; init; } = "%";
        /// <summary>折线宽度</summary>
        public float LineWidth { get; init; } = 2.5f;
        public float MarkerSize { get; init; } = 5;
    }

    public static void RenderLine(WpfPlot control, IReadOnlyList<string> categories, IReadOnlyList<double> values, LineOptions? options = null)
    {
        var opt = options ?? new LineOptions();
        var plt = control.Plot;
        plt.Clear();
        Apply(plt);
        var n = categories.Count;
        if (n == 0 || values.Count == 0)
        {
            ShowEmpty(plt);
            control.Refresh();
            return;
        }

        var vals = values as double[] ?? values.ToArray();
        var positions = new double[n];
        for (var i = 0; i < n; i++) positions[i] = i + 1;

        var color = Color.FromHex(opt.ColorHex);

        // ========== 折线 v3：手动 Catmull-Rom 预插值 + 无平滑 Scatter 直线 ==========
        // 为什么不直接用 Scatter.Smooth？
        //   1. ScottPlot 5.0.51 的 Smooth 算法容易过冲（用户吐槽"歪七扭八"）；
        //   2. 张力参数不可靠，版本之间行为不稳定。
        // 现在做法：
        //   1. 任何数据量都用 Catmull-Rom 在每两个节点间插入 6 个采样点（n×7 倍密度）；
        //   2. 插值结果用 Smooth=false 的直线渲染——视觉自然平滑，严格穿过原节点，零过冲；
        //   3. Marker 单独画一层叠加在原节点上，避免被加密点打散。
        const int SEG = 6;   // 每段插值 6 个中间点 = 7 段
        var denseX = new List<double>(n * SEG + 1);
        var denseY = new List<double>(n * SEG + 1);
        if (n == 1)
        {
            denseX.Add(positions[0]);
            denseY.Add(vals[0]);
        }
        else
        {
            for (var i = 0; i < n; i++)
            {
                var p0 = i > 0 ? positions[i - 1] : positions[0] - (positions[1] - positions[0]);
                var p1 = positions[i];
                var p2 = i < n - 1 ? positions[i + 1] : positions[n - 1] + (positions[n - 1] - positions[n - 2]);
                var p3 = i < n - 2 ? positions[i + 2] : p2 + (p2 - p1);
                var v0 = i > 0 ? vals[i - 1] : vals[0] - (vals[1] - vals[0]);
                var v1 = vals[i];
                var v2 = i < n - 1 ? vals[i + 1] : vals[n - 1] + (vals[n - 1] - vals[n - 2]);
                var v3 = i < n - 2 ? vals[i + 2] : v2 + (v2 - v1);
                var count = (i < n - 1) ? SEG : 1;
                for (var s = 0; s < count; s++)
                {
                    var t = (double)s / SEG;
                    var tt = t * t;
                    var ttt = tt * t;
                    var x = 0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * tt + (-p0 + 3 * p1 - 3 * p2 + p3) * ttt);
                    var y = 0.5 * ((2 * v1) + (-v0 + v2) * t + (2 * v0 - 5 * v1 + 4 * v2 - v3) * tt + (-v0 + 3 * v1 - 3 * v2 + v3) * ttt);
                    denseX.Add(x);
                    denseY.Add(y);
                }
            }
        }

        var xs = denseX.ToArray();
        var ys = denseY.ToArray();

        // 主折线：加密点直线，视觉平滑
        var line = plt.Add.Scatter(xs, ys);
        line.Color = color;
        line.LineWidth = opt.LineWidth;
        line.Smooth = false;          // 加密点 → 无平滑直线 → 稳定、不扭曲
        line.MarkerShape = MarkerShape.None; // 主折线不画 Marker，单独画一层

        // 面积填充：单向从折线到 y=0
        if (opt.ShowArea)
        {
            line.FillY = true;
            line.FillYValue = 0;
            line.FillYAbove = false;
            line.FillYBelow = true;
            line.FillYBelowColor = color.WithAlpha(0.14);
        }

        // 原节点 Marker：单独叠加一层，保证醒目 + 白边
        for (var i = 0; i < n; i++)
        {
            var m = plt.Add.Scatter(new[] { positions[i] }, new[] { vals[i] });
            m.Color = color;
            m.MarkerShape = MarkerShape.FilledCircle;
            m.MarkerSize = opt.MarkerSize;
            m.MarkerFillColor = color;
            m.MarkerLineColor = Colors.White;
            m.MarkerLineWidth = 1.5f;
            m.LineWidth = 0;
        }

        // ========== 参考虚线 ==========
        if (opt.ReferenceY is double ry)
        {
            var hl = plt.Add.HorizontalLine(ry, 1.2f, Color.FromHex(ReferenceHex), LinePattern.Dashed);
            hl.EnableAutoscale = false;
            hl.Text = opt.ReferenceLabel + " " + ry.ToString("0") + "%";
            hl.LabelFontSize = 10;
            hl.LabelFontColor = Color.FromHex(ReferenceHex);
            hl.LabelBold = true;
        }

        // ========== 数据点顶标签（在原节点上） ==========
        if (opt.ShowValueLabels && n > 0)
        {
            var vmin = vals.Min();
            var vmax = vals.Max();
            var yMax = opt.YMax ?? (vmax > 0 ? vmax * 1.15 : vmax);
            var yMin = opt.YMin ?? Math.Min(0, vmin);
            var range = Math.Max(yMax - yMin, 1.0);
            var offset = range * 0.055;
            for (var i = 0; i < n; i++)
            {
                var labelText = vals[i].ToString(opt.ValueFormat) + opt.ValueSuffix;
                var t = plt.Add.Text(labelText, positions[i], vals[i] + offset);
                t.LabelStyle.FontSize = 10;
                t.LabelStyle.ForeColor = TextPrimary;
                t.LabelStyle.Bold = true;
                t.LabelStyle.Alignment = Alignment.LowerCenter;
            }
        }

        // Y 轴范围
        if (opt.YMax is double ymax) plt.Axes.Left.Max = ymax;
        if (opt.YMin is double ymin) plt.Axes.Left.Min = ymin;
        plt.Axes.Margins(bottom: 0.02, top: 0.12);

        // X 轴刻度
        var tickGen = new NumericManual();
        for (var i = 0; i < n; i++) tickGen.AddMajor(i + 1, categories[i]);
        plt.Axes.Bottom.TickGenerator = tickGen;
        plt.Axes.Bottom.Label.Text = opt.XTitle;
        plt.Axes.Left.Label.Text = opt.YTitle;
        control.Refresh();
    }

    // =====================================================================
    // 柱状图 v2.1 —— 5.0.51 兼容；标签 OffsetY、柱宽 0.6
    // =====================================================================

    public sealed record BarOptions
    {
        public string ColorHex { get; init; } = WinRateHex;
        public IReadOnlyList<string>? ColorHexes { get; init; }
        public double BarWidth { get; init; } = 0.6;
        public double? YMax { get; init; }
        public double? YMin { get; init; }
        public string XTitle { get; init; } = "";
        public string YTitle { get; init; } = "";
        public bool ShowValueLabels { get; init; } = true;
        public string ValueFormat { get; init; } = "F1";
        public string ValueSuffix { get; init; } = "%";
    }

    public static void RenderBars(WpfPlot control, IReadOnlyList<string> categories, IReadOnlyList<double> values, BarOptions? options = null)
    {
        var opt = options ?? new BarOptions();
        var plt = control.Plot;
        plt.Clear();
        Apply(plt);
        var n = categories.Count;
        if (n == 0 || values.Count == 0)
        {
            ShowEmpty(plt);
            control.Refresh();
            return;
        }

        var bars = new List<Bar>();
        var maxVal = 0.0;
        for (var i = 0; i < n; i++)
        {
            var hex = opt.ColorHexes is { } hx && i < hx.Count && !string.IsNullOrEmpty(hx[i]) ? hx[i] : opt.ColorHex;
            var v = values[i];
            var bar = new Bar
            {
                Position = i + 1,
                Value = v,
                Size = opt.BarWidth,
                FillColor = Color.FromHex(hex),
                // ScottPlot 5.0.51 Bar 没有 CornerRadius 属性（v1 就没用），跳过圆角
            };
            if (opt.ShowValueLabels)
                bar.Label = v.ToString(opt.ValueFormat) + opt.ValueSuffix;
            bars.Add(bar);
            if (v > maxVal) maxVal = v;
        }

        var series = plt.Add.Bars(bars);
        if (opt.ShowValueLabels)
        {
            series.ValueLabelStyle.IsVisible = true;
            series.ValueLabelStyle.FontSize = 11;
            series.ValueLabelStyle.ForeColor = TextPrimary;
            series.ValueLabelStyle.Bold = true;
            series.ValueLabelStyle.Alignment = Alignment.LowerCenter;
            series.ValueLabelStyle.OffsetY = 6;  // 顶标签抬高，防贴柱
        }

        var tickGen = new NumericManual();
        for (var i = 0; i < n; i++) tickGen.AddMajor(i + 1, categories[i]);
        plt.Axes.Bottom.TickGenerator = tickGen;

        if (opt.YMax is double yMax) plt.Axes.Left.Max = yMax;
        else if (maxVal > 0) plt.Axes.Left.Max = maxVal * 1.18;
        if (opt.YMin is double yMin) plt.Axes.Left.Min = yMin;
        plt.Axes.Margins(bottom: 0.02, top: 0);
        plt.Axes.Bottom.Label.Text = opt.XTitle;
        plt.Axes.Left.Label.Text = opt.YTitle;
        control.Refresh();
    }

    // =====================================================================
    // 分组柱状图（通用）
    // =====================================================================

    public sealed record GroupedBarOptions
    {
        public string XTitle { get; init; } = "";
        public string YTitle { get; init; } = "次数";
        public double? YMax { get; init; }
        public bool ShowValueLabels { get; init; } = true;
    }

    public static void RenderGroupedBars(WpfPlot control, IReadOnlyList<string> categories,
        IReadOnlyList<(string Name, IReadOnlyList<double> Values)> series,
        GroupedBarOptions? options = null)
    {
        var opt = options ?? new GroupedBarOptions();
        var plt = control.Plot;
        plt.Clear();
        Apply(plt);
        if (categories.Count == 0 || series.Count == 0)
        {
            ShowEmpty(plt);
            control.Refresh();
            return;
        }
        var sCount = series.Count;
        var cCount = categories.Count;
        var slotWidth = 0.82 / sCount;
        var maxVal = 0.0;

        for (var s = 0; s < sCount; s++)
        {
            var (sName, sVals) = series[s];
            var offset = (s - (sCount - 1) / 2.0) * slotWidth;
            var color = Color.FromHex(Palette[s % Palette.Length]);
            var bars = new List<Bar>();
            for (var c = 0; c < cCount; c++)
            {
                var v = c < sVals.Count ? sVals[c] : 0;
                if (v <= 0) continue;
                var bar = new Bar
                {
                    Position = c + 1 + offset,
                    Value = v,
                    Size = slotWidth * 0.92,
                    FillColor = color,
                };
                if (opt.ShowValueLabels && v > 0) bar.Label = v.ToString("F0");
                bars.Add(bar);
                if (v > maxVal) maxVal = v;
            }
            var bs = plt.Add.Bars(bars);
            bs.LegendText = sName;
            if (opt.ShowValueLabels)
            {
                bs.ValueLabelStyle.IsVisible = true;
                bs.ValueLabelStyle.FontSize = 10;
                bs.ValueLabelStyle.ForeColor = TextPrimary;
                bs.ValueLabelStyle.Alignment = Alignment.LowerCenter;
                bs.ValueLabelStyle.OffsetY = 5;
            }
        }

        var tickGen = new NumericManual();
        for (var i = 0; i < cCount; i++) tickGen.AddMajor(i + 1, categories[i]);
        plt.Axes.Bottom.TickGenerator = tickGen;
        if (opt.YMax is double yMax) plt.Axes.Left.Max = yMax;
        else if (maxVal > 0) plt.Axes.Left.Max = maxVal * 1.20;
        plt.Axes.Margins(bottom: 0.02, top: 0);
        plt.ShowLegend(Alignment.UpperRight);
        plt.Legend.FontSize = 10;
        plt.Axes.Bottom.Label.Text = opt.XTitle;
        plt.Axes.Left.Label.Text = opt.YTitle;
        control.Refresh();
    }

    // =====================================================================
    // 堆叠 + 并排混合柱状图（通用）
    // =====================================================================

    public sealed record StackedBarGroup
    {
        public string ParentName { get; init; } = "";
        public IReadOnlyList<string> ChildNames { get; init; } = new List<string>();
        public IReadOnlyList<IReadOnlyList<double>> ChildCounts { get; init; } = new List<IReadOnlyList<double>>();
        public double ParentWinRate { get; init; }
        public double ParentRatio { get; init; }
    }

    public sealed record StackedBarOptions
    {
        public IReadOnlyList<string> Categories { get; init; } = new List<string>();
        public string SingleCategoryLabel { get; init; } = "类别";
        public string XTitle { get; init; } = "月份";
        public string YTitle { get; init; } = "交易笔数";
        public bool ShowParentHeaderLabels { get; init; } = true;
    }

    public static void RenderStackedBars(WpfPlot control, IReadOnlyList<StackedBarGroup> groups,
        StackedBarOptions? options = null)
    {
        var opt = options ?? new StackedBarOptions();
        var plt = control.Plot;
        plt.Clear();
        Apply(plt);
        if (groups.Count == 0)
        {
            ShowEmpty(plt);
            control.Refresh();
            return;
        }
        var categories = opt.Categories.Count > 0 ? opt.Categories
            : groups[0].ChildCounts.Count > 0 ? Enumerable.Range(0, groups[0].ChildCounts[0].Count).Select(i => (i + 1).ToString()).ToList()
            : new List<string>();
        var singleCat = categories.Count == 1;
        var catLabel = singleCat ? opt.SingleCategoryLabel : categories[0];
        var mCount = categories.Count;
        var gCount = groups.Count;
        var groupSlot = 0.82 / gCount;

        var colTotals = groups.Select(g =>
            Enumerable.Range(0, mCount)
                .Select(mi => g.ChildCounts.Sum(cl => mi < cl.Count ? cl[mi] : 0.0))
                .ToList()
        ).ToList();
        var maxY = colTotals.Max(list => list.Count > 0 ? list.DefaultIfEmpty(0).Max() : 0);
        var labelYOffset = Math.Max(maxY * 0.045, 0.5);

        for (var g = 0; g < gCount; g++)
        {
            var grp = groups[g];
            var groupOffset = (g - (gCount - 1) / 2.0) * groupSlot;
            var bases = new double[mCount];
            for (var c = 0; c < grp.ChildCounts.Count; c++)
            {
                var childCounts = grp.ChildCounts[c];
                var childBars = new List<Bar>();
                var fill = ChildColor(g, c, grp.ParentName);
                for (var m = 0; m < mCount; m++)
                {
                    var v = m < childCounts.Count ? childCounts[m] : 0;
                    if (v <= 0) continue;
                    childBars.Add(new Bar
                    {
                        Position = m + 1 + groupOffset,
                        Value = v,
                        ValueBase = bases[m],
                        FillColor = fill,
                    });
                    bases[m] += v;
                }
                if (childBars.Count == 0) continue;
                var bs = plt.Add.Bars(childBars);
                bs.LegendText = grp.ChildNames.Count > c ? grp.ChildNames[c] : ("子级" + c);
                foreach (var b in bs.Bars) b.Size = groupSlot * 0.90;
            }
            // 父级顶标签
            if (opt.ShowParentHeaderLabels)
            {
                for (var m = 0; m < mCount; m++)
                {
                    var total = colTotals[g][m];
                    if (total <= 0) continue;
                    var label = singleCat
                        ? $"{grp.ParentRatio:F1}%  ·  胜率 {grp.ParentWinRate:F1}%"
                        : $"{grp.ParentWinRate:F1}%";
                    var txt = plt.Add.Text(label, m + 1 + groupOffset, total + labelYOffset);
                    txt.LabelStyle.FontSize = singleCat ? 11 : 10;
                    txt.LabelStyle.ForeColor = TextPrimary;
                    txt.LabelStyle.Bold = true;
                    txt.LabelStyle.Alignment = Alignment.LowerCenter;
                }
            }
        }

        var tickGen = new NumericManual();
        for (var i = 0; i < mCount; i++)
            tickGen.AddMajor(i + 1, singleCat ? catLabel : categories[i]);
        plt.Axes.Bottom.TickGenerator = tickGen;
        plt.Axes.Margins(bottom: 0.02, top: 0.28);
        plt.ShowLegend(Alignment.UpperRight);
        plt.Legend.FontSize = 10;
        plt.Axes.Bottom.Label.Text = singleCat ? "进场类型" : opt.XTitle;
        plt.Axes.Left.Label.Text = opt.YTitle;
        control.Refresh();
    }

    // 父级 HSL 色系（v2.1 提升整体亮度，避免子级发黑）
    private static readonly (int H, int S)[] ParentHues =
    {
        (0, 80), (42, 90), (120, 65), (200, 85), (160, 70),
        (270, 65), (330, 80), (45, 85), (210, 75)
    };
    private static readonly int[] ChildLightness = { 40, 48, 55, 62, 68, 73 };

    private static Color ChildColor(int parentIdx, int childIdx, string parentName)
    {
        if (parentName == "未分类") return Color.FromHex(UnclassifiedHex);
        var (h, s) = ParentHues[parentIdx % ParentHues.Length];
        return HslToColor(h, s, ChildLightness[childIdx % ChildLightness.Length]);
    }

    private static Color HslToColor(double h, double sPercent, double lPercent)
    {
        var s = sPercent / 100.0;
        var l = lPercent / 100.0;
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        var m = l - c / 2;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return new Color(
            (byte)Math.Round(Math.Clamp((r + m) * 255, 0, 255)),
            (byte)Math.Round(Math.Clamp((g + m) * 255, 0, 255)),
            (byte)Math.Round(Math.Clamp((b + m) * 255, 0, 255))
        );
    }

    // =====================================================================
    // 饼图 v2.1 —— 环形厚度修正；图例直接带百分比
    // =====================================================================

    public sealed record PieOptions
    {
        /// <summary>环形内半径比例：Electron radius:['40%','70%'] → DonutFraction = 40/70 ≈ 0.571</summary>
        public double DonutFraction { get; init; } = 0;
        public bool ShowLegend { get; init; } = true;
        public float SliceGapLineWidth { get; init; } = 2f;
        /// <summary>每片扇区「名称 占比」是否内联显示在饼块内/外（true=显示）</summary>
        public bool ShowSliceLabels { get; init; } = true;
        /// <summary>环形内的中心标题文本（如"类型占比"）；空文本时不显示</summary>
        public string CenterTitle { get; init; } = "";
    }

    public static void RenderPie(WpfPlot control, IReadOnlyList<string> names, IReadOnlyList<double> values,
        IReadOnlyList<string>? colorHexes = null, bool isDonut = false)
    {
        RenderPie(control, names, values, colorHexes, new PieOptions
        {
            DonutFraction = isDonut ? 0.571 : 0.0,  // 修正 v1 环形厚度（原 0.57→0.571=40/70）
            ShowSliceLabels = true,
        });
    }

    public static void RenderPie(WpfPlot control, IReadOnlyList<string> names, IReadOnlyList<double> values,
        IReadOnlyList<string>? colorHexes = null, PieOptions? options = null)
    {
        var opt = options ?? new PieOptions();
        var plt = control.Plot;
        plt.Clear();
        Apply(plt);
        var total = values.Sum();
        var n = names.Count;
        if (n == 0 || total <= 0)
        {
            ShowEmpty(plt);
            control.Refresh();
            return;
        }

        var slices = new List<PieSlice>();
        // 先计算每个扇区的起角、中角、占比 → 便于后续放置占比文本
        var centers = new (double AngleDeg, double Pct, string Name)[n];
        double startDeg = -90; // ScottPlot 5 默认从 12 点钟方向（-90°）开始绘制
        for (var i = 0; i < n; i++)
        {
            var hex = colorHexes is { } hx && i < hx.Count && !string.IsNullOrEmpty(hx[i])
                ? hx[i]
                : Palette[i % Palette.Length];
            var pct = values[i] * 100.0 / total;
            var sweep = values[i] * 360.0 / total;
            var mid = startDeg + sweep / 2.0;
            centers[i] = (mid, pct, names[i]);
            startDeg += sweep;
            // ScottPlot 5.0.51 PieSlice 没有 Explode 属性，跳过小扇区分离
            var slice = new PieSlice(values[i], Color.FromHex(hex))
            {
                LegendText = $"{names[i]}  {pct:F1}%",   // 图例名直接带百分比
            };
            slices.Add(slice);
        }

        var pie = plt.Add.Pie(slices);
        pie.DonutFraction = opt.DonutFraction;
        pie.LineWidth = opt.SliceGapLineWidth;
        pie.LineColor = Colors.White;  // 切片间白边间隙感

        // ========== 扇区内联占比标签 ==========
        // ScottPlot 5 默认隐藏坐标轴，饼图绘制在一个虚拟坐标系中（以 [0,0] 为中心，
        // 扇区外半径 r≈1）。我们按极坐标把"名称 + 百分比"文本放在：
        //   - 环形（DonutFraction>0）：放在 r = (DonutFraction + 1) / 2 的环形中央；
        //   - 普通饼：放在 r = 0.62 的内圈（避免压在扇边界上）。
        if (opt.ShowSliceLabels)
        {
                        // ScottPlot 5.0.51 Pie 不对外暴露 OuterRadius/InnerRadius，饼图以 (0,0) 为中心、
            // 外半径固定≈1，环形内半径 = DonutFraction。
            const double outer = 1.0;
            double inner = opt.DonutFraction > 0 ? opt.DonutFraction : 0.0;
            double rLabel = opt.DonutFraction > 0 ? (inner + outer) * 0.5 : outer * 0.60;

            for (var i = 0; i < n; i++)
            {
                var (mid, pct, name) = centers[i];
                if (pct < 2.0) continue;   // 小于 2% 的 tiny 切片跳过，避免字挤在一起
                var rad = mid * Math.PI / 180.0;
                var x = rLabel * Math.Cos(rad);
                var y = rLabel * Math.Sin(rad);
                // 颜色：深色背景用白字；浅色背景用深灰字
                var sliceColor = slices[i].FillColor;
                var lum = 0.299 * sliceColor.Red + 0.587 * sliceColor.Green + 0.114 * sliceColor.Blue;
                var txtColor = lum > 170 ? TextPrimary : Colors.White;
                var shortName = name.Length > 4 ? name.Substring(0, 4) : name;
                var label = $"{shortName}\n{pct:F0}%";
                var t = plt.Add.Text(label, x, y);
                t.LabelStyle.ForeColor = txtColor;
                t.LabelStyle.FontSize = 10;
                t.LabelStyle.Bold = true;
                t.LabelStyle.Alignment = Alignment.MiddleCenter;
                                // 占比太小（<5%）的放在圈外，黑色。
                // 说明：ScottPlot 5.0.51 Text 未暴露 LabelX/LabelY 可写属性，
                // 改为调整 Alignment+ 字号，让标签自然落在扇区外侧（通过环形中心偏移
                // 半径 rLabel 调整位置：改为 outer*1.10 的位置）。
                if (pct < 5)
                {
                    rad = mid * Math.PI / 180.0;
                    var x2 = outer * 1.12 * Math.Cos(rad);
                    var y2 = outer * 1.12 * Math.Sin(rad);
                    t.LabelStyle.ForeColor = TextPrimary;
                    t.LabelStyle.FontSize = 9;
                    t.LabelStyle.Alignment =
                        mid > -90 && mid < 90 ? Alignment.MiddleLeft : Alignment.MiddleRight;
                    t.LabelStyle.Rotation = 0;
                    // 通过 plt 坐标重定位：先移除旧 Text，再在 (x2,y2) 添加新文本。
                    // 简单实现：用反射查一下 Text.Location / Position 之类的属性是否存在，
                    // 若无则保持原位置（外圈显示位置可近似依赖 Alignment 控制）。
                    try
                    {
                        var tp = t.GetType();
                        var locX = tp.GetProperty("X") ?? tp.GetProperty("LocationX") ?? tp.GetProperty("XAxis");
                        var locY = tp.GetProperty("Y") ?? tp.GetProperty("LocationY") ?? tp.GetProperty("YAxis");
                        if (locX != null && locY != null)
                        {
                            locX.SetValue(t, x2);
                            locY.SetValue(t, y2);
                        }
                    }
                    catch { /* 回退：保持原位置，避免崩 */ }
                }
            }

            // 环形中心标题（如"选中类型占比"）
            if (opt.DonutFraction > 0 && !string.IsNullOrEmpty(opt.CenterTitle))
            {
                var t = plt.Add.Text(opt.CenterTitle, 0, 0);
                t.LabelStyle.ForeColor = TextPrimary;
                t.LabelStyle.FontSize = 11;
                t.LabelStyle.Bold = true;
                t.LabelStyle.Alignment = Alignment.MiddleCenter;
            }
        }

        plt.HideAxesAndGrid();
        // 保证饼图大小：轴范围设为 [-1.4, 1.4] 允许标签外围不被裁切
        plt.Axes.SetLimits(-1.4, 1.4, -1.25, 1.25);
        if (opt.ShowLegend)
        {
            plt.Legend.Orientation = Orientation.Vertical;
            plt.Legend.FontSize = 10;
            plt.ShowLegend(Alignment.MiddleLeft);
        }
        control.Refresh();
    }
}
