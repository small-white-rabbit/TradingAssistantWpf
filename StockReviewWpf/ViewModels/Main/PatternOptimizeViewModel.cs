using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockReview.Core.Data;
using StockReview.Core.MarketData;
using StockReviewWpf.Services;
using StockReviewWpf.ViewModels;

namespace StockReviewWpf.ViewModels.Main;

/// <summary>
/// 模式优化 ViewModel - 对应 PatternOptimizeView.vue（复刻版）
/// 结构：左侧类型导航 + 右侧双态（未选类型=模式概览 tile 列表；选中类型=详情+案例卡网格）
/// </summary>
public partial class PatternOptimizeViewModel : ObservableObject
{
    private readonly DatabaseService _db;
    private readonly ImageService _img;
    private readonly StockOcrService _ocr;
    private readonly MarketDataAggregator _market;
    private readonly MainViewModel? _mainVm;

    private List<EntryTypeItem> _allEntryTypes = new();
    private List<Dictionary<string, object?>> _allTrades = new();
    private List<Dictionary<string, object?>> _allPatternCases = new();
    private List<Dictionary<string, object?>> _allStrongStocks = new();
    private List<Dictionary<string, object?>> _allDailyPicks = new();

    private int? _selectedTypeId;

    // ===== 左侧类型导航 =====
    [ObservableProperty]
    private ObservableCollection<TypeNavItem> _typeNavItems = new();

    // ===== 概览模式 =====
    [ObservableProperty]
    private bool _isTypeSelected;

    [ObservableProperty]
    private ObservableCollection<PatternStat> _overviewList = new();

    [ObservableProperty]
    private int _overviewTotal;

    [ObservableProperty]
    private int _overviewAvgRate;

    // ===== 详情模式 =====
    [ObservableProperty]
    private string _selectedTypeName = "";

    [ObservableProperty]
    private string _selectedTypeDescription = "";

    [ObservableProperty]
    private int _detailSuccessCount;

    [ObservableProperty]
    private int _detailFailCount;

    [ObservableProperty]
    private int _detailSuccessRate;

    [ObservableProperty]
    private int _detailTotalCount;

    [ObservableProperty]
    private string _detailStandardForm = "";

    [ObservableProperty]
    private string _detailNotes = "";

    [ObservableProperty]
    private string _detailReflections = "";

    [ObservableProperty]
    private string _detailTypeImage = "";

    [ObservableProperty]
    private string _detailStandardFormImage = "";

    // 显示每日擒牛 / 每日强股开关（详情页 compare-header 内）
    [ObservableProperty]
    private bool _showDailyPicks;

    [ObservableProperty]
    private bool _showStrongStocks;

    // ===== 案例（成功/失败 + 对比选择） =====
    [ObservableProperty]
    private ObservableCollection<CaseItem> _successCases = new();

    [ObservableProperty]
    private ObservableCollection<CaseItem> _failCases = new();

    [ObservableProperty]
    private int _selectedCount;

