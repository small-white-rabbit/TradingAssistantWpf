using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using StockReview.Core.Data;

namespace StockReview.Core.Services;

/// <summary>
/// 交易计划服务
/// 管理交易计划的 CRUD、调度、监控
/// 持久化到 appConfig 表（对应 localStorage 的 pet_trade_plans 键）
/// </summary>
public class TradePlanService
{
    private readonly IDatabaseService _db;
    private const string StorageKey = "pet_trade_plans";

    // 兼容旧版备份的 camelCase 字段与 WPF 自身的 PascalCase 字段
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private List<TradePlan> _plans = new();
    private TradePlanFilters _filters = new();

    // ============ 常量 ============

    public static class PlanStatus
    {
        public const string Draft = "draft";
        public const string Confirmed = "confirmed";
        public const string Executing = "executing";
        public const string Executed = "executed";
        public const string Pending = "pending";
        public const string Expired = "expired";
        public const string Cancelled = "cancelled";
    }

    public static class PlanType
    {
        public const string Buy = "buy";
        public const string Sell = "sell";
        public const string Watch = "watch";
    }

    public static class ExecutionStatus
    {
        public const string Executed = "executed";
        public const string NotExecuted = "not_executed";
        public const string Partial = "partial";
        public const string Cancelled = "cancelled";
    }

    public static readonly List<(string Value, string Label)> ValidReasons = new()
    {
        ("w_bottom", "W底突破"),
        ("ma_golden", "均线金叉"),
        ("volume_break", "放量突破"),
        ("neckline", "突破颈线"),
        ("pullback", "回踩均线"),
        ("pattern", "形态突破"),
        ("other", "其他")
    };

    public TradePlanService(IDatabaseService db)
    {
        _db = db;
        LoadFromStorage();
    }

    // ============ 持久化 ============

    private void LoadFromStorage()
    {
        try
        {
            var row = _db.GetById("appConfig", StorageKey);
            if (row != null && row.TryGetValue("value", out var val) && val != null)
            {
                var json = val.ToString();
                _plans = JsonSerializer.Deserialize<List<TradePlan>>(json!, JsonOpts) ?? new();
                // 数据迁移：补全 monitorBuyPoint / monitorSellPoint
                foreach (var plan in _plans)
                {
                    if (plan.MonitorBuyPoint == null) plan.MonitorBuyPoint = 0;
                    if (plan.MonitorSellPoint == null) plan.MonitorSellPoint = 1;
                }
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "[TradePlan] 加载交易计划失败");
            _plans = new();
        }
    }

    private void SaveToStorage()
    {
        try
        {
            var json = JsonSerializer.Serialize(_plans);
            _db.Put("appConfig", new Dictionary<string, object?>
            {
                ["key"] = StorageKey,
                ["value"] = json
            });
        }
        catch (Exception e)
        {
            Log.Warning(e, "[TradePlan] 保存交易计划失败");
        }
    }

    // ============ 计算属性 ============

    /// <summary>全量计划（只读快照）</summary>
    public List<TradePlan> Plans => _plans.ToList();

    public List<TradePlan> TodayPlans
    {
        get
        {
            var today = FormatLocalDate(DateTime.UtcNow);
            return _plans.Where(p => p.PlanDate == today).ToList();
        }
    }

    /// <summary>昨日计划（日历日，与 TodayPlans 口径一致）</summary>
    public List<TradePlan> YesterdayPlans
    {
        get
        {
            var yesterday = FormatLocalDate(DateTime.Now.AddDays(-1));
            return _plans.Where(p => p.PlanDate == yesterday).ToList();
        }
    }

    /// <summary>
    /// 持仓过夜监控计划 - 对应原版 getMonitoringPlans：
    /// 所有早于今天且未执行/未取消的活跃计划（不限昨天一天）。
    /// 备份导入的旧日期计划靠它进入监控范围（YesterdayPlans 只覆盖昨天会漏掉）。
    /// </summary>
    public List<TradePlan> MonitoringPlans
    {
        get
        {
            var today = FormatLocalDate(DateTime.UtcNow);
            return _plans.Where(p =>
                !string.IsNullOrEmpty(p.PlanDate) && p.PlanDate.CompareTo(today) < 0 &&
                (p.Status == PlanStatus.Pending ||
                 p.Status == PlanStatus.Confirmed ||
                 p.Status == PlanStatus.Executing) &&
                p.ExecutionStatus != ExecutionStatus.Executed &&
                p.ExecutionStatus != ExecutionStatus.Cancelled
            ).ToList();
        }
    }

