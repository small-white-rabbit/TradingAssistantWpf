using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using StockReview.Core.Data;
using StockReview.Core.MarketData;
using StockReviewWpf.Services;
using StockReviewWpf.ViewModels;

namespace StockReviewWpf.ViewModels.Main;

/// <summary>
/// 强势股池视图 ViewModel - 对应 StrongStocksView.vue（完整 DB 接入版）
/// </summary>
public partial class StrongStocksViewModel : ObservableObject
{
    private readonly DatabaseService _db;
    private readonly ImageService _img;
    private readonly StockOcrService _ocr;
    private readonly MarketDataAggregator _market;

    private List<StrongStockItem> _allStocks = new();
    private List<TradeRecord> _allTrades = new();
    // 截图懒加载并发闸门：同时最多 4 路读盘
    private readonly System.Threading.SemaphoreSlim _shotSemaphore = new(4);

    // ============ 筛选状态 ============
    [ObservableProperty] private int _currentYear = DateTime.Now.Year;
    [ObservableProperty] private int _activeMonth = DateTime.Now.Month;
    [ObservableProperty] private string _selectedMonth = $"{DateTime.Now:yyyy-MM}";
    [ObservableProperty] private string _selectedDate = "";
    [ObservableProperty] private string _viewMode = "card";
    [ObservableProperty] private string _statusText = "就绪";

    [ObservableProperty] private ObservableCollection<int> _availableYears = new();
    [ObservableProperty] private ObservableCollection<string> _availableMonths = new();
    [ObservableProperty] private ObservableCollection<StrongStockItem> _strongStocks = new();
    [ObservableProperty] private ObservableCollection<StrongStockDayGroup> _dayGroups = new();
    [ObservableProperty] private ObservableCollection<DateCell> _dayCells = new();
    [ObservableProperty] private int _filteredStrongStocksCount;

    // 进场类型（强势类型选择器）
    [ObservableProperty] private ObservableCollection<EntryTypeItem> _entryTypeTree = new();
    [ObservableProperty] private ObservableCollection<string> _strongTypeOptions = new();

    // 表单（添加/编辑共用）
    [ObservableProperty] private StrongStockForm _form = new();
    [ObservableProperty] private bool _isAddDialogVisible;
    [ObservableProperty] private bool _isEditDialogVisible;
    [ObservableProperty] private int _editingId = -1;

    // 关联 / 查看
    [ObservableProperty] private bool _isLinkDialogVisible;
    [ObservableProperty] private StrongStockItem? _linkingStock;
    [ObservableProperty] private ObservableCollection<TradeRecord> _myTradesForStock = new();
    [ObservableProperty] private bool _isViewDialogVisible;
    [ObservableProperty] private StrongStockItem? _viewingStock;
    [ObservableProperty] private ObservableCollection<TradeRecord> _viewingRelatedTrades = new();

    // 图片预览
    [ObservableProperty] private bool _isImagePreviewVisible;
    [ObservableProperty] private string _previewImageUrl = "";

    // OCR / 行情自动回填状态
    [ObservableProperty] private bool _isOcrLoading;

    public StrongStocksViewModel(DatabaseService db, ImageService img, StockOcrService ocr, MarketDataAggregator market)
    {
        _db = db;
        _img = img;
        _ocr = ocr;
        _market = market;
#if DEBUG
        DebugSelfCheck();
#endif
        for (int y = DateTime.Now.Year; y >= DateTime.Now.Year - 5; y--)
            AvailableYears.Add(y);
        for (int m = 1; m <= 12; m++)
            AvailableMonths.Add($"{m:00}月");
        _ = LoadAsync();
    }

    // ============ 数据加载 ============

    /// <summary>
    /// 重新加载全部数据（标题栏"刷新"按钮调用；视图有缓存，导航不会重新加载）
    /// </summary>
    [RelayCommand]
    public async Task Reload() => await LoadAsync();

