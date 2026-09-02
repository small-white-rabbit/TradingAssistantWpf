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
/// 自定义提醒服务
/// 管理用户自定义的定时/重复提醒（一次性/每日/每周）
/// 持久化到 appConfig 表（对应 localStorage 的 pet_custom_reminders 键）
/// </summary>
public class CustomRemindersService
{
    private readonly IDatabaseService _db;
    private const string StorageKey = "pet_custom_reminders";

    // 兼容旧版备份的 camelCase 字段与 WPF 自身的 PascalCase 字段
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // 错过补发截止（东八区分钟数，15*60=15:00）
    public const int MissedCutoffMinutes = 15 * 60;

    // 补发标记阈值：距设定触发时间超过 5 分钟的触发视为"错过补发"
    private const long CatchupDiffMs = 5 * 60 * 1000L;

    // 触发去重窗口（1 分钟内不重复触发）
    private const long RecentWindowMs = 60 * 1000L;

    private List<CustomReminder> _reminders = new();
    private readonly Dictionary<string, long> _recentlyTriggeredIds = new();

    // shouldTrigger 最近一次返回 true 时记录的"错过补发"判定
    private CatchUpInfo? _lastCatchUpInfo;

    public IReadOnlyList<CustomReminder> Reminders => _reminders.AsReadOnly();
    public List<CustomReminder> EnabledReminders => _reminders.Where(r => r.Enabled).ToList();

    // ============ 常量 ============

    public static class ReminderType
    {
        public const string Once = "once";
        public const string Daily = "daily";
        public const string Weekly = "weekly";
    }

    public static class ReminderStatus
    {
        public const string Pending = "pending";
        public const string Triggered = "triggered";
        public const string Done = "done";
        public const string Snoozed = "snoozed";
    }

    public static readonly List<ReminderAction> DefaultActions = new()
    {
        new ReminderAction { Type = "custom_done", Label = "完成" },
        new ReminderAction { Type = "custom_snooze", Label = "稍后提醒" }
    };