    public List<TradePlan> PendingTodayPlans
    {
        get
        {
            var today = FormatLocalDate(DateTime.UtcNow);
            return _plans.Where(p =>
                p.PlanDate == today &&
                p.ExecutionStatus != ExecutionStatus.Executed &&
                p.ExecutionStatus != ExecutionStatus.Cancelled
            ).ToList();
        }
    }

    public List<TradePlan> ActivePlans =>
        _plans.Where(p =>
            (p.Status == PlanStatus.Confirmed ||
             p.Status == PlanStatus.Executing ||
             p.Status == PlanStatus.Pending) &&
            p.ExecutionStatus != ExecutionStatus.Executed &&
            p.ExecutionStatus != ExecutionStatus.Cancelled
        ).ToList();

    public List<TradePlan> FilteredPlans
    {
        get
        {
            var result = _plans.AsEnumerable();
            if (_filters.Status != "all")
                result = result.Where(p => p.Status == _filters.Status);
            if (!string.IsNullOrEmpty(_filters.StockCode))
            {
                var code = _filters.StockCode;
                result = result.Where(p =>
                    (p.StockCode?.Contains(code) == true) ||
                    (p.StockName?.Contains(code) == true));
            }
            return result.OrderByDescending(p => p.CreatedAt).ToList();
        }
    }

    // ============ CRUD ============

    public (bool Success, TradePlan? Plan, string? Error) AddPlan(TradePlan planData)
    {
        var validation = ValidatePlan(planData);
        if (!validation.Valid)
            return (false, null, validation.Error);

        var newPlan = new TradePlan
        {
            Id = Guid.NewGuid().ToString(),
            PlanDate = string.IsNullOrWhiteSpace(planData.PlanDate) ? FormatLocalDate(DateTime.UtcNow) : planData.PlanDate,
            PlanType = string.IsNullOrWhiteSpace(planData.PlanType) ? PlanType.Sell : planData.PlanType,
            Status = PlanStatus.Pending,
            ExecutionStatus = ExecutionStatus.NotExecuted,
            MaxHoldDays = planData.MaxHoldDays > 0 ? planData.MaxHoldDays : 3,
            Validity = string.IsNullOrWhiteSpace(planData.Validity) ? "today" : planData.Validity,
            MonitorBuyPoint = planData.MonitorBuyPoint ?? 0,
            MonitorSellPoint = planData.MonitorSellPoint ?? 1,
            CreatedAt = DateTime.UtcNow.ToString("o"),
            UpdatedAt = DateTime.UtcNow.ToString("o"),
            // Copy data fields
            StockCode = planData.StockCode,
            StockName = planData.StockName,
            EntryReason = planData.EntryReason,
            EntryPrice = planData.EntryPrice,
            TargetPrice = planData.TargetPrice,
            StopLoss = planData.StopLoss,
            Quantity = planData.Quantity,
            Note = planData.Note,
        };

        _plans.Insert(0, newPlan);
        SaveToStorage();
        return (true, newPlan, null);
    }

    public (bool Success, TradePlan? Plan, string? Error) UpdatePlan(string id, Action<TradePlan> updates)
    {
        var plan = _plans.FirstOrDefault(p => p.Id == id);
        if (plan == null) return (false, null, "计划不存在");

        updates(plan);
        plan.UpdatedAt = DateTime.UtcNow.ToString("o");
        SaveToStorage();
        return (true, plan, null);
    }

    public bool DeletePlan(string id)
    {
        var idx = _plans.FindIndex(p => p.Id == id);
        if (idx == -1) return false;
        _plans.RemoveAt(idx);
        SaveToStorage();
        return true;
    }

    public TradePlan? GetPlan(string id) => _plans.FirstOrDefault(p => p.Id == id);

    // ============ 验证 ============

    public (bool Valid, string? Error) ValidatePlan(TradePlan plan)
    {
        if (string.IsNullOrEmpty(plan.StockCode))
            return (false, "股票代码必填");
        if (string.IsNullOrEmpty(plan.EntryReason))
            return (false, "进场理由必填");
        if (plan.EntryPrice == null || plan.EntryPrice <= 0)
            return (false, "进场价位必填且必须大于 0");
        if (plan.TargetPrice == null)
            return (false, "目标价位必填（必须先制定离场计划）");
        if (plan.StopLoss == null)
            return (false, "止损价位必填（必须先制定离场计划）");

        if (plan.EntryPrice != null && plan.StopLoss != null && plan.TargetPrice != null)
        {
            if (plan.PlanType == PlanType.Buy)
            {
                if (plan.TargetPrice <= plan.EntryPrice)
                    return (false, "买入计划的目标价应高于进场价");
                if (plan.StopLoss >= plan.EntryPrice)
                    return (false, "买入计划的止损价应低于进场价");
            }
            else if (plan.PlanType == PlanType.Sell)
            {
                if (plan.TargetPrice <= plan.EntryPrice)
                    return (false, "卖出计划的目标价应高于进场价");
                if (plan.StopLoss >= plan.EntryPrice)
                    return (false, "卖出计划的止损价应低于进场价");
            }
        }
        return (true, null);
    }

