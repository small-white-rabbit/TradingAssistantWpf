using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using StockReviewWpf.Models;
using TradePlan = StockReviewWpf.Models.TradePlan;
using TradePlanService = StockReview.Core.Services.TradePlanService;

namespace StockReviewWpf.ViewModels.Pet;

/// <summary>
/// 计划列表面板 ViewModel - 对应 PlanListPanel.vue
/// 从 TradePlanService 加载真实数据（持久化于 appConfig.pet_trade_plans）
/// </summary>
public partial class PlanListPanelViewModel : ObservableObject
{
    private readonly TradePlanService? _planService;

    [ObservableProperty]
    private string _filterStatus = "all";

    [ObservableProperty]
    private ObservableCollection<TradePlan> _plans = new();

    [ObservableProperty]
    private ObservableCollection<TradePlan> _filteredPlans = new();

    [ObservableProperty]
    private TradePlan? _selectedPlan;

    [ObservableProperty]
    private bool _isEditFormVisible;

    [ObservableProperty]
    private bool _isCancelFormVisible;

    [ObservableProperty]
    private TradePlan? _editingPlan;

    [ObservableProperty]
    private string _cancelReason = "";

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private int _todayCount;

    [ObservableProperty]
    private int _executedCount;

    [ObservableProperty]
    private int _cancelledCount;

    /// <summary>15 行空白占位符（无数据时显示）</summary>
    public ObservableCollection<PlaceholderRow> PlaceholderItems { get; } = new();

    /// <summary>是否有数据</summary>
    public bool HasData => FilteredPlans.Count > 0;

    /// <summary>请求打开分时图（点击股票名，由面板 code-behind 转发给宠物窗口）</summary>
    public event Action<string>? OpenIntradayChartRequested;

    public PlanListPanelViewModel() : this(null) { }

    public PlanListPanelViewModel(TradePlanService? planService)
    {
        _planService = planService;
        for (int i = 0; i < 15; i++)
            PlaceholderItems.Add(new PlaceholderRow());
        LoadFromService();
    }

