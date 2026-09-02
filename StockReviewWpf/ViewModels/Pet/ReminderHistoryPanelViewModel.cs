using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockReview.Core.Services;
using StockReviewWpf.Models;

namespace StockReviewWpf.ViewModels.Pet;

/// <summary>
/// 提醒历史面板 ViewModel - 对应 ReminderHistoryPanel.vue
/// 从 ReminderHistoryService 加载真实数据
/// 性能：限量加载最近 300 条 + 扁平化分组列表（配合 UI 虚拟化，避免打开面板全量渲染卡顿）
/// </summary>
public partial class ReminderHistoryPanelViewModel : ObservableObject
{
    private readonly ReminderHistoryService? _historyService;

    /// <summary>单次加载上限：3 天保留期内极端数据量时防止 UI 卡顿</summary>
    private const int MaxLoadRecords = 300;

    [ObservableProperty]
    private string _filterType = "all";

    [ObservableProperty]
    private string _filterLevel = "all";

    [ObservableProperty]
    private string _filterResponse = "all";

    // 下拉筛选即时生效（原版 el-select v-model → computed 过滤）
    partial void OnFilterTypeChanged(string value) => ApplyFilter();
    partial void OnFilterLevelChanged(string value) => ApplyFilter();
    partial void OnFilterResponseChanged(string value) => ApplyFilter();

    [ObservableProperty]
    private ObservableCollection<ReminderHistoryRecord> _allRecords = new();

    /// <summary>渲染用扁平列表：日期分组头 + 记录混排（单层列表才能启用 UI 虚拟化）</summary>
    [ObservableProperty]
    private ObservableCollection<object> _flatRecords = new();

    // 统计卡片 (对齐原版: 总提醒/今日/已响应/未响应)
    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _todayCount;

    [ObservableProperty]
    private int _respondedCount;

    [ObservableProperty]
    private int _unrespondedCount;

    [ObservableProperty]
    private int _filteredCount;

    [ObservableProperty]
    private ReminderHistoryRecord? _selectedRecord;

    [ObservableProperty]
    private bool _isDetailVisible;

    /// <summary>是否有数据</summary>
    public bool HasData => AllRecords.Count > 0;

    public ReminderHistoryPanelViewModel() : this(null) { }

    public ReminderHistoryPanelViewModel(ReminderHistoryService? historyService)
    {
        _historyService = historyService;
        LoadFromService();
        UpdateStats();
    }

    /// <summary>从 ReminderHistoryService 加载真实历史数据（限量最近 300 条，整表替换减少 UI 通知）</summary>
    public void LoadFromService()
    {
        if (_historyService == null) return;
        try
        {
            var source = _historyService.History;
            var loaded = new List<ReminderHistoryRecord>(Math.Min(MaxLoadRecords, source.Count));
            foreach (var r in source.Take(MaxLoadRecords))
            {
                if (r == null) continue; // 备份导入可能混入 null 元素，直接跳过
                loaded.Add(new ReminderHistoryRecord
                {
                    Id = r.Id,
                    DateStr = r.DateStr,
                    Timestamp = r.Timestamp,
                    Type = r.Type,
                    Level = r.Level,
                    Title = r.Title,
                    Content = r.Content,
                    StockCode = r.StockCode,
                    StockName = r.StockName,
                    UserResponse = r.UserResponse,
                    ResponseTime = r.ResponseTime
                });
            }
            AllRecords = new ObservableCollection<ReminderHistoryRecord>(loaded);
            ApplyFilter();
            // 打开面板 RefreshData 只调本方法：统计必须在此刷新，否则停留在构造时数字
            UpdateStats();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[ReminderHistoryPanel] 加载历史数据失败");
        }
    }

    [RelayCommand]
    private void FilterByType(string type)
    {
        FilterType = type;
        ApplyFilter();
    }

    [RelayCommand]
    private void FilterByLevel(string level)
    {
        FilterLevel = level;
        ApplyFilter();
    }

    [RelayCommand]
    private void FilterByResponse(string response)
    {
        FilterResponse = response;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filtered = AllRecords.AsEnumerable();
        if (FilterType != "all")
            filtered = filtered.Where(r => r.Type == FilterType);
        if (FilterLevel != "all")
            filtered = filtered.Where(r => r.Level == FilterLevel);
        // 空字符串响应视为未响应（与统计口径一致：JS falsy 语义）
        if (FilterResponse == "responded")
            filtered = filtered.Where(r => !string.IsNullOrEmpty(r.UserResponse));
        else if (FilterResponse == "unresponded")
            filtered = filtered.Where(r => string.IsNullOrEmpty(r.UserResponse));

        // 扁平化：日期分组头 + 记录混排成单层列表（UI 虚拟化的前提）
        var flat = new List<object>();
        foreach (var g in filtered.GroupBy(r => r.DateStr).OrderByDescending(g => g.Key))
        {
            var records = g.OrderByDescending(r => r.Timestamp).ToList();
            flat.Add(new ReminderDateHeader
            {
                DateStr = g.Key,
                DateLabel = FormatDateLabel(g.Key),
                RecordCount = records.Count
            });
            flat.AddRange(records);
        }
        FlatRecords = new ObservableCollection<object>(flat);
        FilteredCount = flat.OfType<ReminderHistoryRecord>().Count();
        UpdateStats();
    }

    private static string FormatDateLabel(string dateStr)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");
        if (dateStr == today) return "📅 今天";
        if (dateStr == yesterday) return "📅 昨天";
        try
        {
            var parts = dateStr.Split('-');
            return $"{parts[0]}年{int.Parse(parts[1])}月{int.Parse(parts[2])}日";
        }
        catch { return dateStr; }
    }

    private void UpdateStats()
    {
        // 统计基于服务端全量历史（对齐原版 history.length 等全量 computed），
        // 面板列表仅加载最近 300 条（UI 性能），用截断列表统计会导致数字偏小
        if (_historyService != null)
        {
            TotalCount = _historyService.TotalCount;
            TodayCount = _historyService.TodayCount;
            UnrespondedCount = _historyService.UnrespondedCount;
            RespondedCount = _historyService.RespondedCount;
        }
        else
        {
            var today = TradePlanService.FormatLocalDate(DateTime.Now);
            TotalCount = AllRecords.Count;
            TodayCount = AllRecords.Count(r => r.DateStr == today);
            // JS falsy 语义：空字符串响应视为未响应（对应 !h.userResponse）
            RespondedCount = AllRecords.Count(r => !string.IsNullOrEmpty(r.UserResponse));
            UnrespondedCount = TotalCount - RespondedCount;
        }
        OnPropertyChanged(nameof(HasData));
    }

    [RelayCommand]
    private void ShowDetail(ReminderHistoryRecord record)
    {
        SelectedRecord = record;
        IsDetailVisible = true;
    }

    [RelayCommand]
    private void CloseDetail()
    {
        IsDetailVisible = false;
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _historyService?.ClearAll();
        AllRecords = new ObservableCollection<ReminderHistoryRecord>();
        FlatRecords = new ObservableCollection<object>();
        UpdateStats();
    }
}

/// <summary>
/// 提醒历史日期分组头（扁平列表中的分隔项）
/// </summary>
public class ReminderDateHeader
{
    public string DateStr { get; set; } = "";
    public string DateLabel { get; set; } = "";
    public int RecordCount { get; set; }
}