using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using StockReview.Core.Data;
using StockReview.Core.MarketData;
using StockReviewWpf.Services;
using StockReviewWpf.ViewModels;

namespace StockReviewWpf.ViewModels.Main;

/// <summary>
/// 每日擒牛视图 ViewModel - 对应 DailyPickView.vue（完整 DB 接入版）
/// </summary>
public partial class DailyPickViewModel : ObservableObject
{
    private readonly IDatabaseService _db;
    private readonly ImageService _img;
    private readonly StockOcrService _ocr;
    private readonly MarketDataAggregator _market;

    private List<DailyPickRecord> _allPicks = new();

    // 截图懒加载：磁盘并发读上限（4 路并发既发挥 SSD 吞吐，又不至于争抢 IO）
    private readonly SemaphoreSlim _shotSemaphore = new(4);
    // 交易日历（内置节假日表，定位下一交易日时自动跳过周末/节假日）
    private static readonly StockReview.Core.Services.MarketTimeService _marketTime = new();
    // 刷新提示的自动清除令牌（按日期去重：重复点击刷新时旧提示立即作废）
    private readonly Dictionary<string, CancellationTokenSource> _refreshTipCts = new();

    // ============ 显示模式 ============
    [ObservableProperty] private string _activeTab = "daily";
    [ObservableProperty] private int _currentYear = DateTime.Now.Year;
    [ObservableProperty] private int _activeMonth = DateTime.Now.Month;
    [ObservableProperty] private bool _isMonthSelected;
    [ObservableProperty] private bool _isYearChanged;
    [ObservableProperty] private bool _showAllData;
    [ObservableProperty] private string _selectedDate = "";
    [ObservableProperty] private ObservableCollection<DateCell> _dayCells = new();
    [ObservableProperty] private ObservableCollection<DateGroup> _dateGroups = new();

    // ============ 对话框 ============
    // 注：旧 ScottPlot 汇总统计的属性/计算（CaptureRecords/TypeDistribution/MonthlyTrend 等）
    // 已随内嵌前端汇总页（WebChartView）整体下线——统计由网页侧经 DbHostObject 桥自行聚合。
    [ObservableProperty] private bool _isDialogVisible;
    [ObservableProperty] private DailyPickRecord _formPick = new();
    [ObservableProperty] private string _formScreenshotDisplay = "";
    [ObservableProperty] private ObservableCollection<EntryTypeItem> _entryTypes = new();
    [ObservableProperty] private ObservableCollection<string> _allEntryTypeNames = new();

    // 选中股评估项（仅选中后填写）
    [ObservableProperty] private string _evalTrend = "";
    [ObservableProperty] private string _evalCycle = "";
    [ObservableProperty] private string _evalSpace = "";

    // ============ 图片预览 ============
    [ObservableProperty] private bool _imagePreviewVisible;
    [ObservableProperty] private string _previewImageUrl = "";

    [ObservableProperty] private bool _ocrLoading;
    [ObservableProperty] private string? _statusText;

    public static readonly Dictionary<string, string> TypeColorMap = new()
    {
        { "双底", "#2f54eb" }, { "双底右侧突破", "#1890ff" }, { "突破", "#f5222d" },
        { "四连横盘突破", "#fa541c" }, { "平台突破", "#f59e0b" }, { "双底右侧反转", "#10b981" },
        { "双底右侧回踩", "#059669" }, { "回踩关键点", "#06b6d4" }, { "回踩前中枢底", "#8b5cf6" },
        { "其它", "#ec4899" }, { "上穿多均线", "#84cc16" }, { "突入前中枢内", "#3b82f6" },
        { "突破回踩", "#10b981" }, { "五日线回抽", "#dc2626" }
    };

