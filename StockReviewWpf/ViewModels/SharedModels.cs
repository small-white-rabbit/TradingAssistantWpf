using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace StockReviewWpf.ViewModels;

// ============================================================================
// SharedModels.cs
// 所有 ViewModel 引用但未定义的数据模型类型。
// 放在 StockReviewWpf.ViewModels 命名空间下，
// 子命名空间 ViewModels.Main / ViewModels.Pet 可直接通过命名空间层次查找访问。
// ============================================================================

#region 统计视图数据模型

/// <summary>
/// 总览卡片项
/// </summary>
public class OverviewCardItem
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public string ClassName { get; set; } = "";
    public bool IsRanking { get; set; }
    public ObservableCollection<TypeRankingItem>? Ranking { get; set; }
    public bool Clickable { get; set; }
}

/// <summary>
/// 类型排名项
/// </summary>
public class TypeRankingItem
{
    public string Type { get; set; } = "";
    public string Rate { get; set; } = "";
    public int Total { get; set; }
}

/// <summary>
/// 进场类型统计行
/// </summary>
public class EntryTypeStatRow
{
    public string EntryType { get; set; } = "";
    public int Count { get; set; }
    public string WinRate { get; set; } = "";
    public string AvgReturn { get; set; } = "";
    public bool IsParent { get; set; }
    public int Indent { get; set; }
}

/// <summary>
/// 问题统计行
/// </summary>
public class ProblemStatRow
{
    public string Problem { get; set; } = "";
    public int Count { get; set; }
    public string Percentage { get; set; } = "";
}

/// <summary>
/// 月度统计行
/// </summary>
public class MonthlyStatRow
{
    public string Month { get; set; } = "";
    public int Total { get; set; }
    public string WinRate { get; set; } = "";
    public string AvgReturn { get; set; } = "";
    public string Best { get; set; } = "";
    public string Worst { get; set; } = "";
}

