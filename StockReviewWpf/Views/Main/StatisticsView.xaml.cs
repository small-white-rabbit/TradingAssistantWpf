using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using ScottPlot;
using ScottPlot.WPF;
using StockReview.Core.Data;
using StockReviewWpf.Controls;
using StockReviewWpf.Services;
using StockReviewWpf.ViewModels.Main;

namespace StockReviewWpf.Views.Main;

/// <summary>
/// 统计分析视图 - 对应 StatisticsView.vue。
/// 图表按 activeTab 懒渲染（仅渲染可见 tab 的图表）。
/// v2：堆叠/分组柱全部下沉到 ChartTheme 通用引擎，避免手写 offset 导致列宽不齐。
/// </summary>
public partial class StatisticsView : UserControl, IHeavyResourceView
{
    private readonly StatisticsViewModel _viewModel;
    private string _lastRenderedTab = "";
    private bool _vmSubscribed;

    public StatisticsView()
    {
        InitializeComponent();
        _viewModel = new StatisticsViewModel(App.RequireService<DatabaseService>());
        DataContext = _viewModel;
        SubscribeVm();
        Dispatcher.BeginInvoke(new Action(() => RenderChartsForTab(_viewModel.ActiveTab)),
            System.Windows.Threading.DispatcherPriority.Loaded);
        // WPF 的 Unloaded 在每次导航离开都触发（非驱逐）：只退订防泄漏，不清图表——
        // 图表随视图缓存保留，切回时立即可见（零重渲染，切换流畅的关键）。
        // 重型资源（17 张图的 ScottPlot/SkiaSharp 底层位图）仅在视图被 LRU 驱逐时
        // 通过 IHeavyResourceView.ReleaseHeavyResources 释放（由 MainViewModel 调用）。
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        // 缓存视图切回：重订事件（Unloaded 已退订）；图表未清空故无需重渲染
        SubscribeVm();
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeVm();
    }

    private void SubscribeVm()
    {
        if (_vmSubscribed) return;
        _viewModel.PropertyChanged += OnVmPropertyChanged;
        _vmSubscribed = true;
    }

    private void UnsubscribeVm()
    {
        if (!_vmSubscribed) return;
        _viewModel.PropertyChanged -= OnVmPropertyChanged;
        _vmSubscribed = false;
    }

    /// <summary>驱逐时释放重型资源（图表底层位图 + 事件订阅）。仅 MainViewModel.TryDisposeView 调用。</summary>
    public void ReleaseHeavyResources()
    {
        UnsubscribeVm();
        ClearAllPlots();
    }

