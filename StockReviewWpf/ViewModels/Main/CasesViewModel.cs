using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockReview.Core.Data;
using StockReviewWpf.ViewModels;

namespace StockReviewWpf.ViewModels.Main;

/// <summary>
/// 案例视图 ViewModel - 对应 CasesView.vue（一比一复刻）
/// </summary>
public partial class CasesViewModel : ObservableObject
{
    private const int PageSize = 30;

    private readonly IDatabaseService _db;
    private readonly ImageService _img;
    // 截图懒加载并发闸门：同时最多 4 路读盘
    private readonly SemaphoreSlim _shotSemaphore = new(4);

    // 进场类型树（父/子）
    private List<EntryTypeItem> _allEntryTypes = new();

    [ObservableProperty]
    private string _viewMode = "card";

    // 卡片网格自适应列数（对齐原版 grid auto-fill minmax(300,1fr)）
    [ObservableProperty]
    private int _cardColumns = 3;

    [ObservableProperty]
    private string _searchKeyword = "";

    [ObservableProperty]
    private string _selectedEntryType = "";

    [ObservableProperty]
    private string _selectedOutcome = "all"; // all / success / fail / calibration(卖点校准)

    [ObservableProperty]
    private string _sortBy = "change_desc";

    [ObservableProperty]
    private ObservableCollection<CaseItem> _filteredCases = new();

    // 列表视图：成功/失败分列（对齐原版 左右两列）
    [ObservableProperty]
    private ObservableCollection<CaseItem> _successCases = new();

    [ObservableProperty]
    private ObservableCollection<CaseItem> _failCases = new();

    [ObservableProperty]
    private CaseItem? _selectedCase;

    [ObservableProperty]
    private bool _isDetailVisible;

    [ObservableProperty]
    private ObservableCollection<TypeFilterOption> _entryTypeOptions = new();

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private bool _hasMore = true;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _previewImageUrl = "";

    [ObservableProperty]
    private bool _isImagePreviewVisible;

    [ObservableProperty]
    private double _imageScale = 1.0;

    public CasesViewModel(IDatabaseService db, ImageService img)
    {
        _db = db;
        _img = img;
        _ = LoadAsync();
    }

    /// <summary>
    /// 重新加载全部数据（标题栏"刷新"按钮调用；视图有缓存，导航不会重新加载）
    /// </summary>
    [RelayCommand]
    public async Task Reload() => await LoadAsync();

    private async System.Threading.Tasks.Task LoadAsync()
    {
        // 进场类型（仅 isActive）。DB 读取在后台线程
        var rows = await Task.Run(() => _db.GetAll("entryTypes"));
        _allEntryTypes = rows
            .Select(r => new EntryTypeItem
            {
                Id = ToInt(r, "id"),
                Name = S(r, "typeName"),
                SortOrder = ToInt(r, "sortOrder"),
                ParentId = r.TryGetValue("parentId", out var pid) && pid != null ? ToInt(r, "parentId") : (int?)null,
                IsActive = ToInt(r, "isActive") != 0
            })
            .Where(i => i.IsActive)
            .OrderBy(i => i.SortOrder)
            .ToList();

        BuildEntryTypeOptions();

        CurrentPage = 1;
        await ResetAndLoad();
    }

    private void BuildEntryTypeOptions()
    {
        var opts = new ObservableCollection<TypeFilterOption>
        {
            new() { Label = "全部类型", Value = "", IsParent = false }
        };
        var parents = _allEntryTypes.Where(t => !t.ParentId.HasValue).OrderBy(t => t.SortOrder).ToList();
        foreach (var p in parents)
        {
            var children = _allEntryTypes.Where(c => c.ParentId == p.Id).OrderBy(c => c.SortOrder).ToList();
            var childNames = children.Select(c => c.Name).ToList();
            var count = GetTypeCount(p.Name) + children.Sum(c => GetTypeCount(c.Name));
            opts.Add(new TypeFilterOption { Label = $"{p.Name} ({count})", Value = $"parent-{p.Name}", IsParent = true });
            foreach (var c in children)
                opts.Add(new TypeFilterOption { Label = $"{c.Name} ({GetTypeCount(c.Name)})", Value = c.Name, IsParent = false });
        }
        EntryTypeOptions = opts;
    }