/// <summary>
/// 强势年份行
/// </summary>
public class StrongYearRow
{
    public string Year { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>
/// 强势月份行
/// </summary>
public class StrongMonthRow
{
    public string Month { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>
/// 问题频率行
/// </summary>
public class ProblemFreqRow
{
    public string Problem { get; set; } = "";
    public int Count { get; set; }
    public string Percentage { get; set; } = "";
}

/// <summary>
/// 进场类型-问题关联行
/// </summary>
public class EntryTypeProblemRow
{
    public string EntryType { get; set; } = "";
    public string CommonProblem { get; set; } = "";
    public int Count { get; set; }
}

#endregion

#region 案例与每日精选数据模型

/// <summary>
/// 案例数据模型
/// </summary>
public class CaseItem : INotifyPropertyChanged
{
    public int Id { get; set; }
    public string StockCode { get; set; } = "";
    public string StockName { get; set; } = "";
    public string TradeDate { get; set; } = "";
    public string TotalReturn { get; set; } = "";
    public string CaseType { get; set; } = "";
    public string EntryType { get; set; } = "";
    public string EntryPrice { get; set; } = "";
    public string ExitPrice { get; set; } = "";
    public string Note { get; set; } = "";

    // 卖点校准相关
    public string FollowUp { get; set; } = "";
    public string FollowUpDate { get; set; } = "";
    public string SellCalibrationHigh { get; set; } = "";
    public string SellCalibrationMaxChange { get; set; } = "";

    // 反思
    public string Reflection { get; set; } = "";

    // 反思的纯文本（RTF 富文本取纯文本展示用）
    public string ReflectionPlain => StockReviewWpf.Services.RichTextUtil.ToPlain(Reflection);

    // 截图
    public string Screenshot { get; set; } = "";
    // 懒加载：卡片进入可视区时读盘，读完经 INPC 补显（HasScreenshot 联动通知）
    private string _displayScreenshot = "";
    public string DisplayScreenshot
    {
        get => _displayScreenshot;
        set
        {
            if (_displayScreenshot == value) return;
            _displayScreenshot = value;
            OnPropertyChanged(nameof(DisplayScreenshot));
            OnPropertyChanged(nameof(HasScreenshot));
        }
    }
    // 截图懒加载去重标记：该记录的读盘请求已入队（仅 UI 线程读写，无需 INPC）
    public bool ScreenshotLoading { get; set; }

    // 是否自定义案例（来自 patternCases 表，可删除）
    public bool IsCustom { get; set; }

    // 是否被选入案例对比（卡片勾选态需要实时刷新）
    private bool _isInCompare;
    public bool IsInCompare
    {
        get => _isInCompare;
        set { if (_isInCompare != value) { _isInCompare = value; OnPropertyChanged(); } }
    }

    public List<string> FollowUpTags => string.IsNullOrWhiteSpace(FollowUp) || FollowUp.Trim() == "[]"
        ? new List<string>()
        : ParseJsonStringArray(FollowUp);

    public bool HasScreenshot => !string.IsNullOrEmpty(Screenshot) || !string.IsNullOrEmpty(DisplayScreenshot);

    public string TotalReturnText => string.IsNullOrEmpty(TotalReturn) ? "-" : (double.TryParse(TotalReturn, out var v) && v > 0 ? "+" + TotalReturn : TotalReturn) + "%";

    /// <summary>最大涨幅展示口径（对齐 Electron）：+12.34% / -5.20%，无数据为 "-"</summary>
    public string SellCalibrationMaxChangeText
    {
        get
        {
            var s = SellCalibrationMaxChange?.Trim() ?? "";
            if (s.Length == 0 || s == "-") return "-";
            return (s.StartsWith("-") ? "" : "+") + s + "%";
        }
    }

    public bool IsCalibration => FollowUpTags.Count > 0;

    /// <summary>当前是否处于"卖点校准"Tab（由 VM 在查询时写入）</summary>
    public bool IsCalibrationTab { get; set; }

    /// <summary>反思有内容即显示（校准 Tab 也展示反思；普通 Tab 折叠约3行，校准 Tab 完整不折叠）</summary>
    public bool ShowReflection => !string.IsNullOrEmpty(Reflection);

    /// <summary>普通 Tab：反思折叠展示（约3行，超出裁切）</summary>
    public bool ShowReflectionFolded => ShowReflection && !IsCalibrationTab;

    /// <summary>校准 Tab：反思完整展示不折叠（绑定原始 Reflection，保留换行）</summary>
    public bool ShowReflectionFull => ShowReflection && IsCalibrationTab;

    /// <summary>校准 Tab 不显示"详情"按钮：卡片底部直接对齐反思内容，详情入口由双击信息区承担</summary>
    public bool ShowDetailButton => !IsCalibrationTab;

    private static List<string> ParseJsonStringArray(string json)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        var trimmed = json.Trim();
        if (!trimmed.StartsWith("[")) return result;
        var inner = trimmed.Substring(1, trimmed.Length - 2);
        foreach (var part in inner.Split(','))
        {
            var p = part.Trim();
            if (p.Length >= 2 && p.StartsWith("\"") && p.EndsWith("\""))
                result.Add(p.Substring(1, p.Length - 2));
        }
        return result;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));
}

/// <summary>
/// 每日擒牛记录（对应 dailyPicks 表）
/// </summary>
public class DailyPickRecord : INotifyPropertyChanged
{
    public int Id { get; set; }
    public string PickDate { get; set; } = "";

    // OCR/行情回填字段须发变更通知：RecognizeAndFill/AutoFetchStockData 在后台
    // 完成后赋值，无 INPC 则对话框输入框不刷新，表现为「粘贴截图没识别到信息」
    private string _stockCode = "";
    public string StockCode
    {
        get => _stockCode;
        set { if (_stockCode != value) { _stockCode = value; OnPropertyChanged(); } }
    }
    private string _stockName = "";
    public string StockName
    {
        get => _stockName;
        set { if (_stockName != value) { _stockName = value; OnPropertyChanged(); } }
    }
    private double? _price;
    public double? Price
    {
        get => _price;
        set { if (_price != value) { _price = value; OnPropertyChanged(); } }
    }
    private double? _change;
    public double? Change
    {
        get => _change;
        set { if (_change != value) { _change = value; OnPropertyChanged(); } }
    }

    // 表单驱动字段须发变更通知，否则对话框内"选择类型"高亮、"是否选中"评估区不会联动
    private string _pickType = "";
    public string PickType
    {
        get => _pickType;
        set { if (_pickType != value) { _pickType = value; OnPropertyChanged(); } }
    }
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }
    public string Remark { get; set; } = "";
    public string Screenshot { get; set; } = "";
    public double? NextDayHighPrice { get; set; }
    public double? NextDayMaxChange { get; set; }
    public string Evaluation { get; set; } = ""; // JSON

    // 展示用（需变更通知：两阶段加载时截图后台读完逐条补显）
    private string _displayScreenshot = "";
    public string DisplayScreenshot
    {
        get => _displayScreenshot;
        set { if (_displayScreenshot != value) { _displayScreenshot = value; OnPropertyChanged(); } }
    }
    public int Rank { get; set; }

    // 截图懒加载去重标记：该记录的读盘请求已入队（仅 UI 线程读写，无需 INPC）
    public bool ScreenshotLoading { get; set; }

    public bool HasScreenshot => !string.IsNullOrEmpty(Screenshot);
    public bool HasNextDay => NextDayMaxChange.HasValue;
    public string PriceText => Price.HasValue ? $"¥{Price:F2}" : "¥0.00";
    public string ChangeText => Change.HasValue ? $"{(Change >= 0 ? "+" : "")}{Change:F2}%" : "0.00%";
    public string NextDayMaxChangeText => NextDayMaxChange.HasValue ? $"{(NextDayMaxChange >= 0 ? "+" : "")}{NextDayMaxChange:F2}%" : "-";
    public string NextDayHighPriceText => NextDayHighPrice.HasValue ? $"¥{NextDayHighPrice:F2}" : "-";
    public DailyPickEvaluation? Eval => DailyPickEvaluation.Parse(Evaluation);
    public bool EvaluationComplete =>
        Eval != null && !string.IsNullOrEmpty(Eval.TrendStatus)
                     && !string.IsNullOrEmpty(Eval.CyclePattern)
                     && !string.IsNullOrEmpty(Eval.SpaceStatus);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 选中股评估项（趋势形态 / 30-60分形态 / 空间充足）
/// </summary>
public class DailyPickEvaluation
{
    public string? TrendStatus { get; set; }
    public string? CyclePattern { get; set; }
    public string? SpaceStatus { get; set; }
    public static DailyPickEvaluation? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<DailyPickEvaluation>(json); }
        catch { return null; }
    }
    public string ToJson() => JsonSerializer.Serialize(this);
}

