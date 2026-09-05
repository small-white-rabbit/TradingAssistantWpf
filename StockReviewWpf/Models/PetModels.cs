using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace StockReviewWpf.Models;

/// <summary>
/// 15行空白占位符行（无数据时默认显示空表）
/// </summary>
public class PlaceholderRow
{
    public string Empty => "";
    public string Id => "";
}

/// <summary>
/// 交易计划数据模型
/// </summary>
public class TradePlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string StockCode { get; set; } = "";
    public string StockName { get; set; } = "";
    public string PlanType { get; set; } = "sell";
    public string PlanDate { get; set; } = "";
    public string EntryReason { get; set; } = "";
    public decimal? EntryPrice { get; set; }
    public decimal? TargetPrice { get; set; }
    public decimal? StopLoss { get; set; }
    public int MaxHoldDays { get; set; } = 3;
    public string Status { get; set; } = "pending";
    public string ExecutionStatus { get; set; } = "not_executed";
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>是否可执行/取消（仅待执行状态显示完成和取消按钮，对齐原版）</summary>
    public bool CanExecute =>
        string.IsNullOrEmpty(ExecutionStatus) ||
        ExecutionStatus == "pending" ||
        ExecutionStatus == "not_executed";

    /// <summary>是否可编辑（待执行/已执行/部分执行/已取消 都可编辑）</summary>
    public bool CanEdit =>
        CanExecute ||
        ExecutionStatus == "executed" ||
        ExecutionStatus == "partial" ||
        ExecutionStatus == "cancelled";

    // ====== UI 显示属性（对齐原版 getPlanTypeLabel/getStatusLabel/getEntryReasonLabel） ======

    public string PlanTypeLabel => PlanType switch
    {
        "buy" => "买入",
        "watch" => "数据收集",
        _ => "卖出"
    };

    public System.Windows.Media.Brush PlanTypeBg => PlanType switch
    {
        "buy" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFE, 0xF0, 0xF0)),
        "watch" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xF4, 0xF5)),
        _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xF9, 0xEB))
    };

    public System.Windows.Media.Brush PlanTypeBorder => PlanType switch
    {
        "buy" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFB, 0xC4, 0xC4)),
        "watch" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD3, 0xD4, 0xD6)),
        _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC2, 0xE7, 0xB0))
    };

    public System.Windows.Media.Brush PlanTypeFg => PlanType switch
    {
        "buy" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0x6C, 0x6C)),
        "watch" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x90, 0x93, 0x99)),
        _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x67, 0xC2, 0x3A))
    };

    public string StatusLabel =>
        ExecutionStatus == "executed" || Status == "executed" ? "已执行" :
        ExecutionStatus == "cancelled" || Status == "cancelled" ? "已取消" :
        Status == "expired" ? "已过期" :
        "待执行";

    /// <summary>状态背景色（WPF Brush 可绑定）</summary>
    public System.Windows.Media.Brush StatusBrush =>
        ExecutionStatus == "executed" || Status == "executed"
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x67, 0xC2, 0x3A))
            : ExecutionStatus == "cancelled" || Status == "cancelled"
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0x6C, 0x6C))
                : Status == "expired"
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x90, 0x93, 0x99))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE6, 0xA2, 0x3C));

    public string EntryReasonLabel => EntryReason switch
    {
        "break_high" => "突破新高",
        "pullback_support" => "回踩支撑",
        "volume_breakout" => "放量突破",
        "ma_golden_cross" => "均线金叉",
        "bottom_reversal" => "底部反转",
        "sector_rotation" => "板块轮动",
        "news_catalyst" => "消息催化",
        "limit_up" => "涨停板",
        _ => string.IsNullOrEmpty(EntryReason) ? "-" : EntryReason
    };
}

