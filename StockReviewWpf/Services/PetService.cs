using System;
using System.Collections.Generic;
using Serilog;

namespace StockReviewWpf.Services;

/// <summary>
/// 宠物服务 - 对应 petStore.js + petSettingsStore.js + petAppearanceStore.js
/// 充当「系统提醒 → 宠物气泡」的统一转发器：任何提醒生产方调用 ShowBubble/ShowReminder，
/// PetWindow 订阅 BubbleRequested 后统一用分类气泡呈现。
/// </summary>
public class PetService
{
    // (text, bubbleType, durationMs, title, actions, slot, schedulerDriven)
    public event Action<string, string, int, string?, IReadOnlyList<StockReview.Core.Services.BubbleAction>?, string, bool>? BubbleRequested;
    public event Action? SpriteChanged;

    /// <summary>调度器等外部驱动宠物心情（MoodType=StockReview.Core.Services.MoodType）</summary>
    public event Action<StockReview.Core.Services.MoodType>? MoodRequested;

    /// <summary>请求隐藏气泡（slot, force）。slot=null 表示全部槽位；
    /// force=false 为常规隐藏（如调度器过期 hide），PetWindow 端不关闭待操作的动作气泡；
    /// force=true 为显式隐藏（动作点击/手动/退出清理），无条件关闭。</summary>
    public event Action<string?, bool>? BubbleHiddenRequested;

    /// <summary>展示分类气泡。type：encourage 鼓励 / hint 提醒 / tease 吐槽 / playful 嬉闹。
    /// title 对齐 Electron bubble.title；actions 为气泡动作按钮列表（可空，空则显示 × 关闭按钮）；
    /// slot 为目标槽位（top/left/right，对齐 Electron currentBubbles 键），默认 top。
    /// schedulerDriven：true 表示由气泡调度器队列出队（PetWindow 不启本地倒计时，由调度器 hide 关闭）；
    /// false 表示本地直呼（更新提示/兜底直显等），PetWindow 启动本地倒计时自动关闭。</summary>
    public void ShowBubble(string text, string type = "encourage", int durationMs = 8000, string? title = null,
        IReadOnlyList<StockReview.Core.Services.BubbleAction>? actions = null, string slot = StockReview.Core.Services.BubbleSlots.Top,
        bool schedulerDriven = false)
        => BubbleRequested?.Invoke(text, type, durationMs, title, actions, slot, schedulerDriven);

    /// <summary>
    /// 系统提醒统一入口：按类别映射到分类气泡类型与时长（对应 bubbleSchedulerStore.js）。
    /// category：trade 交易 / insight 洞察 / signal 信号，其余归为 encourage。
    /// </summary>
    public void ShowReminder(string category, string text, string? title = null,
        IReadOnlyList<StockReview.Core.Services.BubbleAction>? actions = null)
    {
        var settings = PetSettingsStore.Load();
        if (!settings.ReminderEnabled)
        {
            Log.Debug("[宠物] 提醒总开关已关闭，跳过 category={Category}", category);
            return;
        }
        var (type, duration) = category switch
        {
            "trade" => ("hint", EffectiveDuration(settings.BubbleDurationTrade, 6000)),
            "insight" => ("encourage", EffectiveDuration(settings.BubbleDurationInsight, 8000)),
            "signal" => ("hint", EffectiveDuration(settings.BubbleDurationSignal, 8000)),
            _ => ("encourage", 8000)
        };
        ShowBubble(text, type, duration, title, actions);
        Log.Information("[宠物] 系统提醒气泡 category={Category}, type={Type}, len={Len}", category, type, text.Length);
    }

    public void RefreshSprite() => SpriteChanged?.Invoke();

    /// <summary>设置宠物心情（外部驱动，如交易计划调度）</summary>
    public void SetMood(StockReview.Core.Services.MoodType mood) => MoodRequested?.Invoke(mood);

    /// <summary>隐藏气泡。slot=null 隐藏全部槽位；force=false 常规隐藏（动作气泡显示中不关闭）；force=true 显式隐藏（无条件关闭）。</summary>
    public void HideBubble(string? slot = null, bool force = false) => BubbleHiddenRequested?.Invoke(slot, force);

    // BubbleDuration* 为 -1 表示采用默认时长（对应原版按类型时长映射）
    private static int EffectiveDuration(int configured, int fallback) => configured > 0 ? configured : fallback;
}