using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dapper;
using StockReview.Core.Data;
using StockReview.Core.MarketData;
using StockReviewWpf.Models;
using StockReviewWpf.Services;

namespace StockReviewWpf.ViewModels.Main;

/// <summary>
/// 年月视图 ViewModel - 对应 YearMonthView.vue
/// 完整复刻：年份导航、月份选择、日期选择、显示模式、交易卡片、强股卡片
/// </summary>
public partial class YearMonthViewModel : ObservableObject
{
    private readonly IDatabaseService _db;
    private readonly ImageService _img;
    private readonly StockOcrService _ocr;
    private readonly MarketDataAggregator _market;
    private readonly IDialogService _dialogs;

    // Raw data caches
    private List<Dictionary<string, object?>> _allTrades = new();
    private List<Dictionary<string, object?>> _allStrongStocks = new();
    private List<Dictionary<string, object?>> _entryTypes = new();

    // Color pool for entry type tags (matching Vue)
    private static readonly string[] ColorPool = { "danger", "warning", "success", "primary", "info", "" };

    [ObservableProperty]
    private int _currentYear = DateTime.Now.Year;

    [ObservableProperty]
    private int _activeMonth = DateTime.Now.Month;

    [ObservableProperty]
    private string _selectedDate = "";

    [ObservableProperty]
    private string _selectedMonthKey = "";

    [ObservableProperty]
    private string _displayMode = "show"; // hidden / compact / show

    [ObservableProperty]
    private ObservableCollection<MonthDataGroup> _months = new();

    [ObservableProperty]
    private ObservableCollection<DateCell> _daysInMonth = new();

    [ObservableProperty]
    private ObservableCollection<string> _allDataDates = new();

    [ObservableProperty]
    private bool _showStrongStocks = true;

    [ObservableProperty]
    private bool _isLoading;

    // Dialog states
    [ObservableProperty]
    private bool _showForm;

    [ObservableProperty]
    private bool _showDiaryDialog;

    [ObservableProperty]
    private bool _showStrongDialog;

    [ObservableProperty]
    private TradeRecord? _editingTrade;

    [ObservableProperty]
    private StrongStockItem? _selectedStrongStock;

    // 当日强股列表（强股详情对话框内"查看每日强股"用）
    [ObservableProperty]
    private ObservableCollection<StrongStockItem> _dayStrongStocks = new();

    // 截图预览
    [ObservableProperty]
    private bool _showScreenshotPreview;

    [ObservableProperty]
    private string _previewScreenshotPath = "";

    // Diary form
    [ObservableProperty]
    private string _diaryType = "daily";

    [ObservableProperty]
    private string _diaryDate = DateTime.Now.ToString("yyyy-MM-dd");

    [ObservableProperty]
    private string _diaryTitle = "";

    [ObservableProperty]
    private string _diaryContent = "";

    [ObservableProperty]
    private bool _isSavingDiary;

    /// <summary>草稿自动保存提示（如"已自动保存 14:32"，空串隐藏）。</summary>
    [ObservableProperty]
    private string _diaryDraftHint = "";

    // 草稿防抖计时器：编辑停顿 2 秒后自动落草稿（边思考边写不丢内容）
    private readonly System.Windows.Threading.DispatcherTimer _diaryDraftTimer;

    // Month buttons (1-12)
    public ObservableCollection<MonthButtonItem> MonthButtons { get; } = new(
        Enumerable.Range(1, 12).Select(m => new MonthButtonItem { Month = m }));

    // 录入表单下拉数据
    public ObservableCollection<EntryTypeItem> EntryTypeItems { get; } = new();
    public ObservableCollection<ProblemTagItem> ProblemTagItems { get; } = new();

    // ===== 交易录入表单字段（对应原版表单）=====
    [ObservableProperty] private string _formTradeDate = DateTime.Now.ToString("yyyy-MM-dd");
    [ObservableProperty] private string _formStockCode = "";
    [ObservableProperty] private string _formStockName = "";
    [ObservableProperty] private string _formClosePrice = "";
    [ObservableProperty] private string _formPrevClose = "";
    [ObservableProperty] private string _formHighPrice = "";
    [ObservableProperty] private string _formChangePct = "";
    [ObservableProperty] private string _formMaxChangePct = "";
    [ObservableProperty] private string _formEntryType = "";
    [ObservableProperty] private string _formPositionStatus = "首次建仓";
    [ObservableProperty] private string _formFirstDate = "";
    [ObservableProperty] private string _formTodayPerformance = "";
    [ObservableProperty] private string _formMeetExpectation = "";
    [ObservableProperty] private string _formExitDate = "";
    [ObservableProperty] private string _formTotalReturn = "";
    [ObservableProperty] private string _formRemark = "";
    [ObservableProperty] private string _formReflection = "";
    [ObservableProperty] private string _formProblemTags = "";      // 逗号分隔
    [ObservableProperty] private string _formFollowUp = "";        // 逗号分隔
    [ObservableProperty] private string _formScreenshot = "";
    [ObservableProperty] private bool _isEditMode;
    [ObservableProperty] private int _editingTradeId;

    public bool ShouldIncludeStrongOnlyDates => DisplayMode == "show";

    // 持仓状态下拉选项（对应原版 TradeForm 的 positionStatus 单选组）
    public ObservableCollection<string> PositionStatusOptions { get; } = new()
    {
        "首次建仓", "持仓中", "已清仓"
    };

    public YearMonthViewModel(IDatabaseService db, ImageService img, StockOcrService ocr, MarketDataAggregator market, IDialogService? dialogs = null)
    {
        _db = db;
        _img = img;
        _ocr = ocr;
        _market = market;
        _dialogs = dialogs ?? DialogService.Instance;

        _diaryDraftTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _diaryDraftTimer.Tick += (_, _) =>
        {
            _diaryDraftTimer.Stop();
            _ = FlushDiaryDraftAsync();
        };

        // 从 appConfig 恢复显示模式（对应 旧版 localStorage 持久化）
        RestoreDisplayMode();

        var now = DateTime.Now;
        SelectedMonthKey = $"{now.Year}-{now.Month:D2}";
        UpdateMonthButtonStates();
        UpdateDaysInMonth();
        // 表单选项（进场类型/问题标签）延迟加载：不阻塞首屏交易数据渲染
        _ = Task.Run(() =>
        {
            try { LoadFormOptions(); }
            catch { }
        });
        _ = LoadDataAsync();
    }

