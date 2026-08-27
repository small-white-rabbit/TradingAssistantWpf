using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockReview.Core.Data;
using StockReviewWpf.Services;
using StockReviewWpf.ViewModels;

namespace StockReviewWpf.ViewModels.Main;

/// <summary>
/// 心得记录视图 ViewModel - 对应 InsightsView.vue（数据驱动核心版）
/// </summary>
public partial class InsightsViewModel : ObservableObject
{
    private readonly DatabaseService _db;
    private readonly ImageService _img;
    private readonly StockOcrService _ocr;
    private readonly MainViewModel _mainVm;

    [ObservableProperty]
    private ObservableCollection<InsightItem> _insightList = new();

    // 全量数据源：搜索/筛选从它过滤后写入 InsightList。
    // 若直接从 InsightList（已过滤结果）再次过滤，被筛掉的条目将永久丢失，无法回退显示全部。
    private List<InsightItem> _allInsights = new();

    [ObservableProperty]
    private InsightItem? _selectedInsight;

    [ObservableProperty]
    private bool _isDetailVisible;

    [ObservableProperty]
    private int _filterImportance;

    // 关键词/关注度变化即时过滤（搜索框为 PropertyChanged 实时回传，清空即恢复全部心得）
    partial void OnFilterImportanceChanged(int value) => ApplyFilter();

    [ObservableProperty]
    private string _searchKeyword = "";

    partial void OnSearchKeywordChanged(string value) => ApplyFilter();

    [ObservableProperty]
    private string _sortBy = "date_desc";

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _filteredCount;

    [ObservableProperty]
    private int _importantCount;

    // 统计卡片（对齐原版：非常重要5/很重要4/重要3/一般2+1）
    [ObservableProperty]
    private int _veryImportantCount;

    [ObservableProperty]
    private int _quiteImportantCount;

    [ObservableProperty]
    private int _moderateCount;

    [ObservableProperty]
    private int _normalCount;

    // 新增/编辑
    [ObservableProperty]
    private bool _isEditVisible;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private int _editingId;

    /// <summary>标记当前编辑的是日记（而非心得），保存时写入 dailySummaries 表</summary>
    private bool _editingDiary;

    [ObservableProperty]
    private string _formRecordDate = "";

    [ObservableProperty]
    private string _formTitle = "";

    [ObservableProperty]
    private string _formContent = "";

    [ObservableProperty]
    private int _formImportance = 5;

    [ObservableProperty]
    private string _formStockCode = "";

    [ObservableProperty]
    private string _formStockName = "";

    [ObservableProperty]
    private string _formTagsText = "";

    [ObservableProperty]
    private ObservableCollection<string> _formScreenshots = new();

    [ObservableProperty]
    private ObservableCollection<string> _formScreenshotDisplays = new();

    // OCR 识别状态：idle / recognizing / done / failed
    [ObservableProperty]
    private string _ocrStatus = "idle";

    [ObservableProperty]
    private string _ocrMessage = "";

    [ObservableProperty]
    private string _previewImageUrl = "";

    [ObservableProperty]
    private bool _isImagePreviewVisible;

    [ObservableProperty]
    private ObservableCollection<string> _detailScreenshots = new();

    // 左侧侧栏导航状态：insights / diary / center
    [ObservableProperty]
    private string _sideNav = "insights";


    // 心得列表展示风格（来自显示设置，card/grid/timeline/paper/magazine/compact）
    [ObservableProperty]
    private string _insightListStyle = "card";

    // 心得纸张滚动模式（paper 风格专用：vertical 网格 / horizontal 左右翻页）
    [ObservableProperty]
    private string _insightPaperScrollMode = "vertical";

    // 纸张横向翻页当前索引（对应原版 insightPaperIndex）
    [ObservableProperty]
    private int _insightPaperIndex;

    [ObservableProperty]
    private bool _canPrevPaper = true;

    [ObservableProperty]
    private bool _canNextPaper = true;

    partial void OnInsightPaperIndexChanged(int value) => UpdateInsightPaperNav();

    partial void OnInsightListChanged(ObservableCollection<InsightItem> value)
    {
        UpdateInsightPaperNav();
    }

