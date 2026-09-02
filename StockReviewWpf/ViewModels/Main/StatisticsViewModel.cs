using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockReview.Core.Data;
using StockReviewWpf.Models;

namespace StockReviewWpf.ViewModels.Main;

/// <summary>
/// 统计分析 ViewModel - 对应 StatisticsView.vue（1:1 复刻，DB 全量接入版）。
/// 数据通过 DatabaseService 读取 trades / strongStocks / entryTypes 三表，在 C# 端聚合，
/// 计算逻辑与旧版的 calculateMonthlyStats / 各 computed 保持一致：
///   - 胜率分母只算已清仓且 totalReturn>0(赢)/<0(亏)，==0 不计入。
///   - 胜率保留 1 位小数，平均收益保留 2 位小数。
///   - 问题表分母：month 表用标签总出现次数，其余用交易笔数。
///   - StrongTypeChart 用 filteredStrongStocks（受月份筛选），StrongYear/Month 与 StrongMonthlyChart 用全局。
///   - AllWinRateChart 为单系列柱状（按类型胜率），其余 WinRate 图为父级-子级堆叠柱（按笔数）。
/// </summary>
public partial class StatisticsViewModel : ObservableObject
{
    private readonly IDatabaseService _db;

    // 原始数据
    private List<Dictionary<string, object?>> _allTrades = new();
    private List<Dictionary<string, object?>> _allStrongStocks = new();
    private List<EntryTypeItem> _entryTypeTree = new();

    [ObservableProperty] private string _selectedMonth = "";
    [ObservableProperty] private string _selectedYear = "";
    [ObservableProperty] private string _activeTab = "month";

    [ObservableProperty] private ObservableCollection<string> _availableYears = new();
    [ObservableProperty] private ObservableCollection<string> _availableMonths = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private ObservableCollection<OverviewCardItem> _overviewCards = new();
    [ObservableProperty] private ObservableCollection<OverviewCardItem> _strongOverviewCards = new();
    [ObservableProperty] private ObservableCollection<OverviewCardItem> _problemOverviewCards = new();

    [ObservableProperty] private ObservableCollection<EntryTypeStatRow> _monthEntryTypeData = new();
    [ObservableProperty] private ObservableCollection<ProblemStatRow> _monthProblemData = new();
    [ObservableProperty] private ObservableCollection<EntryTypeStatRow> _last6EntryTypeData = new();
    [ObservableProperty] private ObservableCollection<ProblemStatRow> _last6ProblemData = new();
    [ObservableProperty] private ObservableCollection<EntryTypeStatRow> _last12EntryTypeData = new();
    [ObservableProperty] private ObservableCollection<ProblemStatRow> _last12ProblemData = new();
    [ObservableProperty] private ObservableCollection<EntryTypeStatRow> _yearlyEntryTypeData = new();
    [ObservableProperty] private ObservableCollection<ProblemStatRow> _yearlyProblemData = new();
    [ObservableProperty] private ObservableCollection<EntryTypeStatRow> _allEntryTypeData = new();
    [ObservableProperty] private ObservableCollection<ProblemStatRow> _allProblemData = new();

    [ObservableProperty] private ObservableCollection<MonthlyStatRow> _tableData = new();
    [ObservableProperty] private ObservableCollection<StrongYearRow> _strongYearData = new();
    [ObservableProperty] private ObservableCollection<StrongMonthRow> _strongMonthData = new();
    [ObservableProperty] private ObservableCollection<ProblemFreqRow> _problemFrequencyData = new();
    [ObservableProperty] private ObservableCollection<EntryTypeProblemRow> _entryTypeProblemData = new();

    [ObservableProperty] private bool _isYearlyFilterVisible;

    // ============ 图表数据（供 code-behind ScottPlot 渲染） ============
    // WinRate 堆叠柱：每个父级一组（父名 -> (月份列表, 子级名列表, 每子级每月笔数, 父级总胜率, 父级占比)）
    public class WinRateStackGroup
    {
        public string ParentName { get; set; } = "";
        public List<string> Months { get; set; } = new();
        public List<string> ChildNames { get; set; } = new();
        public List<List<double>> ChildCounts { get; set; } = new(); // [childIdx][monthIdx]
        public double ParentWinRate { get; set; }
        public double ParentRatio { get; set; } // 占所有父级总笔数百分比
    }

    public class SimpleBarSeries
    {
        public List<string> Categories { get; set; } = new();
        public List<double> Values { get; set; } = new();
    }

    public class LineSeries
    {
        public List<string> Months { get; set; } = new();
        public List<double> Values { get; set; } = new();
    }

    public class PieSlice
    {
        public string Name { get; set; } = "";
        public double Value { get; set; }
    }