    public async Task LoadAsync()
    {
        try
        {
            // 两阶段加载：① DB 查询（快）→ 立即 Rebuild 显示卡片（截图位置空白）
            // ② 截图磁盘读取（慢）在后台继续，读完逐条补显
            var (stocks, tradeRows) = await Task.Run(() =>
            {
                var s = _db.GetAll("strongStocks").Select(MapStock).ToList();
                return (s, _db.GetAll("trades"));
            });
            _allStocks = stocks;
            _allTrades = tradeRows.Select(MapTrade).ToList();

            await LoadEntryTypesAsync();
            Rebuild();
            StatusText = $"已加载 {_allStocks.Count} 只强势股";

            // 阶段②：截图懒加载——卡片滚动进入可视区时才读盘（见 RequestScreenshot），
            // 只加载当前看得见的内容，替代原“打开页面后台全量预读所有截图”的方案
        }
        catch (Exception ex)
        {
            StatusText = "加载强势股失败: " + ex.Message;
        }
    }

    /// <summary>
    /// 截图懒加载：卡片进入可视区（Image Loaded / DataContextChanged）时触发，
    /// 只读当前看得见的截图；4 路并发读盘，读完经 INPC 补显到卡片。
    /// </summary>
    public void RequestScreenshot(StrongStockItem rec, bool openPreviewWhenDone = false)
    {
        if (string.IsNullOrEmpty(rec.Screenshot) || rec.Screenshot.StartsWith("data:")
            || rec.ScreenshotLoading || rec.DisplayScreenshot.Length > 0) return;
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
                    if (openPreviewWhenDone)
                    {
                        PreviewImageUrl = data;
                        IsImagePreviewVisible = true;
                    }
                });
            }
            finally
            {
                _shotSemaphore.Release();
            }
        });
    }

    private async Task LoadEntryTypesAsync()
    {
        var rows = await Task.Run(() => _db.GetAll("entryTypes"));
        var items = rows.Select(r => new EntryTypeItem
        {
            Id = ToInt(r, "id"),
            Name = S(r, "typeName"),
            SortOrder = ToInt(r, "sortOrder"),
            IsActive = ToInt(r, "isActive") != 0,
            ParentId = r.TryGetValue("parentId", out var pid) && pid != null ? ToInt(r, "parentId") : (int?)null
        }).Where(i => i.IsActive).OrderBy(i => i.SortOrder).ToList();

        var nodes = items.ToDictionary(i => i.Id, i => i);
        foreach (var i in items)
        {
            if (i.ParentId.HasValue && nodes.ContainsKey(i.ParentId.Value))
                nodes[i.ParentId.Value].Children.Add(i);
        }

        EntryTypeTree = new ObservableCollection<EntryTypeItem>(
            items.Where(i => !i.ParentId.HasValue || !nodes.ContainsKey(i.ParentId.Value)));

        var opts = new List<string>();
        foreach (var root in EntryTypeTree)
        {
            if (root.Children.Count > 0) opts.AddRange(root.Children.Select(c => c.Name));
            else opts.Add(root.Name);
        }
        StrongTypeOptions = new ObservableCollection<string>(opts);
    }

    // ============ 重建视图数据 ============

    private void Rebuild()
    {
        RebuildDayGroups();
        RebuildDayCells();
        FilteredStrongStocksCount = DayGroups.Sum(g => g.Stocks.Count);
        // 列表视图展示全部强势股（与 Electron 的 strongStocks 一致）；卡片视图使用按月分组的 DayGroups
        StrongStocks = new ObservableCollection<StrongStockItem>(_allStocks.OrderBy(s => s.Date));
    }

    private void RebuildDayGroups()
    {
        // 全部模式（SelectedMonth 为空）按当年过滤，选月模式按 YYYY-MM 过滤
        var prefix = SelectedMonth.Length >= 7 ? SelectedMonth : CurrentYear.ToString();
        var groups = new Dictionary<string, StrongStockDayGroup>();
        foreach (var s in _allStocks)
        {
            if (!s.Date.StartsWith(prefix)) continue;
            if (!groups.ContainsKey(s.Date))
            {
                groups[s.Date] = new StrongStockDayGroup
                {
                    Date = s.Date,
                    Day = int.Parse(s.Date.Substring(8, 2)),
                    Month = s.Date.Substring(5, 2),
                    WeekDay = GetWeekDay(s.Date)
                };
            }
            groups[s.Date].Stocks.Add(s);
        }
        DayGroups = new ObservableCollection<StrongStockDayGroup>(
            groups.Values.OrderBy(g => g.Date));
    }

    private void RebuildDayCells()
    {
        var days = DateTime.DaysInMonth(CurrentYear, ActiveMonth);
        // 日期条展示 ActiveMonth，HasData 始终按该月检测（与全部模式无关）
        var monthPrefix = $"{CurrentYear}-{ActiveMonth:00}";
        var dataDates = new HashSet<string>(
            _allStocks.Where(s => s.Date.StartsWith(monthPrefix)).Select(s => s.Date));
        var cells = new ObservableCollection<DateCell>();
        for (int d = 1; d <= days; d++)
        {
            var ds = $"{CurrentYear}-{ActiveMonth:00}-{d:00}";
            cells.Add(new DateCell { Day = d, DateStr = ds, HasData = dataDates.Contains(ds) });
        }
        DayCells = cells;
    }

    private static string GetWeekDay(string dateStr)
    {
        var days = new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
        if (DateTime.TryParse(dateStr, out var dt))
            return days[(int)dt.DayOfWeek];
        return "";
    }

    // ============ 命令 ============

    [RelayCommand]
    private void ChangeYear(string delta)
    {
        if (!int.TryParse(delta, out var d)) return;
        CurrentYear += d;
        SelectedMonth = $"{CurrentYear}-{ActiveMonth:00}";
        Rebuild();
    }

    [RelayCommand]
    private void SelectMonth(string month)
    {
        if (!int.TryParse(month, out var m)) return;
        ActiveMonth = m;
        SelectedMonth = $"{CurrentYear}-{m:00}";
        SelectedDate = "";
        Rebuild();
    }

    [RelayCommand]
    private void ShowAllMonths()
    {
        SelectedMonth = "";
        SelectedDate = "";
        Rebuild();
    }

    [RelayCommand]
    private void SelectDate(string date)
    {
        SelectedDate = date;
    }

    [RelayCommand]
    private void SetViewMode(string mode)
    {
        ViewMode = mode;
    }

    [RelayCommand]
    private void ShowAddDialog()
    {
        Form = new StrongStockForm
        {
            Date = string.IsNullOrEmpty(SelectedDate) ? DateTime.Now.ToString("yyyy-MM-dd") : SelectedDate
        };
        IsAddDialogVisible = true;
    }

    [RelayCommand]
    private void CloseAddDialog() => IsAddDialogVisible = false;

    [RelayCommand]
    private void Save()
    {
        if (IsEditDialogVisible) _ = SaveEdit();
        else _ = AddStrongStock();
    }

    [RelayCommand]
    private async Task AddStrongStock()
    {
        if (string.IsNullOrWhiteSpace(Form.StockCode))
        {
            StatusText = "请填写股票代码";
            return;
        }
        try
        {
            var existing = await Task.Run(() => _db.WhereCompoundFirst("strongStocks",
                new Dictionary<string, object> { { "date", Form.Date }, { "stockCode", Form.StockCode } }));
            if (existing != null)
                await Task.Run(() => _db.Delete("strongStocks", existing["id"]!));

            var data = BuildStockDict(Form, "[]");
            await Task.Run(() => _db.Add("strongStocks", data));
            IsAddDialogVisible = false;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = "添加失败: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenEditDialog(StrongStockItem item)
    {
        if (item == null) return;
        EditingId = item.Id;
        Form = new StrongStockForm
        {
            Date = item.Date,
            StockCode = item.StockCode,
            StockName = item.StockName,
            ClosePrice = item.ClosePrice?.ToString() ?? "",
            ChangePct = item.ChangePct?.ToString() ?? "",
            HighPrice = item.HighPrice?.ToString() ?? "",
            MaxChangePct = item.MaxChangePct?.ToString() ?? "",
            StrongType = item.StrongType,
            Screenshot = item.Screenshot,
            ScreenshotDisplay = item.DisplayScreenshot
        };
        IsEditDialogVisible = true;
    }

    [RelayCommand]
    private async Task SaveEdit()
    {
        if (EditingId < 0) return;
        try
        {
            var existing = await Task.Run(() => _db.WhereCompoundFirst("strongStocks",
                new Dictionary<string, object> { { "date", Form.Date }, { "stockCode", Form.StockCode } }));

            if (existing != null && ToInt(existing, "id") != EditingId)
            {
                // 合并关联并替换旧记录
                var merged = ParseIds(S(existing, "relatedTradeIds"));
                var cur = await Task.Run(() => _db.GetById("strongStocks", EditingId));
                var curIds = cur != null ? ParseIds(S(cur, "relatedTradeIds")) : new List<int>();
                merged = merged.Union(curIds).Distinct().ToList();

                var data = BuildStockDict(Form, JsonSerializer.Serialize(merged));
                await Task.Run(() => _db.Update("strongStocks", existing["id"]!, data));
                await Task.Run(() => _db.Delete("strongStocks", EditingId));
            }
            else
            {
                var cur = await Task.Run(() => _db.GetById("strongStocks", EditingId));
                var ids = cur != null ? S(cur, "relatedTradeIds") : "[]";
                var data = BuildStockDict(Form, ids);
                await Task.Run(() => _db.Update("strongStocks", EditingId, data));
            }

            IsEditDialogVisible = false;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = "保存失败: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteStrongStock(StrongStockItem item)
    {
        if (item == null) return;
        try
        {
            if (!string.IsNullOrEmpty(item.Screenshot) && !item.Screenshot.StartsWith("data:"))
                _img.DeleteImage(item.Screenshot);
            await Task.Run(() => _db.Delete("strongStocks", item.Id));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = "删除失败: " + ex.Message;
        }
    }

    [RelayCommand]
    private void SelectStrongType(string type)
    {
        Form.StrongType = type;
    }

    [RelayCommand]
    private void LinkTrade(StrongStockItem item)
    {
        if (item == null) return;
        LinkingStock = item;
        MyTradesForStock = new ObservableCollection<TradeRecord>(
            _allTrades.Where(t => t.StockCode == item.StockCode).OrderBy(t => t.TradeDate));
        IsLinkDialogVisible = true;
    }

    [RelayCommand]
    private async Task SelectTradeToLink(TradeRecord trade)
    {
        if (LinkingStock == null || trade == null) return;
        try
        {
            var ids = ParseIds(LinkingStock.RelatedTradeIds);
            if (!ids.Contains(trade.Id)) ids.Add(trade.Id);
            await Task.Run(() => _db.Update("strongStocks", LinkingStock.Id,
                new Dictionary<string, object?> { { "relatedTradeIds", JsonSerializer.Serialize(ids) } }));
            IsLinkDialogVisible = false;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = "关联失败: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ViewRelated(StrongStockItem item)
    {
        if (item == null) return;
        ViewingStock = item;
        var ids = ParseIds(item.RelatedTradeIds);
        var trades = ids.Count > 0
            ? await Task.Run(() => _db.WhereAnyOf("trades", "id", ids.Cast<object>()))
            : new List<Dictionary<string, object?>>();
        ViewingRelatedTrades = new ObservableCollection<TradeRecord>(trades.Select(MapTrade));
        IsViewDialogVisible = true;
    }

    [RelayCommand]
    private void GoToTrade(TradeRecord trade)
    {
        IsViewDialogVisible = false;
        var main = App.Host?.Services.GetRequiredService<MainViewModel>();
        main?.NavigateCommand.Execute("yearMonth");
    }

    [RelayCommand]
    private void CloseLinkDialog() => IsLinkDialogVisible = false;

    [RelayCommand]
    private void CloseViewDialog() => IsViewDialogVisible = false;

    [RelayCommand]
    private void PreviewImage(StrongStockItem item)
    {
        if (item == null) return;
        if (!string.IsNullOrEmpty(item.DisplayScreenshot))
        {
            PreviewImageUrl = item.DisplayScreenshot;
            IsImagePreviewVisible = true;
            return;
        }
        // 懒加载尚未完成时点击缩略图：立即触发读盘，完成后自动弹出大图预览
        if (!string.IsNullOrEmpty(item.Screenshot))
        {
            item.ScreenshotLoading = false;
            RequestScreenshot(item, openPreviewWhenDone: true);
        }
    }

    [RelayCommand]
    private void CloseImagePreview() => IsImagePreviewVisible = false;

    // ============ 截图 / OCR / 行情自动回填 ============

    /// <summary>OCR 识别截图中的股票代码并自动回填行情（对应 recognizeStockFromImage + autoFetchStockData）</summary>
    public async Task RecognizeAndFill(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return;
        IsOcrLoading = true;
        StatusText = "正在识别股票代码...";
        try
        {
            var result = await _ocr.RecognizeStockCodeAsync(base64);
            if (!result.Success)
            {
                StatusText = "未识别到股票代码，请手动输入";
                return;
            }
            Form.StockCode = result.Code;
            if (!string.IsNullOrEmpty(result.Name)) Form.StockName = result.Name;
            StatusText = result.Source;
            await AutoFetchStockData();
        }
        catch (Exception ex)
        {
            StatusText = "OCR 识别失败: " + ex.Message;
        }
        finally
        {
            IsOcrLoading = false;
        }
    }

    /// <summary>股票代码或名称输入框回车：按当前表单自动回填行情</summary>
    public async Task OnFormEnter()
    {
        var code = Form.StockCode?.Trim();
        if (code is { Length: 6 })
        {
            await AutoFetchStockData();
            return;
        }
        // 代码缺失/不完整时尝试按名称搜索（对应 handleStockInputEnter）
        var name = Form.StockName?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            var hits = await Task.Run(() => _ocr.SearchStocks(name));
            if (hits.Count > 0)
            {
                Form.StockCode = hits[0].Code;
                Form.StockName = hits[0].Name;
                await AutoFetchStockData();
            }
            else
            {
                StatusText = "未匹配到股票，请检查名称";
            }
        }
    }

    /// <summary>表单日期变更：代码已填时重新获取对应日期的行情</summary>
    public async Task OnFormDateChanged()
    {
        var code = Form.StockCode?.Trim();
        if (code is { Length: 6 })
            await AutoFetchStockData();
    }

    /// <summary>自动获取行情并回填表单（对应 Electron autoFetchStockData）</summary>
    public async Task AutoFetchStockData()
    {
        var code = Form.StockCode?.Trim();
        if (code is not { Length: 6 }) return;
        try
        {
            StatusText = "获取行情...";
            var data = await StockMarketService.Fetch(_ocr, _market, code, Form.Date);
            if (data != null)
            {
                if (!string.IsNullOrEmpty(data.Name)) Form.StockName = data.Name;
                Form.ClosePrice = data.Close;
                Form.ChangePct = data.ChangePct;
                Form.HighPrice = data.High;
                Form.MaxChangePct = data.MaxChangePct;
                StatusText = data.Source;
            }
            else
            {
                if (string.IsNullOrEmpty(Form.StockName))
                {
                    var nm = await Task.Run(() => _ocr.GetNameByCode(code));
                    if (!string.IsNullOrEmpty(nm)) Form.StockName = nm;
                }
                StatusText = "未能获取行情，请手动填写";
            }
        }
        catch (Exception ex)
        {
            StatusText = "获取行情失败: " + ex.Message;
        }
    }

    #if DEBUG
    private static bool _selfChecked;
    /// <summary>Debug 自检：历史K线→行情字段最小值断言（不用框架，纯 assert）</summary>
    private static void DebugSelfCheck()
    {
        if (_selfChecked) return;
        _selfChecked = true;
        var klines = new List<KLineData>
        {
            new() { Date = new DateTime(2026, 8, 20), Close = 10.00m, High = 10.60m },
            new() { Date = new DateTime(2026, 8, 21), Close = 10.50m, High = 11.00m } // 目标日：昨收10.00
        };
        var r = StockMarketService.BuildFromKlines(klines, new DateTime(2026, 8, 21));
        System.Diagnostics.Debug.Assert(r != null, "应命中目标日");
        System.Diagnostics.Debug.Assert(r!.Close == "10.50", "收盘价错误");
        System.Diagnostics.Debug.Assert(r.High == "11.00", "最高价错误");
        System.Diagnostics.Debug.Assert(r.MaxChangePct == "10.00", $"最大涨幅应为10.00,实际{r.MaxChangePct}");
        System.Diagnostics.Debug.Assert(r.ChangePct == "5.00", $"涨跌幅应为5.00,实际{r.ChangePct}");
        // 非交易日回退到最近交易日
        var nr = StockMarketService.BuildFromKlines(klines, new DateTime(2026, 8, 23));
        System.Diagnostics.Debug.Assert(nr != null && nr.Close == "10.50", "非交易日应回退最近交易日");
        System.Diagnostics.Debug.Assert(StockMarketService.BuildFromKlines(new List<KLineData>(), DateTime.Today) == null, "空K线应返回null");
    }
#endif

    public void AttachScreenshotFromBase64(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return;
        var (ok, path, err) = _img.SaveImage(base64);
        if (ok)
        {
            Form.Screenshot = path!;
            Form.ScreenshotDisplay = base64;
        }
        else
        {
            StatusText = "保存截图失败: " + err;
        }
    }

    // ============ 映射辅助 ============

    private StrongStockItem MapStock(Dictionary<string, object?> r)
    {
        return new StrongStockItem
        {
            Id = ToInt(r, "id"),
            Date = S(r, "date"),
            StockCode = S(r, "stockCode"),
            StockName = S(r, "stockName"),
            HighPrice = D(r, "highPrice"),
            MaxChangePct = D(r, "maxChangePct"),
            ChangePct = D(r, "changePct"),
            ClosePrice = D(r, "closePrice"),
            Screenshot = S(r, "screenshot"),
            StrongType = S(r, "strongType"),
            RelatedTradeIds = S(r, "relatedTradeIds"),
            CreatedAt = S(r, "createdAt"),
            UpdatedAt = S(r, "updatedAt")
        };
    }

    private TradeRecord MapTrade(Dictionary<string, object?> r)
    {
        return new TradeRecord
        {
            Id = ToInt(r, "id"),
            TradeDate = S(r, "tradeDate"),
            StockCode = S(r, "stockCode"),
            StockName = S(r, "stockName"),
            EntryType = S(r, "entryType"),
            PositionStatus = S(r, "positionStatus"),
            TotalReturn = D(r, "totalReturn")
        };
    }

    private Dictionary<string, object?> BuildStockDict(StrongStockForm f, string relatedTradeIds)
    {
        return new Dictionary<string, object?>
        {
            ["date"] = f.Date,
            ["stockCode"] = f.StockCode,
            ["stockName"] = f.StockName,
            ["highPrice"] = ParseDouble(f.HighPrice),
            ["maxChangePct"] = ParseDouble(f.MaxChangePct),
            ["changePct"] = ParseDouble(f.ChangePct),
            ["closePrice"] = ParseDouble(f.ClosePrice),
            ["screenshot"] = f.Screenshot,
            ["strongType"] = f.StrongType,
            ["relatedTradeIds"] = relatedTradeIds
        };
    }

    private static string S(Dictionary<string, object?> r, string k)
        => r.TryGetValue(k, out var v) && v != null ? v.ToString()! : "";

    private static double? D(Dictionary<string, object?> r, string k)
    {
        if (r.TryGetValue(k, out var v) && v != null)
        {
            if (v is double d) return d;
            if (v is int or long && v is IConvertible)
                return Convert.ToDouble(v);
            if (double.TryParse(v.ToString(), out var dd)) return dd;
        }
        return null;
    }

    private static int ToInt(Dictionary<string, object?> r, string k)
    {
        if (r.TryGetValue(k, out var v) && v != null)
        {
            if (v is bool b) return b ? 1 : 0; // is* 字段被数据层还原成 bool
            if (v is int or long) return Convert.ToInt32(v);
            if (int.TryParse(v.ToString(), out var i)) return i;
        }
        return 0;
    }

    private static double? ParseDouble(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return double.TryParse(s, out var d) ? d : null;
    }

    private static List<int> ParseIds(string s)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(s) || s.Trim() == "[]") return list;
        try
        {
            var arr = JsonSerializer.Deserialize<List<int>>(s);
            if (arr != null) list = arr;
        }
        catch { }
        return list;
    }
}

/// <summary>
/// 强势股编辑/添加表单（字符串字段，便于 TextBox 双向绑定）
/// </summary>
public partial class StrongStockForm : ObservableObject
{
    [ObservableProperty] private string _date = DateTime.Now.ToString("yyyy-MM-dd");
    [ObservableProperty] private string _stockCode = "";
    [ObservableProperty] private string _stockName = "";
    [ObservableProperty] private string _closePrice = "";
    [ObservableProperty] private string _changePct = "";
    [ObservableProperty] private string _highPrice = "";
    [ObservableProperty] private string _maxChangePct = "";
    [ObservableProperty] private string _strongType = "";
    [ObservableProperty] private string _screenshot = "";
    [ObservableProperty] private string _screenshotDisplay = "";
}
