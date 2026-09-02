using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace StockReview.Core.Services;

/// <summary>
/// 自定义提醒调度服务
/// 纯时间计算逻辑，不依赖窗口或行情模块
/// </summary>
public class CustomReminderSchedulerService
{
    private readonly CustomRemindersService _remindersService;
    private readonly IPetSettingsStore _settingsStore;
    private readonly ILogger _logger;
    private const long DayMs = 24 * 60 * 60 * 1000L;

    // 错过补发截止（东八区分钟数，15*60=15:00）
    public const int MissedCutoffMinutes = 15 * 60;

    // 调度状态
    private Timer? _customReminderTimer;
    private bool _running;

    public bool Running => _running;

    public CustomReminderSchedulerService(CustomRemindersService remindersService, IPetSettingsStore settingsStore)
    {
        _remindersService = remindersService;
        _settingsStore = settingsStore;
        _logger = Log.ForContext<CustomReminderSchedulerService>();
    }

    /// <summary>
    /// 获取东八区时间字符串 HH:MM:SS
    /// </summary>
    public static string GetShanghaiTime(DateTime date)
    {
        var parts = CustomRemindersService.GetShanghaiParts(date);
        return $"{parts.Hours:D2}:{parts.Minutes:D2}:{parts.Seconds:D2}";
    }

    // ============ 纯时间计算：下次运行时间 ============

    /// <summary>
    /// 计算提醒的下次触发时间戳（对应 JS 版 computeNextRun）
    /// </summary>
    public static long? ComputeNextRun(CustomReminder reminder, long nowTs)
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(nowTs).UtcDateTime;

        // snoozeUntil 优先
        if (!string.IsNullOrEmpty(reminder.SnoozeUntil))
        {
            if (DateTime.TryParse(reminder.SnoozeUntil, out var snoozeDate))
            {
                var snoozeTs = new DateTimeOffset(snoozeDate).ToUnixTimeMilliseconds();
                if (snoozeTs > nowTs) return snoozeTs;
            }
        }

        // 已完成或已禁用 → 无下次运行
        if (reminder.Status == "done" || !reminder.Enabled) return null;

        var nowShanghai = CustomRemindersService.GetShanghaiParts(now);
        var timeParts = (reminder.Time ?? "09:00").Split(':');
        int h = timeParts.Length > 0 && int.TryParse(timeParts[0], out var h0) ? h0 : 0;
        int min = timeParts.Length > 1 && int.TryParse(timeParts[1], out var m0) ? m0 : 0;
        int triggerMinutes = h * 60 + min;
        bool pastMissedCutoff = triggerMinutes < MissedCutoffMinutes &&
            (nowShanghai.Hours * 60 + nowShanghai.Minutes) >= MissedCutoffMinutes;

        // 当日是否已触发
        bool alreadyTriggeredToday = false;
        if (!string.IsNullOrEmpty(reminder.LastTriggeredAt))
        {
            if (DateTime.TryParse(reminder.LastTriggeredAt, out var lastDate))
            {
                var last = CustomRemindersService.GetShanghaiParts(lastDate);
                alreadyTriggeredToday = last.Year == nowShanghai.Year &&
                    last.Month == nowShanghai.Month && last.Day == nowShanghai.Day;
            }
        }

        // ONCE 类型
        if (reminder.Type == "once")
        {
            if (string.IsNullOrEmpty(reminder.Date) || !string.IsNullOrEmpty(reminder.LastTriggeredAt))
                return null;

            var dateParts = reminder.Date.Split('-');
            if (dateParts.Length < 3) return null;
            var targetDate = CustomRemindersService.ShanghaiDate(
                int.Parse(dateParts[0]), int.Parse(dateParts[1]), int.Parse(dateParts[2]), h, min, 0);
            var targetTs = new DateTimeOffset(targetDate).ToUnixTimeMilliseconds();

            if (targetTs <= nowTs)
            {
                var target = CustomRemindersService.GetShanghaiParts(targetDate);
                bool isToday = target.Year == nowShanghai.Year &&
                    target.Month == nowShanghai.Month && target.Day == nowShanghai.Day;
                return isToday && !pastMissedCutoff ? targetTs : (long?)null;
            }
            return targetTs;
        }

        // DAILY 类型
        if (reminder.Type == "daily")
        {
            var triggerDate = CustomRemindersService.ShanghaiDate(
                nowShanghai.Year, nowShanghai.Month, nowShanghai.Day, h, min, 0);
            var triggerTs = new DateTimeOffset(triggerDate).ToUnixTimeMilliseconds();

            if (triggerTs <= nowTs || alreadyTriggeredToday)
            {
                if (triggerTs <= nowTs && !alreadyTriggeredToday && !pastMissedCutoff)
                    return nowTs + 1000;
                triggerTs += DayMs;
            }
            return triggerTs;
        }