    // 堆叠柱（笔数）
    public List<WinRateStackGroup> MonthWinRateStacks { get; set; } = new();
    public List<WinRateStackGroup> Last6WinRateStacks { get; set; } = new();
    public List<WinRateStackGroup> Last12WinRateStacks { get; set; } = new();
    public List<WinRateStackGroup> YearlyWinRateStacks { get; set; } = new();
    // 单系列柱状（胜率%）
    public SimpleBarSeries AllWinRateBars { get; set; } = new();
    // 综合胜率折线
    public LineSeries Last6OverallLine { get; set; } = new();
    public LineSeries Last12OverallLine { get; set; } = new();
    public LineSeries YearlyOverallLine { get; set; } = new();
    public LineSeries AllOverallLine { get; set; } = new();
    // 收益趋势折线
    public LineSeries Last6ReturnLine { get; set; } = new();
    public LineSeries Last12ReturnLine { get; set; } = new();
    public LineSeries YearlyReturnLine { get; set; } = new();
    public LineSeries AllReturnLine { get; set; } = new();
    // 强股
    public LineSeries StrongMonthlyLine { get; set; } = new();
    public List<PieSlice> StrongTypePie { get; set; } = new();
    // 问题
    public List<PieSlice> ProblemTypePie { get; set; } = new();
    // EntryType-Problem 分组柱
    public List<string> EntryTypeProblemTags { get; set; } = new();
    public List<(string EntryType, List<double> Counts)> EntryTypeProblemBars { get; set; } = new();

    public StatisticsViewModel(IDatabaseService db)
    {
        _db = db;
        // 异步加载：避免同步 GetAll("trades") 阻塞 UI 线程数百毫秒
        _ = LoadDataAsync();
    }

    // 无参构造（设计器/兼容）
    public StatisticsViewModel() : this(null!) { }

    /// <summary>
    /// 重新加载全部数据（标题栏"刷新"按钮调用；视图有缓存，导航不会重新加载）
    /// </summary>
    [RelayCommand]
    public async Task Reload() => await LoadDataAsync();