    public DailyPickViewModel(IDatabaseService db, ImageService img, StockOcrService ocr, MarketDataAggregator market)
    {
        _db = db;
        _img = img;
        _ocr = ocr;
        _market = market;
        // fire-and-forget 但观察异常，避免初始化失败静默白屏
        _ = LoadAsync().ContinueWith(
            t => Log.Error(t.Exception, "[每日擒牛] 初始化加载失败"),
            System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
    }

    // ============ 加载 ============
    partial void OnShowAllDataChanged(bool value) => RebuildAll();

    /// <summary>
    /// 重新加载全部数据（标题栏"刷新"按钮调用；视图有缓存，导航不会重新加载）
    /// </summary>
    [RelayCommand]
    public async Task Reload() => await LoadAsync();

    private async Task LoadAsync()
    {
        // 两阶段加载：① DB 查询（快）→ 立即 RebuildAll 渲染卡片（截图位置空白）
        // ② 截图懒加载：卡片滚动进入可视区时才读盘（见 RequestScreenshot），
        //    只加载当前看得见的内容，替代原"打开页面后台全量预读几百张"的方案
        var picks = await Task.Run(() =>
            _db.Query<Dictionary<string, object?>>("SELECT * FROM dailyPicks").Select(Map).ToList());
        _allPicks = picks;
        RebuildAll();
        // 入口类型仅对话框使用，放到首屏渲染之后再加载
        await LoadEntryTypes();
    }

    /// <summary>
    /// 截图懒加载：卡片进入可视区（Image Loaded / DataContextChanged）时触发，
    /// 只读当前看得见的截图；4 路并发读盘，读完经 INPC 补显到卡片。
    /// </summary>
    public void RequestScreenshot(DailyPickRecord rec, bool openPreviewWhenDone = false)
    {
        if (!rec.HasScreenshot || rec.ScreenshotLoading || rec.DisplayScreenshot.Length > 0) return;
        rec.ScreenshotLoading = true;
        var path = rec.Screenshot;
        _ = Task.Run(async () =>
        {
            await _shotSemaphore.WaitAsync();
            try
            {
                var (ok, data, _) = _img.ReadImage(path);
                if (!ok) return;
                await App.Current.Dispatcher.InvokeAsync(() =>
                {
                    rec.DisplayScreenshot = data;
                    if (openPreviewWhenDone) OpenImagePreview(data);
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[每日擒牛] 截图加载失败 {Path}", path);
            }
            finally
            {
                _shotSemaphore.Release();
            }
        });
    }

    private async Task LoadEntryTypes()
    {
        var rows = await Task.Run(() => _db.Query<Dictionary<string, object?>>("SELECT * FROM entryTypes ORDER BY sortOrder"));
        var nodes = rows.Select(r => new EntryTypeItem
        {
            Id = GetInt(r, "id"),
            SortOrder = GetInt(r, "sortOrder"),
            Name = GetStr(r, "typeName"),
            Color = GetStr(r, "color"),
            IsStrongType = GetInt(r, "isStrongType") == 1,
            Description = GetStr(r, "description"),
            IsActive = GetInt(r, "isActive") != 0,
            ParentId = GetIntNullable(r, "parentId")
        }).ToList();

        // 父级索引：跳过 id<=0 或重复键，避免空 id 行引发 ToDictionary 重复键崩溃
        var byId = new Dictionary<int, EntryTypeItem>();
        foreach (var n in nodes)
        {
            if (n.Id > 0 && !byId.ContainsKey(n.Id)) byId[n.Id] = n;
        }
        var roots = new List<EntryTypeItem>();
        foreach (var n in nodes)
        {
            if (n.ParentId.HasValue && byId.TryGetValue(n.ParentId.Value, out var parent))
            {
                parent.Children.Add(n);
            }
            else
            {
                roots.Add(n);
            }
        }
        EntryTypes = new ObservableCollection<EntryTypeItem>(roots);
        var flat = new List<string>();
        foreach (var r in roots)
        {
            flat.Add(r.Name);
            flat.AddRange(r.Children.Select(c => c.Name));
        }
        AllEntryTypeNames = new ObservableCollection<string>(flat);
    }

    private void RebuildAll()
    {
        BuildDayCells();
        BuildDateGroups();
        // 汇总统计 Tab 现为内嵌前端页面（WebChartView），统计由网页侧经 DbHostObject 桥
        // 自行聚合；旧 ScottPlot 统计重算（RebuildStats/BuildStatistics/BuildChartData）已移除，
        // 切月/切年/切 Tab 不再白跑全表 LINQ。
    }

    // ============ 日期筛选 ============
    private void BuildDayCells()
    {
        var days = DateTime.DaysInMonth(CurrentYear, ActiveMonth);
        // 有数据的日期先建 HashSet，避免每个日期全表扫描（O(31×N)→O(N)）
        var datesWithData = new HashSet<string>(_allPicks.Select(p => p.PickDate));
        var cells = new ObservableCollection<DateCell>();
        for (var d = 1; d <= days; d++)
        {
            var ds = $"{CurrentYear}-{ActiveMonth:00}-{d:00}";
            cells.Add(new DateCell
            {
                Day = d,
                DateStr = ds,
                HasData = datesWithData.Contains(ds)
            });
        }
        DayCells = cells;
    }

    private IEnumerable<DailyPickRecord> FilteredPicks()
    {
        if (ShowAllData) return _allPicks;
        if (IsMonthSelected)
        {
            var ym = $"{CurrentYear}-{ActiveMonth:00}";
            return _allPicks.Where(p => p.PickDate.StartsWith(ym));
        }
        if (IsYearChanged)
        {
            var y = CurrentYear.ToString();
            return _allPicks.Where(p => p.PickDate.StartsWith(y));
        }
        var yearAgo = DateTime.Now.AddMonths(-12).ToString("yyyy-MM-dd");
        return _allPicks.Where(p => string.Compare(p.PickDate, yearAgo, StringComparison.Ordinal) >= 0);
    }

    private void BuildDateGroups()
    {
        var groups = new Dictionary<string, List<DailyPickRecord>>();
        foreach (var p in FilteredPicks())
        {
            if (!groups.TryGetValue(p.PickDate, out var list))
            {
                list = new List<DailyPickRecord>();
                groups[p.PickDate] = list;
            }
            list.Add(p);
        }

        var result = new ObservableCollection<DateGroup>();
        foreach (var kv in groups.OrderByDescending(g => g.Key))
        {
            var picks = kv.Value;
            picks.Sort((a, b) =>
            {
                var av = a.NextDayMaxChange ?? double.MinValue;
                var bv = b.NextDayMaxChange ?? double.MinValue;
                return bv.CompareTo(av);
            });
            for (var i = 0; i < picks.Count; i++) picks[i].Rank = i + 1;
            result.Add(new DateGroup { Date = kv.Key, Picks = new ObservableCollection<DailyPickRecord>(picks) });
        }
        DateGroups = result;
    }

    // ============ 命令 ============
    [RelayCommand]
    private void SwitchTab(string tab) => ActiveTab = tab;

    [RelayCommand]
    private void ChangeYear(string delta)
    {
        if (!int.TryParse(delta, out var d)) return;
        CurrentYear += d;
        IsYearChanged = true;
        IsMonthSelected = false;
        SelectedDate = "";
        RebuildAll();
    }

    [RelayCommand]
    private void SelectMonth(string month)
    {
        if (!int.TryParse(month, out var m)) return;
        ActiveMonth = m;
        IsMonthSelected = true;
        IsYearChanged = false;
        SelectedDate = "";
        RebuildAll();
    }

    [RelayCommand]
    private void ShowAllMonths()
    {
        IsMonthSelected = false;
        IsYearChanged = false;
        SelectedDate = "";
        RebuildAll();
    }

    [RelayCommand]
    private void SelectDate(string date)
    {
        SelectedDate = date;
    }

    [RelayCommand]
    private void GoToDate(string date)
    {
        ActiveTab = "daily";
        SelectedDate = date;
        // 容错解析 yyyy-MM-dd（防越界/非数字抛异常）
        if (date.Length >= 7
            && int.TryParse(date.Substring(0, 4), out var y)
            && int.TryParse(date.Substring(5, 2), out var m)
            && m is >= 1 and <= 12)
        {
            CurrentYear = y;
            ActiveMonth = m;
            IsMonthSelected = true;
            IsYearChanged = false;
            SelectedDate = date;
            RebuildAll();
        }
    }

    [RelayCommand]
    private void AddPickRecord()
    {
        FormPick = new DailyPickRecord { PickDate = string.IsNullOrEmpty(SelectedDate) ? DateTime.Now.ToString("yyyy-MM-dd") : SelectedDate };
        EvalTrend = "";
        EvalCycle = "";
        EvalSpace = "";
        FormScreenshotDisplay = "";
        IsDialogVisible = true;
    }

    [RelayCommand]
    private void EditPickRecord(DailyPickRecord pick)
    {
        FormPick = new DailyPickRecord
        {
            Id = pick.Id,
            PickDate = pick.PickDate,
            StockCode = pick.StockCode,
            StockName = pick.StockName,
            Price = pick.Price,
            Change = pick.Change,
            PickType = pick.PickType,
            IsSelected = pick.IsSelected,
            Remark = pick.Remark,
            Screenshot = pick.Screenshot,
            NextDayHighPrice = pick.NextDayHighPrice,
            NextDayMaxChange = pick.NextDayMaxChange,
            Evaluation = pick.Evaluation
        };
        var ev = pick.Eval;
        EvalTrend = ev?.TrendStatus ?? "";
        EvalCycle = ev?.CyclePattern ?? "";
        EvalSpace = ev?.SpaceStatus ?? "";
        FormScreenshotDisplay = pick.DisplayScreenshot;
        IsDialogVisible = true;
    }

    [RelayCommand]
    private void SelectPickType(string typeName) => FormPick.PickType = typeName;

    [RelayCommand]
    private async Task SavePick()
    {
        if (string.IsNullOrEmpty(FormPick.StockName) || string.IsNullOrEmpty(FormPick.StockCode))
        {
            StatusText = "请填写股票名称和代码";
            return;
        }
        if (FormPick.IsSelected && (string.IsNullOrEmpty(EvalTrend) || string.IsNullOrEmpty(EvalCycle) || string.IsNullOrEmpty(EvalSpace)))
        {
            StatusText = "选中股必须完成所有评估项才能保存";
            return;
        }

        var eval = FormPick.IsSelected
            ? new DailyPickEvaluation { TrendStatus = EvalTrend, CyclePattern = EvalCycle, SpaceStatus = EvalSpace }.ToJson()
            : "";

        var dict = new Dictionary<string, object?>
        {
            ["pickDate"] = FormPick.PickDate,
            ["stockName"] = FormPick.StockName,
            ["stockCode"] = FormPick.StockCode,
            ["price"] = FormPick.Price,
            ["change"] = FormPick.Change,
            ["pickType"] = FormPick.PickType,
            ["isSelected"] = FormPick.IsSelected ? 1 : 0,
            ["remark"] = FormPick.Remark,
            ["screenshot"] = FormPick.Screenshot,
            ["nextDayHighPrice"] = FormPick.NextDayHighPrice,
            ["nextDayMaxChange"] = FormPick.NextDayMaxChange,
            ["evaluation"] = eval
        };

        if (FormPick.Id > 0)
            _db.Update("dailyPicks", FormPick.Id, dict);
        else
            _db.Add("dailyPicks", dict);

        IsDialogVisible = false;
        await LoadAsync();
        StatusText = "保存成功";
    }

    [RelayCommand]
    private async Task DeletePick(DailyPickRecord pick)
    {
        if (pick.Id > 0) _db.Delete("dailyPicks", pick.Id);
        await LoadAsync();
    }

    /// <summary>
    /// 刷新指定日期擒牛记录的次日数据（对应原版 refreshNextDayForDate）：
    /// 用日K线定位 pickDate 之后的第一根交易K线（天然跳过周末/节假日），
    /// 回填 nextDayHighPrice / nextDayMaxChange（最高价相对 pickDate 收盘的最大涨幅）
    /// </summary>
    /// <summary>在指定日期组的刷新按钮后显示绿色提示，10 秒后自动消失</summary>
    private void ShowRefreshTip(string date, string message)
    {
        if (_refreshTipCts.TryGetValue(date, out var old))
        {
            old.Cancel();
            _refreshTipCts.Remove(date);
        }
        var group = DateGroups?.FirstOrDefault(g => g.Date == date);
        if (group == null) return;
        group.RefreshTip = message;

        var cts = new CancellationTokenSource();
        _refreshTipCts[date] = cts;
        _ = Task.Delay(TimeSpan.FromSeconds(10), cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            App.Current.Dispatcher.Invoke(() =>
            {
                // 仅当未被更新的提示覆盖时才清除
                if (group.RefreshTip == message) group.RefreshTip = null;
            });
        });
    }

    [RelayCommand]
    private async Task RefreshNextDayForDate(string date)
    {
        if (string.IsNullOrWhiteSpace(date) || _allPicks == null) return;
        var picksForDate = _allPicks.Where(p => p.PickDate == date).ToList();
        if (picksForDate.Count == 0)
        {
            ShowRefreshTip(date, "该日期没有擒牛记录");
            return;
        }
        if (!DateTime.TryParse(date, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var pickDate)) return;

        var todaySh = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, StockReview.Core.Services.CnTimeZone.Get);

        // 下一交易日（自动跳过周末/节假日）15:00 收盘后数据即定型；
        // 此时若次日数据已齐，跳过行情请求直接提示"已是最新数据"
        var nextTradeDay = _marketTime.GetNextTradingDay(pickDate);
        var sessionClosed = todaySh.Date > nextTradeDay.Date
            || (todaySh.Date == nextTradeDay.Date && todaySh.Hour * 60 + todaySh.Minute >= 15 * 60);
        if (sessionClosed && picksForDate.All(p => p.HasNextDay))
        {
            ShowRefreshTip(date, "已是最新数据");
            return;
        }

        ShowRefreshTip(date, "正在刷新次日数据...");

        var updated = 0;
        var skipped = 0;
        foreach (var pick in picksForDate)
        {
            try
            {
                // 默认拉 30 根（≈近 30 天，擒牛记录通常在近期）；数据范围未覆盖
                // pickDate（久远记录，取不到昨收）时才回退 60 根重拉
                var klines = await Task.Run(() => _market.GetDailyKLinesAsync(pick.StockCode, 30));
                var idx = FindNextBarIdx(klines, pickDate);
                if (idx == 0)
                {
                    klines = await Task.Run(() => _market.GetDailyKLinesAsync(pick.StockCode, 60));
                    idx = FindNextBarIdx(klines, pickDate);
                }
                // 次日尚未交易，或缺少 pickDate 当日K线（无法取昨收）
                if (idx <= 0) { skipped++; continue; }

                var bar = klines[idx];
                // 次日即今日时，9:15 前尚无有效高点（对应原版 allowRefreshTime）
                if (bar.Date.Date == todaySh.Date && todaySh.Hour * 60 + todaySh.Minute < 9 * 60 + 15)
                { skipped++; continue; }

                var prevClose = klines[idx - 1].Close;
                if (prevClose <= 0 || bar.High <= 0) { skipped++; continue; }

                _db.Update("dailyPicks", pick.Id, new Dictionary<string, object?>
                {
                    ["nextDayHighPrice"] = (double)bar.High,
                    ["nextDayMaxChange"] = (double)Math.Round((bar.High - prevClose) / prevClose * 100, 2)
                });
                updated++;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[每日擒牛] 刷新 {Code} 次日数据失败", pick.StockCode);
            }
        }

        await LoadAsync();
        ShowRefreshTip(date, updated > 0
            ? $"已刷新 {updated} 条次日数据" + (skipped > 0 ? $"（{skipped} 条暂无有效数据）" : "")
            : "暂无可刷新的次日数据（次日尚未开盘或缺历史K线）");
    }

    /// <summary>找 pickDate 之后第一根日K的下标；-1=次日尚未交易，0=数据范围未覆盖 pickDate（缺昨收）</summary>
    private static int FindNextBarIdx(List<KLineData> klines, DateTime pickDate)
    {
        for (var i = 0; i < klines.Count; i++)
        {
            if (klines[i].Date.Date > pickDate) return i;
        }
        return -1;
    }

    // ============ 截图 / OCR / 行情自动回填 ============

    /// <summary>
    /// 由 UI 层（文件选择/剪贴板）传入 base64 图片并保存到磁盘
    /// </summary>
    public void AttachScreenshotFromBase64(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return;
        var (ok, path, err) = _img.SaveImage(base64);
        if (!ok)
        {
            StatusText = "截图保存失败: " + err;
            return;
        }
        FormPick.Screenshot = path ?? "";
        FormScreenshotDisplay = base64;
    }

    /// <summary>OCR 识别截图中的股票代码并自动回填（对应 recognizeStockFromImage）</summary>
    public async Task RecognizeAndFill(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return;
        OcrLoading = true;
        StatusText = "正在识别股票代码...";
        try
        {
            var result = await _ocr.RecognizeStockCodeAsync(base64);
            if (!result.Success)
            {
                StatusText = "未识别到股票代码：" + (result.Error ?? "请手动输入");
                return;
            }
            FormPick.StockCode = result.Code;
            if (!string.IsNullOrEmpty(result.Name)) FormPick.StockName = result.Name;
            // 名称未匹配时显式提示人工确认（对齐 InsightsView）
            StatusText = string.IsNullOrEmpty(result.Name)
                ? $"已识别 {result.Code}（{result.Source}，未匹配到名称，请人工确认）"
                : $"已识别 {result.Code} {result.Name}（{result.Source}），正在获取行情…";
            await AutoFetchStockData(forceUpdate: true);
        }
        catch (Exception ex)
        {
            StatusText = "OCR 识别失败: " + ex.Message;
        }
        finally
        {
            OcrLoading = false;
        }
    }

    /// <summary>股票代码或名称输入框回车自动回填（对应 handleStockInputEnter）</summary>
    public async Task OnFormEnter()
    {
        var code = FormPick.StockCode?.Trim();
        if (code is not { Length: 6 })
        {
            var name = FormPick.StockName?.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                var hits = await Task.Run(() => _ocr.SearchStocks(name));
                if (hits.Count > 0)
                {
                    FormPick.StockCode = hits[0].Code;
                    FormPick.StockName = hits[0].Name;
                }
            }
        }
        await AutoFetchStockData(forceUpdate: false);
    }