    private void UpdateInsightPaperNav()
    {
        CanPrevPaper = InsightPaperIndex > 0;
        CanNextPaper = InsightPaperIndex < InsightList.Count - 1;
    }

    // 日记列表
    [ObservableProperty]
    private ObservableCollection<DiaryItem> _diaryList = new();

    // 日记列表展示风格（来自显示设置 diaryListStyle，card/timeline/grid/bubble/paper）
    [ObservableProperty]
    private string _diaryListStyle = "card";

    // 日记详情展示风格（来自显示设置 diaryStyle，card/split/timeline/dark）
    [ObservableProperty]
    private string _diaryStyle = "card";

    // 日记纸张滚动模式（来自显示设置 paperScrollMode：vertical 网格 / horizontal 左右翻页）
    [ObservableProperty]
    private string _diaryPaperScrollMode = "vertical";

    // 日记纸张横向翻页索引与导航（对应原版 paperPageIndex）
    [ObservableProperty]
    private int _diaryPaperIndex;

    [ObservableProperty]
    private bool _canPrevDiaryPaper = true;

    [ObservableProperty]
    private bool _canNextDiaryPaper = true;

    partial void OnDiaryPaperIndexChanged(int value) => UpdateDiaryPaperNav();

    partial void OnDiaryListChanged(ObservableCollection<DiaryItem> value)
    {
        UpdateDiaryPaperNav();
    }

    private void UpdateDiaryPaperNav()
    {
        CanPrevDiaryPaper = DiaryPaperIndex > 0;
        CanNextDiaryPaper = DiaryPaperIndex < DiaryList.Count - 1;
    }

    public InsightsViewModel(DatabaseService db, ImageService img, StockOcrService ocr, MainViewModel mainVm)
    {
        _db = db;
        _img = img;
        _ocr = ocr;
        _mainVm = mainVm;
        _ = LoadAsync();
        _ = LoadDiariesAsync();
    }