    // ============ 执行记录 ============

    public (bool Success, TradePlan? Plan, string? Error) RecordExecution(string planId, ExecutionRecord executionData)
    {
        var plan = GetPlan(planId);
        if (plan == null) return (false, null, "计划不存在");

        string newStatus = plan.Status;
        if (executionData.ExecutionStatus == ExecutionStatus.Executed)
            newStatus = PlanStatus.Executed;
        else if (executionData.ExecutionStatus == ExecutionStatus.Partial)
            newStatus = PlanStatus.Executing;
        else if (executionData.ExecutionStatus == ExecutionStatus.NotExecuted)
            newStatus = PlanStatus.Pending;
        else if (executionData.ExecutionStatus == ExecutionStatus.Cancelled)
            newStatus = PlanStatus.Cancelled;

        return UpdatePlan(planId, p =>
        {
            p.ExecutionStatus = executionData.ExecutionStatus;
            p.ExecutionTime = DateTime.UtcNow.ToString("o");
            p.ExecutionPrice = executionData.ExecutionPrice;
            p.ExecutionNote = executionData.Note ?? "";
            p.ActualProfitLoss = executionData.ProfitLoss;
            p.Status = newStatus;
        });
    }

    public (bool Success, TradePlan? Plan, string? Error) CancelPlan(string planId, string? reason)
    {
        return UpdatePlan(planId, p =>
        {
            p.Status = PlanStatus.Cancelled;
            p.ExecutionStatus = ExecutionStatus.Cancelled;
            p.CancelReason = reason ?? "";
            p.CancelledAt = DateTime.UtcNow.ToString("o");
        });
    }

    // ============ 监控辅助 ============

    public List<TradePlan> GetMonitoringPlans()
    {
        var today = FormatLocalDate(DateTime.UtcNow);
        return _plans.Where(p =>
            string.Compare(p.PlanDate, today, StringComparison.Ordinal) < 0 &&
            (p.Status == PlanStatus.Pending ||
             p.Status == PlanStatus.Confirmed ||
             p.Status == PlanStatus.Executing) &&
            p.ExecutionStatus != ExecutionStatus.Cancelled &&
            p.ExecutionStatus != ExecutionStatus.Executed
        ).ToList();
    }

    public void SetFilter(string key, string value)
    {
        if (key == "status") _filters.Status = value;
        else if (key == "stockCode") _filters.StockCode = value;
    }

    /// <summary>
    /// 从存储重新加载（跨窗口同步）
    /// </summary>
    public void ReloadFromStorage()
    {
        LoadFromStorage();
    }

    // ============ 辅助 ============

    /// <summary>
    /// 格式化本地日期（东八区，YYYY-MM-DD）
    /// </summary>
    public static string FormatLocalDate(DateTime date)
    {
        var tz = CnTimeZone.Get;
        var shanghai = TimeZoneInfo.ConvertTimeFromUtc(date.ToUniversalTime(), tz);
        return shanghai.ToString("yyyy-MM-dd");
    }
}

// ============ 数据模型 ============

public class TradePlan
{
    public string Id { get; set; } = "";
    public string PlanDate { get; set; } = "";
    public string PlanType { get; set; } = "sell";
    public string Status { get; set; } = "pending";
    public string ExecutionStatus { get; set; } = "not_executed";
    public string StockCode { get; set; } = "";
    public string StockName { get; set; } = "";
    public string? EntryReason { get; set; }
    public decimal? EntryPrice { get; set; }
    public decimal? TargetPrice { get; set; }
    public decimal? StopLoss { get; set; }
    public int? Quantity { get; set; }
    public int MaxHoldDays { get; set; } = 3;
    public string Validity { get; set; } = "today";
    public int? MonitorBuyPoint { get; set; }
    public int? MonitorSellPoint { get; set; }
    public string? Note { get; set; }
    public string? ExecutionTime { get; set; }
    public decimal? ExecutionPrice { get; set; }
    public string? ExecutionNote { get; set; }
    public decimal? ActualProfitLoss { get; set; }
    public string? CancelReason { get; set; }
    public string? CancelledAt { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public class TradePlanFilters
{
    public string Status { get; set; } = "all";
    public string StockCode { get; set; } = "";
}

public class ExecutionRecord
{
    public string ExecutionStatus { get; set; } = "";
    public decimal? ExecutionPrice { get; set; }
    public string? Note { get; set; }
    public decimal? ProfitLoss { get; set; }
}