    /// <summary>自动获取行情并回填名称/股价/涨幅（对应原版 autoFetchStockData）</summary>
    public async Task AutoFetchStockData(bool forceUpdate)
    {
        var code = FormPick.StockCode?.Trim();
        if (code is not { Length: 6 }) return;
        try
        {
            StatusText = "获取行情...";
            var data = await StockMarketService.Fetch(_ocr, _market, code, FormPick.PickDate);
            if (data != null)
            {
                if (!string.IsNullOrEmpty(data.Name)) FormPick.StockName = data.Name;
                if (double.TryParse(data.Close, out var close) && (forceUpdate || !(FormPick.Price.HasValue && FormPick.Price.Value > 0)))
                    FormPick.Price = close;
                if (double.TryParse(data.ChangePct, out var chg) && (forceUpdate || !(FormPick.Change.HasValue && FormPick.Change.Value != 0)))
                    FormPick.Change = chg;
                StatusText = data.Source;
            }
            else
            {
                if (string.IsNullOrEmpty(FormPick.StockName))
                {
                    var nm = await Task.Run(() => _ocr.GetNameByCode(code));
                    if (!string.IsNullOrEmpty(nm)) FormPick.StockName = nm;
                }
                StatusText = "未能获取行情，请手动填写";
            }
        }
        catch (Exception ex)
        {
            StatusText = "获取行情失败: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenImagePreview(string url)
    {
        PreviewImageUrl = url;
        ImagePreviewVisible = !string.IsNullOrEmpty(url);
    }

    [RelayCommand]
    private void PreviewScreenshot(DailyPickRecord pick)
    {
        if (!string.IsNullOrEmpty(pick.DisplayScreenshot))
        {
            OpenImagePreview(pick.DisplayScreenshot);
            return;
        }
        // 懒加载尚未完成时点击缩略图：立即触发读盘，完成后自动弹出大图预览
        if (pick.HasScreenshot)
        {
            pick.ScreenshotLoading = false;
            RequestScreenshot(pick, openPreviewWhenDone: true);
        }
    }

    [RelayCommand]
    private void CloseImagePreview() => ImagePreviewVisible = false;

    // ============ 映射辅助 ============
    private static DailyPickRecord Map(Dictionary<string, object?> row)
    {
        return new DailyPickRecord
        {
            Id = GetInt(row, "id"),
            PickDate = GetStr(row, "pickDate"),
            StockCode = GetStr(row, "stockCode"),
            StockName = GetStr(row, "stockName"),
            Price = GetDouble(row, "price"),
            Change = GetDouble(row, "change"),
            PickType = GetStr(row, "pickType"),
            IsSelected = GetInt(row, "isSelected") == 1,
            Remark = GetStr(row, "remark"),
            Screenshot = GetStr(row, "screenshot"),
            NextDayHighPrice = GetDouble(row, "nextDayHighPrice"),
            NextDayMaxChange = GetDouble(row, "nextDayMaxChange"),
            Evaluation = GetStr(row, "evaluation")
        };
    }

    private static string GetStr(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v != null ? v.ToString() ?? "" : "";

    private static int GetInt(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var v) && v != null)
        {
            if (v is bool b) return b ? 1 : 0; // is* 字段被数据层还原成 bool
            if (v is int i) return i;
            if (v is long l) return (int)l;
            if (int.TryParse(v.ToString(), out var r)) return r;
        }
        return 0;
    }

    private static int? GetIntNullable(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var v) && v != null)
        {
            if (v is int i) return i;
            if (v is long l) return (int)l;
            if (int.TryParse(v.ToString(), out var r)) return r;
        }
        return null;
    }

    private static double? GetDouble(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var v) && v != null)
        {
            if (v is double d) return d;
            if (v is long l) return (double)l;
            if (v is decimal m) return (double)m;
            if (double.TryParse(v.ToString(), out var r)) return r;
        }
        return null;
    }
}