    private void LoadDisplayStyles()
    {
        var cfg = _db.GetById("appConfig", "displayConfig");
        if (cfg != null && cfg.TryGetValue("value", out var v) && v != null)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(v.ToString()!);
                var r = doc.RootElement;
                if (r.TryGetProperty("insightListStyle", out var ils)) InsightListStyle = ils.GetString() ?? "card";
                if (r.TryGetProperty("insightPaperScrollMode", out var ipsm)) InsightPaperScrollMode = ipsm.GetString() ?? "vertical";
                if (r.TryGetProperty("diaryListStyle", out var dls)) DiaryListStyle = dls.GetString() ?? "card";
                if (r.TryGetProperty("diaryStyle", out var ds)) DiaryStyle = ds.GetString() ?? "card";
                if (r.TryGetProperty("paperScrollMode", out var psm)) DiaryPaperScrollMode = psm.GetString() ?? "vertical";
            }
            catch { }
        }
    }

    /// <summary>从 dailySummaries 表加载日记列表（日/周/月记），并按原版口径计算纸张风格的交易统计。</summary>
    private async System.Threading.Tasks.Task LoadDiariesAsync()
    {
        try
        {
            var rows = await System.Threading.Tasks.Task.Run(() => _db.GetAll("dailySummaries"));
            var tradeRows = await System.Threading.Tasks.Task.Run(() => _db.GetAll("trades"));
            var trades = tradeRows
                .Select(r => new TradeRecord
                {
                    Id = ToInt(r, "id"),
                    TradeDate = S(r, "tradeDate"),
                    StockCode = S(r, "stockCode"),
                    StockName = S(r, "stockName"),
                    EntryType = S(r, "entryType"),
                    PositionStatus = S(r, "positionStatus"),
                    TotalReturn = D(r, "totalReturn")
                })
                .OrderByDescending(t => t.TradeDate)
                .ToList();

            var items = rows
                .Select(r =>
                {
                    // 兼容两种存储：早期日记富文本存在 content 字段，后期存在 summary 字段
                    var summaryVal = S(r, "summary");
                    var contentVal = S(r, "content");
                    var d = new DiaryItem
                    {
                        Id = ToInt(r, "id"),
                        RecordDate = S(r, "recordDate"),
                        SummaryType = S(r, "summaryType") is { Length: > 0 } t ? t : "daily",
                        Title = S(r, "title"),
                        // 优先 content（早期数据），回退 summary（后期数据）
                        Summary = !string.IsNullOrEmpty(contentVal) ? contentVal
                                  : !string.IsNullOrEmpty(summaryVal) ? summaryVal
                                  : "",
                        StartDate = S(r, "startDate"),
                        EndDate = S(r, "endDate"),
                        CreatedAt = S(r, "createdAt"),
                    };
                    FillDiaryStats(d, trades);
                    return d;
                })
                .OrderByDescending(d => d.RecordDate)
                .ToList();
            // 赋值页码：最新的=1，最老的=N（对应原版 paperPageIndex 1-based）
            for (var i = 0; i < items.Count; i++)
                items[i].PaperNumber = i + 1;
            DiaryList = new ObservableCollection<DiaryItem>(items);
        }
        catch { }
    }

    /// <summary>按原版 calculateDailyStats/Weekly/Monthly 口径：统计区间内已清仓交易的笔数/胜率/均盈，并列出区间交易。</summary>
    private static void FillDiaryStats(DiaryItem diary, List<TradeRecord> trades)
    {
        var start = diary.SummaryType == "daily" ? diary.RecordDate
            : (diary.StartDate is { Length: > 0 } s ? s : diary.RecordDate);
        var end = diary.SummaryType == "daily" ? diary.RecordDate
            : (diary.EndDate is { Length: > 0 } e ? e : diary.RecordDate);
        if (string.IsNullOrEmpty(start)) return;

        var range = trades.Where(t => t.TradeDate.CompareTo(start) >= 0 && t.TradeDate.CompareTo(end) <= 0).ToList();
        // 原版默认 showAllTrades=false：表格只列已清仓
        diary.Trades = range.Where(t => t.PositionStatus == "已清仓").ToList();

        var cleared = range.Where(t => t.PositionStatus == "已清仓").ToList();
        diary.TradeTotal = cleared.Count;
        if (diary.TradeTotal > 0)
        {
            var wins = cleared.Count(t => (t.TotalReturn ?? 0) >= 0);
            diary.WinRate = Math.Round(wins * 100.0 / diary.TradeTotal, 1);
            diary.AvgReturn = Math.Round(cleared.Average(t => t.TotalReturn ?? 0), 2);
        }
    }

    [RelayCommand]
    private void SelectSideNav(string nav)
    {
        SideNav = nav;
        if (nav == "diary") _ = LoadDiariesAsync();
    }

    // 纸张横向翻页：上一页/下一页（对应原版箭头按钮 + 滚轮）
    [RelayCommand]
    private void PrevPaperPage()
    {
        if (InsightPaperIndex > 0) InsightPaperIndex--;
    }

    [RelayCommand]
    private void NextPaperPage()
    {
        if (InsightPaperIndex < InsightList.Count - 1) InsightPaperIndex++;
    }

    // 日记纸张横向翻页：上一页/下一页
    [RelayCommand]
    private void PrevDiaryPaperPage()
    {
        if (DiaryPaperIndex > 0) DiaryPaperIndex--;
    }

    [RelayCommand]
    private void NextDiaryPaperPage()
    {
        if (DiaryPaperIndex < DiaryList.Count - 1) DiaryPaperIndex++;
    }

    [RelayCommand]
    private void OpenDiary()
    {
        // 原地新增日记：复用本页编辑弹窗（保存时走 dailySummaries 分支），不跳转交易记录页
        _editingDiary = true;
        IsEditing = false;
        EditingId = 0;
        FormRecordDate = DateTime.Now.ToString("yyyy-MM-dd");
        FormTitle = "";
        FormContent = "";
        FormImportance = 0;
        FormStockCode = "";
        FormStockName = "";
        FormTagsText = "";
        FormScreenshots.Clear();
        FormScreenshotDisplays.Clear();
        IsEditVisible = true;
    }

    [RelayCommand]
    private void EditDiary(DiaryItem? item)
    {
        if (item == null) return;
        // 原地编辑：在心得页面内打开编辑弹窗，不跳转到交易记录页
        _editingDiary = true;
        IsEditing = true;
        EditingId = item.Id;
        FormRecordDate = item.RecordDate;
        FormTitle = item.Title;
        FormContent = item.Summary;
        FormImportance = 0;
        FormStockCode = "";
        FormStockName = "";
        FormTagsText = "";
        FormScreenshots.Clear();
        FormScreenshotDisplays.Clear();
        IsEditVisible = true;
    }

    [RelayCommand]
    private void DeleteDiary(DiaryItem item)
    {
        if (item == null) return;
        var confirm = System.Windows.MessageBox.Show("确定要删除这则日记吗？", "确认删除",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;
        _db.Delete("dailySummaries", item.Id);
        _ = LoadDiariesAsync();
    }

    /// <summary>设置当前要在弹窗中查看的日记（富文本渲染）。</summary>
    [ObservableProperty]
    private DiaryItem? _selectedDiary;

    [ObservableProperty]
    private bool _isDiaryDetailVisible;

    [RelayCommand]
    private void ViewDiary(DiaryItem item)
    {
        if (item == null) return;
        SelectedDiary = item;
        DiaryPaperIndex = DiaryList.IndexOf(item);
        IsDiaryDetailVisible = true;
    }

    [RelayCommand]
    private void CloseDiaryDetail() => IsDiaryDetailVisible = false;

    // 详情弹窗内上一篇/下一篇（对应原版 prevDiary/nextDiary）
    [RelayCommand]
    private void PrevDiary()
    {
        var idx = SelectedDiary == null ? -1 : DiaryList.IndexOf(SelectedDiary);
        if (idx > 0) SelectedDiary = DiaryList[idx - 1];
    }

    [RelayCommand]
    private void NextDiary()
    {
        var idx = SelectedDiary == null ? -1 : DiaryList.IndexOf(SelectedDiary);
        if (idx >= 0 && idx < DiaryList.Count - 1) SelectedDiary = DiaryList[idx + 1];
    }

    /// <summary>
    /// 重新加载全部数据（标题栏"刷新"按钮调用；视图有缓存，导航不会重新加载）
    /// </summary>
    [RelayCommand]
    public async System.Threading.Tasks.Task Reload()
    {
        await LoadAsync();
        await LoadDiariesAsync();
    }

    private async System.Threading.Tasks.Task LoadAsync()
    {
        LoadDisplayStyles();
        // DB 读取在后台线程（同步执行会冻结 UI）
        var items = await System.Threading.Tasks.Task.Run(() =>
            _db.GetAll("insights").Select(MapInsight).OrderByDescending(i => i.RecordDate).ToList());
        foreach (var it in items)
        {
            var displayCases = new List<InsightItem.DisplayCaseBrief>();
            if (it.RelatedCaseIds != null)
            {
                for (var i = 0; i < it.RelatedCaseIds.Count; i++)
                {
                    var caseId = it.RelatedCaseIds[i];
                    var caseType = i < it.RelatedCaseTypes?.Count ? it.RelatedCaseTypes[i] : "";
                    displayCases.Add(new InsightItem.DisplayCaseBrief
                    {
                        Id = caseId, StockName = it.StockName, StockCode = it.StockCode,
                        IsSuccess = caseType == "成功案例"
                    });
                }
            }
            it.DisplayCases = displayCases;
        }
        InsightList = new ObservableCollection<InsightItem>(items);
        _allInsights = items;
        TotalCount = items.Count;
        ImportantCount = items.Count(i => i.Importance >= 4);
        VeryImportantCount = items.Count(i => i.Importance == 5);
        QuiteImportantCount = items.Count(i => i.Importance == 4);
        ModerateCount = items.Count(i => i.Importance == 3);
        NormalCount = items.Count(i => i.Importance <= 2);
        ApplyFilter();

        // 阶段②：后台批量读截图，逐条补显（不阻塞 UI，列表先显示文字内容）
        var itemsWithShots = items.Where(x => x.Screenshots.Count > 0).ToList();
        if (itemsWithShots.Count > 0)
        {
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var it in itemsWithShots)
                {
                    foreach (var sc in it.Screenshots)
                    {
                        var (ok, data, _) = _img.ReadImage(sc);
                        if (ok)
                        {
                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                                it.DisplayScreenshots.Add(data));
                        }
                    }
                }
            });
        }
    }

    private InsightItem MapInsight(Dictionary<string, object?> r)
    {
        var item = new InsightItem
        {
            Id = ToInt(r, "id"),
            RecordDate = S(r, "recordDate"),
            Title = S(r, "title"),
            Content = S(r, "content"),
            Importance = ToInt(r, "importance"),
            StockCode = S(r, "stockCode"),
            StockName = S(r, "stockName"),
            // 数组列（tags/screenshot 等）数据层还原为 List<object>，须传原始值而非 S() 后的
            // "System.Collections.Generic.List`1[System.Object]" 字符串
            RelatedCaseIds = Services.ArrayFieldUtil.ToStringList(r.GetValueOrDefault("relatedCaseIds")),
            RelatedCaseTypes = Services.ArrayFieldUtil.ToStringList(r.GetValueOrDefault("relatedCaseTypes")),
            Tags = Services.ArrayFieldUtil.ToStringList(r.GetValueOrDefault("tags")),
            Screenshots = Services.ArrayFieldUtil.ToStringList(r.GetValueOrDefault("screenshot")),
            IsPinned = ToInt(r, "isPinned") != 0,
            PinnedAt = S(r, "pinnedAt"),
            CreatedAt = S(r, "createdAt"),
            UpdatedAt = S(r, "updatedAt")
        };
        // 截图延迟加载：不阻塞列表渲染，读完再逐条补显
        // （原实现在 MapInsight 中同步 ReadImage，心得多时加载延迟数秒）
        // 截图路径先占位，后续在 LoadAsync 完成后后台批量读取
        return item;
    }

    private void ApplyFilter()
    {
        // 从全量数据源过滤（不能从 InsightList 自身过滤，否则结果越滤越少且无法回退）
        var result = _allInsights.ToList();
        if (FilterImportance > 0)
            result = result.Where(i => i.Importance == FilterImportance).ToList();
        if (!string.IsNullOrWhiteSpace(SearchKeyword))
        {
            var kw = SearchKeyword.Trim().ToLowerInvariant();
            result = result.Where(i =>
                (i.Title ?? "").ToLowerInvariant().Contains(kw) ||
                (i.PlainContent ?? "").ToLowerInvariant().Contains(kw) ||
                (i.StockCode ?? "").Contains(kw) ||
                (i.StockName ?? "").ToLowerInvariant().Contains(kw) ||
                i.Tags.Any(t => (t ?? "").ToLowerInvariant().Contains(kw))).ToList();
        }

        // 排序：置顶优先，然后按选择
        var pinned = result.Where(i => i.IsPinned)
            .OrderByDescending(i => i.PinnedAt).ToList();
        var unpinned = result.Where(i => !i.IsPinned).ToList();
        switch (SortBy)
        {
            case "date_asc":
                unpinned = unpinned.OrderBy(i => i.RecordDate).ToList();
                break;
            case "importance_desc":
                unpinned = unpinned.OrderByDescending(i => i.Importance).ToList();
                break;
            default:
                unpinned = unpinned.OrderByDescending(i => i.RecordDate).ToList();
                break;
        }
        var ordered = pinned.Concat(unpinned).ToList();
        InsightList = new ObservableCollection<InsightItem>(ordered);
        FilteredCount = ordered.Count;
    }

    [RelayCommand]
    private void Search() => ApplyFilter();

    [RelayCommand]
    private void SetImportance(int level)
    {
        FilterImportance = level;
        ApplyFilter();
    }

    [RelayCommand]
    private void SetSort(string sort)
    {
        SortBy = sort;
        ApplyFilter();
    }

    [RelayCommand]
    private void ShowDetail(InsightItem item)
    {
        SelectedInsight = item;
        DetailScreenshots.Clear();
        foreach (var d in item.DisplayScreenshots) DetailScreenshots.Add(d);
        IsDetailVisible = true;
    }

    [RelayCommand]
    private void CloseDetail() => IsDetailVisible = false;

    [RelayCommand]
    private void TogglePin(InsightItem item)
    {
        if (item == null) return;
        var isPinned = !item.IsPinned;
        item.IsPinned = isPinned;
        item.PinnedAt = isPinned ? DateTime.Now.ToString("o") : "";
        _db.Update("insights", item.Id, new Dictionary<string, object?>
        {
            ["isPinned"] = isPinned ? 1 : 0,
            ["pinnedAt"] = item.PinnedAt
        });
        ApplyFilter();
    }

    [RelayCommand]
    private void AddInsight()
    {
        _editingDiary = false;
        IsEditing = false;
        EditingId = 0;
        FormRecordDate = DateTime.Now.ToString("yyyy-MM-dd");
        FormTitle = "";
        FormContent = "";
        FormImportance = 5;
        FormStockCode = "";
        FormStockName = "";
        FormTagsText = "";
        FormScreenshots.Clear();
        FormScreenshotDisplays.Clear();
        IsEditVisible = true;
    }

    [RelayCommand]
    private void RemoveFormScreenshot(int index)
    {
        if (index < 0 || index >= FormScreenshots.Count) return;
        FormScreenshots.RemoveAt(index);
        FormScreenshotDisplays.RemoveAt(index);
    }

    [RelayCommand]
    private void RemoveFormScreenshotByData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;
        var idx = FormScreenshotDisplays.IndexOf(data);
        if (idx < 0) return;
        FormScreenshots.RemoveAt(idx);
        FormScreenshotDisplays.RemoveAt(idx);
    }

    [RelayCommand]
    private void EditInsight(InsightItem item)
    {
        if (item == null) return;
        _editingDiary = false;
        IsEditing = true;
        EditingId = item.Id;
        FormRecordDate = item.RecordDate;
        FormTitle = item.Title;
        FormContent = item.Content;
        FormImportance = item.Importance;
        FormStockCode = item.StockCode;
        FormStockName = item.StockName;
        FormTagsText = string.Join(", ", item.Tags);
        FormScreenshots.Clear();
        FormScreenshotDisplays.Clear();
        foreach (var s in item.Screenshots) FormScreenshots.Add(s);
        foreach (var d in item.DisplayScreenshots) FormScreenshotDisplays.Add(d);
        IsEditVisible = true;
    }

    [RelayCommand]
    private void CloseEdit() => IsEditVisible = false;

    [RelayCommand]
    private void AttachScreenshot()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            Title = "选择截图",
            Multiselect = true
        };
        if (dlg.ShowDialog() == true)
        {
            foreach (var file in dlg.FileNames)
            {
                try
                {
                    var bytes = System.IO.File.ReadAllBytes(file);
                    var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                    var mime = ext switch
                    {
                        ".png" => "image/png",
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".bmp" => "image/bmp",
                        ".gif" => "image/gif",
                        _ => "image/png"
                    };
                    var base64 = $"data:{mime};base64," + Convert.ToBase64String(bytes);
                    var (ok, path, _) = _img.SaveImage(base64);
                    if (ok && path != null)
                    {
                        FormScreenshots.Add(path);
                        FormScreenshotDisplays.Add(base64);
                    }
                }
                catch { }
            }
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task PasteAndRecognize()
    {
        if (OcrStatus == "recognizing") return;
        // 从剪贴板读取图片（WPF 原生）
        if (!System.Windows.Clipboard.ContainsImage())
        {
            OcrStatus = "failed";
            OcrMessage = "剪贴板中没有图片，请先截图";
            return;
        }
        try
        {
            OcrStatus = "recognizing";
            OcrMessage = "正在识别股票代码…";

            var bmp = System.Windows.Clipboard.GetImage();
            if (bmp == null)
            {
                OcrStatus = "failed";
                OcrMessage = "剪贴板图片读取失败";
                return;
            }

            // 转 base64 并保存截图
            var base64 = BitmapToBase64(bmp);
            if (!string.IsNullOrEmpty(base64))
            {
                var (ok, path, _) = _img.SaveImage(base64);
                if (ok && path != null)
                {
                    FormScreenshots.Add(path);
                    FormScreenshotDisplays.Add(base64);
                }
            }

            // OCR 识别
            var result = await _ocr.RecognizeStockCodeAsync(base64);
            if (result.Success && result.Code.Length == 6)
            {
                FormStockCode = result.Code;
                FormStockName = result.Name;
                OcrStatus = "done";
                OcrMessage = string.IsNullOrEmpty(result.Name)
                    ? $"识别到代码 {result.Code}（未在股票列表匹配名称）"
                    : $"识别成功：{result.Code} {result.Name}";
            }
            else
            {
                OcrStatus = "failed";
                OcrMessage = result.Error ?? "识别失败";
            }
        }
        catch (Exception ex)
        {
            OcrStatus = "failed";
            OcrMessage = "识别异常: " + ex.Message;
        }
    }

    private static string BitmapToBase64(System.Windows.Media.Imaging.BitmapSource bmp)
    {
        try
        {
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var ms = new System.IO.MemoryStream();
            enc.Save(ms);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch
        {
            return "";
        }
    }

    [RelayCommand]
    private void SaveInsight()
    {
        if (string.IsNullOrWhiteSpace(FormRecordDate) || string.IsNullOrWhiteSpace(FormTitle) || string.IsNullOrWhiteSpace(FormContent))
            return;

        // 日记编辑：保存到 dailySummaries 表，保存后刷新日记列表
        if (_editingDiary)
        {
            var diaryData = new Dictionary<string, object?>
            {
                ["title"] = FormTitle,
                ["summary"] = FormContent,
                ["content"] = FormContent,
                ["recordDate"] = FormRecordDate,
                ["updatedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            if (IsEditing && EditingId > 0)
                _db.Update("dailySummaries", EditingId, diaryData);
            else
            {
                diaryData["summaryType"] = "daily";
                diaryData["createdAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                _db.Add("dailySummaries", diaryData);
            }
            IsEditVisible = false;
            _ = LoadDiariesAsync();
            return;
        }

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var tags = FormTagsText.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        var dict = new Dictionary<string, object?>
        {
            ["recordDate"] = FormRecordDate,
            ["title"] = FormTitle,
            ["content"] = FormContent,
            ["importance"] = FormImportance,
            ["stockCode"] = FormStockCode,
            ["stockName"] = FormStockName,
            // 编辑时保留原有的关联案例，新增时为空（避免覆盖丢失数据）
            ["relatedCaseIds"] = IsEditing
                ? System.Text.Json.JsonSerializer.Serialize(
                    (InsightList.FirstOrDefault(i => i.Id == EditingId)?.RelatedCaseIds) ?? new List<string>())
                : "[]",
            ["relatedCaseTypes"] = IsEditing
                ? System.Text.Json.JsonSerializer.Serialize(
                    (InsightList.FirstOrDefault(i => i.Id == EditingId)?.RelatedCaseTypes) ?? new List<string>())
                : "[]",
            ["tags"] = System.Text.Json.JsonSerializer.Serialize(tags),
            ["screenshot"] = System.Text.Json.JsonSerializer.Serialize(FormScreenshots.ToList()),
            ["updatedAt"] = now
        };
        if (IsEditing)
        {
            _db.Update("insights", EditingId, dict);
        }
        else
        {
            dict["createdAt"] = now;
            dict["isPinned"] = 0;
            dict["pinnedAt"] = "";
            _db.Add("insights", dict);
        }
        IsEditVisible = false;
        _ = LoadAsync();
    }

    [RelayCommand]
    private void DeleteInsight(InsightItem item)
    {
        if (item == null) return;
        var confirm = System.Windows.MessageBox.Show("确定要删除这条心得记录吗？", "确认删除",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;
        _db.Delete("insights", item.Id);
        _ = LoadAsync();
    }

    [RelayCommand]
    private void PreviewScreenshot(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        PreviewImageUrl = url;
        IsImagePreviewVisible = true;
    }

    [RelayCommand]
    private void CloseImagePreview() => IsImagePreviewVisible = false;

    // ===== helpers =====
    private static string S(Dictionary<string, object?> r, string k) =>
        r.TryGetValue(k, out var v) && v != null ? v.ToString() ?? "" : "";

    private static int ToInt(Dictionary<string, object?> r, string k)
    {
        var s = S(r, k);
        return int.TryParse(s, out var v) ? v : 0;
    }

    private static double? D(Dictionary<string, object?> r, string k)
    {
        var s = S(r, k);
        return double.TryParse(s, out var v) ? v : null;
    }

    private static List<string> ParseJsonStringArray(string json)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            var arr = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
            if (arr != null) return arr;
        }
        catch { }
        // 兼容单个字符串
        if (!json.StartsWith("["))
        {
            var t = json.Trim().Trim('"');
            if (!string.IsNullOrEmpty(t)) result.Add(t);
        }
        return result;
    }
}