/// <summary>
/// 自定义提醒数据模型
/// </summary>
public class CustomReminder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Type { get; set; } = "once";
    public string Time { get; set; } = "09:00";
    public string? Date { get; set; }
    public ObservableCollection<int> Weekdays { get; set; } = new() { 1, 2, 3, 4, 5 };
    public string? StockCode { get; set; }
    public string? StockName { get; set; }
    public string Content { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int RepeatBurstCount { get; set; } = 1;
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>气泡提醒的动作按钮（原版 DEFAULT_ACTIONS：✅ 完成 / ⏰ 稍后提醒，可勾选）</summary>
    public List<StockReview.Core.Services.ReminderAction>? Actions { get; set; }

    // ====== UI 显示属性（对齐原版 getTypeLabel/getStatusLabel/formatTriggerTime） ======

    public string TypeLabel => Type switch
    {
        "once" => "一次性",
        "daily" => "每日",
        "weekly" => "每周",
        _ => Type
    };

    public System.Windows.Media.Brush TypeTagBg => Type switch
    {
        "once" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xF4, 0xF5)),
        "daily" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEC, 0xF5, 0xFF)),
        "weekly" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFD, 0xF6, 0xEC)),
        _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xF4, 0xF5))
    };

    public System.Windows.Media.Brush TypeTagBorder => Type switch
    {
        "once" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD3, 0xD4, 0xD6)),
        "daily" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB3, 0xD8, 0xFF)),
        "weekly" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0xDA, 0xB1)),
        _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD3, 0xD4, 0xD6))
    };

    public System.Windows.Media.Brush TypeTagFg => Type switch
    {
        "once" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x90, 0x93, 0x99)),
        "daily" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x40, 0x9E, 0xFF)),
        "weekly" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE6, 0xA2, 0x3C)),
        _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x90, 0x93, 0x99))
    };

    public string StatusLabel => Status switch
    {
        "pending" => "待触发",
        "triggered" => "已触发",
        "done" => "已完成",
        "snoozed" => "稍后",
        _ => Status
    };

    /// <summary>状态背景色（el-tag 浅底配色：pending=info 灰 / triggered=warning / done=success / snoozed=info）</summary>
    public System.Windows.Media.Brush StatusTagBg => Status switch
    {
        "triggered" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFD, 0xF6, 0xEC)),
        "done" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xF9, 0xEB)),
        _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xF4, 0xF5))
    };

    /// <summary>状态文字色（与浅底配套）</summary>
    public System.Windows.Media.Brush StatusTagFg => Status switch
    {
        "triggered" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE6, 0xA2, 0x3C)),
        "done" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x67, 0xC2, 0x3A)),
        _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x90, 0x93, 0x99))
    };

    /// <summary>状态边框色（el-tag 浅边框）</summary>
    public System.Windows.Media.Brush StatusTagBorder => Status switch
    {
        "triggered" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFA, 0xEC, 0xD8)),
        "done" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE1, 0xF3, 0xD8)),
        _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE9, 0xE9, 0xEB))
    };

    public string TriggerTimeText
    {
        get
        {
            var time = string.IsNullOrEmpty(Time) ? "09:00" : Time;
            return Type switch
            {
                "once" => string.IsNullOrEmpty(Date) ? $"{time}（未设置日期）" : $"{Date} {time}",
                "daily" => $"每天 {time}",
                "weekly" when Weekdays != null && Weekdays.Count > 0 =>
                    $"{string.Join(" ", Weekdays.OrderBy(w => w).Select(w => $"周{new[] { "日", "一", "二", "三", "四", "五", "六" }[w]}"))} {time}",
                "weekly" => "未设置",
                _ => time
            };
        }
    }

    /// <summary>触发时间第一行：日期/星期（周一 周二 … / 每天 / yyyy-MM-dd）</summary>
    public string TriggerScheduleText => Type switch
    {
        "once" => string.IsNullOrEmpty(Date) ? "未设置日期" : Date,
        "daily" => "每天",
        "weekly" when Weekdays != null && Weekdays.Count > 0 =>
            string.Join(" ", Weekdays.OrderBy(w => w).Select(w => $"周{new[] { "日", "一", "二", "三", "四", "五", "六" }[w]}")),
        "weekly" => "未设置",
        _ => ""
    };

    /// <summary>触发时间第二行：HH:mm（有关联股票时附带名称）</summary>
    public string TriggerClockText
    {
        get
        {
            var time = string.IsNullOrEmpty(Time) ? "09:00" : Time;
            return string.IsNullOrEmpty(StockName) ? time : $"{time} · {StockName}";
        }
    }
}

/// <summary>
/// 宠物外观目录项
/// </summary>
public class PetCatalogItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Author { get; set; } = "";
    public int SpriteVersionNumber { get; set; } = 1;
    public int Version => SpriteVersionNumber;
    public bool IsInstalled { get; set; }
    public bool IsActive { get; set; }

    private string? _thumbnailPath;
    /// <summary>精灵缩略图路径（已安装=本地首帧裁剪；未安装=在线预览图缓存）</summary>
    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set
        {
            _thumbnailPath = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ThumbnailPath)));
        }
    }
}


