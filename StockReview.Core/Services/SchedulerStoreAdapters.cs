using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace StockReview.Core.Services;

// ============================================================================
// SchedulerStoreAdapters.cs
// Stage 4：为 PlanSchedulerService 的存储接口提供 DB 支撑适配器。
// 委托给既有核心服务 TradePlanService / CustomRemindersService（同一数据源），
// 保持单一数据真相来源。
// ============================================================================

/// <summary>
/// ITradePlanStore 适配器 - 桥接调度器与 TradePlanService
/// </summary>
public class SchedulerTradePlanStore : ITradePlanStore
{
    private readonly TradePlanService _service;

    public SchedulerTradePlanStore(TradePlanService service)
    {
        _service = service;
    }

    public List<TradePlan> Plans => _service.Plans;

    public List<TradePlan> TodayPlans => _service.TodayPlans;

    public List<TradePlan> YesterdayPlans => _service.YesterdayPlans;

    public List<TradePlan> MonitoringPlans => _service.MonitoringPlans;

    public List<TradePlan> PendingTodayPlans => _service.PendingTodayPlans;

    public TradePlan? GetPlan(string id) => _service.GetPlan(id);

    public void UpdatePlan(string id, object updates)
    {
        // 调度器传入匿名对象（如 { planDate, status, executionStatus }），转字典后映射到 TradePlan
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(updates)) ?? new();

        _service.UpdatePlan(id, p =>
        {
            if (dict.TryGetValue("planDate", out var pd) && pd.ValueKind == JsonValueKind.String)
                p.PlanDate = pd.GetString()!;
            if (dict.TryGetValue("status", out var st) && st.ValueKind == JsonValueKind.String)
                p.Status = st.GetString()!;
            if (dict.TryGetValue("executionStatus", out var es) && es.ValueKind == JsonValueKind.String)
                p.ExecutionStatus = es.GetString()!;
        });
    }

    public void RecordExecution(string id, object executionData)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(executionData)) ?? new();

        dict.TryGetValue("executionStatus", out var es);
        dict.TryGetValue("note", out var note);
        var record = new ExecutionRecord
        {
            ExecutionStatus = es.ValueKind == JsonValueKind.String ? es.GetString()! : "",
            Note = note.ValueKind == JsonValueKind.String ? note.GetString() : null
        };
        _service.RecordExecution(id, record);
    }
}

/// <summary>
/// ICustomRemindersStore 适配器 - 桥接调度器与 CustomRemindersService
/// </summary>
public class SchedulerCustomRemindersStore : ICustomRemindersStore
{
    private readonly CustomRemindersService _service;

    public SchedulerCustomRemindersStore(CustomRemindersService service)
    {
        _service = service;
    }

    public List<CustomReminder> GetReminders() => _service.Reminders.ToList();

    public void AddReminder(CustomReminder reminder) => _service.AddReminder(reminder);

    public void UpdateReminder(string id, CustomReminder reminder) => _service.UpdateReminder(id, reminder);

    public void DeleteReminder(string id) => _service.DeleteReminder(id);
}