    public bool CanCompare => SelectedCount == 2;

    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(CanCompare));

    // ===== 概览 tile 行内编辑 =====
    [ObservableProperty]
    private string _editingContent = "";

    // ===== 编辑模式信息弹窗 =====
    [ObservableProperty]
    private bool _isEditPatternVisible;

    [ObservableProperty]
    private string _editStandardFormImage = "";

    [ObservableProperty]
    private string _editTypeImage = "";

    [ObservableProperty]
    private string _editPlusItemsText = "";

    [ObservableProperty]
    private string _editMinusItemsText = "";

    [ObservableProperty]
    private string _editReflections = "";

    // ===== 从案例选择截图弹窗 =====
    [ObservableProperty]
    private bool _isSelectScreenshotVisible;

    [ObservableProperty]
    private string _selectScreenshotField = "";

    [ObservableProperty]
    private ObservableCollection<CaseItem> _casesWithScreenshots = new();

    // ===== 新增案例弹窗 =====
    [ObservableProperty]
    private bool _isAddCaseVisible;

    [ObservableProperty]
    private string _addCaseType = "成功案例";

    [ObservableProperty]
    private string _addStockCode = "";

    [ObservableProperty]
    private string _addStockName = "";

    [ObservableProperty]
    private string _addTradeDate = "";

    [ObservableProperty]
    private string _addTotalReturn = "";

    [ObservableProperty]
    private string _addReflection = "";

    [ObservableProperty]
    private string _addScreenshot = "";

    [ObservableProperty]
    private string _addScreenshotDisplay = "";

    [ObservableProperty]
    private bool _addFetching;

    [ObservableProperty]
    private string _addStatusText = "";

    // ===== 案例级对比弹窗 =====
    [ObservableProperty]
    private bool _isCaseCompareVisible;

    [ObservableProperty]
    private CaseItem? _compareLeft;

    [ObservableProperty]
    private CaseItem? _compareRight;

    // ===== 图片预览弹窗 =====
    [ObservableProperty]
    private bool _isImagePreviewVisible;

    [ObservableProperty]
    private string _previewImageUrl = "";

    public PatternOptimizeViewModel(DatabaseService db, ImageService img, StockOcrService ocr,
        MarketDataAggregator market, MainViewModel? mainVm = null)
    {
        _db = db;
        _img = img;
        _ocr = ocr;
        _market = market;
        _mainVm = mainVm;
        _ = LoadAsync();
    }

    /// <summary>
    /// 重新加载全部数据（标题栏"刷新"按钮调用；视图有缓存，导航不会重新加载）
    /// </summary>
    [RelayCommand]
    public async Task Reload() => await LoadAsync();

    private async Task LoadAsync()
    {
        // 阶段①：仅 DB 查询（快），立即 BuildNav/BuildOverview 显示文字内容
        var data = await Task.Run(() =>
        {
            var types = _db.GetAll("entryTypes");
            var trades = _db.GetAll("trades");
            var patternCases = _db.GetAll("patternCases");
            var strongStocks = _db.GetAll("strongStocks");
            var dailyPicks = _db.GetAll("dailyPicks");
            return (types, trades, patternCases, strongStocks, dailyPicks);
        });
        var (types, trades, patternCases, strongStocks, dailyPicks) = data;

        _allEntryTypes = types
            .Select(r => new EntryTypeItem
            {
                Id = ToInt(r, "id"),
                Name = S(r, "typeName"),
                SortOrder = ToInt(r, "sortOrder"),
                ParentId = r.TryGetValue("parentId", out var pid) && pid != null ? ToInt(r, "parentId") : (int?)null,
                IsStrongType = ToInt(r, "isStrongType") != 0,
                Description = S(r, "description"),
                IsActive = ToInt(r, "isActive") != 0,
                StandardForm = S(r, "standardForm"),
                Notes = S(r, "notes"),
                Reflections = S(r, "reflections"),
                PlusItems = S(r, "plusItems"),
                MinusItems = S(r, "minusItems"),
                TypeImage = S(r, "typeImage"),
                StandardFormImage = S(r, "standardFormImage")
            })
            .Where(t => t.IsActive)
            .OrderBy(t => t.SortOrder)
            .ToList();

        _allTrades = trades;
        _allPatternCases = patternCases;
        _allStrongStocks = strongStocks;
        _allDailyPicks = dailyPicks;

        BuildNav();
        BuildOverview();
        RefreshDetail();

        // 阶段②：后台预热截图缓存，读完自动刷新当前详情页
        _ = Task.Run(() =>
        {
            foreach (var r in trades.Concat(patternCases))
            {
                var path = S(r, "screenshot");
                if (!string.IsNullOrEmpty(path)) LoadCaseScreenshot(path);
            }
        });
    }

    /// <summary>截图路径级缓存：AllCases 每次重建都重新读盘是性能坑，缓存后仅首次读盘</summary>
    private readonly Dictionary<string, string> _caseScreenshotCache = new();

    private string LoadCaseScreenshot(string relativePath)
    {
        if (_caseScreenshotCache.TryGetValue(relativePath, out var hit)) return hit;
        var (ok, data, _) = _img.ReadImage(relativePath);
        var result = ok ? data : "";
        _caseScreenshotCache[relativePath] = result;
        return result;
    }

    /// <summary>合并所有案例（trades + patternCases，按开关附加每日强股/擒牛）</summary>
    private List<CaseItem> AllCases()
    {
        var list = new List<CaseItem>();
        foreach (var r in _allTrades.Where(r => !string.IsNullOrEmpty(S(r, "caseType")) && S(r, "caseType") != "未归类"))
            list.Add(MapCase(r, false));
        foreach (var r in _allPatternCases)
            list.Add(MapCase(r, true));

        if (ShowStrongStocks)
        {
            foreach (var r in _allStrongStocks)
            {
                list.Add(new CaseItem
                {
                    Id = -ToInt(r, "id") - 100000,
                    StockCode = S(r, "stockCode"),
                    StockName = S(r, "stockName"),
                    TradeDate = S(r, "date"),
                    TotalReturn = FormatReturn(r, "maxChangePct"),
                    CaseType = "成功案例",
                    EntryType = S(r, "strongType") is { Length: > 0 } st ? st : "其他强势"
                });
            }
        }

        if (ShowDailyPicks)
        {
            foreach (var r in _allDailyPicks)
            {
                var changeRaw = S(r, "nextDayMaxChange");
                if (!double.TryParse(changeRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var change)) continue;
                var caseType = change >= 5 ? "成功案例" : (change <= -3 ? "失败案例" : null);
                if (caseType == null) continue;
                list.Add(new CaseItem
                {
                    Id = -ToInt(r, "id") - 200000,
                    StockCode = S(r, "stockCode"),
                    StockName = S(r, "stockName"),
                    TradeDate = S(r, "pickDate"),
                    TotalReturn = change.ToString("F2", CultureInfo.InvariantCulture),
                    CaseType = caseType,
                    EntryType = S(r, "pickType") is { Length: > 0 } pt ? pt : "其他"
                });
            }
        }

        return list;
    }

    // ===== 左侧导航构建 =====
    private void BuildNav()
    {
        var cases = AllCases();
        var items = new ObservableCollection<TypeNavItem>();
        var parents = _allEntryTypes.Where(t => !t.ParentId.HasValue).OrderBy(t => t.SortOrder).ToList();
        var parentIds = parents.Select(p => p.Id).ToHashSet();

        foreach (var p in parents)
        {
            var childNames = _allEntryTypes.Where(t => t.ParentId == p.Id).Select(t => t.Name).ToList();
            items.Add(new TypeNavItem
            {
                Id = p.Id,
                TypeName = p.Name,
                TotalCount = cases.Count(c => c.EntryType == p.Name || childNames.Contains(c.EntryType)),
                IsStrongType = p.IsStrongType
            });
            foreach (var c in _allEntryTypes.Where(t => t.ParentId == p.Id).OrderBy(t => t.SortOrder))
                items.Add(new TypeNavItem
                {
                    Id = c.Id,
                    TypeName = c.Name,
                    IsChild = true,
                    TotalCount = cases.Count(x => x.EntryType == c.Name),
                    IsStrongType = c.IsStrongType
                });
        }

        // 没有父级的孤立子类型
        foreach (var o in _allEntryTypes.Where(t => t.ParentId.HasValue && !parentIds.Contains(t.ParentId.Value)).OrderBy(t => t.SortOrder))
            items.Add(new TypeNavItem
            {
                Id = o.Id,
                TypeName = o.Name,
                TotalCount = cases.Count(x => x.EntryType == o.Name),
                IsStrongType = o.IsStrongType
            });

        // 开启每日强股时追加强势类型分组（无真实 ID，点击回到概览）
        if (ShowStrongStocks && _allStrongStocks.Count > 0)
        {
            foreach (var st in _allStrongStocks.Select(s => S(s, "strongType")).Where(n => n.Length > 0).Distinct())
                items.Add(new TypeNavItem
                {
                    Id = 0,
                    TypeName = st,
                    TotalCount = cases.Count(c => c.Id < 0 && c.EntryType == st),
                    IsStrongType = true
                });
        }

        TypeNavItems = items;
        SyncNavSelection();
    }

    private void SyncNavSelection()
    {
        foreach (var item in TypeNavItems)
            item.IsSelected = _selectedTypeId.HasValue && item.Id == _selectedTypeId.Value;
    }

    // ===== 概览构建 =====
    private void BuildOverview()
    {
        var overview = new ObservableCollection<PatternStat>();
        var added = new HashSet<int>();
        var active = _allEntryTypes.Where(t => t.IsActive).OrderBy(t => t.SortOrder).ToList();

        // 只展示子类型（按父子层级排序），再补孤立子类型
        foreach (var parent in active.Where(t => !t.ParentId.HasValue))
            foreach (var child in active.Where(t => t.ParentId == parent.Id))
                if (added.Add(child.Id))
                    overview.Add(BuildOverviewStat(child));
        foreach (var type in active.Where(t => t.ParentId.HasValue))
            if (added.Add(type.Id))
                overview.Add(BuildOverviewStat(type));

        OverviewList = overview;

        var allSuccess = overview.Sum(o => o.SuccessCount);
        var allFail = overview.Sum(o => o.FailCount);
        OverviewTotal = overview.Count;
        OverviewAvgRate = allSuccess + allFail > 0 ? (int)Math.Round((double)allSuccess / (allSuccess + allFail) * 100) : 0;
    }

    private PatternStat BuildOverviewStat(EntryTypeItem type)
    {
        var stat = ComputeTypeStat(type.Name);
        var typeCases = AllCases().Where(c => c.EntryType == type.Name).ToList();
        var topSuccess = typeCases.Where(c => c.CaseType == "成功案例")
            .OrderByDescending(c => ParseDouble(c.TotalReturn))
            .Take(5)
            .Select(ToBrief).ToList();
        var topFail = typeCases.Where(c => c.CaseType == "失败案例")
            .OrderBy(c => ParseDouble(c.TotalReturn))
            .Take(5)
            .Select(ToBrief).ToList();

        return new PatternStat
        {
            Id = type.Id,
            TypeName = type.Name,
            Description = type.Description ?? "",
            IsParent = !type.ParentId.HasValue,
            IsStrongType = type.IsStrongType,
            TypeImage = type.TypeImage,
            StandardFormImage = type.StandardFormImage,
            Reflections = NullWhenEmpty(type.Reflections),
            PlusItems = SplitLines(type.PlusItems),
            MinusItems = SplitLines(type.MinusItems),
            TopSuccessCases = topSuccess,
            TopFailCases = topFail,
            TotalCount = stat.total,
            SuccessCount = stat.success,
            FailCount = stat.fail,
            SuccessRate = stat.rate.ToString()
        };
    }

    private static PatternCaseBrief ToBrief(CaseItem c) => new()
    {
        Id = c.Id,
        StockName = c.StockName,
        StockCode = c.StockCode,
        TradeDate = c.TradeDate,
        MaxChangePct = ParseDouble(c.TotalReturn),
        IsCustom = c.IsCustom,
        EntryType = c.EntryType
    };

    private (int success, int fail, int total, int rate) ComputeTypeStat(string typeName)
    {
        var all = AllCases().Where(c => c.EntryType == typeName).ToList();
        var success = all.Count(c => c.CaseType == "成功案例");
        var fail = all.Count(c => c.CaseType == "失败案例");
        var total = success + fail;
        var rate = total > 0 ? (int)Math.Round((double)success / total * 100) : 0;
        return (success, fail, total, rate);
    }

    private static List<string> SplitLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    // ===== 详情加载 =====
    private void RefreshDetail()
    {
        var type = _selectedTypeId.HasValue
            ? _allEntryTypes.FirstOrDefault(t => t.Id == _selectedTypeId.Value)
            : null;
        LoadTypeDetail(type);
    }

    private void LoadTypeDetail(EntryTypeItem? type)
    {
        SelectedTypeName = type?.Name ?? "";
        SelectedTypeDescription = type?.Description ?? "";
        DetailStandardForm = NullWhenEmpty(type?.StandardForm) ?? "";
        DetailNotes = NullWhenEmpty(type?.Notes) ?? "";
        DetailReflections = NullWhenEmpty(type?.Reflections) ?? "";
        DetailTypeImage = type?.TypeImage ?? "";
        DetailStandardFormImage = type?.StandardFormImage ?? "";

        SuccessCases.Clear();
        FailCases.Clear();
        if (type != null)
        {
            var stat = ComputeTypeStat(type.Name);
            DetailSuccessCount = stat.success;
            DetailFailCount = stat.fail;
            DetailTotalCount = stat.total;
            DetailSuccessRate = stat.rate;

            foreach (var c in AllCases().Where(c => c.EntryType == type.Name))
            {
                c.IsInCompare = SelectedCasesInternal.Any(s => s.Id == c.Id && s.IsCustom == c.IsCustom);
                if (c.CaseType == "成功案例") SuccessCases.Add(c);
                else if (c.CaseType == "失败案例") FailCases.Add(c);
            }
        }
        else
        {
            DetailSuccessCount = 0;
            DetailFailCount = 0;
            DetailTotalCount = 0;
            DetailSuccessRate = 0;
        }
    }

    private readonly List<CaseItem> SelectedCasesInternal = new();

    private CaseItem MapCase(Dictionary<string, object?> r, bool isCustom)
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
            Note = S(r, "remark") ?? S(r, "reflection"),
            Reflection = S(r, "reflection"),
            Screenshot = S(r, "screenshot"),
            IsCustom = isCustom
        };
        if (!string.IsNullOrEmpty(item.Screenshot))
        {
            item.DisplayScreenshot = LoadCaseScreenshot(item.Screenshot);
        }
        return item;
    }

    private void RefreshAll()
    {
        BuildNav();
        BuildOverview();
        RefreshDetail();
    }

    // ===== Commands =====

    [RelayCommand]
    private void SelectTypeItem(TypeNavItem? item)
    {
        if (item == null) return;
        if (item.Id == 0)
        {
            ShowOverview();
            return;
        }
        _selectedTypeId = item.Id;
        IsTypeSelected = true;
        SyncNavSelection();
        RefreshDetail();
    }

    [RelayCommand]
    private void ShowOverview()
    {
        _selectedTypeId = null;
        IsTypeSelected = false;
        SyncNavSelection();
        RefreshDetail();
    }

    [RelayCommand]
    private void SelectOverview(PatternStat? stat)
    {
        if (stat == null) return;
        _selectedTypeId = stat.Id;
        IsTypeSelected = true;
        SyncNavSelection();
        RefreshDetail();
    }

    [RelayCommand]
    private void ToggleDailyPicks()
    {
        ShowDailyPicks = !ShowDailyPicks;
        RefreshAll();
    }

    [RelayCommand]
    private void ToggleStrongStocks()
    {
        ShowStrongStocks = !ShowStrongStocks;
        RefreshAll();
    }

    // ===== 概览 tile 行内编辑（加分项/减分项/思考） =====
    public void StartTileEdit(PatternStat? stat, string field)
    {
        if (stat == null) return;
        CancelTileEdit();
        EditingContent = field switch
        {
            "plus" => string.Join("\n", stat.PlusItems),
            "minus" => string.Join("\n", stat.MinusItems),
            _ => RichTextUtil.ToPlain(stat.Reflections ?? "")
        };
        stat.EditingField = field;
    }

    [RelayCommand]
    private void CancelTileEdit()
    {
        foreach (var s in OverviewList)
            if (s.EditingField.Length > 0) s.EditingField = "";
        EditingContent = "";
    }

    [RelayCommand]
    private void SaveTileEdit()
    {
        var stat = OverviewList.FirstOrDefault(s => s.EditingField.Length > 0);
        if (stat == null) return;
        var field = stat.EditingField;
        var type = _allEntryTypes.FirstOrDefault(t => t.Id == stat.Id);
        if (type != null)
        {
            var content = (EditingContent ?? "").Trim();
            var plusLines = SplitLines(type.PlusItems).Select(l => "+ " + l).ToList();
            var minusLines = SplitLines(type.MinusItems).Select(l => "- " + l).ToList();
            if (field == "plus")
            {
                type.PlusItems = content;
                plusLines = SplitLines(content).Select(l => "+ " + l).ToList();
            }
            else if (field == "minus")
            {
                type.MinusItems = content;
                minusLines = SplitLines(content).Select(l => "- " + l).ToList();
            }
            else
            {
                type.Reflections = content;
            }
            var notes = string.Join("\n", plusLines.Concat(minusLines));
            type.Notes = notes;
            _db.Update("entryTypes", type.Id, new Dictionary<string, object?>
            {
                ["plusItems"] = type.PlusItems,
                ["minusItems"] = type.MinusItems,
                ["reflections"] = type.Reflections,
                ["notes"] = notes
            });
        }
        CancelTileEdit();
        BuildOverview();
    }

    // ===== 编辑模式信息 =====
    [RelayCommand]
    private void EditPatternInfo()
    {
        var type = _selectedTypeId.HasValue ? _allEntryTypes.FirstOrDefault(t => t.Id == _selectedTypeId.Value) : null;
        if (type == null) return;
        EditStandardFormImage = type.StandardFormImage ?? "";
        EditTypeImage = type.TypeImage ?? "";
        EditPlusItemsText = string.Join("\n", SplitLines(type.PlusItems));
        EditMinusItemsText = string.Join("\n", SplitLines(type.MinusItems));
        EditReflections = RichTextUtil.ToPlain(type.Reflections ?? "");
        IsEditPatternVisible = true;
    }

    [RelayCommand]
    private void SavePatternInfo()
    {
        var type = _selectedTypeId.HasValue ? _allEntryTypes.FirstOrDefault(t => t.Id == _selectedTypeId.Value) : null;
        if (type == null) return;
        var notes = string.Join("\n",
            SplitLines(EditPlusItemsText).Select(l => "+ " + l)
                .Concat(SplitLines(EditMinusItemsText).Select(l => "- " + l)));
        _db.Update("entryTypes", type.Id, new Dictionary<string, object?>
        {
            ["standardFormImage"] = EditStandardFormImage,
            ["typeImage"] = EditTypeImage,
            ["plusItems"] = EditPlusItemsText,
            ["minusItems"] = EditMinusItemsText,
            ["reflections"] = EditReflections,
            ["notes"] = notes
        });
        type.StandardFormImage = EditStandardFormImage;
        type.TypeImage = EditTypeImage;
        type.PlusItems = EditPlusItemsText;
        type.MinusItems = EditMinusItemsText;
        type.Reflections = EditReflections;
        type.Notes = notes;
        IsEditPatternVisible = false;
        BuildOverview();
        RefreshDetail();
    }

    [RelayCommand]
    private void CancelEditPattern() => IsEditPatternVisible = false;

    [RelayCommand]
    private void ClearEditStandardForm() => EditStandardFormImage = "";

    [RelayCommand]
    private void ClearEditTypeImage() => EditTypeImage = "";

    // 粘贴标准形态截图（剪贴板 → base64，对齐原版）
    [RelayCommand]
    private void PasteEditStandard()
    {
        if (ClipboardImageToBase64(out var base64)) EditStandardFormImage = base64;
    }

    [RelayCommand]
    private void PasteEditTypeImage()
    {
        if (ClipboardImageToBase64(out var base64)) EditTypeImage = base64;
    }

    // ===== 从案例选择截图 =====
    [RelayCommand]
    private void OpenSelectScreenshot(string? field)
    {
        SelectScreenshotField = field ?? "standardFormImage";
        var currentType = SelectedTypeName;
        if (string.IsNullOrEmpty(currentType)) return;
        var cases = AllCases()
            .Where(c => c.EntryType == currentType && !string.IsNullOrEmpty(c.DisplayScreenshot))
            .Take(60)
            .ToList();
        if (cases.Count == 0) return;
        CasesWithScreenshots = new ObservableCollection<CaseItem>(cases);
        IsSelectScreenshotVisible = true;
    }

    [RelayCommand]
    private void SelectScreenshot(CaseItem? item)
    {
        if (item == null || string.IsNullOrEmpty(item.DisplayScreenshot)) return;
        if (SelectScreenshotField == "typeImage") EditTypeImage = item.DisplayScreenshot;
        else EditStandardFormImage = item.DisplayScreenshot;
        IsSelectScreenshotVisible = false;
    }

    [RelayCommand]
    private void CloseSelectScreenshot() => IsSelectScreenshotVisible = false;

    // ===== 案例勾选与对比 =====
    [RelayCommand]
    private void ToggleCaseSelect(CaseItem? item)
    {
        if (item == null) return;
        var existing = SelectedCasesInternal.FirstOrDefault(c => c.Id == item.Id && c.IsCustom == item.IsCustom);
        if (existing != null)
        {
            SelectedCasesInternal.Remove(existing);
            existing.IsInCompare = false;
        }
        else if (SelectedCasesInternal.Count < 2)
        {
            SelectedCasesInternal.Add(item);
            item.IsInCompare = true;
        }
        SelectedCount = SelectedCasesInternal.Count;
    }

    [RelayCommand]
    private void OpenCaseCompare()
    {
        if (SelectedCasesInternal.Count != 2) return;
        CompareLeft = SelectedCasesInternal[0];
        CompareRight = SelectedCasesInternal[1];
        IsCaseCompareVisible = true;
    }

    [RelayCommand]
    private void CloseCaseCompare() => IsCaseCompareVisible = false;

    [RelayCommand]
    private void ClearCaseSelection()
    {
        foreach (var c in SelectedCasesInternal) c.IsInCompare = false;
        SelectedCasesInternal.Clear();
        SelectedCount = 0;
    }

    [RelayCommand]
    private void DeleteCustomCase(CaseItem? item)
    {
        if (item == null || !item.IsCustom) return;
        var box = MessageBox.Show(
            $"确定要删除案例「{item.StockName}」吗？",
            "删除确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (box != MessageBoxResult.Yes) return;
        _db.Delete("patternCases", item.Id);
        _ = LoadAsync();
    }

    // ===== 新增案例 =====
    [RelayCommand]
    private void ShowAddCase(string? caseType)
    {
        AddCaseType = string.IsNullOrEmpty(caseType) ? "成功案例" : caseType;
        AddStockCode = "";
        AddStockName = "";
        AddTradeDate = DateTime.Now.ToString("yyyy-MM-dd");
        AddTotalReturn = "";
        AddReflection = "";
        AddScreenshot = "";
        AddScreenshotDisplay = "";
        AddStatusText = "";
        IsAddCaseVisible = true;
    }

    [RelayCommand]
    private void CloseAddCase() => IsAddCaseVisible = false;

    [RelayCommand]
    private async Task FetchStockInfo()
    {
        var code = (AddStockCode ?? "").Trim();
        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            AddStatusText = "请输入 6 位数字代码";
            return;
        }
        AddFetching = true;
        try
        {
            var data = await StockMarketService.Fetch(_ocr, _market, code, AddTradeDate);
            if (data != null && !string.IsNullOrEmpty(data.Name))
            {
                AddStockName = data.Name;
                AddStatusText = data.Source;
            }
            else
            {
                var nm = await Task.Run(() => _ocr.GetNameByCode(code));
                if (!string.IsNullOrEmpty(nm)) AddStockName = nm;
                AddStatusText = "未能获取名称，请手动填写";
            }
        }
        catch (Exception ex)
        {
            AddStatusText = "获取失败: " + ex.Message;
        }
        finally
        {
            AddFetching = false;
        }
    }

    [RelayCommand]
    private async Task PasteAddScreenshot()
    {
        if (!ClipboardImageToBase64(out var base64)) return;
        var (ok, path, _) = _img.SaveImage(base64);
        if (ok)
        {
            AddScreenshot = path ?? "";
            AddScreenshotDisplay = base64;
        }

        // 对齐 Electron PatternOptimizeView.performOCR：粘贴截图后自动 OCR 识别回填股票代码，
        // 再调 FetchStockInfo 获取名称（Electron 的 fetchStockInfoWithReturn）。
        // 复用统一双通道识别模块（百度优先、失败降级本地 Tesseract），与其余页面一致。
        try
        {
            var result = await _ocr.RecognizeStockCodeAsync(base64);
            if (result.Success && result.Code.Length == 6)
            {
                AddStockCode = result.Code;
                if (!string.IsNullOrEmpty(result.Name)) AddStockName = result.Name;
                await FetchStockInfo();
            }
            else
            {
                AddStatusText = "未识别到股票代码，请手动输入";
            }
        }
        catch (Exception ex)
        {
            AddStatusText = "OCR 识别失败: " + ex.Message;
        }
    }

    [RelayCommand]
    private void ClearAddScreenshot()
    {
        AddScreenshot = "";
        AddScreenshotDisplay = "";
    }

    [RelayCommand]
    private void SaveAddCase()
    {
        if (string.IsNullOrWhiteSpace(AddStockCode) || string.IsNullOrWhiteSpace(AddStockName))
        {
            AddStatusText = "请填写股票代码和名称";
            return;
        }
        var dict = new Dictionary<string, object?>
        {
            ["entryType"] = SelectedTypeName,
            ["caseType"] = AddCaseType,
            ["stockCode"] = AddStockCode.Trim(),
            ["stockName"] = AddStockName.Trim(),
            ["tradeDate"] = AddTradeDate,
            ["totalReturn"] = ParseDouble(AddTotalReturn),
            ["screenshot"] = AddScreenshot,
            ["reflection"] = AddReflection,
            ["createdAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["updatedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };
        _db.Add("patternCases", dict);
        IsAddCaseVisible = false;
        _ = LoadAsync();
    }

    // ===== 图片预览 =====
    [RelayCommand]
    private void OpenImagePreview(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        PreviewImageUrl = url;
        IsImagePreviewVisible = true;
    }

    [RelayCommand]
    private void CloseImagePreview() => IsImagePreviewVisible = false;

    // ===== 概览 tile 案例排行跳转 =====
    [RelayCommand]
    private void GoToCaseDetail(PatternCaseBrief? brief)
    {
        if (brief == null) return;
        if (brief.IsCustom)
        {
            // 自定义案例：选中其进场类型
            var type = _allEntryTypes.FirstOrDefault(t => t.Name == brief.EntryType);
            if (type != null)
            {
                _selectedTypeId = type.Id;
                IsTypeSelected = true;
                SyncNavSelection();
                RefreshDetail();
            }
        }
        else
        {
            // 案例库案例：跳转交易记录（按交易日期）
            _mainVm?.NavigateToYearMonthWithDate(brief.TradeDate);
        }
    }

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
        return ParseDouble(s).ToString("F2", CultureInfo.InvariantCulture);
    }

    private static string? NullWhenEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static bool ClipboardImageToBase64(out string base64)
    {
        base64 = "";
        try
        {
            if (!Clipboard.ContainsImage()) return false;
            var bmp = Clipboard.GetImage();
            if (bmp == null) return false;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new System.IO.MemoryStream();
            encoder.Save(ms);
            base64 = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            return true;
        }
        catch
        {
            return false;
        }
    }
}