    private void ClearAllPlots()
    {
        try
        {
            var plots = new[] { EntryTypeChart, Last6WinRateChart, Last6OverallChart, Last6ReturnChart,
                Last12WinRateChart, Last12OverallChart, Last12ReturnChart,
                YearlyWinRateChart, YearlyOverallChart, YearlyReturnChart,
                AllWinRateChart, AllOverallChart, AllReturnChart,
                StrongMonthlyChart, StrongTypeChart, ProblemChart, EntryTypeProblemChart };
            foreach (var p in plots)
            {
                p.Plot.Clear();
                try { p.Plot.Remove<IPlottable>(); } catch { /* ScottPlot 5 某些版本无此方法 */ }
            }
            _lastRenderedTab = "";
        }
        catch { /* 清理失败不阻塞 */ }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StatisticsViewModel.ActiveTab)
            or nameof(StatisticsViewModel.SelectedMonth)
            or nameof(StatisticsViewModel.SelectedYear))
        {
            Dispatcher.BeginInvoke(new Action(() => RenderChartsForTab(_viewModel.ActiveTab)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private static readonly string[] TabNames =
        { "month", "6months", "last12", "yearly", "all", "strong", "problem", "table" };

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tc || tc.SelectedIndex < 0 || tc.SelectedIndex >= TabNames.Length) return;
        var name = TabNames[tc.SelectedIndex];
        if (_viewModel.ActiveTab != name) _viewModel.ActiveTab = name;
    }

    // ===== 综合胜率折线（红 + 50% 基准虚线） =====
    private static void RenderWinRateLine(WpfPlot plot, StatisticsViewModel.LineSeries line) =>
        ChartTheme.RenderLine(plot, line.Months, line.Values, new ChartTheme.LineOptions
        {
            YTitle = "胜率%",
            YMax = 100,
            ReferenceY = 50,
            ReferenceLabel = "盈亏线",
        });

    // ===== 收益趋势折线（蓝） =====
    private static void RenderReturnLine(WpfPlot plot, StatisticsViewModel.LineSeries line) =>
        ChartTheme.RenderLine(plot, line.Months, line.Values, new ChartTheme.LineOptions
        {
            ColorHex = ChartTheme.ReturnHex,
            YTitle = "平均收益%",
        });

    private void RenderChartsForTab(string tab)
    {
        try
        {
            switch (tab)
            {
                case "month":
                    RenderWinRateStack(EntryTypeChart, _viewModel.MonthWinRateStacks);
                    break;
                case "6months":
                    RenderWinRateStack(Last6WinRateChart, _viewModel.Last6WinRateStacks);
                    RenderWinRateLine(Last6OverallChart, _viewModel.Last6OverallLine);
                    RenderReturnLine(Last6ReturnChart, _viewModel.Last6ReturnLine);
                    break;
                case "last12":
                    RenderWinRateStack(Last12WinRateChart, _viewModel.Last12WinRateStacks);
                    RenderWinRateLine(Last12OverallChart, _viewModel.Last12OverallLine);
                    RenderReturnLine(Last12ReturnChart, _viewModel.Last12ReturnLine);
                    break;
                case "yearly":
                    RenderWinRateStack(YearlyWinRateChart, _viewModel.YearlyWinRateStacks);
                    RenderWinRateLine(YearlyOverallChart, _viewModel.YearlyOverallLine);
                    RenderReturnLine(YearlyReturnChart, _viewModel.YearlyReturnLine);
                    break;
                case "all":
                    ChartTheme.RenderBars(AllWinRateChart, _viewModel.AllWinRateBars.Categories, _viewModel.AllWinRateBars.Values,
                        new ChartTheme.BarOptions
                        {
                            ColorHex = ChartTheme.AllWinRateHex,
                            XTitle = "进场类型",
                            YTitle = "胜率%",
                            YMax = 100,
                        });
                    RenderWinRateLine(AllOverallChart, _viewModel.AllOverallLine);
                    RenderReturnLine(AllReturnChart, _viewModel.AllReturnLine);
                    break;
                case "strong":
                    ChartTheme.RenderLine(StrongMonthlyChart, _viewModel.StrongMonthlyLine.Months, _viewModel.StrongMonthlyLine.Values,
                        new ChartTheme.LineOptions
                        {
                            ColorHex = ChartTheme.StrongHex,
                            YTitle = "强股数量",
                            ValueFormat = "F0",
                            ValueSuffix = "",
                            ShowArea = true,
                        });
                    ChartTheme.RenderPie(StrongTypeChart,
                        _viewModel.StrongTypePie.Select(s => s.Name).ToList(),
                        _viewModel.StrongTypePie.Select(s => s.Value).ToList(),
                        isDonut: false);
                    break;
                case "problem":
                    ChartTheme.RenderPie(ProblemChart,
                        _viewModel.ProblemTypePie.Select(s => s.Name).ToList(),
                        _viewModel.ProblemTypePie.Select(s => s.Value).ToList(),
                        isDonut: false);
                    RenderEntryTypeProblem(EntryTypeProblemChart, _viewModel.EntryTypeProblemTags, _viewModel.EntryTypeProblemBars);
                    break;
            }

            var plots = tab switch
            {
                "month" => new[] { EntryTypeChart },
                "6months" => new[] { Last6WinRateChart, Last6OverallChart, Last6ReturnChart },
                "last12" => new[] { Last12WinRateChart, Last12OverallChart, Last12ReturnChart },
                "yearly" => new[] { YearlyWinRateChart, YearlyOverallChart, YearlyReturnChart },
                "all" => new[] { AllWinRateChart, AllOverallChart, AllReturnChart },
                "strong" => new[] { StrongMonthlyChart, StrongTypeChart },
                "problem" => new[] { ProblemChart, EntryTypeProblemChart },
                _ => Array.Empty<WpfPlot>()
            };
            foreach (var p in plots)
            {
                if (p.Name is "StrongTypeChart" or "ProblemChart")
                    ChartAnimations.AnimatePieChart(p);
                else
                    ChartAnimations.AnimateBarChart(p);
            }
            AnimateOverviewCardsCountUp();

            _lastRenderedTab = tab;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Statistics chart render error: " + ex.Message);
        }
    }

    // ============ 堆叠柱（父级并排 + 子级堆叠）→ 调用 ChartTheme 通用引擎 ============
    private static void RenderWinRateStack(WpfPlot plot, List<StatisticsViewModel.WinRateStackGroup> groups)
    {
        var groupsV2 = groups.Select(g => new ChartTheme.StackedBarGroup
        {
            ParentName = g.ParentName,
            ChildNames = g.ChildNames,
            ChildCounts = g.ChildCounts.Select(c => (IReadOnlyList<double>)c).ToList(),
            ParentWinRate = g.ParentWinRate,
            ParentRatio = g.ParentRatio,
        }).ToList();
        var months = groups.Count > 0 ? groups[0].Months : new List<string>();
        ChartTheme.RenderStackedBars(plot, groupsV2, new ChartTheme.StackedBarOptions
        {
            Categories = months,
            SingleCategoryLabel = "进场类型",
            XTitle = "月份",
            YTitle = "交易笔数",
        });
    }

    // ============ 进场类型×问题 分组柱 → 调用 ChartTheme 通用引擎 ============
    private static void RenderEntryTypeProblem(WpfPlot plot, List<string> tags, List<(string EntryType, List<double> Counts)> bars)
    {
        var series = bars.Select(b => (b.EntryType, (IReadOnlyList<double>)b.Counts)).ToList();
        ChartTheme.RenderGroupedBars(plot, tags, series, new ChartTheme.GroupedBarOptions
        {
            XTitle = "问题标签",
            YTitle = "次数",
            ShowValueLabels = true,
        });
    }

    private void AnimateOverviewCardsCountUp()
    {
        var allTextBlocks = FindVisualChildren<TextBlock>(this)
            .Where(t => t.Text != null && t.Text.Length > 0 && t.FontSize >= 18)
            .ToList();
        foreach (var tb in allTextBlocks)
        {
            var raw = Regex.Replace(tb.Text, "[^0-9.\\-]", "");
            if (double.TryParse(raw, out var target))
            {
                var suffix = tb.Text.EndsWith('%') ? "%" : "";
                NumberAnimator.Run(tb, target, "F1", suffix, 1500);
            }
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
    {
        if (depObj == null) yield break;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
        {
            var child = VisualTreeHelper.GetChild(depObj, i);
            if (child is T t) yield return t;
            foreach (var x in FindVisualChildren<T>(child)) yield return x;
        }
    }
}