    public CustomRemindersService(IDatabaseService db)
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
                _reminders = JsonSerializer.Deserialize<List<CustomReminder>>(json!, JsonOpts) ?? new();
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "[CustomReminders] 加载失败");
            _reminders = new();
        }
    }

    private void Persist()
    {
        try
        {
            var json = JsonSerializer.Serialize(_reminders);
            _db.Put("appConfig", new Dictionary<string, object?>
            {
                ["key"] = StorageKey,
                ["value"] = json
            });
        }
        catch (Exception e)
        {
            Log.Warning(e, "[CustomReminders] 保存失败");
        }
    }

    // ============ 时间工具（东八区） ============

    /// <summary>
    /// 获取东八区时间分量
    /// </summary>
    public static ShanghaiTimeParts GetShanghaiParts(DateTime date)
    {
        var tz = CnTimeZone.Get;
        var shanghai = TimeZoneInfo.ConvertTimeFromUtc(date.ToUniversalTime(), tz);
        return new ShanghaiTimeParts
        {
            Year = shanghai.Year,
            Month = shanghai.Month,
            Day = shanghai.Day,
            Hours = shanghai.Hour,
            Minutes = shanghai.Minute,
            Seconds = shanghai.Second,
            DayOfWeek = (int)shanghai.DayOfWeek
        };
    }

    /// <summary>
    /// 构造东八区某日某时的 DateTime（转为 UTC 存储）
    /// </summary>
    public static DateTime ShanghaiDate(int year, int month, int day, int hours = 0, int minutes = 0, int seconds = 0)
    {
        var tz = CnTimeZone.Get;
        var local = new DateTime(year, month, day, hours, minutes, seconds, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }

    /// <summary>
    /// 解析时间字符串 "HH:MM" 为当天的东八区 DateTime
    /// </summary>
    public DateTime ParseTimeToDate(string? timeStr, DateTime? baseDate = null)
    {
        var parts = (timeStr ?? "09:00").Split(':');
        int h = parts.Length > 0 && int.TryParse(parts[0], out var h0) ? h0 : 0;
        int m = parts.Length > 1 && int.TryParse(parts[1], out var m0) ? m0 : 0;
        var p = GetShanghaiParts(baseDate ?? DateTime.UtcNow);
        return ShanghaiDate(p.Year, p.Month, p.Day, h, m, 0);
    }

    // ============ 触发判断 ============

    /// <summary>
    /// 检查提醒是否应该触发
    /// </summary>
    public bool ShouldTrigger(CustomReminder reminder, DateTime? now = null)
    {
        now ??= DateTime.UtcNow;
        var nowShanghai = GetShanghaiParts(now.Value);

        if (!reminder.Enabled) return false;

        // 稍后提醒模式：检查延时是否到期
        bool snoozeCleared = false;
        if (reminder.Status == ReminderStatus.Snoozed || reminder.SnoozeUntil != null)
        {
            if (reminder.SnoozeUntil != null)
            {
                if (DateTime.TryParse(reminder.SnoozeUntil, out var untilDate) && now.Value < untilDate)
                    return false;

                // 到期：清除 snoozeUntil + lastTriggeredAt + recentlyTriggeredIds
                reminder.SnoozeUntil = null;
                reminder.LastTriggeredAt = null;
                _recentlyTriggeredIds.Remove(reminder.Id);
                if (reminder.Status == ReminderStatus.Snoozed)
                    reminder.Status = ReminderStatus.Pending;
                snoozeCleared = true;
                Log.Information("[ShouldTrigger] snooze 到期恢复: {Title}", reminder.Title);
            }
        }

        // 已完成（一次性提醒），不触发
        if (reminder.Status == ReminderStatus.Done) return false;

        // 避免同一分钟内重复触发
        var nowMs = new DateTimeOffset(now.Value).ToUnixTimeMilliseconds();
        if (_recentlyTriggeredIds.TryGetValue(reminder.Id, out var recentTs) && nowMs - recentTs < RecentWindowMs)
            return false;

        // 当日是否已触发
        if (reminder.LastTriggeredAt != null)
        {
            if (DateTime.TryParse(reminder.LastTriggeredAt, out var lastDate))
            {
                var lastShanghai = GetShanghaiParts(lastDate);
                var sameDay = lastShanghai.Year == nowShanghai.Year &&
                              lastShanghai.Month == nowShanghai.Month &&
                              lastShanghai.Day == nowShanghai.Day;
                if (sameDay) return false;
            }
        }

        // 使用东八区时间计算触发时间
        var triggerTime = ParseTimeToDate(reminder.Time, now);

        // 错过补发截止
        var timeParts = (reminder.Time ?? "09:00").Split(':');
        int th = timeParts.Length > 0 && int.TryParse(timeParts[0], out var th0) ? th0 : 0;
        int tm = timeParts.Length > 1 && int.TryParse(timeParts[1], out var tm0) ? tm0 : 0;
        int triggerMinutes = th * 60 + tm;
        int nowMinutes = nowShanghai.Hours * 60 + nowShanghai.Minutes;
        if (triggerMinutes < MissedCutoffMinutes && nowMinutes >= MissedCutoffMinutes)
        {
            if (snoozeCleared) Persist();
            return false;
        }

        bool shouldFire = false;

        switch (reminder.Type)
        {
            case ReminderType.Once:
            {
                if (string.IsNullOrEmpty(reminder.Date)) break;
                var dateParts = reminder.Date.Split('-');
                if (dateParts.Length < 3) break;
                int y = int.Parse(dateParts[0]);
                int mth = int.Parse(dateParts[1]);
                int d = int.Parse(dateParts[2]);
                var targetDate = ShanghaiDate(y, mth, d, th, tm, 0);
                var diff = (now.Value - targetDate).TotalMilliseconds;
                var targetShanghai = GetShanghaiParts(targetDate);
                var sameDay = nowShanghai.Year == targetShanghai.Year &&
                              nowShanghai.Month == targetShanghai.Month &&
                              nowShanghai.Day == targetShanghai.Day;
                shouldFire = diff >= 0 && sameDay;
                break;
            }
            case ReminderType.Daily:
            {
                var diff = (now.Value - triggerTime).TotalMilliseconds;
                shouldFire = diff >= 0;
                break;
            }
            case ReminderType.Weekly:
            {
                if (reminder.Weekdays == null || reminder.Weekdays.Count == 0) break;
                var dayOfWeek = nowShanghai.DayOfWeek;
                if (!reminder.Weekdays.Contains(dayOfWeek)) break;
                var diff = (now.Value - triggerTime).TotalMilliseconds;
                shouldFire = diff >= 0;
                break;
            }
        }

        if (!shouldFire && snoozeCleared) Persist();

        // 记录"错过补发"标志
        if (shouldFire)
        {
            var triggerDiff = (now.Value - triggerTime).TotalMilliseconds;
            _lastCatchUpInfo = new CatchUpInfo
            {
                ReminderId = reminder.Id,
                IsCatchUp = !snoozeCleared &&
                    (reminder.BurstFired ?? 0) == 0 &&
                    (long)triggerDiff >= CatchupDiffMs
            };
        }
        return shouldFire;
    }

    /// <summary>
    /// 读取 shouldTrigger 最近一次返回 true 时记录的补发标志
    /// </summary>
    public bool IsLastTriggerCatchUp(string reminderId)
    {
        return _lastCatchUpInfo?.ReminderId == reminderId && _lastCatchUpInfo.IsCatchUp;
    }

    // ============ CRUD ============

    public CustomReminder AddReminder(Action<CustomReminder> configure)
    {
        var newReminder = new CustomReminder
        {
            Id = Guid.NewGuid().ToString(),
            Type = ReminderType.Once,
            Title = "",
            Content = "",
            Time = "09:00",
            Date = null,
            Weekdays = new List<int>(),
            StockCode = null,
            StockName = null,
            Enabled = true,
            Status = ReminderStatus.Pending,
            CreatedAt = DateTime.UtcNow.ToString("o"),
            UpdatedAt = DateTime.UtcNow.ToString("o"),
            LastTriggeredAt = null,
            SnoozeUntil = null,
            RepeatBurstCount = 1,
            BurstFired = 0,
            Actions = DefaultActions
        };
        configure(newReminder);

        // ONCE 类型：创建时已过目标时间则阻止当天触发
        if (newReminder.Type == ReminderType.Once && !string.IsNullOrEmpty(newReminder.Date) && !string.IsNullOrEmpty(newReminder.Time))
        {
            try
            {
                var dateParts = newReminder.Date.Split('-');
                var timeParts = newReminder.Time.Split(':');
                var targetDate = ShanghaiDate(
                    int.Parse(dateParts[0]), int.Parse(dateParts[1]), int.Parse(dateParts[2]),
                    int.Parse(timeParts[0]), int.Parse(timeParts[1]), 0);
                if (DateTime.UtcNow > targetDate)
                {
                    newReminder.LastTriggeredAt = DateTime.UtcNow.ToString("o");
                    Log.Information("[CustomReminders] ONCE 提醒创建时已过目标时间，阻止当天触发: {Title}", newReminder.Title);
                }
            }
            catch { /* ignore */ }
        }

        _reminders.Insert(0, newReminder);
        Persist();
        return newReminder;
    }

    public CustomReminder? UpdateReminder(string id, Action<CustomReminder> updates)
    {
        var reminder = _reminders.FirstOrDefault(r => r.Id == id);
        if (reminder == null) return null;
        updates(reminder);
        reminder.UpdatedAt = DateTime.UtcNow.ToString("o");
        // 编辑后重置触发状态
        reminder.Status = ReminderStatus.Pending;
        reminder.LastTriggeredAt = null;
        reminder.SnoozeUntil = null;
        reminder.BurstFired = 0;
        _recentlyTriggeredIds.Remove(id);
        Persist();
        return reminder;
    }

    /// <summary>按完整对象新增（适配调度器接口，复用 JSON 克隆）</summary>
    public CustomReminder AddReminder(CustomReminder reminder)
    {
        var clone = CloneReminder(reminder);
        if (string.IsNullOrEmpty(clone.Id)) clone.Id = Guid.NewGuid().ToString();
        clone.CreatedAt = DateTime.UtcNow.ToString("o");
        clone.UpdatedAt = DateTime.UtcNow.ToString("o");
        _reminders.Insert(0, clone);
        Persist();
        return clone;
    }

    /// <summary>按完整对象覆盖更新（适配调度器接口）</summary>
    public CustomReminder? UpdateReminder(string id, CustomReminder reminder)
    {
        var clone = CloneReminder(reminder);
        clone.Id = id;
        return UpdateReminder(id, r => CopyReminder(r, clone));
    }

    private static CustomReminder CloneReminder(CustomReminder source)
    {
        var clone = JsonSerializer.Deserialize<CustomReminder>(JsonSerializer.Serialize(source))!;
        clone.Actions ??= DefaultActions;
        return clone;
    }

    private static void CopyReminder(CustomReminder target, CustomReminder source)
    {
        target.Type = source.Type;
        target.Title = source.Title;
        target.Content = source.Content;
        target.Time = source.Time;
        target.Date = source.Date;
        target.Weekdays = source.Weekdays;
        target.StockCode = source.StockCode;
        target.StockName = source.StockName;
        target.Enabled = source.Enabled;
        target.Status = source.Status;
        target.LastTriggeredAt = source.LastTriggeredAt;
        target.SnoozeUntil = source.SnoozeUntil;
        target.RepeatBurstCount = source.RepeatBurstCount;
        target.BurstFired = source.BurstFired;
        target.Actions = source.Actions;
    }

    public bool DeleteReminder(string id)
    {
        var idx = _reminders.FindIndex(r => r.Id == id);
        if (idx == -1) return false;
        _reminders.RemoveAt(idx);
        Persist();
        return true;
    }

    /// <summary>批量替换全部提醒（用于面板同步）</summary>
    public void ReplaceAll(List<CustomReminder> reminders)
    {
        _reminders = reminders;
        Persist();
    }

    public void ToggleEnabled(string id)
    {
        var r = _reminders.FirstOrDefault(r => r.Id == id);
        if (r == null) return;
        r.Enabled = !r.Enabled;
        r.UpdatedAt = DateTime.UtcNow.ToString("o");
        Persist();
    }

    // ============ 触发与响应 ============

    /// <summary>
    /// 标记提醒已触发
    /// </summary>
    public void MarkTriggered(string id)
    {
        var r = _reminders.FirstOrDefault(r => r.Id == id);
        if (r == null) return;
        r.LastTriggeredAt = DateTime.UtcNow.ToString("o");
        r.SnoozeUntil = null;
        // 一次性提醒：repeatBurstCount=1 立即标记 DONE
        if (r.Type == ReminderType.Once && (r.RepeatBurstCount ?? 1) <= 1)
            r.Status = ReminderStatus.Done;
        Persist();
        _recentlyTriggeredIds[id] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// 用户响应提醒
    /// </summary>
    public ReminderResponseResult RespondToReminder(string id, string responseType, int snoozeMinutes = 10)
    {
        // ID 规范化：去掉 'custom_' 前缀
        var normalizedId = id.StartsWith("custom_") ? id.Substring(7) : id;
        var r = _reminders.FirstOrDefault(rem => rem.Id == normalizedId);
        if (r == null) return new ReminderResponseResult();

        var result = new ReminderResponseResult();

        switch (responseType)
        {
            case "done":
            {
                var burstTotal = Math.Max(1, r.RepeatBurstCount ?? 1);
                var burstFired = Math.Max(0, r.BurstFired ?? 0);
                if (burstTotal > 1 && burstFired + 1 < burstTotal)
                {
                    // 连弹模式：本次"完成"仅消耗 1 次
                    r.BurstFired = burstFired + 1;
                    r.Status = ReminderStatus.Pending;
                    r.SnoozeUntil = null;
                    r.LastTriggeredAt = null;
                    _recentlyTriggeredIds.Remove(normalizedId);
                    result.BurstRemaining = burstTotal - burstFired - 1;
                    result.NeedImmediateRecheck = true;
                }
                else
                {
                    r.BurstFired = 0;
                    r.SnoozeUntil = null;
                    r.Status = r.Type == ReminderType.Once ? ReminderStatus.Done : ReminderStatus.Pending;
                }
                break;
            }
            case "snooze":
            {
                r.Status = ReminderStatus.Snoozed;
                r.SnoozeUntil = DateTime.UtcNow.AddMinutes(snoozeMinutes).ToString("o");
                break;
            }
        }
        r.UpdatedAt = DateTime.UtcNow.ToString("o");
        Persist();
        return result;
    }

    /// <summary>
    /// 清理一次性已完成的提醒（超过 7 天）
    /// </summary>
    public void CleanupOnceReminders()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var before = _reminders.Count;
        _reminders = _reminders.Where(r =>
        {
            if (r.Type != ReminderType.Once) return true;
            if (r.Status != ReminderStatus.Done) return true;
            if (r.LastTriggeredAt == null) return true;
            if (!DateTime.TryParse(r.LastTriggeredAt, out var triggeredAt)) return true;
            var triggeredMs = new DateTimeOffset(triggeredAt).ToUnixTimeMilliseconds();
            return nowMs - triggeredMs < 7 * 24 * 60 * 60 * 1000L;
        }).ToList();
        if (_reminders.Count != before) Persist();
    }

    /// <summary>
    /// 从存储全量重载（备份恢复后调用）
    /// </summary>
    public void ReloadFromStorage()
    {
        LoadFromStorage();
    }
}