        // WEEKLY 类型
        if (reminder.Type == "weekly" && reminder.Weekdays != null && reminder.Weekdays.Count > 0)
        {
            for (int i = 0; i < 8; i++)
            {
                int weekday = (nowShanghai.DayOfWeek + i) % 7;
                if (!reminder.Weekdays.Contains(weekday)) continue;

                var todayTs = new DateTimeOffset(CustomRemindersService.ShanghaiDate(
                    nowShanghai.Year, nowShanghai.Month, nowShanghai.Day, h, min, 0))
                    .ToUnixTimeMilliseconds();

                if (i == 0)
                {
                    if (todayTs <= nowTs)
                    {
                        if (!alreadyTriggeredToday && !pastMissedCutoff) return nowTs + 1000;
                        continue;
                    }
                    if (!alreadyTriggeredToday) return todayTs;
                    continue;
                }
                return todayTs + (long)i * DayMs;
            }
        }

        return null;
    }

    // ============ 调度循环 ============

    /// <summary>
    /// 启动调度器
    /// </summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        ScheduleNextCheck();
        _logger.Information("[CustomReminderScheduler] 调度器已启动");
    }

    /// <summary>
    /// 停止调度器
    /// </summary>
    public void Stop()
    {
        _running = false;
        _customReminderTimer?.Dispose();
        _customReminderTimer = null;
        _logger.Information("[CustomReminderScheduler] 调度器已停止");
    }

    /// <summary>
    /// 重新计算下次调度时间（外部修改提醒后调用）
    /// </summary>
    public void RefreshSchedule()
    {
        _customReminderTimer?.Dispose();
        ScheduleNextCheck();
    }

    /// <summary>
    /// 立即执行一次提醒检查
    /// </summary>
    public void CheckNow()
    {
        CheckCustomReminders(DateTime.UtcNow);
    }

    private void ScheduleNextCheck()
    {
        if (!_running) return;

        try
        {
            var reminders = _remindersService.EnabledReminders;
            var nowTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (reminders.Count == 0)
            {
                _customReminderTimer = new Timer(_ => ScheduleNextCheck(), null, 30000, Timeout.Infinite);
                return;
            }

            long? nextRun = null;
            string nextTitle = "";

            foreach (var reminder in reminders)
            {
                var run = ComputeNextRun(reminder, nowTs);
                if (run.HasValue && (nextRun == null || run.Value < nextRun.Value))
                {
                    nextRun = run.Value;
                    nextTitle = reminder.Title;
                }
            }

            if (nextRun == null)
            {
                _customReminderTimer = null;
                return;
            }

            var delta = nextRun.Value - nowTs;
            int delay = delta < 0 ? 100
                : delta >= 3600000 ? 3600000
                : delta >= 600000 ? 600000
                : delta >= 60000 ? 60000
                : delta >= 10000 ? 1000
                : delta >= 1000 ? 200
                : 100;

            _logger.Debug("[CustomReminderScheduler] 下次检查 delay={Delay}ms → {Title} 目标时间={Target}",
                delay, nextTitle, GetShanghaiTime(DateTimeOffset.FromUnixTimeMilliseconds(nextRun.Value).UtcDateTime));

            _customReminderTimer = new Timer(_ =>
            {
                if (!_running) return;
                CheckCustomReminders(DateTime.UtcNow);
                ScheduleNextCheck();
            }, null, delay, Timeout.Infinite);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[CustomReminderScheduler] 调度失败，30秒后重试");
            _customReminderTimer = new Timer(_ => ScheduleNextCheck(), null, 30000, Timeout.Infinite);
        }
    }

    /// <summary>
    /// 检查所有自定义提醒是否应该触发
    /// </summary>
    private void CheckCustomReminders(DateTime now)
    {
        // 全局开关：设置关闭自定义提醒时不触发（原由 PlanSchedulerService 旧轮询路径负责检查，
        // 该路径停用后移到这里）
        if (!_settingsStore.Settings.CustomRemindersEnabled) return;

        try
        {
            foreach (var reminder in _remindersService.EnabledReminders)
            {
                if (_remindersService.ShouldTrigger(reminder, now))
                {
                    _remindersService.MarkTriggered(reminder.Id);
                    Log.Information("[CustomReminderScheduler] 触发提醒: {Title} (catchUp={IsCatchUp})",
                        reminder.Title, _remindersService.IsLastTriggerCatchUp(reminder.Id));
                    OnReminderTriggered?.Invoke(this, reminder);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[CustomReminderScheduler] 检查提醒异常");
        }
    }

    /// <summary>
    /// 提醒触发事件（外部订阅后通过 BubbleScheduler 入队）
    /// </summary>
    public event EventHandler<CustomReminder>? OnReminderTriggered;
}