    /// <summary>
    /// 加载进场类型与问题标签下拉数据（录入表单用）。
    /// 可在任意线程调用：DB 查询在本线程执行，集合更新切回 UI 线程。
    /// </summary>
    private void LoadFormOptions()
    {
        try
        {
            // P5：SQL 已下沉 Core（IDatabaseService.GetActiveEntryTypes/GetActiveProblemTags）
            var etItems = new List<EntryTypeItem>();
            foreach (var dict in _db.GetActiveEntryTypes())
            {
                etItems.Add(new EntryTypeItem
                {
                    Id = GetInt(dict, "id"),
                    SortOrder = GetInt(dict, "sortOrder"),
                    Name = GetStr(dict, "typeName"),
                    ParentId = dict.TryGetValue("parentId", out var p) && p != null ? (int?)GetInt(dict, "parentId") : null,
                    IsActive = true
                });
            }

            var ptItems = new List<ProblemTagItem>();
            foreach (var dict in _db.GetActiveProblemTags())
            {
                ptItems.Add(new ProblemTagItem
                {
                    Id = GetInt(dict, "id"),
                    SortOrder = GetInt(dict, "sortOrder"),
                    Name = GetStr(dict, "tagName"),
                    IsActive = true
                });
            }

            // 切回 UI 线程更新 ObservableCollection（线程安全）
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                EntryTypeItems.Clear();
                foreach (var it in etItems) EntryTypeItems.Add(it);
                ProblemTagItems.Clear();
                foreach (var it in ptItems) ProblemTagItems.Add(it);
            });
        }
        catch { }
    }

    /// <summary>
    /// 启动时从 appConfig 读回 showStrongStocks / tradeDisplayMode，保持用户上次的显示偏好。
    /// </summary>
    private void RestoreDisplayMode()
    {
        try
        {
            var strongRow = _db.GetById("appConfig", "showStrongStocks");
            var modeRow = _db.GetById("appConfig", "tradeDisplayMode");
            var strongVal = strongRow != null && strongRow.TryGetValue("value", out var sv) && sv != null ? sv.ToString() : null;
            var modeVal = modeRow != null && modeRow.TryGetValue("value", out var mv) && mv != null ? mv.ToString() : null;

            if (!string.IsNullOrEmpty(modeVal))
            {
                DisplayMode = modeVal;
            }
            else if (!string.IsNullOrEmpty(strongVal))
            {
                // 旧版备份存 "true"/"false"（小写），WPF 自身存 "True"/"False"，忽略大小写兼容两者
                DisplayMode = string.Equals(strongVal, "false", StringComparison.OrdinalIgnoreCase) ? "hidden" : "show";
            }
            ShowStrongStocks = DisplayMode != "hidden";
        }
        catch
        {
            // 读不到则用默认值
        }
    }

    /// <summary>
    /// 重新加载全部数据（标题栏"刷新"按钮调用；视图有缓存，导航不会重新加载）
    /// </summary>
    [RelayCommand]
    public async Task Reload() => await LoadDataAsync();

    /// <summary>
    /// Load trades and strong stocks for the current year from database
    /// </summary>
    public async Task LoadDataAsync()
    {
        IsLoading = true;
        try
        {
            var yearPrefix = $"{CurrentYear}-";
            await Task.Run(() =>
            {
                // P5：SQL 已下沉 Core（IDatabaseService 领域方法）
                _allTrades = _db.GetTradesByYearPrefix(yearPrefix);
                _allStrongStocks = _db.GetStrongStocksByYearPrefix(yearPrefix);
                _entryTypes = _db.GetActiveEntryTypes();
            });

            ShowStrongStocks = DisplayMode != "hidden";
            UpdateAllDataDates();
            RebuildMonths();
            UpdateDaysInMonth();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[YearMonthViewModel] LoadData failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Update the set of all dates that have data
    /// </summary>
    private void UpdateAllDataDates()
    {
        var dates = new HashSet<string>();
        foreach (var t in _allTrades)
        {
            if (t.TryGetValue("tradeDate", out var d) && d != null)
                dates.Add(d.ToString()!);
        }
        if (ShouldIncludeStrongOnlyDates)
        {
            foreach (var s in _allStrongStocks)
            {
                if (s.TryGetValue("date", out var d) && d != null)
                    dates.Add(d.ToString()!);
            }
        }
        AllDataDates = new ObservableCollection<string>(dates.OrderBy(d => d));
    }

    /// <summary>
    /// Rebuild the month data groups (buildings) from raw data
    /// </summary>
    private void RebuildMonths()
    {
        Months.Clear();
        for (var m = 1; m <= 12; m++)
        {
            var key = $"{CurrentYear}-{m:D2}";

            // If a specific month is selected, skip others
            if (!string.IsNullOrEmpty(SelectedMonthKey) && SelectedMonthKey != key)
                continue;

            var (trades, strongStocks) = GetMonthData(CurrentYear, m);

            // Skip months with no data
            if (trades.Count == 0 && strongStocks.Count == 0)
                continue;

            // Build day groups
            var dayMap = new Dictionary<string, DayGroupData>();
            var tradeDates = new HashSet<string>(trades.Select(t => t.TradeDate).Where(d => !string.IsNullOrEmpty(d)));

            foreach (var t in trades)
            {
                if (!dayMap.TryGetValue(t.TradeDate, out var dg))
                {
                    var dt = DateTime.TryParse(t.TradeDate, out var parsed) ? parsed : DateTime.Now;
                    dg = new DayGroupData
                    {
                        Date = t.TradeDate,
                        Day = dt.Day,
                        Month = dt.Month.ToString(),
                        WeekDay = GetChineseWeekDay(dt)
                    };
                    dayMap[t.TradeDate] = dg;
                }
                t.IsStrongToday = strongStocks.Any(s => s.Date == t.TradeDate && s.StockCode == t.StockCode);
                dg.Trades.Add(t);
            }

            // Add strong stocks to day groups
            foreach (var s in strongStocks)
            {
                if (!dayMap.TryGetValue(s.Date, out var dg))
                {
                    if (!ShouldIncludeStrongOnlyDates) continue;
                    var dt = DateTime.TryParse(s.Date, out var parsed) ? parsed : DateTime.Now;
                    dg = new DayGroupData
                    {
                        Date = s.Date,
                        Day = dt.Day,
                        Month = dt.Month.ToString(),
                        WeekDay = GetChineseWeekDay(dt)
                    };
                    dayMap[s.Date] = dg;
                }
                dg.StrongStocks.Add(s);
            }

            // Sort day groups ascending (old dates first)
            var dayGroups = dayMap.Values.OrderBy(d => d.Date).ToList();

            // Calculate stats
            var total = trades.Count;
            var wins = trades.Count(t => (t.TotalReturn ?? 0) > 0);
            var avgReturn = total > 0
                ? (trades.Sum(t => t.TotalReturn ?? 0) / total).ToString("F2")
                : "0.00";

            var monthData = new MonthDataGroup
            {
                Key = key,
                Year = CurrentYear,
                MonthNum = m,
                Month = m.ToString(),
                DisplayName = $"{CurrentYear}年{m}月",
                Trades = new ObservableCollection<TradeRecord>(trades),
                StrongStocks = new ObservableCollection<StrongStockItem>(strongStocks),
                DayGroups = new ObservableCollection<DayGroupData>(dayGroups),
                Stats = new MonthStats
                {
                    Total = total,
                    WinRate = total > 0 ? ((wins * 100.0 / total)).ToString("F1") : "0.0",
                    AvgReturn = avgReturn
                }
            };

            Months.Add(monthData);
        }

        // 阶段②：卡片已显示，截图后台逐张补显（不阻塞 UI）
        FillScreenshotsInBackground();
    }

    /// <summary>
    /// 后台补显截图：快照当前月份组里的记录，磁盘读取在后台线程，
    /// 读完经 Dispatcher 回 UI 线程触发 INPC（DisplayScreenshot）。
    /// RebuildMonths 被再次触发时旧快照的更新无害（对象已不再被引用）。
    /// </summary>
    private void FillScreenshotsInBackground()
    {
        var trades = Months
            .SelectMany(m => m.Trades)
            .Where(t => !string.IsNullOrEmpty(t.Screenshot) && string.IsNullOrEmpty(t.DisplayScreenshot))
            .Select(t => (t.Screenshot, (object)t))
            .ToList();
        var stocks = Months
            .SelectMany(m => m.StrongStocks)
            .Where(s => !string.IsNullOrEmpty(s.Screenshot) && string.IsNullOrEmpty(s.DisplayScreenshot))
            .Select(s => (s.Screenshot, (object)s))
            .ToList();
        var targets = trades.Concat(stocks).ToList();
        if (targets.Count == 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var (path, item) in targets)
                {
                    var data = LoadScreenshot(path);
                    if (string.IsNullOrEmpty(data)) continue;
                    if (item is TradeRecord tr)
                        await App.Current.Dispatcher.InvokeAsync(() => tr.DisplayScreenshot = data);
                    else if (item is StrongStockItem ss)
                        await App.Current.Dispatcher.InvokeAsync(() => ss.DisplayScreenshot = data);
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[复盘] 批量加载截图失败");
            }
        });
    }

    /// <summary>
    /// Get trades and strong stocks for a specific month
    /// </summary>
    private (List<TradeRecord> trades, List<StrongStockItem> strongStocks) GetMonthData(int year, int month)
    {
        var ym = $"{year}-{month:D2}";

        var trades = _allTrades
            .Where(t => t.TryGetValue("tradeDate", out var d) && d?.ToString()?.StartsWith(ym) == true)
            .Select(MapTradeRecord)
            .ToList();

        var tradeDates = new HashSet<string>(trades.Select(t => t.TradeDate).Where(d => !string.IsNullOrEmpty(d)));

        var strongStocks = ShowStrongStocks
            ? _allStrongStocks
                .Where(s =>
                {
                    if (s.TryGetValue("date", out var d) && d?.ToString()?.StartsWith(ym) == true)
                        return ShouldIncludeStrongOnlyDates || tradeDates.Contains(d.ToString()!);
                    return false;
                })
                .Select(MapStrongStock)
                .ToList()
            : new List<StrongStockItem>();

        return (trades, strongStocks);
    }

    /// <summary>
    /// Map a dictionary row to a TradeRecord
    /// </summary>
    private TradeRecord MapTradeRecord(Dictionary<string, object?> row)
    {
        return new TradeRecord
        {
            Id = GetInt(row, "id"),
            TradeDate = GetStr(row, "tradeDate"),
            StockCode = GetStr(row, "stockCode"),
            StockName = GetStr(row, "stockName"),
            EntryType = GetStr(row, "entryType"),
            ParentEntryType = GetStr(row, "parentEntryType"),
            PositionStatus = GetStr(row, "positionStatus"),
            CaseType = GetStr(row, "caseType"),
            FirstDate = GetStr(row, "firstDate"),
            ClosePrice = GetDouble(row, "closePrice"),
            PrevClose = GetDouble(row, "prevClose"),
            HighPrice = GetDouble(row, "highPrice"),
            ChangePct = GetDouble(row, "changePct"),
            MaxChangePct = GetDouble(row, "maxChangePct"),
            TodayPerformance = GetStr(row, "todayPerformance"),
            MeetExpectation = GetStr(row, "meetExpectation"),
            ExitPrice = GetDouble(row, "exitPrice"),
            ExitDate = GetStr(row, "exitDate"),
            TotalReturn = GetDouble(row, "totalReturn"),
            Remark = GetStr(row, "remark"),
            ProblemTags = string.Join(",", Services.ArrayFieldUtil.ToStringList(row.GetValueOrDefault("problemTags"))),
            FollowUp = GetStr(row, "followUp"),
            FollowUpDate = GetStr(row, "followUpDate"),
            SellCalibrationHigh = GetDouble(row, "sellCalibrationHigh"),
            SellCalibrationMaxChange = GetDouble(row, "sellCalibrationMaxChange"),
            Reflection = GetStr(row, "reflection"),
            Screenshot = GetStr(row, "screenshot"),
            // 截图不在此处读盘（两阶段加载）：先显卡片，后台读完经 INPC 补显
            CreatedAt = GetStr(row, "createdAt"),
            UpdatedAt = GetStr(row, "updatedAt"),
            EntryTagType = GetTagType(GetStr(row, "entryType")) is { Length: > 0 } t ? t : "primary"
        };
    }

    /// <summary>
    /// Map a dictionary row to a StrongStockItem
    /// </summary>
    private StrongStockItem MapStrongStock(Dictionary<string, object?> row)
    {
        return new StrongStockItem
        {
            Id = GetInt(row, "id"),
            Date = GetStr(row, "date"),
            StockCode = GetStr(row, "stockCode"),
            StockName = GetStr(row, "stockName"),
            HighPrice = GetDouble(row, "highPrice"),
            MaxChangePct = GetDouble(row, "maxChangePct"),
            ChangePct = GetDouble(row, "changePct"),
            ClosePrice = GetDouble(row, "closePrice"),
            Screenshot = GetStr(row, "screenshot"),
            StrongType = GetStr(row, "strongType"),
            RelatedTradeIds = GetStr(row, "relatedTradeIds"),
            CreatedAt = GetStr(row, "createdAt"),
            UpdatedAt = GetStr(row, "updatedAt")
        };
    }

    /// <summary>
    /// Get the entry type tag color for a given entry type
    /// </summary>
    public string GetTagType(string entryType)
    {
        var typeObj = _entryTypes.FirstOrDefault(t => GetStr(t, "typeName") == entryType);
        if (typeObj == null) return "info";
        var sortOrder = GetInt(typeObj, "sortOrder");
        var index = sortOrder % ColorPool.Length;
        return ColorPool[index] ?? "info";
    }

    // ============ Commands ============

    [RelayCommand]
    private async Task ChangeYear(string delta)
    {
        if (!int.TryParse(delta, out var d)) return;
        CurrentYear += d;
        var currentMonth = SelectedMonthKey.Length >= 7 ? SelectedMonthKey.Substring(5) : DateTime.Now.Month.ToString("D2");
        SelectedMonthKey = $"{CurrentYear}-{currentMonth}";
        // 切换年份后保持当前月份高亮与日期条同步（对应原版 MonthNavigation 年份切换联动）
        if (int.TryParse(currentMonth, out var m)) ActiveMonth = m;
        UpdateMonthButtonStates();
        UpdateDaysInMonth();
        await LoadDataAsync();
    }

    [RelayCommand]
    private void SelectMonth(string month)
    {
        if (!int.TryParse(month, out var m)) return;
        ActiveMonth = m;
        var targetKey = $"{CurrentYear}-{m:D2}";
        SelectedMonthKey = targetKey;
        SelectedDate = "";
        UpdateDaysInMonth();
        RebuildMonths();
        UpdateMonthButtonStates();
    }

    private void UpdateMonthButtonStates()
    {
        foreach (var btn in MonthButtons)
            btn.IsActive = btn.Month == ActiveMonth;
    }

    [RelayCommand]
    private void SelectDate(string date)
    {
        SelectedDate = date;
        // 仅把日期条对齐到该日期所在月份，不改变"月份筛选"（避免折叠为单月）
        if (date.Length >= 7 && int.TryParse(date.Substring(5, 2), out var m))
        {
            ActiveMonth = m;
            UpdateDaysInMonth();
        }
    }

    [RelayCommand]
    private void SelectAllMonths()
    {
        SelectedMonthKey = "";
        SelectedDate = "";
        RebuildMonths();
    }

    [RelayCommand]
    private void SetDisplayMode(string mode)
    {
        DisplayMode = mode;
        ShowStrongStocks = mode != "hidden";
        UpdateAllDataDates();
        RebuildMonths();

        // Save to app config
        _ = Task.Run(() =>
        {
            try
            {
                SaveConfig("tradeDisplayMode", mode);
                SaveConfig("showStrongStocks", (mode != "hidden").ToString());
            }
            catch { }
        });
    }

    [RelayCommand]
    private void AddTrade(string? date)
    {
        // 打开录入表单（仅在点击新增/当日+号时出现）
        IsEditMode = false;
        EditingTradeId = 0;
        FormTradeDate = date ?? DateTime.Now.ToString("yyyy-MM-dd");
        FormStockCode = "";
        FormStockName = "";
        FormClosePrice = "";
        FormPrevClose = "";
        FormHighPrice = "";
        FormChangePct = "";
        FormMaxChangePct = "";
        FormEntryType = "";
        FormPositionStatus = "已清仓";
        // 首次日期默认上一交易日、清仓日期默认当日（用户指定），减少手动填写
        var marketTime = new StockReview.Core.Services.MarketTimeService();
        var baseDate = DateTime.TryParse(FormTradeDate, out var d) ? d : DateTime.Now;
        FormFirstDate = marketTime.FormatDate(marketTime.GetPreviousTradingDay(baseDate));
        FormTodayPerformance = "";
        FormMeetExpectation = "";
        FormExitDate = baseDate.ToString("yyyy-MM-dd");
        FormTotalReturn = "";
        FormRemark = "";
        FormReflection = "";
        FormProblemTags = "";
        FormFollowUp = "";
        FormScreenshot = "";
        SelectedDate = FormTradeDate;
        ShowForm = true;
    }

    [RelayCommand]
    private void EditTrade(TradeRecord trade)
    {
        // 打开录入表单并回填（仅在双击编辑时出现）
        IsEditMode = true;
        EditingTradeId = trade.Id;
        FormTradeDate = trade.TradeDate;
        FormStockCode = trade.StockCode;
        FormStockName = trade.StockName;
        FormClosePrice = (trade.ClosePrice ?? 0).ToString("F2").TrimEnd('0').TrimEnd('.');
        FormPrevClose = (trade.PrevClose ?? 0).ToString("F2").TrimEnd('0').TrimEnd('.');
        FormHighPrice = (trade.HighPrice ?? 0).ToString("F2").TrimEnd('0').TrimEnd('.');
        FormChangePct = (trade.ChangePct ?? 0).ToString("F2").TrimEnd('0').TrimEnd('.');
        FormMaxChangePct = (trade.MaxChangePct ?? 0).ToString("F2").TrimEnd('0').TrimEnd('.');
        FormEntryType = trade.EntryType;
        FormPositionStatus = trade.PositionStatus;
        FormFirstDate = trade.FirstDate;
        FormTodayPerformance = trade.TodayPerformance;
        FormMeetExpectation = trade.MeetExpectation;
        FormExitDate = trade.ExitDate;
        FormTotalReturn = (trade.TotalReturn ?? 0).ToString("F2").TrimEnd('0').TrimEnd('.');
        FormRemark = trade.Remark;
        FormReflection = trade.Reflection;
        FormProblemTags = trade.ProblemTags;
        FormFollowUp = trade.FollowUp;
        FormScreenshot = trade.Screenshot;
        SelectedDate = trade.TradeDate;
        ShowForm = true;
    }

    [RelayCommand]
    private void CloseForm()
    {
        ShowForm = false;
    }

    [RelayCommand]
    private async Task SaveTrade()
    {
        if (string.IsNullOrWhiteSpace(FormStockCode) && string.IsNullOrWhiteSpace(FormStockName))
        {
            _dialogs.Warn("请填写股票代码或名称");
            return;
        }
        if (string.IsNullOrWhiteSpace(FormTradeDate))
        {
            _dialogs.Warn("请选择日期");
            return;
        }

        try
        {
            // 查找父级进场类型
            string parentEntryType = "";
            var selType = EntryTypeItems.FirstOrDefault(t => t.Name == FormEntryType);
            if (selType?.ParentId != null)
            {
                var parent = EntryTypeItems.FirstOrDefault(t => t.Id == selType.ParentId);
                if (parent != null) parentEntryType = parent.Name;
            }

            // 自动归类案例（对齐 autoClassifyCase：清仓按阈值，未清仓/中间态均记「未归类」）
            string caseType;
            if (FormPositionStatus == "已清仓" && double.TryParse(FormTotalReturn, out var tr))
            {
                caseType = tr >= 5 ? "成功案例" : (tr <= -3 ? "失败案例" : "未归类");
            }
            else
            {
                caseType = "未归类";
            }

            var data = new Dictionary<string, object?>
            {
                ["tradeDate"] = FormTradeDate,
                ["stockCode"] = FormStockCode,
                ["stockName"] = string.IsNullOrWhiteSpace(FormStockName) ? FormStockCode : FormStockName,
                ["closePrice"] = ToNullableDouble(FormClosePrice),
                ["prevClose"] = ToNullableDouble(FormPrevClose),
                ["highPrice"] = ToNullableDouble(FormHighPrice),
                ["changePct"] = ToNullableDouble(FormChangePct),
                ["maxChangePct"] = ToNullableDouble(FormMaxChangePct),
                ["entryType"] = FormEntryType,
                ["parentEntryType"] = parentEntryType,
                ["positionStatus"] = FormPositionStatus,
                ["firstDate"] = FormFirstDate,
                ["todayPerformance"] = FormTodayPerformance,
                ["meetExpectation"] = FormMeetExpectation,
                ["exitDate"] = FormExitDate,
                ["exitPrice"] = FormExitDate != null ? ToNullableDouble(FormClosePrice) : null,
                ["totalReturn"] = ToNullableDouble(FormTotalReturn),
                ["remark"] = FormRemark,
                ["reflection"] = FormReflection,
                // problemTags 列为 JSON 数组（对齐 旧版存储格式），读取侧由 ArrayFieldUtil 还原
                ["problemTags"] = System.Text.Json.JsonSerializer.Serialize(
                    FormProblemTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()),
                ["followUp"] = FormFollowUp,
                ["screenshot"] = FormScreenshot,
                ["caseType"] = caseType,
                ["updatedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            // 最大涨幅 >5% 时自动写入强股表并关联本交易（对齐 TradeForm.vue）
            var maxChange = ToNullableDouble(FormMaxChangePct) ?? 0;
            var linkStrong = maxChange > 5 && !string.IsNullOrWhiteSpace(FormStockCode);
            var strongDate = FormTradeDate;
            var strongCode = FormStockCode;

            // trades 写入与强股关联必须同事务原子完成（2026-09-04 P1）：
            // 中途失败回滚，避免"交易已保存但强股关联丢失"的半完成状态。
            object? savedTradeId = await Task.Run(() => _db.RunInTransaction<object?>(() =>
            {
                object? tradeId;
                if (IsEditMode)
                {
                    _db.Update("trades", EditingTradeId, data);
                    tradeId = EditingTradeId;
                }
                else
                {
                    data["createdAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    tradeId = _db.Add("trades", data);
                }

                if (linkStrong)
                {
                    var existing = _db.WhereCompoundFirst("strongStocks",
                        new Dictionary<string, object> { ["date"] = strongDate, ["stockCode"] = strongCode });
                    var relatedIds = new List<object?>();
                    if (existing?.TryGetValue("relatedTradeIds", out var rt) == true && rt != null)
                    {
                        try
                        {
                            var arr = System.Text.Json.JsonSerializer.Deserialize<List<object?>>(rt.ToString()!, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (arr != null) relatedIds.AddRange(arr);
                        }
                        catch { /* 忽略损坏的关联 JSON */ }
                    }
                    if (tradeId != null && !relatedIds.Contains(tradeId))
                        relatedIds.Add(tradeId);

                    var strongData = new Dictionary<string, object?>
                    {
                        ["stockName"] = string.IsNullOrWhiteSpace(FormStockName) ? strongCode : FormStockName,
                        ["closePrice"] = ToNullableDouble(FormClosePrice),
                        ["changePct"] = ToNullableDouble(FormChangePct),
                        ["highPrice"] = ToNullableDouble(FormHighPrice),
                        ["maxChangePct"] = ToNullableDouble(FormMaxChangePct),
                        ["strongType"] = FormEntryType ?? "",
                        ["screenshot"] = FormScreenshot ?? "",
                        ["relatedTradeIds"] = System.Text.Json.JsonSerializer.Serialize(relatedIds)
                    };
                    if (existing != null && existing.TryGetValue("id", out var eid) && eid != null)
                    {
                        _db.Update("strongStocks", eid, strongData);
                    }
                    else
                    {
                        strongData["date"] = strongDate;
                        strongData["stockCode"] = strongCode;
                        _db.Add("strongStocks", strongData);
                    }
                }
                return tradeId;
            }));

            ShowForm = false;
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            _dialogs.Error($"保存失败: {ex.Message}");
        }
    }

    private static double? ToNullableDouble(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return double.TryParse(s, out var v) ? v : null;
    }

    [RelayCommand]
    private async Task DeleteTrade(int id)
    {
        if (!_dialogs.Confirm("确定要删除这条记录吗？", "确认删除")) return;

        try
        {
            await Task.Run(() => _db.Delete("trades", id));
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            _dialogs.Error($"删除失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ViewStrongStock(StrongStockItem stock)
    {
        SelectedStrongStock = stock;
        // 加载当日所有强股（对话框内"查看每日强股"）
        DayStrongStocks = new ObservableCollection<StrongStockItem>(
            _allStrongStocks
                .Where(s => GetStr(s, "date") == stock.Date)
                .Select(MapStrongStock)
                .ToList());
        ShowStrongDialog = true;
    }

    [RelayCommand]
    private void OpenDiaryDialog()
    {
        // 草稿优先恢复（含重启后）；无草稿且无本次会话残留时才用默认值，弹窗不自动清空
        if (DiaryDraftStore.TryLoad(_db, out var type, out var date, out var title, out var content))
        {
            DiaryType = type;
            DiaryDate = date;
            DiaryTitle = title;
            DiaryContent = content;
        }
        else if (string.IsNullOrEmpty(DiaryTitle) && string.IsNullOrEmpty(DiaryContent))
        {
            DiaryType = "daily";
            DiaryDate = DateTime.Now.ToString("yyyy-MM-dd");
        }
        ShowDiaryDialog = true;
    }

    /// <summary>按 id 将已有日记预填到写日记弹窗（供「编辑」入口调用）。</summary>
    public void LoadDiaryForEdit(int id)
    {
        var row = _db.GetById("dailySummaries", id);
        if (row == null) return;
        DiaryType = row.TryGetValue("summaryType", out var st) && st?.ToString() is { Length: > 0 } t ? t : "daily";
        DiaryDate = row.TryGetValue("recordDate", out var rd) ? rd?.ToString() ?? "" : "";
        DiaryTitle = row.TryGetValue("title", out var tl) ? tl?.ToString() ?? "" : "";
        // 兼容两种存储：早期日记富文本存在 content 字段，后期存在 summary 字段
        var contentVal = row.TryGetValue("content", out var cv) ? cv?.ToString() ?? "" : "";
        var summaryVal = row.TryGetValue("summary", out var sm) ? sm?.ToString() ?? "" : "";
        DiaryContent = !string.IsNullOrEmpty(contentVal) ? contentVal : summaryVal;
        // 有更新草稿（同类型同日期）优先：上次编辑未保存的内容不丢
        if (DiaryDraftStore.TryLoad(_db, out var dType, out var dDate, out var dTitle, out var dContent)
            && dType == DiaryType && dDate == DiaryDate)
        {
            DiaryTitle = dTitle;
            DiaryContent = dContent;
        }
        ShowDiaryDialog = true;
    }

    /// <summary>日记编辑停顿防抖：任意字段变更后重启 2 秒计时，到点自动落草稿。</summary>
    partial void OnDiaryTypeChanged(string value) => RestartDiaryDraftTimer();
    partial void OnDiaryDateChanged(string value) => RestartDiaryDraftTimer();
    partial void OnDiaryTitleChanged(string value) => RestartDiaryDraftTimer();
    partial void OnDiaryContentChanged(string value) => RestartDiaryDraftTimer();

    /// <summary>弹窗关闭（取消/Esc/点遮罩/保存后）：立即落草稿（空内容则清除），字段保留不清空。</summary>
    partial void OnShowDiaryDialogChanged(bool value)
    {
        if (value) return;
        _diaryDraftTimer.Stop();
        _ = FlushDiaryDraftAsync();
    }

    private void RestartDiaryDraftTimer()
    {
        if (!ShowDiaryDialog) return;
        _diaryDraftTimer.Stop();
        _diaryDraftTimer.Start();
    }

    private async Task FlushDiaryDraftAsync()
    {
        var type = DiaryType;
        var date = DiaryDate;
        var title = DiaryTitle;
        var content = DiaryContent;
        try
        {
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(content))
            {
                await Task.Run(() => DiaryDraftStore.Clear(_db));
                DiaryDraftHint = "";
            }
            else
            {
                await Task.Run(() => DiaryDraftStore.Save(_db, type, date, title, content));
                DiaryDraftHint = $"已自动保存 {DateTime.Now:HH:mm}";
            }
        }
        catch
        {
            // 草稿保存失败不阻断编辑
        }
    }

    [RelayCommand]
    private async Task SaveDiary()
    {
        if (string.IsNullOrEmpty(DiaryDate) || string.IsNullOrEmpty(DiaryContent))
        {
            _dialogs.Warn("请填写日期和内容");
            return;
        }

        IsSavingDiary = true;
        try
        {
            var (startDate, endDate) = GetDateRangeByType(DiaryDate, DiaryType);

            // Check if exists（P5：SQL 已下沉 Core）
            List<Dictionary<string, object?>> existing = new();
            await Task.Run(() =>
            {
                existing = _db.GetDailySummariesInRange(startDate, endDate, DiaryType);
            });

            var data = new Dictionary<string, object?>
            {
                ["summaryType"] = DiaryType,
                ["title"] = DiaryTitle,
                ["summary"] = DiaryContent,
                ["content"] = DiaryContent,
                ["recordDate"] = DiaryDate,
                ["startDate"] = startDate,
                ["endDate"] = endDate
            };

            if (existing != null && existing.Count > 0)
            {
                var existingId = GetInt(existing[0], "id");
                await Task.Run(() => _db.Update("dailySummaries", existingId, data));
                _dialogs.Info("日记更新成功");
            }
            else
            {
                await Task.Run(() => _db.Add("dailySummaries", data));
                _dialogs.Info("日记保存成功");
            }

            // 已入库：清除草稿与表单残留（下次打开为全新日记）
            DiaryDraftHint = "";
            DiaryTitle = "";
            DiaryContent = "";
            await Task.Run(() => DiaryDraftStore.Clear(_db));
            ShowDiaryDialog = false;
        }
        catch (Exception ex)
        {
            _dialogs.Error($"保存失败: {ex.Message}");
        }
        finally
        {
            IsSavingDiary = false;
        }
    }

    [RelayCommand]
    private void CloseStrongDialog()
    {
        ShowStrongDialog = false;
        SelectedStrongStock = null;
    }

    [RelayCommand]
    private void PreviewScreenshot(TradeRecord trade)
    {
        if (trade == null) return;
        PreviewScreenshotPath = trade.DisplayScreenshot;
        ShowScreenshotPreview = !string.IsNullOrEmpty(PreviewScreenshotPath);
    }

    [RelayCommand]
    private void CloseScreenshotPreview()
    {
        ShowScreenshotPreview = false;
        PreviewScreenshotPath = "";
    }

    /// <summary>从剪贴板粘贴截图到交易录入表单（对齐原版表单截图能力）</summary>
    [RelayCommand]
    private void AttachScreenshotFromClipboard()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsImage())
            {
                ScreenshotFeedback = "剪贴板中没有图片";
                return;
            }
            var bmp = System.Windows.Clipboard.GetImage();
            if (bmp == null)
            {
                ScreenshotFeedback = "读取剪贴板图片失败";
                return;
            }
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var ms = new System.IO.MemoryStream();
            encoder.Save(ms);
            var b64 = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            var (ok, filePath, _) = _img.SaveImage(b64, "screenshot");
            FormScreenshot = ok && filePath != null ? filePath : FormScreenshot;
            ScreenshotFeedback = ok && filePath != null ? "截图已附加" : "截图保存失败";
            _ = RecognizeAndFill(b64);
        }
        catch
        {
            ScreenshotFeedback = "粘贴截图失败";
        }
    }

    // ============ 交易表单 OCR / 行情自动回填（对齐原版 TradeForm） ============

    [ObservableProperty] private bool _formOcrLoading;

    /// <summary>最近一次 OCR 识别使用的通道（baidu[...]=百度云端，其余=本地 Tesseract）。
    /// 行情回填会覆盖 ScreenshotFeedback，保留该字段以便在最终提示中暴露识别通道，
    /// 让用户能确认云端/本地通道是否按配置生效。</summary>
    private string _lastOcrSource = "";

    /// <summary>OCR 识别交易表单截图中的股票代码并回填行情</summary>
    public async Task RecognizeAndFill(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return;
        FormOcrLoading = true;
        try
        {
            var result = await _ocr.RecognizeStockCodeAsync(base64);
            if (!result.Success)
            {
                // 修复：原实现失败直接 return，ScreenshotFeedback 仍是「截图已附加」，
                // 用户无从知晓 OCR 为何没回填。现给出明确原因，并记录日志便于诊断。
                ScreenshotFeedback = "未识别到股票代码：" + result.Error;
                Serilog.Log.Information("[OCR] 识别失败：{Error}", result.Error);
                return;
            }
            FormStockCode = result.Code;
            if (!string.IsNullOrEmpty(result.Name)) FormStockName = result.Name;
            // 名称未匹配时显式提示人工确认（对齐 InsightsView；OCR 代码未经本地股票表佐证）
            ScreenshotFeedback = string.IsNullOrEmpty(result.Name)
                ? $"已识别 {result.Code}（{result.Source}，未匹配到名称，请人工确认）"
                : $"已识别 {result.Code} {result.Name}（{result.Source}），正在获取行情…";
            Serilog.Log.Information("[OCR] 识别成功 code={Code} name={Name} source={Source}",
                result.Code, result.Name, result.Source);
            _lastOcrSource = result.Source;
            await AutoFetchStockData();
        }
        catch (Exception ex)
        {
            ScreenshotFeedback = "识别异常：" + ex.Message;
            Serilog.Log.Warning(ex, "[OCR] 识别异常");
            // 失败不阻断录入，静默降级
        }
        finally
        {
            FormOcrLoading = false;
        }
    }

    /// <summary>代码（或名称）回车：自动匹配并回填行情</summary>
    public async Task OnFormEnter()
    {
        var code = FormStockCode?.Trim();
        if (code is not { Length: 6 })
        {
            var name = FormStockName?.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                var hits = await Task.Run(() => _ocr.SearchStocks(name));
                if (hits.Count > 0)
                {
                    FormStockCode = hits[0].Code;
                    FormStockName = hits[0].Name;
                }
            }
        }
        await AutoFetchStockData();
    }

    /// <summary>自动获取行情并回填股票名称与价格字段（对应原版 autoFetchStockData）</summary>
    public async Task AutoFetchStockData()
    {
        var code = FormStockCode?.Trim();
        if (code is not { Length: 6 }) return;
        try
        {
            ScreenshotFeedback = "获取行情...";
            var data = await StockMarketService.Fetch(_ocr, _market, code, FormTradeDate);
            if (data != null)
            {
                if (!string.IsNullOrEmpty(data.Name)) FormStockName = data.Name;
                if (!string.IsNullOrEmpty(data.Close)) FormClosePrice = data.Close;
                if (!string.IsNullOrEmpty(data.PrevClose)) FormPrevClose = data.PrevClose;
                if (!string.IsNullOrEmpty(data.High)) FormHighPrice = data.High;
                // 修复：行情回填漏写涨跌幅，导致「涨跌幅」框空白。data.ChangePct 由
                // StockMarketService 已按 (close-prev)/prev*100 计算好，直接回填即可。
                if (!string.IsNullOrEmpty(data.ChangePct)) FormChangePct = data.ChangePct;
                if (!string.IsNullOrEmpty(data.MaxChangePct)) FormMaxChangePct = data.MaxChangePct;
                // 拼接 OCR 通道 + 行情来源：行情覆盖提示后用户仍能确认识别走的是百度还是本地
                ScreenshotFeedback = string.IsNullOrEmpty(_lastOcrSource)
                    ? data.Source
                    : $"OCR {_lastOcrSource} · {data.Source}";
            }
            else
            {
                ScreenshotFeedback = "未能获取行情，请手动填写";
            }
        }
        catch (Exception ex)
        {
            ScreenshotFeedback = "获取行情失败: " + ex.Message;
        }
    }

    [ObservableProperty] private string _screenshotFeedback = "";

    [ObservableProperty] private string _formScreenshotPreview = "";

    partial void OnFormScreenshotChanged(string value)
    {
        // 表单截图持相对路径，预览用 base64 data URL（对齐卡片展示）
        FormScreenshotPreview = string.IsNullOrEmpty(value) ? "" : LoadScreenshot(value);
    }

    [RelayCommand]
    private void ClearFormScreenshot()
    {
        FormScreenshot = "";
        ScreenshotFeedback = "";
    }

    [RelayCommand]
    private void CloseDiaryDialog()
    {
        ShowDiaryDialog = false;
    }

    [RelayCommand]
    private void SetDiaryType(string type)
    {
        DiaryType = type;
    }

    // ============ Helper Methods ============

    private void UpdateDaysInMonth()
    {
        var days = DateTime.DaysInMonth(CurrentYear, ActiveMonth);
        var list = new List<DateCell>();
        for (var d = 1; d <= days; d++)
        {
            var dateStr = $"{CurrentYear}-{ActiveMonth:D2}-{d:D2}";
            list.Add(new DateCell { Day = d, DateStr = dateStr, HasData = HasDataOnDate(dateStr) });
        }
        DaysInMonth = new ObservableCollection<DateCell>(list);
    }

    public bool HasDataOnDate(string date)
    {
        return AllDataDates.Contains(date);
    }

    public string GetDateStr(int day)
    {
        return $"{CurrentYear}-{ActiveMonth:D2}-{day:D2}";
    }

    private static string GetChineseWeekDay(DateTime date)
    {
        var days = new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
        return days[(int)date.DayOfWeek];
    }

    private static (string start, string end) GetDateRangeByType(string dateStr, string type)
    {
        if (!DateTime.TryParse(dateStr, out var date))
            return (dateStr, dateStr);

        return type switch
        {
            "weekly" => (date.AddDays(-(int)date.DayOfWeek).ToString("yyyy-MM-dd"),
                         date.AddDays(6 - (int)date.DayOfWeek).ToString("yyyy-MM-dd")),
            "monthly" => (new DateTime(date.Year, date.Month, 1).ToString("yyyy-MM-dd"),
                          new DateTime(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month)).ToString("yyyy-MM-dd")),
            _ => (dateStr, dateStr)
        };
    }

    private static int GetInt(Dictionary<string, object?> dict, string key)
    {
        if (dict.TryGetValue(key, out var v) && v != null)
        {
            if (v is int i) return i;
            if (v is long l) return (int)l;
            if (int.TryParse(v.ToString(), out var parsed)) return parsed;
        }
        return 0;
    }

    private static string GetStr(Dictionary<string, object?> dict, string key)
    {
        return dict.TryGetValue(key, out var v) && v != null ? v.ToString()! : "";
    }

    private static double? GetDouble(Dictionary<string, object?> dict, string key)
    {
        if (dict.TryGetValue(key, out var v) && v != null)
        {
            if (v is double d) return d;
            if (v is float f) return f;
            if (v is decimal dec) return (double)dec;
            if (v is int i) return i;
            if (v is long l) return l;
            if (double.TryParse(v.ToString(), out var parsed)) return parsed;
        }
        return null;
    }

    /// <summary>
    /// 通过 ImageService 从磁盘读取截图并返回 base64 data URL（供 Image.Source 绑定）。
    /// 路径级缓存：月份切换重建卡片时避免重复磁盘 IO + base64 编码（卡顿主因）。
    /// </summary>
    private readonly Dictionary<string, string> _screenshotCache = new();
    private readonly object _screenshotCacheLock = new();

    private string LoadScreenshot(string relativePath)
    {
        if (_img == null || string.IsNullOrEmpty(relativePath)) return "";
        lock (_screenshotCacheLock)
        {
            if (_screenshotCache.TryGetValue(relativePath, out var hit)) return hit;
        }
        try
        {
            var (ok, data, _) = _img.ReadImage(relativePath);
            var result = ok ? data : "";
            lock (_screenshotCacheLock)
            {
                _screenshotCache[relativePath] = result;
            }
            return result;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Save a key-value config to appConfig table (upsert pattern)
    /// P5：改走 DatabaseService.Put（INSERT OR REPLACE 单语句 upsert，语义与旧手写 SQL 等价）
    /// </summary>
    private void SaveConfig(string key, string value)
    {
        _db.Put("appConfig", new Dictionary<string, object?>
        {
            ["key"] = key,
            ["value"] = value
        });
    }
}

/// <summary>
/// Month button item with active state
/// </summary>
public partial class MonthButtonItem : ObservableObject
{
    public int Month { get; set; }

    [ObservableProperty]
    private bool _isActive;
}