// ============ 数据模型 ============

public class CustomReminder
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "once";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Time { get; set; } = "09:00";
    public string? Date { get; set; }
    public List<int>? Weekdays { get; set; }
    public string? StockCode { get; set; }
    public string? StockName { get; set; }
    public bool Enabled { get; set; } = true;
    public string Status { get; set; } = "pending";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string? LastTriggeredAt { get; set; }
    public string? SnoozeUntil { get; set; }
    public int? RepeatBurstCount { get; set; } = 1;
    public int? BurstFired { get; set; } = 0;
    public List<ReminderAction>? Actions { get; set; }
}

public class ReminderAction
{
    public string Type { get; set; } = "";
    public string Label { get; set; } = "";
    public List<string>? PlanIds { get; set; }
    /// <summary>自定义提醒原始 ID（触发时注入，供气泡按钮回查，对齐原版 action.reminderId）</summary>
    public string? ReminderId { get; set; }
}

public class ShanghaiTimeParts
{
    public int Year { get; set; }
    public int Month { get; set; }
    public int Day { get; set; }
    public int Hours { get; set; }
    public int Minutes { get; set; }
    public int Seconds { get; set; }
    public int DayOfWeek { get; set; }
}

public class CatchUpInfo
{
    public string ReminderId { get; set; } = "";
    public bool IsCatchUp { get; set; }
}

public class ReminderResponseResult
{
    public int BurstRemaining { get; set; }
    public bool NeedImmediateRecheck { get; set; }
}