    /// <summary>从 TradePlanService 加载真实计划并刷新视图</summary>
    public void LoadFromService()
    {
        Plans.Clear();
        if (_planService != null)
        {
            try
            {
                foreach (var p in _planService.Plans)
                {
                    Plans.Add(new TradePlan
                    {
                        Id = p.Id,
                        PlanDate = p.PlanDate,
                        PlanType = p.PlanType,
                        Status = p.Status,
                        ExecutionStatus = p.ExecutionStatus,
                        StockCode = p.StockCode,
                        StockName = p.StockName,
                        EntryReason = p.EntryReason ?? "",
                        EntryPrice = p.EntryPrice,
                        TargetPrice = p.TargetPrice,
                        StopLoss = p.StopLoss,
                        MaxHoldDays = p.MaxHoldDays,
                        Note = p.Note ?? "",
                        CreatedAt = DateTime.TryParse(p.CreatedAt, out var t) ? t : DateTime.MinValue
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[PlanList] 加载交易计划失败");
            }
        }
        ApplyFilter();
        UpdateCounts();
        Log.Information("[PlanList] 加载 {Total} 条计划，筛选 {Filter} 后显示 {Shown} 条",
            Plans.Count, FilterStatus, FilteredPlans.Count);
    }

    [RelayCommand]
    public void FilterByStatus(string status)
    {
        FilterStatus = status;
        ApplyFilter();
    }

    /// <summary>筛选逻辑对齐原版：today=按计划日期，其余按状态；排序 待执行(0) > 已执行(1) > 过期/取消(2)</summary>
    private void ApplyFilter()
    {
        var today = TradePlanService.FormatLocalDate(DateTime.Now);
        var filtered = Plans.AsEnumerable();
        if (FilterStatus == "today")
            filtered = filtered.Where(p => p.PlanDate == today);
        else if (FilterStatus != "all")
            filtered = filtered.Where(p => p.Status == FilterStatus);

        var sorted = filtered
            .OrderBy(PlanListPanelViewModel.StatusOrder)
            .ThenByDescending(p => p.CreatedAt);
        FilteredPlans.Clear();
        foreach (var p in sorted)
            FilteredPlans.Add(p);
        OnPropertyChanged(nameof(HasData));
        UpdatePlaceholderCount(FilteredPlans.Count);
    }

    /// <summary>数据行不足 15 行时用空白占位补齐（el-table 固定高度观感）</summary>
    private void UpdatePlaceholderCount(int shownRows)
    {
        var need = Math.Max(0, 15 - shownRows);
        while (PlaceholderItems.Count > need) PlaceholderItems.RemoveAt(PlaceholderItems.Count - 1);
        while (PlaceholderItems.Count < need) PlaceholderItems.Add(new PlaceholderRow());
    }

    private static int StatusOrder(TradePlan p)
    {
        if (p.ExecutionStatus == "executed" || p.Status == "executed") return 1;
        if (p.Status == "expired" || p.Status == "cancelled" || p.ExecutionStatus == "cancelled") return 2;
        return 0;
    }

    [RelayCommand]
    public void ShowEditForm(TradePlan plan)
    {
        EditingPlan = plan;
        IsEditFormVisible = true;
    }

    [RelayCommand]
    public void SaveEdit()
    {
        if (_planService != null && EditingPlan != null)
        {
            var plan = EditingPlan;
            _planService.UpdatePlan(plan.Id, p =>
            {
                p.PlanDate = plan.PlanDate;
                p.PlanType = plan.PlanType;
                p.EntryPrice = plan.EntryPrice;
                p.TargetPrice = plan.TargetPrice;
                p.StopLoss = plan.StopLoss;
                p.Note = plan.Note;
            });
        }
        IsEditFormVisible = false;
        EditingPlan = null;
        LoadFromService();
    }

    [RelayCommand]
    public void CancelEdit()
    {
        IsEditFormVisible = false;
        EditingPlan = null;
        LoadFromService();
    }

    [RelayCommand]
    public void ShowCancelForm(TradePlan plan)
    {
        EditingPlan = plan;
        CancelReason = "";
        IsCancelFormVisible = true;
    }

    [RelayCommand]
    public void ConfirmCancel()
    {
        if (_planService != null && EditingPlan != null)
            _planService.CancelPlan(EditingPlan.Id, CancelReason);
        IsCancelFormVisible = false;
        EditingPlan = null;
        LoadFromService();
    }

    [RelayCommand]
    public void CancelCancelForm()
    {
        IsCancelFormVisible = false;
        EditingPlan = null;
    }

    [RelayCommand]
    public void DeletePlan(TradePlan plan)
    {
        _planService?.DeletePlan(plan.Id);
        LoadFromService();
    }

    [RelayCommand]
    public void ExecutePlan(TradePlan plan)
    {
        if (_planService != null)
            _planService.RecordExecution(plan.Id,
                new StockReview.Core.Services.ExecutionRecord
                {
                    ExecutionStatus = "executed"
                });
        LoadFromService();
    }

    [RelayCommand]
    public void OpenIntradayChart(string? stockCode)
    {
        if (!string.IsNullOrWhiteSpace(stockCode))
            OpenIntradayChartRequested?.Invoke(stockCode);
    }

    private void UpdateCounts()
    {
        var today = TradePlanService.FormatLocalDate(DateTime.Now);
        TotalCount = Plans.Count;
        PendingCount = Plans.Count(p => p.Status is "pending" or "confirmed" or "executing" or "draft"
                                        && p.ExecutionStatus != "executed" && p.ExecutionStatus != "cancelled");
        TodayCount = Plans.Count(p => p.PlanDate == today && p.Status is "pending" or "confirmed" or "executing" or "draft");
        ExecutedCount = Plans.Count(p => p.Status == "executed" || p.ExecutionStatus == "executed");
        CancelledCount = Plans.Count(p => p.Status == "cancelled" || p.ExecutionStatus == "cancelled");
    }
}