/// <summary>
/// 按日期分组的擒牛记录，用于 records 页的日期卡片流
/// </summary>
public class DateGroup : INotifyPropertyChanged
{
    public string Date { get; set; } = "";
    public ObservableCollection<DailyPickRecord> Picks { get; set; } = new();

    /// <summary>刷新提示：显示在刷新按钮后的绿色文字，10 秒自动消失（null = 隐藏）</summary>
    private string? _refreshTip;
    public string? RefreshTip
    {
        get => _refreshTip;
        set { if (_refreshTip != value) { _refreshTip = value; OnPropertyChanged(nameof(RefreshTip)); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 强势股数据模型
/// </summary>
public class StrongStockItem : INotifyPropertyChanged
{
    public int Id { get; set; }
    public string Date { get; set; } = "";
    public string StockCode { get; set; } = "";
    public string StockName { get; set; } = "";
    public double? HighPrice { get; set; }
    public double? MaxChangePct { get; set; }
    public double? ChangePct { get; set; }
    public double? ClosePrice { get; set; }
    public string Screenshot { get; set; } = "";
    // 两阶段加载：截图后台读完经 INPC 补显
    private string _displayScreenshot = "";
    public string DisplayScreenshot
    {
        get => _displayScreenshot;
        set { if (_displayScreenshot != value) { _displayScreenshot = value; OnPropertyChanged(nameof(DisplayScreenshot)); } }
    }
    // 截图懒加载去重标记：该记录的读盘请求已入队（仅 UI 线程读写，无需 INPC）
    public bool ScreenshotLoading { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public string StrongType { get; set; } = "";
    public string RelatedTradeIds { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";

    /// <summary>截图门控按路径派生：清空 DisplayScreenshot（导航离开）后卡片图区不坍塌，
    /// 切回视图时 Image.Loaded 重新触发懒加载</summary>
    public bool HasScreenshot => !string.IsNullOrEmpty(Screenshot);

    // Convenience properties for display
    public string HighPriceText => HighPrice.HasValue ? HighPrice.Value.ToString("F2") : "";
    public string MaxChangePctText => MaxChangePct.HasValue ? (MaxChangePct.Value >= 0 ? "+" : "") + MaxChangePct.Value.ToString("F2") + "%" : "";
    public string ChangePctText => ChangePct.HasValue ? (ChangePct.Value >= 0 ? "+" : "") + ChangePct.Value.ToString("F2") + "%" : "";
    public bool HasRelated => !string.IsNullOrWhiteSpace(RelatedTradeIds) && RelatedTradeIds.Trim() != "[]";
}

/// <summary>
/// 强势股按日期分组（对应原版的 dayGroup）
/// </summary>
public class StrongStockDayGroup
{
    public string Date { get; set; } = "";
    public int Day { get; set; }
    public string Month { get; set; } = "";
    public string WeekDay { get; set; } = "";
    public ObservableCollection<StrongStockItem> Stocks { get; set; } = new();
    public bool HasData => Stocks.Count > 0;
}

#endregion

#region 形态优化数据模型

/// <summary>
/// 模式优化统计（对应原版 overviewList 项；实现 INPC 支持概览 tile 行内编辑切换）
/// </summary>
public class PatternStat : INotifyPropertyChanged
{
    public int Id { get; set; }
    public string TypeName { get; set; } = "";
    public int TotalCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailCount { get; set; }
    public string SuccessRate { get; set; } = "0";
    public bool IsParent { get; set; }
    public bool IsStrongType { get; set; }

    // 概览明细字段（对应原版 overviewList）
    public string Description { get; set; } = "";
    public string? TypeImage { get; set; }
    public string? StandardFormImage { get; set; }
    public string? Reflections { get; set; }

    // 形态感悟纯文本（RTF 富文本取纯文本供列表卡片预览）
    public string ReflectionsPlain => StockReviewWpf.Services.RichTextUtil.ToPlain(Reflections ?? "");
    public List<string> PlusItems { get; set; } = new();
    public List<string> MinusItems { get; set; } = new();
    public List<PatternCaseBrief> TopSuccessCases { get; set; } = new();
    public List<PatternCaseBrief> TopFailCases { get; set; } = new();

    // 行内编辑状态：""=显示模式，"plus"/"minus"/"reflection"=对应区块处于编辑模式
    private string _editingField = "";
    public string EditingField
    {
        get => _editingField;
        set { if (_editingField != value) { _editingField = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));
}

/// <summary>
/// 模式优化左侧类型导航项（父级/子级/孤立子级/强势类型分组共用）
/// </summary>
public class TypeNavItem : INotifyPropertyChanged
{
    /// <summary>类型 ID；0 表示无真实 ID 的强势分组（点击回到概览，对齐原版行为）</summary>
    public int Id { get; set; }
    public string TypeName { get; set; } = "";
    public bool IsChild { get; set; }
    public bool IsStrongType { get; set; }
    public int TotalCount { get; set; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));
}

/// <summary>
/// 概览案例排行简项
/// </summary>
public class PatternCaseBrief
{
    public int Id { get; set; }
    public string StockName { get; set; } = "";
    public string StockCode { get; set; } = "";
    public string TradeDate { get; set; } = "";
    public double MaxChangePct { get; set; }
    public string? DisplayScreenshot { get; set; }
    public bool IsCustom { get; set; }
    public string EntryType { get; set; } = "";

    public string MaxChangePctText => (MaxChangePct >= 0 ? "+" : "") + MaxChangePct.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "%";
}

#endregion

#region 设置数据模型

/// <summary>
/// 进场类型数据模型
/// </summary>
public class EntryTypeItem
{
    public int Id { get; set; }
    public int SortOrder { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#409EFF";
    public bool IsStrongType { get; set; }
    public string Description { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int? ParentId { get; set; }
    public ObservableCollection<EntryTypeItem> Children { get; set; } = new();
    public bool HasChildren => Children.Count > 0;
    /// <summary>树形平铺展示：子级缩进量（父级不缩进，替代原 └ 符号）</summary>
    public Thickness Indent => ParentId.HasValue ? new Thickness(18, 0, 0, 0) : new Thickness(0);

    // 形态优化详情字段
    public string StandardForm { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Reflections { get; set; } = "";
    public string PlusItems { get; set; } = "";
    public string MinusItems { get; set; } = "";
    public string TypeImage { get; set; } = "";
    public string StandardFormImage { get; set; } = "";
}

/// <summary>
/// 心得记录（对应 insights 表）
/// </summary>
public class InsightItem
{
    public int Id { get; set; }
    public string RecordDate { get; set; } = "";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public int Importance { get; set; }
    public string StockCode { get; set; } = "";
    public string StockName { get; set; } = "";
    public List<string> RelatedCaseIds { get; set; } = new();
    public List<string> RelatedCaseTypes { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public List<string> Screenshots { get; set; } = new();
    public bool IsPinned { get; set; }
    public List<DisplayCaseBrief> DisplayCases { get; set; } = new();

    public class DisplayCaseBrief
    {
        public string Id { get; set; } = "";
        public string StockName { get; set; } = "";
        public string StockCode { get; set; } = "";
        public bool IsSuccess { get; set; }
    }
    public string PinnedAt { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";

    // 内存治理（2026-09-06）：移除 DisplayScreenshots（原列表期全量预读 base64 常驻），
    // 详情/编辑弹窗改为打开时按需读盘补显（InsightsViewModel.LoadScreenshotsOnDemand）。

    public string PlainContent => StockReviewWpf.Services.RichTextUtil.ToPlain(Content);
    public string StarsText => new string('★', Math.Clamp(Importance, 1, 5));
    public bool HasImage =>
        (Screenshots?.Count(s => !string.IsNullOrWhiteSpace(s)) ?? 0) > 0
        || (Content?.Contains("<img", StringComparison.OrdinalIgnoreCase) ?? false);
    // 对应原版 shouldShowMoreHint：正文超 60 字或有截图时提示“点击查看详情”
    public bool ShowMoreHint => PlainContent.Length > 60 || HasImage;
    public string ImportanceLabel => Importance switch
    {
        5 => "非常重要",
        4 => "重要",
        3 => "一般",
        2 => "较低",
        1 => "低",
        _ => "未分级"
    };
}

/// <summary>
/// 日记数据模型（对应 dailySummaries 表，Summary 支持 RTF 富文本）。
/// </summary>
public class DiaryItem
{
    public int Id { get; set; }
    public string RecordDate { get; set; } = "";
    public string SummaryType { get; set; } = "daily";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string StartDate { get; set; } = "";
    public string EndDate { get; set; } = "";
    public string CreatedAt { get; set; } = "";

    // 纸张风格：该时段的交易列表与统计（对应原版 getDiaryTrades / diary.stats）
    public List<TradeRecord> Trades { get; set; } = new();
    public int TradeTotal { get; set; }
    public double WinRate { get; set; }
    public double AvgReturn { get; set; }
    public bool HasStats => TradeTotal > 0;
    public bool WinRateUp => WinRate >= 50;
    public bool AvgReturnUp => AvgReturn >= 0;

    public string TypeLabel => SummaryType switch
    {
        "weekly" => "周记",
        "monthly" => "月记",
        _ => "日记"
    };
    public string PlainContent => StockReviewWpf.Services.RichTextUtil.ToPlain(Summary);
    public bool IsWeekly => SummaryType == "weekly";
    public bool IsMonthly => SummaryType == "monthly";
    public bool IsDaily => SummaryType == "daily";

    /// <summary>页码标签（由 InsightsViewModel 在加载列表时赋值，1-based：最新=1, 最老=N）</summary>
    public int PaperNumber { get; set; }
    // 纸张页眉：日记无标题时回退显示日期（对应原版 diary.title || diary.recordDate）
    public string HeaderTitle => IsDaily && string.IsNullOrWhiteSpace(Title) ? RecordDate : Title;
    public string CreatedDate => string.IsNullOrEmpty(CreatedAt) ? "" : CreatedAt.Split('T', ' ')[0];
    public string TradeTotalText => $"{TradeTotal}笔";
    public string WinRateText => $"{WinRate}%";
    public string AvgReturnText => $"{AvgReturn}%";
    // 纸张页眉日期：日记显示单日；周/月记显示区间
    public string DateRangeText
    {
        get
        {
            var start = string.IsNullOrEmpty(StartDate) ? RecordDate : StartDate;
            var end = string.IsNullOrEmpty(EndDate) ? RecordDate : EndDate;
            return start == end ? start : $"{start} ~ {end}";
        }
    }
}
/// 问题标签数据模型
/// </summary>
public class ProblemTagItem
{
    public int Id { get; set; }
    public int SortOrder { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#F56C6C";
    public string Description { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

#endregion

#region 年月视图数据模型

/// <summary>
/// 月份数据分组
/// </summary>
public class MonthDataGroup
{
    public string Key { get; set; } = "";
    public int Year { get; set; }
    public int MonthNum { get; set; }
    public string Month { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public ObservableCollection<TradeRecord> Trades { get; set; } = new();
    public ObservableCollection<StrongStockItem> StrongStocks { get; set; } = new();
    public ObservableCollection<DayGroupData> DayGroups { get; set; } = new();
    public MonthStats Stats { get; set; } = new();

    /// <summary>该月既无交易也无强股（仅用于空态判断，与原版 空态条件一致）</summary>
    public bool IsEmpty => Trades.Count == 0 && StrongStocks.Count == 0;
}

/// <summary>
/// 日期分组数据（按日期分组的交易+强股）
/// </summary>
public class DayGroupData
{
    public string Date { get; set; } = "";
    public int Day { get; set; }
    public string Month { get; set; } = "";
    public string WeekDay { get; set; } = "";
    public ObservableCollection<TradeRecord> Trades { get; set; } = new();
    public ObservableCollection<StrongStockItem> StrongStocks { get; set; } = new();
}

/// <summary>
/// 日期选择条单元格（对应原版的日期格）
/// </summary>
public class DateCell
{
    public int Day { get; set; }
    public string DateStr { get; set; } = "";
    public bool HasData { get; set; }
}

/// <summary>
/// 月度统计
/// </summary>
public class MonthStats
{
    public int Total { get; set; }
    public string WinRate { get; set; } = "0";
    public string AvgReturn { get; set; } = "0";
}

/// <summary>
/// 年月视图交易记录
/// </summary>
public class TradeRecord : INotifyPropertyChanged
{
    public int Id { get; set; }
    public string TradeDate { get; set; } = "";
    public string StockCode { get; set; } = "";
    public string StockName { get; set; } = "";
    public string EntryType { get; set; } = "";
    public string ParentEntryType { get; set; } = "";
    public string PositionStatus { get; set; } = "";
    public string CaseType { get; set; } = "";
    public string FirstDate { get; set; } = "";
    public double? ClosePrice { get; set; }
    public double? PrevClose { get; set; }
    public double? HighPrice { get; set; }
    public double? ChangePct { get; set; }
    public double? MaxChangePct { get; set; }
    public string TodayPerformance { get; set; } = "";
    public string MeetExpectation { get; set; } = "";
    public double? ExitPrice { get; set; }
    public string ExitDate { get; set; } = "";
    public double? TotalReturn { get; set; }
    public string Remark { get; set; } = "";
    public string ProblemTags { get; set; } = "";
    public string FollowUp { get; set; } = "";
    public string FollowUpDate { get; set; } = "";
    // 纸张交易表用的短显示（对应原版 tradeDate.slice(5) / ±收益% / 清-持）
    public string TradeDateShort => TradeDate.Length >= 10 ? TradeDate.Substring(5, 5) : TradeDate;
    public bool ReturnUp => (TotalReturn ?? 0) >= 0;
    public string StatusShort => PositionStatus == "已清仓" ? "清" : "持";
    public double? SellCalibrationHigh { get; set; }
    public double? SellCalibrationMaxChange { get; set; }
    public string Reflection { get; set; } = "";
    public string Screenshot { get; set; } = "";
    // 内存治理（2026-09-06）：移除 DisplayScreenshot（原两阶段加载把全年截图 base64
    // 字符串常驻在每条记录上，达数百 MB）。卡片改为直绑 Screenshot 路径 +
    // Base64ImageConverter(IsAsync) 按可视区解码，位图由转换器 12 张 LRU 封顶。
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public bool IsStrongToday { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";

    // Convenience properties for display（原版涨跌幅带 % 与正负号）
    public string ClosePriceText => ClosePrice.HasValue ? ClosePrice.Value.ToString("F2") : "-";
    public string ChangePctText => ChangePct.HasValue ? (ChangePct.Value >= 0 ? "+" : "") + ChangePct.Value.ToString("F2") + "%" : "-";
    public string MaxChangePctText => MaxChangePct.HasValue ? (MaxChangePct.Value >= 0 ? "+" : "") + MaxChangePct.Value.ToString("F2") + "%" : "-";
    public string HighPriceText => HighPrice.HasValue ? HighPrice.Value.ToString("F2") : "-";
    public string TotalReturnText => TotalReturn.HasValue ? (TotalReturn.Value >= 0 ? "+" : "") + TotalReturn.Value.ToString("F2") + "%" : "-";
    public bool HasScreenshot => !string.IsNullOrEmpty(Screenshot);
    public bool IsCleared => PositionStatus == "已清仓";
    public bool IsClearedUp => PositionStatus == "已清仓" && (TotalReturn ?? 0) >= 0;
    public bool IsClearedDown => PositionStatus == "已清仓" && (TotalReturn ?? 0) < 0;

    // 进场类型标签配色（对应原版 getTagType：按 entryTypes.sortOrder 从 colorPool 取，
    // primary/danger/warning/success/info，'' 视为 primary）
    public string EntryTagType { get; set; } = "info";

    // 【HHV 最高价 ±最大涨幅%】（对应原版 .max-change 模板）
    public bool HasHhv => HighPrice.HasValue && MaxChangePct.HasValue;
    public string HhvRangeText => HasHhv
        ? $"【HHV {HighPriceText}  {(MaxChangePct!.Value >= 0 ? "+" : "")}{MaxChangePct.Value:0.00}%】"
        : "";
}

#endregion

#region 宠物相关数据模型

/// <summary>
/// 交易计划数据模型
/// </summary>
public class TradePlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string StockCode { get; set; } = "";
    public string StockName { get; set; } = "";
    public string PlanType { get; set; } = "sell";
    public string PlanDate { get; set; } = "";
    public string EntryReason { get; set; } = "";
    public decimal? EntryPrice { get; set; }
    public decimal? TargetPrice { get; set; }
    public decimal? StopLoss { get; set; }
    public int MaxHoldDays { get; set; } = 3;
    public string Status { get; set; } = "pending";
    public string ExecutionStatus { get; set; } = "not_executed";
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // ====== UI 显示属性（对齐原版 PlanListPanel.vue getEntryReasonLabel / getStatusLabel / getStatusType）======

    /// <summary>进场理由中文标签（原始值如 w_bottom → W底突破，未命中回退原值）</summary>
    public string EntryReasonLabel
    {
        get
        {
            if (string.IsNullOrEmpty(EntryReason)) return "-";
            var hit = StockReview.Core.Services.TradePlanService.ValidReasons.Find(r => r.Value == EntryReason);
            return string.IsNullOrEmpty(hit.Label) ? EntryReason : hit.Label;
        }
    }

    /// <summary>状态中文标签（draft→草稿 / confirmed→已确认 / executing→执行中 / pending→待执行 / executed→已执行 / expired→已过期 / cancelled→已取消）</summary>
    public string StatusLabel => Status switch
    {
        "draft" => "草稿",
        "confirmed" => "已确认",
        "executing" => "执行中",
        "pending" => "待执行",
        "executed" => "已执行",
        "expired" => "已过期",
        "cancelled" => "已取消",
        _ => Status
    };

    /// <summary>状态徽章底色（对齐 el-tag 实心配色：primary蓝/warning橙/success绿/info灰/danger红）</summary>
    public Brush StatusBrush
    {
        get
        {
            var hex = Status switch
            {
                "confirmed" => "#409EFF",
                "executing" or "pending" => "#E6A23C",
                "executed" => "#67C23A",
                "cancelled" => "#F56C6C",
                _ => "#909399"
            };
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}

/// <summary>
/// 提醒历史记录
/// </summary>
public class ReminderHistoryRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DateStr { get; set; } = "";
    public long Timestamp { get; set; }
    public string Type { get; set; } = "";
    public string Level { get; set; } = "hint";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string? StockCode { get; set; }
    public string? StockName { get; set; }
    public string? UserResponse { get; set; }
    public int? ResponseTime { get; set; }

    // ====== UI 显示属性 ======

    public string LevelLabel => Level switch
    {
        "hint" => "提示",
        "alert" => "警告",
        "warning" => "警戒",
        "critical" => "严重",
        "force" => "强制",
        _ => Level
    };

    public string TypeLabel => Type switch
    {
        "price_alert" => "价格提醒",
        "stop_loss" => "止损提醒",
        "target_price" => "目标价提醒",
        "limit_move" => "涨跌停提醒",
        "sell_point" => "卖点信号",
        "signal" => "信号提醒",
        "insight" => "心得提醒",
        "trade" => "交易提醒",
        "after_market" => "收盘提醒",
        "after_market_review" => "盘后复盘",
        "market_digest" => "休市摘要",
        "custom_reminder" => "自定义提醒",
        "combined_signals" => "多信号提醒",
        "surge" => "快速拉升",
        "plunge" => "快速下跌",
        _ => Type
    };

    public string TimeFormatted
    {
        get
        {
            try
            {
                var dt = DateTimeOffset.FromUnixTimeMilliseconds(Timestamp).ToLocalTime();
                return dt.ToString("HH:mm:ss");
            }
            catch { return ""; }
        }
    }

    /// <summary>响应中文标签（对齐原版 responseLabel）</summary>
    public string ResponseLabel => UserResponse switch
    {
        "executed" => "已执行",
        "delayed" => "延迟处理",
        "ignored" => "忽略",
        "done" => "已完成",
        "snooze" => "稍后提醒",
        "view" => "已查看",
        "custom_done" => "已完成",
        "custom_snooze" => "稍后提醒",
        "after_market_record" => "添加记录",
        "after_market_continue" => "继续执行",
        "after_market_complete" => "全部完成",
        "after_market_dismiss" => "稍后提醒",
        _ => UserResponse ?? ""
    };
}

/// <summary>
/// 宠物设置模型
/// </summary>
public class PetSettings
{
    public string PrimarySource { get; set; } = "eastmoney";
    public int RefreshInterval { get; set; } = 5000;
    public bool FutuEnabled { get; set; }
    public string FutuHost { get; set; } = "127.0.0.1";
    public int FutuPort { get; set; } = 11111;
    public string FutuPythonPath { get; set; } = "python";
    public string FutuOpenDPath { get; set; } = "";
    public bool OpendAlertEnabled { get; set; } = true;
    public double PriceChangeThreshold { get; set; } = 3.0;
    public double PriceNearThreshold { get; set; } = 1.0;
    public double SurgePullbackThreshold { get; set; } = 2.0;
    public double VolumeAmplifyMultiple { get; set; } = 2.0;
    public double SupportBreakdownTolerance { get; set; } = 1.0;
    public bool PreCloseMA5Check { get; set; } = true; // 默认开启（旧设置文件无此字段时视为开）
    public bool ReminderEnabled { get; set; } = true;
    public bool ScreenFlashEnabled { get; set; } = true;
    public bool FullscreenOverlayEnabled { get; set; }
    public int BubbleDisplayDuration { get; set; } = 30000;
    public int BubbleDurationTrade { get; set; } = -1;
    public int BubbleDurationInsight { get; set; } = -1;
    public int BubbleDurationSignal { get; set; } = -1;
    public int AfterMarketReminderInterval { get; set; } = 15;
    public bool SellPointDetection { get; set; } = true;
    public bool KeyLevelDetection { get; set; } = true;
    public bool InsightReminderEnabled { get; set; } = true;
    public int InsightReminderInterval { get; set; } = 60;
    public int InsightMinStars { get; set; } = 0;
    public bool AutoStart { get; set; }
    public double PetSize { get; set; } = 1.0;
    public double PetOpacity { get; set; } = 1.0;
    public double BubbleBackgroundOpacity { get; set; } = 0.95;
    public double AnimationSpeed { get; set; } = 1.0;
    public bool ClickThrough { get; set; }
    public bool DragMoveEnabled { get; set; } = true;
    public bool Enabled { get; set; }
}

#endregion