    private Dictionary<string, int> _typeCounts = new();
    private Dictionary<string, int> GetTypeCounts()
    {
        if (_typeCounts.Count > 0) return _typeCounts;
        var rows = _db.GetAll("trades");
        foreach (var r in rows)
        {
            var ct = S(r, "caseType");
            if (string.IsNullOrEmpty(ct) || ct == "未归类") continue;
            var et = S(r, "entryType");
            if (string.IsNullOrEmpty(et)) continue;
            _typeCounts[et] = _typeCounts.TryGetValue(et, out var v) ? v + 1 : 1;
        }
        return _typeCounts;
    }
    private int GetTypeCount(string name) => GetTypeCounts().TryGetValue(name, out var v) ? v : 0;

    private List<string> GetChildTypeNames(string parentTypeName)
    {
        var p = _allEntryTypes.FirstOrDefault(t => !t.ParentId.HasValue && t.Name == parentTypeName);
        if (p == null) return new List<string>();
        return _allEntryTypes.Where(c => c.ParentId == p.Id).Select(c => c.Name).ToList();
    }

    private (List<CaseItem> data, int total) QueryCases(int page, int pageSize)
    {
        var all = _db.GetAll("trades");
        // 基础：案例非空且非"未归类"
        var q = all.Where(r =>
        {
            var ct = S(r, "caseType");
            return !string.IsNullOrEmpty(ct) && ct != "未归类";
        });

        // 案例类型
        if (SelectedOutcome == "success") q = q.Where(r => S(r, "caseType") == "成功案例");
        else if (SelectedOutcome == "fail") q = q.Where(r => S(r, "caseType") == "失败案例");
        else if (SelectedOutcome == "calibration")
            q = q.Where(r => { var f = S(r, "followUp"); return !string.IsNullOrEmpty(f) && f != "[]"; });

        // 进场类型
        if (!string.IsNullOrEmpty(SelectedEntryType))
        {
            if (SelectedEntryType.StartsWith("parent-"))
            {
                var pname = SelectedEntryType.Replace("parent-", "");
                var names = new List<string> { pname };
                names.AddRange(GetChildTypeNames(pname));
                q = q.Where(r => names.Contains(S(r, "entryType")));
            }
            else
            {
                q = q.Where(r => S(r, "entryType") == SelectedEntryType);
            }
        }

        // 关键词
        if (!string.IsNullOrWhiteSpace(SearchKeyword))
        {
            var kw = SearchKeyword.Trim();
            q = q.Where(r =>
                S(r, "stockCode").Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                S(r, "stockName").Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                (S(r, "remark") ?? "").Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                (S(r, "reflection") ?? "").Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        // 排序
        var list = q.ToList();
        if (SortBy == "change_desc") list = list.OrderByDescending(r => ParseDouble(S(r, "totalReturn"))).ToList();
        else if (SortBy == "change_asc") list = list.OrderBy(r => ParseDouble(S(r, "totalReturn"))).ToList();
        else if (SortBy == "date_asc") list = list.OrderBy(r => S(r, "tradeDate")).ToList();
        else list = list.OrderByDescending(r => S(r, "tradeDate")).ToList();

        var total = list.Count;
        var paged = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var items = paged.Select(MapCase).ToList();
        return (items, total);
    }

    private CaseItem MapCase(Dictionary<string, object?> r)
    {
        var item = new CaseItem
        {
            Id = ToInt(r, "id"),
            StockCode = S(r, "stockCode"),
            StockName = S(r, "stockName"),
            TradeDate = S(r, "tradeDate"),
            TotalReturn = FormatReturn(r),
            CaseType = S(r, "caseType"),
            EntryType = S(r, "entryType"),
            EntryPrice = FormatPrice(r, "entryPrice"),
            ExitPrice = FormatPrice(r, "exitPrice"),
            Note = S(r, "remark"),
            FollowUp = S(r, "followUp"),
            FollowUpDate = S(r, "followUpDate"),
            SellCalibrationHigh = FormatPrice(r, "sellCalibrationHigh"),
            SellCalibrationMaxChange = FormatReturn(r, "sellCalibrationMaxChange"),
            Reflection = S(r, "reflection"),
            Screenshot = S(r, "screenshot")
        };
        // 截图不在此读盘：卡片进入可视区时懒加载（见 RequestScreenshot）
        item.IsCalibrationTab = SelectedOutcome == "calibration";
        return item;
    }

    /// <summary>
    /// 截图懒加载：卡片进入可视区（Image Loaded / DataContextChanged）时触发，
    /// 只读当前看得见的截图；4 路并发读盘，读完经 INPC 补显到卡片。
    /// </summary>
    public void RequestScreenshot(CaseItem rec, bool openPreviewWhenDone = false)
    {
        if (string.IsNullOrEmpty(rec.Screenshot)) return;
        // data: 内联截图（历史遗留直接存库）：无需读盘，直接显示
        if (rec.Screenshot.StartsWith("data:")) { rec.DisplayScreenshot = rec.Screenshot; return; }
        if (rec.ScreenshotLoading || rec.DisplayScreenshot.Length > 0) return;
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
                        ImageScale = 1.0;
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

    /// <summary>内存治理（2026-09-06 v2）：导航离开（Unloaded）时清空截图字符串（同 DailyPickViewModel 说明）。
    /// 案例库为分页加载（FilteredCases），清空三个集合绑定的全部记录</summary>
    public void ClearTransientScreenshots()
    {
        foreach (var c in FilteredCases)
        {
            c.DisplayScreenshot = "";
            c.ScreenshotLoading = false;
        }
        foreach (var c in SuccessCases)
        {
            c.DisplayScreenshot = "";
            c.ScreenshotLoading = false;
        }
        foreach (var c in FailCases)
        {
            c.DisplayScreenshot = "";
            c.ScreenshotLoading = false;
        }
    }

    private async System.Threading.Tasks.Task ResetAndLoad()
    {
        CurrentPage = 1;
        await RunQuery(false);
    }

    private async System.Threading.Tasks.Task RunQuery(bool append)
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            // 查询 + 截图磁盘读取在后台线程（列表模式一次可达数百条，同步读会冻结 UI）
            var (data, total) = await Task.Run(() => QueryCases(CurrentPage, ViewMode == "list" ? 9999 : PageSize));
            if (append) foreach (var d in data) FilteredCases.Add(d);
            else { FilteredCases.Clear(); foreach (var d in data) FilteredCases.Add(d); }
            TotalCount = total;
            HasMore = FilteredCases.Count < total;

            // 列表模式：成功/失败分列（失败按收益升序，对齐原版 failCases.sort）
            var success = data.Where(c => c.CaseType == "成功案例").ToList();
            var fail = data.Where(c => c.CaseType == "失败案例").ToList();
            fail.Sort((a, b) =>
            {
                var av = double.TryParse(a.TotalReturn, out var x) ? x : 0;
                var bv = double.TryParse(b.TotalReturn, out var y) ? y : 0;
                return av.CompareTo(bv);
            });
            SuccessCases = new ObservableCollection<CaseItem>(success);
            FailCases = new ObservableCollection<CaseItem>(fail);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async System.Threading.Tasks.Task LoadMore()
    {
        if (!HasMore || IsLoading) return;
        CurrentPage++;
        await RunQuery(true);
    }

    // ===== Commands =====

    [RelayCommand]
    private void SwitchToCardView() => ViewMode = "card";

    [RelayCommand]
    private void SwitchToListView() => ViewMode = "list";

    private CancellationTokenSource? _searchCts;

    /// <summary>输入 300ms 防抖自动搜索（对齐原版 watch + debounce）</summary>
    partial void OnSearchKeywordChanged(string value)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _ = DebouncedSearch(cts.Token);
    }

    private async Task DebouncedSearch(CancellationToken token)
    {
        try
        {
            await Task.Delay(300, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }
        await ResetAndLoad();
    }

    [RelayCommand]
    private async Task Search() => await ResetAndLoad();

    [RelayCommand]
    private async Task ClearEntryTypeFilter()
    {
        SelectedEntryType = "";
        await ResetAndLoad();
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task ChangeOutcome(string outcome)
    {
        SelectedOutcome = outcome;
        await ResetAndLoad();
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task ChangeEntryType(string type) => await ResetAndLoad();

    [RelayCommand]
    private async System.Threading.Tasks.Task ChangeSort(string sort)
    {
        SortBy = sort;
        await ResetAndLoad();
    }

    [RelayCommand]
    private void ShowDetail(CaseItem item)
    {
        SelectedCase = item;
        IsDetailVisible = true;
        // 详情弹窗需要截图：立即触发读盘（INPC 补显后截图区自动出现）
        RequestScreenshot(item);
    }

    [RelayCommand]
    private void CloseDetail() => IsDetailVisible = false;

    [RelayCommand]
    private void LoadMoreCmd() => _ = LoadMore();

    [RelayCommand]
    private void PreviewImage(CaseItem item)
    {
        if (string.IsNullOrEmpty(item.DisplayScreenshot))
        {
            // 懒加载尚未完成时点击缩略图：立即触发读盘，完成后自动弹出大图预览
            if (!string.IsNullOrEmpty(item.Screenshot))
                RequestScreenshot(item, openPreviewWhenDone: true);
            return;
        }
        PreviewImageUrl = item.DisplayScreenshot;
        ImageScale = 1.0;
        IsImagePreviewVisible = true;
    }

    [RelayCommand]
    private void PreviewByUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        PreviewImageUrl = url;
        ImageScale = 1.0;
        IsImagePreviewVisible = true;
    }

    [RelayCommand]
    private void CloseImagePreview() => IsImagePreviewVisible = false;

    [RelayCommand]
    private void ZoomIn() { if (ImageScale < 3) ImageScale += 0.25; }

    [RelayCommand]
    private void ZoomOut() { if (ImageScale > 0.5) ImageScale -= 0.25; }

    [RelayCommand]
    private void ResetZoom() => ImageScale = 1.0;

    // ===== helpers =====

    private static string S(Dictionary<string, object?> r, string k) =>
        r.TryGetValue(k, out var v) && v != null ? v.ToString() ?? "" : "";

    private static int ToInt(Dictionary<string, object?> r, string k)
    {
        // is* 字段被数据层 DeserializeRecord 还原成 bool，需先按 bool 取值
        if (r.TryGetValue(k, out var bv) && bv is bool b) return b ? 1 : 0;
        var s = S(r, k);
        return int.TryParse(s, out var v) ? v : 0;
    }

    private static double ParseDouble(string s) => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string FormatReturn(Dictionary<string, object?> r, string field = "totalReturn")
    {
        var s = S(r, field);
        if (string.IsNullOrWhiteSpace(s)) return "";
        var d = ParseDouble(s);
        return d.ToString("F2", CultureInfo.InvariantCulture);
    }

    private static string FormatPrice(Dictionary<string, object?> r, string field)
    {
        var s = S(r, field);
        if (string.IsNullOrWhiteSpace(s)) return "";
        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return d.ToString("F2", CultureInfo.InvariantCulture);
        return s;
    }
}

public class TypeFilterOption
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public bool IsParent { get; set; }
}
