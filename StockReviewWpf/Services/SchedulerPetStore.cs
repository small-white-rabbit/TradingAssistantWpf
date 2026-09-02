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

        // 宠物窗口订阅就绪（PetWindow.SetPetService）后补渲染当前占用槽位：
        // 调度器随 Host 后台启动（500ms tick 立即开始），而宠物窗口延迟 5s 创建，
        // 空窗期出队的气泡 show 发给无订阅者事件会静默丢失（调度器占用、UI 空置的幽灵槽位）
        _petService.BubbleConsumerAttached += OnBubbleConsumerAttached;

        // 启动气泡调度循环（500ms tick）
        _bubbleScheduler.Start();
    }

    /// <summary>宠物窗口订阅就绪：始终延迟到 Dispatcher 队列执行（订阅发生在窗口 Show 之前，
    /// 同步执行会在窗口显示前渲染 Popup；入队后必在 ShowPet 同步块之后执行）</summary>
    private void OnBubbleConsumerAttached()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        // Dispatcher 不可用 = 应用已关闭/无 UI 宿主，此时不存在待补渲染的宠物窗口，
        // 同步执行 ResyncSlotViews 会在气泡调度线程直接操作 WPF 控件（跨线程崩溃风险），直接跳过
        if (dispatcher == null)
        {
            Serilog.Log.Debug("[SchedulerPetStore] Dispatcher 不可用，跳过槽位补渲染");
            return;
        }
        dispatcher.BeginInvoke(ResyncSlotViews);
    }

    /// <summary>按调度器当前槽位状态补渲染（空窗期丢失 show 的槽位重新显示，幂等）</summary>
    private void ResyncSlotViews()
    {
        try
        {
            var synced = 0;
            foreach (var slot in StockReview.Core.Services.BubbleSlots.All)
            {
                var item = _bubbleScheduler.GetSlotItem(slot);
                if (item == null) continue;
                ShowSlotItem(item, slot);
                synced++;
            }
            if (synced > 0)
                Serilog.Log.Information("[SchedulerPetStore] 宠物窗口就绪，补渲染 {Count} 个占用槽位（订阅空窗期丢失的 show）", synced);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[SchedulerPetStore] 槽位补渲染失败");
        }
    }

    private void OnBubbleTick(TickResult result)
    {
        // 逐槽位事件转发（对应原版 petStore._doTick 的 per-slot diff）：
        // show → 该槽位显示气泡；hide → 该槽位气泡过期/被抢占，仅隐藏该槽位
        if (result.Events == null || result.Events.Count == 0) return;

        foreach (var evt in result.Events)
        {
            switch (evt.Action)
            {
                case "show" when evt.Item != null:
                    ShowSlotItem(evt.Item, evt.Slot);
                    break;

                case "hide":
                    // 该槽位气泡过期 → 隐藏该槽位（非强制：不影响其它槽位待操作的动作气泡）
                    _petService.HideBubble(evt.Slot, force: false);
                    break;
            }
        }
    }

    /// <summary>槽位项渲染（tick 出队与补渲染共用）：等级映射样式 + 内容回退 + 动作按钮转发</summary>
    private void ShowSlotItem(StockReview.Core.Services.BubbleQueueItem item, string slot)
    {
        // 样式优先按提醒等级映射（critical/alert/warning 有专属配色，对齐原版 level），
        // 无专属样式时回退重要度类别
        var category = MapLevelToStyle(item.Level)
            ?? MapImportanceToCategory(item.Importance ?? 3);
        var text = !string.IsNullOrEmpty(item.Content)
            ? item.Content!
            : item.Title;
        _petService.ShowBubble(text, category, (int)(item.DurationMs ?? 8000),
            item.Title, item.Actions, slot, schedulerDriven: true);
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
        // 全槽位清空（对应原版 hideAllBubbles）：手动隐藏属显式操作，
        // 清空调度器全部槽位并无条件关闭所有气泡（绕过动作气泡守卫）
        _bubbleScheduler.AckAllSlots("manual_hide");
        _petService.HideBubble(slot: null, force: true);
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

}
