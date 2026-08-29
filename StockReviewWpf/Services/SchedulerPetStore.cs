using StockReview.Core.Services;
using StockReviewWpf.ViewModels;

namespace StockReviewWpf.Services;

/// <summary>
/// 调度器宠物流水线桥（IPetStore 真实实现，对应 planScheduler → petStore）。
/// 把交易计划调度器产生的提醒/心情/气泡动作转发到 WPF 宠物系统（PetService/PetWindow）。
///
/// 管线接通（2026-08-23 修复）：
/// 1. AddReminder → BubbleSchedulerService.Enqueue（优先级队列排序）+ ReminderHistoryService.AddRecord（历史记录）
/// 2. BubbleScheduler.OnTick → PetService.ShowBubble（队列出队时显示）/ HideBubble（过期时隐藏）
/// 3. 气泡调度器 Start() 在 PetService 构造时启动
/// </summary>
public class SchedulerPetStore : IPetStore
{
    private readonly PetService _petService;
    private readonly BubbleSchedulerService _bubbleScheduler;
    private readonly ReminderHistoryService _reminderHistory;

    public SchedulerPetStore(
        PetService petService,
        BubbleSchedulerService bubbleScheduler,
        ReminderHistoryService reminderHistory)
    {
        _petService = petService;
        _bubbleScheduler = bubbleScheduler;
        _reminderHistory = reminderHistory;

        // 订阅气泡调度器的 Tick 回调，将 show/hide 转发到宠物窗口
        _bubbleScheduler.OnTick += OnBubbleTick;

        // 启动气泡调度循环（500ms tick）
        _bubbleScheduler.Start();
    }

    private void OnBubbleTick(TickResult result)
    {
        switch (result.Action)
        {
            case "show" when result.NewItem != null:
                // 队列出队 → 显示气泡（动作按钮一并转发渲染）
                // 样式优先按提醒等级映射（critical/alert/warning 有专属配色，对齐 Electron level），
                // 无专属样式时回退重要度类别
                var category = MapLevelToStyle(result.NewItem.Level)
                    ?? MapImportanceToCategory(result.NewItem.Importance ?? 3);
                var text = !string.IsNullOrEmpty(result.NewItem.Content)
                    ? result.NewItem.Content!
                    : result.NewItem.Title;
                _petService.ShowBubble(text, category, (int)(result.NewItem.DurationMs ?? 8000),
                    result.NewItem.Title, result.NewItem.Actions);
                break;

            case "hide":
                // 当前气泡过期 → 隐藏
                _petService.HideBubble();
                break;
        }
    }

    public void AddReminder(ReminderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) && string.IsNullOrWhiteSpace(request.Content))
            return;

        var text = string.IsNullOrWhiteSpace(request.Content) ? request.Title : request.Content;

        // 1) 记录到提醒历史
        try
        {
            _reminderHistory.AddRecord(new ReminderSnapshot
            {
                Id = request.Id,
                Type = request.Type,
                Level = request.Level.ToString().ToLowerInvariant(),
                Importance = request.Importance,
                Title = request.Title,
                Content = request.Content,
                StockCode = request.StockCode,
                StockName = request.StockName
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[SchedulerPetStore] 提醒历史记录失败");
        }

        // 2) 入队气泡调度器（优先级排序 + 去重）
        // Id 沿用提醒 ID（自定义提醒为 custom_{原始ID}_{日期}），供动作响应回写提醒历史
        var enqueued = _bubbleScheduler.Enqueue(new BubbleQueueItem
        {
            Id = request.Id,
            Title = request.Title,
            Content = text,
            Type = request.Type,
            Level = request.Level.ToString().ToLowerInvariant(),
            Importance = request.Importance,
            DurationMs = request.DurationMs > 0 ? request.DurationMs : DefaultDuration(request.Type),
            Persistent = request.Persistent,
            StockCode = request.StockCode,
            StockName = request.StockName,
            Actions = request.Actions?.ConvertAll(a => new BubbleAction
            {
                Type = a.Type,
                Label = a.Label,
                PlanIds = a.PlanIds,
                ReminderId = a.ReminderId
            })
        });

        // 如果入队被去重拦截，仍即时显示一次（低优先级场景直接显示）
        if (!enqueued && request.Level < ReminderLevel.Alert)
        {
            var category = MapImportanceToCategory(request.Importance);
            // 动作按钮一并转发：该路径漏传动作会导致气泡显示但无按钮（用户无法操作）
            var converted = request.Actions?.ConvertAll(a => new BubbleAction
            {
                Type = a.Type,
                Label = a.Label,
                PlanIds = a.PlanIds,
                ReminderId = a.ReminderId
            });
            _petService.ShowReminder(category, text, request.Title, converted);
        }
    }

    public void HideBubble()
    {
        _bubbleScheduler.AckCurrent("manual_hide");
        _petService.HideBubble(force: true); // 手动隐藏属显式操作，绕过动作气泡守卫
    }

    public void SetMood(MoodType mood) => _petService.SetMood(mood);

    public void ScheduleMoodRestore(int delayMs)
    {
        if (delayMs <= 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs);
            _petService.SetMood(MoodType.Neutral);
        });
    }

    public void ScheduleUpgrade(ReminderRequest reminder, int delayMs, string level)
    {
        if (string.IsNullOrWhiteSpace(reminder.Content)) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs);
            reminder.Level = ReminderLevel.Alert;
            AddReminder(reminder);
        });
    }

    public void UpdateTimeStatus() { /* 主窗自行反映时段，无需转发 */ }

    private static string MapImportanceToCategory(int importance) => importance switch
    {
        >= 4 => "signal",
        >= 2 => "hint",
        _ => "encourage"
    };

    /// <summary>提醒等级 → 气泡样式（对齐 PetBubble.vue level 配色；无专属样式返回 null 走重要度类别）</summary>
    private static string? MapLevelToStyle(string? level) => level switch
    {
        "critical" => "critical",
        "alert" => "alert",
        "warning" => "warning",
        _ => null
    };

    private static int DefaultDuration(string type)
    {
        var t = (type ?? "").ToLowerInvariant();
        if (t.Contains("signal") || t.Contains("sell") || t.Contains("buy")) return 10000;
        if (t.Contains("insight") || t.Contains("summary")) return 8000;
        if (t.Contains("after_market") || t.Contains("weekend")) return 12000;
        return 8000;
    }

    private static string InferCategory(string type)
    {
        var t = (type ?? "").ToLowerInvariant();
        if (t.Contains("sell") || t.Contains("target") || t.Contains("stop") || t.Contains("rapid")
            || t.Contains("signal") || t.Contains("seal") || t.Contains("buy"))
            return "signal";
        if (t.Contains("insight") || t.Contains("summary") || t.Contains("after_market") || t.Contains("weekend"))
            return "insight";
        return "trade";
    }
}