    private async Task LoadDataAsync()
    {
        IsLoading = true;
        try
        {
            _allTrades = _db != null
                ? await Task.Run(() => _db.GetAll("trades"))
                : new List<Dictionary<string, object?>>();
            _allStrongStocks = _db != null
                ? await Task.Run(() => _db.GetAll("strongStocks"))
                : new List<Dictionary<string, object?>>();
            _entryTypeTree = await Task.Run(() => LoadEntryTypeTree());

            // 可用年度（降序），至少当前年
            var years = new SortedSet<int>();
            foreach (var t in _allTrades)
            {
                var d = S(t, "tradeDate");
                if (d.Length >= 4 && int.TryParse(d.Substring(0, 4), out var y)) years.Add(y);
            }
            if (years.Count == 0) years.Add(DateTime.Now.Year);
            AvailableYears = new ObservableCollection<string>(years.OrderByDescending(y => y).Select(y => y.ToString()));

            // 可用月份（YYYY-MM，降序，供月份下拉）
            var months = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var t in _allTrades)
            {
                var d = S(t, "tradeDate");
                if (d.Length >= 7) months.Add(d.Substring(0, 7));
            }
            if (months.Count == 0) months.Add(GetCurrentMonth());
            AvailableMonths = new ObservableCollection<string>(months.OrderByDescending(m => m));

            // 默认选中"最近一个有数据的月份"
            SelectedMonth = GetDefaultMonth();
            SelectedYear = DateTime.Now.Year.ToString();

            Recompute();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>最近一个有交易数据的月份；无数据时回退当前月。</summary>
    private string GetDefaultMonth()
    {
        var latest = _allTrades
            .Select(t => S(t, "tradeDate"))
            .Where(d => d.Length >= 7)
            .DefaultIfEmpty("")
            .Max() ?? "";
        return latest.Length >= 7 ? latest.Substring(0, 7) : GetCurrentMonth();
    }

    private List<EntryTypeItem> LoadEntryTypeTree()
    {
        if (_db == null) return new List<EntryTypeItem>();
        var rows = _db.GetAll("entryTypes");
        var items = rows.Select(r => new EntryTypeItem
        {
            Id = ToInt(r, "id"),
            Name = S(r, "typeName"),
            SortOrder = ToInt(r, "sortOrder"),
            ParentId = r.TryGetValue("parentId", out var pid) && pid != null ? ToInt(r, "parentId") : (int?)null,
            IsActive = ToInt(r, "isActive") != 0
        }).Where(i => i.IsActive).OrderBy(i => i.SortOrder).ToList();

        var nodes = items.ToDictionary(i => i.Id, i => i);
        foreach (var i in items)
        {
            if (i.ParentId.HasValue && nodes.ContainsKey(i.ParentId.Value))
                nodes[i.ParentId.Value].Children.Add(i);
        }
        return items.Where(i => !i.ParentId.HasValue || !nodes.ContainsKey(i.ParentId.Value)).ToList();
    }

    private void Recompute()
    {
        var month = string.IsNullOrEmpty(SelectedMonth) ? GetDefaultMonth() : SelectedMonth;
        var year = string.IsNullOrEmpty(SelectedYear) ? DateTime.Now.Year.ToString() : SelectedYear;

        var filteredTrades = FilterTrades(_allTrades, month);
        var last6 = FilterByMonths(_allTrades, GetLastNMonths(6));
        var last12 = FilterByMonths(_allTrades, GetLastNMonths(12));
        var yearTrades = FilterTrades(_allTrades, year);
        var allTrades = _allTrades;

        var filteredStrong = FilterStrong(_allStrongStocks, month);
        var last6Strong = FilterStrongByMonths(_allStrongStocks, GetLastNMonths(6));
        var last12Strong = FilterStrongByMonths(_allStrongStocks, GetLastNMonths(12));
        var allStrong = _allStrongStocks;

        // 总览卡片（按 ActiveTab 选子集）
        var overviewCleared = ActiveTab switch
        {
            "6months" => last6.Where(Cleared),
            "yearly" => yearTrades.Where(Cleared),
            "last12" => last12.Where(Cleared),
            "all" => allTrades.Where(Cleared),
            _ => filteredTrades.Where(Cleared)
        };
        OverviewCards = ComputeOverviewCards(overviewCleared, ActiveTab);

        // 进场类型表
        MonthEntryTypeData = ComputeEntryTypeTable(filteredTrades.Where(Cleared).ToList());
        Last6EntryTypeData = ComputeEntryTypeTable(last6.Where(Cleared).ToList());
        Last12EntryTypeData = ComputeEntryTypeTable(last12.Where(Cleared).ToList());
        YearlyEntryTypeData = ComputeEntryTypeTable(yearTrades.Where(Cleared).ToList());
        AllEntryTypeData = ComputeEntryTypeTable(allTrades.Where(Cleared).ToList());

        // 问题表（注意分母差异）
        MonthProblemData = ComputeProblemTable(filteredTrades, useTradeCountDenominator: false);
        Last6ProblemData = ComputeProblemTable(last6, useTradeCountDenominator: true);
        Last12ProblemData = ComputeProblemTable(last12, useTradeCountDenominator: true);
        YearlyProblemData = ComputeProblemTable(yearTrades, useTradeCountDenominator: true);
        AllProblemData = ComputeProblemTable(allTrades, useTradeCountDenominator: true);

        // 数据表格（近 12 个月按月）
        TableData = ComputeTableData();

        // 强股分析
        StrongOverviewCards = ComputeStrongOverviewCards(ActiveTab, filteredStrong, allStrong);
        StrongYearData = ComputeStrongYear(allStrong);
        StrongMonthData = ComputeStrongMonth(allStrong);
        StrongMonthlyLine = ComputeStrongMonthlyLine(allStrong);
        StrongTypePie = ComputeStrongTypePie(filteredStrong);

        // 问题分析（按 tab 精确分流，对齐 Vue problemTypeOption 口径）
        var problemSubset = ActiveTab switch
        {
            "problem" or "all" => allTrades,
            "month" => filteredTrades,
            "yearly" => yearTrades,
            "last12" => last12,
            _ => last6  // 6months
        };
        ProblemOverviewCards = ComputeProblemOverviewCards(problemSubset);
        ProblemTypePie = ComputeProblemTypePie(problemSubset);
        ProblemFrequencyData = ComputeProblemFrequency(filteredTrades);
        // 该图仅在问题分析 tab 渲染，原版按 activeTab 分流（问题页=全部交易）
        (EntryTypeProblemTags, EntryTypeProblemBars) = ComputeEntryTypeProblem(problemSubset);
        EntryTypeProblemData = ComputeEntryTypeProblemTable(filteredTrades);

        // 图表
        MonthWinRateStacks = ComputeWinRateStacks(filteredTrades.Where(Cleared).ToList(), new List<string> { month });
        Last6WinRateStacks = ComputeWinRateStacks(last6.Where(Cleared).ToList(), GetLastNMonths(6));
        Last12WinRateStacks = ComputeWinRateStacks(last12.Where(Cleared).ToList(), GetLastNMonths(12));
        YearlyWinRateStacks = ComputeWinRateStacks(yearTrades.Where(Cleared).ToList(), GetYearMonthsUpToNow(year));
        AllWinRateBars = ComputeAllWinRateBars(allTrades.Where(Cleared).ToList());

        Last6OverallLine = ComputeOverallLine(last6, 6);
        Last12OverallLine = ComputeOverallLine(last12, 12);
        YearlyOverallLine = ComputeOverallLineMonths(yearTrades, GetYearMonthsUpToNow(year));
        AllOverallLine = ComputeOverallLine(allTrades, -1);
        AllReturnLine = ComputeReturnLine(_allTrades, GetAllDataMonths(_allTrades));

        Last6ReturnLine = ComputeReturnLine(_allTrades, GetLastNMonths(6));
        Last12ReturnLine = ComputeReturnLine(_allTrades, GetLastNMonths(12));
        YearlyReturnLine = ComputeReturnLine(yearTrades, GetYearMonthsUpToNow(year));
    }

    // ============ 总览卡片 ============
    private ObservableCollection<OverviewCardItem> ComputeOverviewCards(IEnumerable<Dictionary<string, object?>> cleared, string tab)
    {
        var list = cleared.ToList();
        var total = list.Count;
        var wins = list.Count(t => ParseDouble(S(t, "totalReturn")) > 0);
        var avgReturn = total > 0 ? list.Sum(t => ParseDouble(S(t, "totalReturn"))) / total : 0;
        var returns = list.Select(t => ParseDouble(S(t, "totalReturn"))).ToList();
        var maxGain = returns.Count > 0 ? returns.Max() : 0;
        var losses = returns.Where(r => r < 0).ToList();
        var maxDrawdown = losses.Count > 0 ? losses.Min() : 0;

        var prefix = tab switch
        {
            "6months" => "近6个月",
            "yearly" => "年度",
            "last12" => "近12个月",
            "all" => "全部",
            "month" when !string.IsNullOrEmpty(SelectedMonth) => SelectedMonth,
            _ => "当月"
        };

        // 子级胜率排名
        var childTypes = new List<string>();
        foreach (var root in _entryTypeTree)
            foreach (var c in root.Children) childTypes.Add(c.Name);
        var childStats = new Dictionary<string, (int total, int wins)>();
        foreach (var t in list)
        {
            var type = S(t, "entryType");
            if (childTypes.Contains(type))
            {
                if (!childStats.ContainsKey(type)) childStats[type] = (0, 0);
                var s = childStats[type];
                s.total++;
                if (ParseDouble(S(t, "totalReturn")) > 0) s.wins++;
                childStats[type] = s;
            }
        }
        var ranking = childStats.Where(kv => kv.Value.total > 0)
            .Select(kv => new TypeRankingItem
            {
                Type = kv.Key,
                Rate = (kv.Value.total > 0 ? (kv.Value.wins * 100.0 / kv.Value.total) : 0).ToString("F1"),
                Total = kv.Value.total
            })
            .OrderByDescending(r => ParseDouble(r.Rate))
            .Take(3).ToList();

        var cards = new ObservableCollection<OverviewCardItem>
        {
            new() { Label = $"{prefix}交易数", Value = total + "笔" },
            new() { Label = $"{prefix}成功率", Value = (total > 0 ? (wins * 100.0 / total) : 0).ToString("F1") + "%",
                    ClassName = (total > 0 && wins * 100.0 / total >= 50) ? "up" : "down" },
            new() { Label = $"{prefix}平均收益", Value = avgReturn.ToString("F2") + "%",
                    ClassName = avgReturn >= 0 ? "up" : "down" },
            new() { Label = "最大收益", Value = maxGain.ToString("F2") + "%", ClassName = "up", Clickable = true },
            new() { Label = "最大回撤", Value = maxDrawdown.ToString("F2") + "%", ClassName = "down", Clickable = true },
            new() { Label = "子级胜率排名", IsRanking = true, Ranking = new ObservableCollection<TypeRankingItem>(ranking) }
        };
        return cards;
    }

    // ============ 进场类型表 ============
    private ObservableCollection<EntryTypeStatRow> ComputeEntryTypeTable(List<Dictionary<string, object?>> cleared)
    {
        var typeStats = new Dictionary<string, (int total, int wins, List<double> returns)>();
        foreach (var t in cleared)
        {
            var type = string.IsNullOrWhiteSpace(S(t, "entryType")) ? "未分类" : S(t, "entryType");
            if (!typeStats.ContainsKey(type)) typeStats[type] = (0, 0, new List<double>());
            var s = typeStats[type];
            s.total++;
            if (ParseDouble(S(t, "totalReturn")) > 0) s.wins++;
            s.returns.Add(ParseDouble(S(t, "totalReturn")));
            typeStats[type] = s;
        }

        var rows = new ObservableCollection<EntryTypeStatRow>();
        foreach (var root in _entryTypeTree)
        {
            var childrenWithData = root.Children.Where(c => typeStats.ContainsKey(c.Name)).ToList();
            if (childrenWithData.Count == 0)
            {
                if (typeStats.TryGetValue(root.Name, out var ps))
                {
                    rows.Add(new EntryTypeStatRow
                    {
                        EntryType = root.Name,
                        Count = ps.total,
                        WinRate = (ps.total > 0 ? ps.wins * 100.0 / ps.total : 0).ToString("F1"),
                        AvgReturn = (ps.returns.Count > 0 ? ps.returns.Average() : 0).ToString("F2"),
                        IsParent = true,
                        Indent = 0
                    });
                }
                continue;
            }
            int pTotal = 0, pWins = 0;
            var pReturns = new List<double>();
            foreach (var c in childrenWithData)
            {
                var cs = typeStats[c.Name];
                pTotal += cs.total; pWins += cs.wins; pReturns.AddRange(cs.returns);
                rows.Add(new EntryTypeStatRow
                {
                    EntryType = c.Name,
                    Count = cs.total,
                    WinRate = (cs.total > 0 ? cs.wins * 100.0 / cs.total : 0).ToString("F1"),
                    AvgReturn = (cs.returns.Count > 0 ? cs.returns.Average() : 0).ToString("F2"),
                    IsParent = false,
                    Indent = 1
                });
            }
            rows.Insert(rows.Count - childrenWithData.Count, new EntryTypeStatRow
            {
                EntryType = root.Name,
                Count = pTotal,
                WinRate = (pTotal > 0 ? pWins * 100.0 / pTotal : 0).ToString("F1"),
                AvgReturn = (pReturns.Count > 0 ? pReturns.Average() : 0).ToString("F2"),
                IsParent = true,
                Indent = 0
            });
        }
        if (typeStats.TryGetValue("未分类", out var us))
        {
            rows.Add(new EntryTypeStatRow
            {
                EntryType = "未分类",
                Count = us.total,
                WinRate = (us.total > 0 ? us.wins * 100.0 / us.total : 0).ToString("F1"),
                AvgReturn = (us.returns.Count > 0 ? us.returns.Average() : 0).ToString("F2"),
                IsParent = true,
                Indent = 0
            });
        }
        return rows;
    }

    // ============ 问题表 ============
    private ObservableCollection<ProblemStatRow> ComputeProblemTable(List<Dictionary<string, object?>> trades, bool useTradeCountDenominator)
    {
        var counts = new Dictionary<string, int>();
        foreach (var t in trades)
        {
            var tags = ParseTags(t.GetValueOrDefault("problemTags"));
            foreach (var tag in tags)
                counts[tag] = counts.TryGetValue(tag, out var c) ? c + 1 : 1;
        }
        var denom = useTradeCountDenominator ? trades.Count : counts.Values.Sum();
        if (denom <= 0) denom = 1;
        return new ObservableCollection<ProblemStatRow>(
            counts.OrderByDescending(kv => kv.Value)
                .Select(kv => new ProblemStatRow
                {
                    Problem = kv.Key,
                    Count = kv.Value,
                    Percentage = (kv.Value * 100.0 / denom).ToString("F1")
                }));
    }

    // ============ 数据表格（近 12 个月按月） ============
    private ObservableCollection<MonthlyStatRow> ComputeTableData()
    {
        var months = GetLastNMonths(12);
        var rows = new ObservableCollection<MonthlyStatRow>();
        foreach (var ym in months)
        {
            var stats = CalculateMonthlyStats(_allTrades, ym);
            rows.Add(new MonthlyStatRow
            {
                Month = ym,
                Total = stats.total,
                WinRate = stats.winRate,
                AvgReturn = stats.avgReturn,
                Best = stats.bestTrade != null ? S(stats.bestTrade, "totalReturn") : "0",
                Worst = stats.worstTrade != null ? S(stats.worstTrade, "totalReturn") : "0"
            });
        }
        return rows;
    }

    // ============ 强股 ============
    private ObservableCollection<OverviewCardItem> ComputeStrongOverviewCards(string tab, List<Dictionary<string, object?>> filteredStrong, List<Dictionary<string, object?>> allStrong)
    {
        var stocks = (tab == "month") ? filteredStrong : allStrong;
        var total = stocks.Count;
        var typeCount = new Dictionary<string, int>();
        foreach (var s in stocks)
        {
            var type = S(s, "strongType");
            if (string.IsNullOrWhiteSpace(type)) continue;
            typeCount[type] = typeCount.TryGetValue(type, out var c) ? c + 1 : 1;
        }
        var prefix = tab == "month" && !string.IsNullOrEmpty(SelectedMonth) ? SelectedMonth : "全部";
        if (typeCount.Count == 0)
        {
            return new ObservableCollection<OverviewCardItem>
            {
                new() { Label = $"{prefix}强股数", Value = "0只" },
                new() { Label = "最多类型", Value = "-" },
                new() { Label = "类型数量", Value = "0次" },
                new() { Label = "类型占比", Value = "0%" }
            };
        }
        var top = typeCount.OrderByDescending(kv => kv.Value).First();
        return new ObservableCollection<OverviewCardItem>
        {
            new() { Label = $"{prefix}强股数", Value = total + "只" },
            new() { Label = "最多类型", Value = top.Key },
            new() { Label = "类型数量", Value = top.Value + "次" },
            new() { Label = "类型占比", Value = (top.Value * 100.0 / total).ToString("F1") + "%" }
        };
    }

    private ObservableCollection<StrongYearRow> ComputeStrongYear(List<Dictionary<string, object?>> allStrong)
    {
        var d = new Dictionary<string, int>();
        foreach (var s in allStrong)
        {
            var date = S(s, "date");
            if (date.Length < 4) continue;
            var y = date.Substring(0, 4);
            d[y] = d.TryGetValue(y, out var c) ? c + 1 : 1;
        }
        return new ObservableCollection<StrongYearRow>(d.OrderByDescending(kv => kv.Key).Select(kv => new StrongYearRow { Year = kv.Key, Count = kv.Value }));
    }

    private ObservableCollection<StrongMonthRow> ComputeStrongMonth(List<Dictionary<string, object?>> allStrong)
    {
        var d = new Dictionary<string, int>();
        foreach (var s in allStrong)
        {
            var date = S(s, "date");
            if (date.Length < 7) continue;
            var ym = date.Substring(0, 7);
            d[ym] = d.TryGetValue(ym, out var c) ? c + 1 : 1;
        }
        return new ObservableCollection<StrongMonthRow>(d.OrderByDescending(kv => kv.Key).Select(kv => new StrongMonthRow { Month = kv.Key, Count = kv.Value }));
    }

    private LineSeries ComputeStrongMonthlyLine(List<Dictionary<string, object?>> allStrong)
    {
        var months = GetLastNMonths(12);
        var counts = months.Select(ym => (double)allStrong.Count(s => S(s, "date").StartsWith(ym))).ToList();
        return new LineSeries { Months = months, Values = counts };
    }

    private List<PieSlice> ComputeStrongTypePie(List<Dictionary<string, object?>> filteredStrong)
    {
        var typeCount = new Dictionary<string, int>();
        foreach (var s in filteredStrong)
        {
            var type = S(s, "strongType");
            if (string.IsNullOrWhiteSpace(type)) continue;
            typeCount[type] = typeCount.TryGetValue(type, out var c) ? c + 1 : 1;
        }
        return typeCount.Select(kv => new PieSlice { Name = kv.Key, Value = kv.Value }).ToList();
    }

    // ============ 问题概览/饼图 ============
    private ObservableCollection<OverviewCardItem> ComputeProblemOverviewCards(List<Dictionary<string, object?>> trades)
    {
        var withProblems = trades.Where(t => ParseTags(t.GetValueOrDefault("problemTags")).Count > 0).ToList();
        var total = withProblems.Count;
        var counts = new Dictionary<string, int>();
        foreach (var t in withProblems)
            foreach (var tag in ParseTags(t.GetValueOrDefault("problemTags")))
                counts[tag] = counts.TryGetValue(tag, out var c) ? c + 1 : 1;
        var totalProblems = counts.Values.Sum();
        var prefix = ActiveTab == "problem" || ActiveTab == "all" ? "全部" : (ActiveTab == "month" && !string.IsNullOrEmpty(SelectedMonth) ? SelectedMonth : "当月");
        var mostCommon = counts.OrderByDescending(kv => kv.Value).FirstOrDefault();
        return new ObservableCollection<OverviewCardItem>
        {
            new() { Label = $"{prefix}问题交易", Value = total + "笔" },
            new() { Label = "问题总数", Value = totalProblems + "个" },
            new() { Label = "最常见问题", Value = mostCommon.Key ?? "-" },
            new() { Label = "出现次数", Value = (mostCommon.Value).ToString() + "次" }
        };
    }

    private List<PieSlice> ComputeProblemTypePie(List<Dictionary<string, object?>> trades)
    {
        var counts = new Dictionary<string, int>();
        foreach (var t in trades)
            foreach (var tag in ParseTags(t.GetValueOrDefault("problemTags")))
                counts[tag] = counts.TryGetValue(tag, out var c) ? c + 1 : 1;
        return counts.Select(kv => new PieSlice { Name = kv.Key, Value = kv.Value }).ToList();
    }

    private ObservableCollection<ProblemFreqRow> ComputeProblemFrequency(List<Dictionary<string, object?>> filteredTrades)
    {
        var counts = new Dictionary<string, int>();
        foreach (var t in filteredTrades)
            foreach (var tag in ParseTags(t.GetValueOrDefault("problemTags")))
                counts[tag] = counts.TryGetValue(tag, out var c) ? c + 1 : 1;
        var total = counts.Values.Sum();
        if (total <= 0) total = 1;
        return new ObservableCollection<ProblemFreqRow>(
            counts.OrderByDescending(kv => kv.Value)
                .Select(kv => new ProblemFreqRow
                {
                    Problem = kv.Key,
                    Count = kv.Value,
                    Percentage = (kv.Value * 100.0 / total).ToString("F1")
                }));
    }

    private (List<string>, List<(string, List<double>)>) ComputeEntryTypeProblem(List<Dictionary<string, object?>> filteredTrades)
    {
        var tagSet = new HashSet<string>();
        var map = new Dictionary<string, Dictionary<string, int>>();
        foreach (var t in filteredTrades)
        {
            var type = S(t, "entryType");
            var tags = ParseTags(t.GetValueOrDefault("problemTags"));
            if (string.IsNullOrWhiteSpace(type) || tags.Count == 0) continue;
            if (!map.ContainsKey(type)) map[type] = new Dictionary<string, int>();
            foreach (var tag in tags)
            {
                tagSet.Add(tag);
                map[type][tag] = map[type].TryGetValue(tag, out var c) ? c + 1 : 1;
            }
        }
        var tagsList = tagSet.ToList();
        var bars = map.Select(kv => (kv.Key, tagsList.Select(tag => (double)(kv.Value.TryGetValue(tag, out var c) ? c : 0)).ToList())).ToList();
        return (tagsList, bars);
    }

    private ObservableCollection<EntryTypeProblemRow> ComputeEntryTypeProblemTable(List<Dictionary<string, object?>> filteredTrades)
    {
        var map = new Dictionary<string, Dictionary<string, int>>();
        foreach (var t in filteredTrades)
        {
            var type = S(t, "entryType");
            var tags = ParseTags(t.GetValueOrDefault("problemTags"));
            if (string.IsNullOrWhiteSpace(type) || tags.Count == 0) continue;
            if (!map.ContainsKey(type)) map[type] = new Dictionary<string, int>();
            foreach (var tag in tags)
                map[type][tag] = map[type].TryGetValue(tag, out var c) ? c + 1 : 1;
        }
        return new ObservableCollection<EntryTypeProblemRow>(
            map.Select(kv =>
                {
                    var top = kv.Value.OrderByDescending(x => x.Value).First();
                    return new EntryTypeProblemRow { EntryType = kv.Key, CommonProblem = top.Key, Count = top.Value };
                })
                .OrderByDescending(r => r.Count));
    }

    // ============ 图表数据 ============
    private List<WinRateStackGroup> ComputeWinRateStacks(List<Dictionary<string, object?>> cleared, List<string> months)
    {
        // 按类型统计每月笔数 + 胜负
        var typeMonth = new Dictionary<string, (int total, int wins, Dictionary<string, int> byMonth)>();
        foreach (var t in cleared)
        {
            var type = string.IsNullOrWhiteSpace(S(t, "entryType")) ? "未分类" : S(t, "entryType");
            if (!typeMonth.ContainsKey(type)) typeMonth[type] = (0, 0, new Dictionary<string, int>());
            var s = typeMonth[type];
            s.total++;
            if (ParseDouble(S(t, "totalReturn")) > 0) s.wins++;
            var ym = S(t, "tradeDate").Length >= 7 ? S(t, "tradeDate").Substring(0, 7) : "";
            if (!string.IsNullOrEmpty(ym))
            {
                if (!s.byMonth.ContainsKey(ym)) s.byMonth[ym] = 0;
                s.byMonth[ym]++;
            }
            typeMonth[type] = s;
        }

        var groups = new List<WinRateStackGroup>();
        double grandTotal = typeMonth.Values.Sum(v => v.total);
        if (grandTotal <= 0) grandTotal = 1;

        foreach (var root in _entryTypeTree)
        {
            var childrenWithData = root.Children.Where(c => typeMonth.ContainsKey(c.Name)).ToList();
            var parentTotal = 0;
            var parentWins = 0;
            var childNames = new List<string>();
            var childCounts = new List<List<double>>();
            if (childrenWithData.Count > 0)
            {
                foreach (var c in childrenWithData)
                {
                    var cs = typeMonth[c.Name];
                    parentTotal += cs.total; parentWins += cs.wins;
                    childNames.Add(c.Name);
                    childCounts.Add(months.Select(ym => (double)(cs.byMonth.TryGetValue(ym, out var v) ? v : 0)).ToList());
                }
            }
            else if (typeMonth.TryGetValue(root.Name, out var ps))
            {
                parentTotal = ps.total; parentWins = ps.wins;
                childNames.Add(root.Name);
                childCounts.Add(months.Select(ym => (double)(ps.byMonth.TryGetValue(ym, out var v) ? v : 0)).ToList());
            }
            if (parentTotal == 0) continue;
            groups.Add(new WinRateStackGroup
            {
                ParentName = root.Name,
                Months = months,
                ChildNames = childNames,
                ChildCounts = childCounts,
                ParentWinRate = parentTotal > 0 ? parentWins * 100.0 / parentTotal : 0,
                ParentRatio = parentTotal * 100.0 / grandTotal
            });
        }
        if (typeMonth.TryGetValue("未分类", out var us))
        {
            groups.Add(new WinRateStackGroup
            {
                ParentName = "未分类",
                Months = months,
                ChildNames = new List<string> { "未分类" },
                ChildCounts = new List<List<double>> { months.Select(ym => (double)(us.byMonth.TryGetValue(ym, out var v) ? v : 0)).ToList() },
                ParentWinRate = us.total > 0 ? us.wins * 100.0 / us.total : 0,
                ParentRatio = us.total * 100.0 / grandTotal
            });
        }
        // 占比分母改为实际展示的父级总笔数（对齐原版 allTotal，排除已删除类型的干扰）
        var shownTotal = groups.Sum(g => g.ChildCounts.Sum(cl => cl.Sum()));
        if (shownTotal > 0)
            foreach (var g in groups)
                g.ParentRatio = g.ChildCounts.Sum(cl => cl.Sum()) * 100.0 / shownTotal;
        return groups;
    }

    private SimpleBarSeries ComputeAllWinRateBars(List<Dictionary<string, object?>> cleared)
    {
        var types = cleared.Select(t => string.IsNullOrWhiteSpace(S(t, "entryType")) ? "未分类" : S(t, "entryType"))
            .Distinct().OrderBy(x => x).ToList();
        var cats = new List<string>();
        var vals = new List<double>();
        foreach (var type in types)
        {
            var subset = cleared.Where(t => (string.IsNullOrWhiteSpace(S(t, "entryType")) ? "未分类" : S(t, "entryType")) == type).ToList();
            var total = subset.Count;
            var wins = subset.Count(t => ParseDouble(S(t, "totalReturn")) > 0);
            cats.Add(type);
            vals.Add(total > 0 ? wins * 100.0 / total : 0);
        }
        return new SimpleBarSeries { Categories = cats, Values = vals };
    }

    private LineSeries ComputeOverallLine(List<Dictionary<string, object?>> trades, int monthsCount = 12)
    {
        var months = monthsCount <= 0 ? GetAllDataMonths(trades) : GetLastNMonths(monthsCount);
        return ComputeOverallLineMonths(trades, months);
    }

    /// <summary>采集所有有交易数据的月份（升序、去重），用于"总览"曲线全量展示。</summary>
    private static List<string> GetAllDataMonths(List<Dictionary<string, object?>> trades)
        => trades.Select(t => { var d = S(t, "tradeDate"); return d.Length >= 7 ? d.Substring(0, 7) : ""; })
                 .Where(m => m.Length == 7)
                 .Distinct()
                 .OrderBy(m => m)
                 .ToList();

    private LineSeries ComputeOverallLineMonths(List<Dictionary<string, object?>> trades, List<string> months)
    {
        var values = months.Select(ym =>
        {
            var mt = trades.Where(t => S(t, "tradeDate").StartsWith(ym) && Cleared(t)).ToList();
            if (mt.Count == 0) return 0.0;
            var wins = mt.Count(t => ParseDouble(S(t, "totalReturn")) > 0);
            return wins * 100.0 / mt.Count;
        }).ToList();
        return new LineSeries { Months = months, Values = values };
    }

    private LineSeries ComputeReturnLine(List<Dictionary<string, object?>> allTradesSet, List<string> months)
    {
        var values = months.Select(ym =>
        {
            var stats = CalculateMonthlyStats(allTradesSet, ym);
            return ParseDouble(stats.avgReturn);
        }).ToList();
        return new LineSeries { Months = months, Values = values };
    }

    // ============ 核心算法（对应 statistics.js） ============
    private (int total, string winRate, string avgReturn, Dictionary<string, object?>? bestTrade, Dictionary<string, object?>? worstTrade)
        CalculateMonthlyStats(List<Dictionary<string, object?>> trades, string yearMonth)
    {
        var monthTrades = trades.Where(t => S(t, "tradeDate").StartsWith(yearMonth) && Cleared(t)).ToList();
        var total = monthTrades.Count;
        if (total == 0) return (0, "0", "0", null, null);
        var wins = monthTrades.Count(t => ParseDouble(S(t, "totalReturn")) > 0);
        var avg = monthTrades.Sum(t => ParseDouble(S(t, "totalReturn"))) / total;
        var best = monthTrades.OrderByDescending(t => ParseDouble(S(t, "totalReturn"))).First();
        var worst = monthTrades.OrderBy(t => ParseDouble(S(t, "totalReturn"))).First();
        return (total, (wins * 100.0 / total).ToString("F1"), avg.ToString("F2"),
            best, worst);
    }

    // ============ 过滤器/工具 ============
    private static List<Dictionary<string, object?>> FilterTrades(List<Dictionary<string, object?>> trades, string prefix)
        => trades.Where(t => S(t, "tradeDate").StartsWith(prefix)).ToList();

    private static List<Dictionary<string, object?>> FilterByMonths(List<Dictionary<string, object?>> trades, List<string> months)
        => trades.Where(t => months.Contains(S(t, "tradeDate").Length >= 7 ? S(t, "tradeDate").Substring(0, 7) : "")).ToList();

    private static List<Dictionary<string, object?>> FilterStrong(List<Dictionary<string, object?>> strong, string prefix)
        => strong.Where(s => S(s, "date").StartsWith(prefix)).ToList();

    private static List<Dictionary<string, object?>> FilterStrongByMonths(List<Dictionary<string, object?>> strong, List<string> months)
        => strong.Where(s => months.Contains(S(s, "date").Length >= 7 ? S(s, "date").Substring(0, 7) : "")).ToList();

    private static bool Cleared(Dictionary<string, object?> t) => S(t, "positionStatus") == "已清仓";

    private static List<string> GetLastNMonths(int n)
    {
        var months = new List<string>();
        var now = DateTime.Now;
        for (var i = n - 1; i >= 0; i--)
        {
            var d = new DateTime(now.Year, now.Month, 1).AddMonths(-i);
            months.Add($"{d.Year}-{d.Month:00}");
        }
        return months;
    }

    private static List<string> GetYearMonthsUpToNow(string year)
    {
        var months = new List<string>();
        var y = int.Parse(year);
        var now = DateTime.Now;
        var limit = (y == now.Year) ? now.Month : 12;
        for (var m = 1; m <= limit; m++)
            months.Add($"{y}-{m:00}");
        return months;
    }

    private static string GetCurrentMonth() => $"{DateTime.Now.Year}-{DateTime.Now.Month:00}";

    // problemTags 列被数据层还原成 List<object>，必须传原始值而非 S() 后的字符串
    private static List<string> ParseTags(object? raw) => Services.ArrayFieldUtil.ToStringList(raw);

    private static string S(Dictionary<string, object?> r, string key)
        => r.TryGetValue(key, out var v) && v != null ? v.ToString() ?? "" : "";

    private static int ToInt(Dictionary<string, object?> r, string key)
    {
        // is* 字段被数据层 DeserializeRecord 还原成 bool，需先按 bool 取值
        if (r.TryGetValue(key, out var bv) && bv is bool b) return b ? 1 : 0;
        var s = S(r, key);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;
    }

    private static double ParseDouble(string s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    // ============ 命令 ============
    [RelayCommand]
    private void HandleMonthChange() => Recompute();

    [RelayCommand]
    private void HandleYearChange() => Recompute();

    [RelayCommand]
    private void ResetFilter()
    {
        SelectedMonth = "";
        SelectedYear = DateTime.Now.Year.ToString();
        Recompute();
    }

    partial void OnActiveTabChanged(string value)
    {
        IsYearlyFilterVisible = value == "yearly";
        Recompute();
    }

    partial void OnSelectedMonthChanged(string value) => Recompute();
    partial void OnSelectedYearChanged(string value) => Recompute();
}
