using System;
using System.Collections.Generic;
using Serilog;

namespace StockReview.Core.Services;

/// <summary>
/// 东八区时区统一入口：Windows 用 "China Standard Time"，
/// 非 Windows（IANA-only）回退自定义 +08:00 时区，避免 TimeZoneNotFoundException。
/// </summary>
public static class CnTimeZone
{
    private static readonly TimeZoneInfo Tz = Create();

    public static TimeZoneInfo Get => Tz;

    private static TimeZoneInfo Create()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.CreateCustomTimeZone("shanghai", TimeSpan.FromHours(8), "Shanghai", "Shanghai"); }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.CreateCustomTimeZone("shanghai", TimeSpan.FromHours(8), "Shanghai", "Shanghai"); }
    }
}

/// <summary>
/// 东八区市场时间服务 - 对应 marketTime.js（marketTime module）。
/// 所有时间分量均按 Asia/Shanghai 墙钟取值，不依赖系统本地时区，
/// 确保海外系统时区也能得到正确的交易时段判断。
/// </summary>
public class MarketTimeService : IMarketTimeService
{
    private static readonly TimeZoneInfo Shanghai = FindShanghaiTz();

    private static TimeZoneInfo FindShanghaiTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.CreateCustomTimeZone("shanghai", TimeSpan.FromHours(8), "Shanghai", "Shanghai"); }
    }

    // 节假日表（硬编码到 2028，超出回退「仅周末休市」+ 一次性告警）
    private static readonly Dictionary<int, string[]> HolidaysByYear = new()
    {
        [2026] = [
            "2026-01-01","2026-01-02","2026-01-03",
            "2026-02-16","2026-02-17","2026-02-18","2026-02-19","2026-02-20","2026-02-21","2026-02-22",
            "2026-04-04","2026-04-05","2026-04-06",
            "2026-05-01","2026-05-02","2026-05-03","2026-05-04","2026-05-05",
            "2026-06-19","2026-06-20","2026-06-21",
            "2026-09-25","2026-09-26","2026-09-27",
            "2026-10-01","2026-10-02","2026-10-03","2026-10-04","2026-10-05","2026-10-06","2026-10-07","2026-10-08"
        ],
        [2027] = [
            "2027-01-01","2027-01-02","2027-01-03",
            "2027-02-06","2027-02-07","2027-02-08","2027-02-09","2027-02-10","2027-02-11","2027-02-12",
            "2027-04-05","2027-04-06","2027-04-07",
            "2027-05-01","2027-05-02","2027-05-03","2027-05-04","2027-05-05",
            "2027-06-09","2027-06-10","2027-06-11",
            "2027-09-15","2027-09-16","2027-09-17",
            "2027-10-01","2027-10-02","2027-10-03","2027-10-04","2027-10-05","2027-10-06","2027-10-07","2027-10-08"
        ],
        [2028] = [
            "2028-01-01","2028-01-02","2028-01-03",
            "2028-01-26","2028-01-27","2028-01-28","2028-01-29","2028-01-30","2028-01-31","2028-02-01",
            "2028-04-04","2028-04-05","2028-04-06",
            "2028-05-01","2028-05-02","2028-05-03","2028-05-04","2028-05-05",
            "2028-05-28","2028-05-29","2028-05-30",
            "2028-10-04","2028-10-05","2028-10-06",
            "2028-10-09","2028-10-10","2028-10-11","2028-10-12","2028-10-13"
        ]
    };

    // 标准节假日名称按「月-日」匹配
    private static readonly Dictionary<string, string> HolidayNames = new()
    {
        ["01-01"] = "元旦",
        ["04-04"] = "清明节",
        ["04-05"] = "清明节",
        ["05-01"] = "劳动节",
        ["10-01"] = "国庆节",
        ["10-02"] = "国庆节",
        ["10-03"] = "国庆节"
    };

    private readonly HashSet<string> _holidays;
    private readonly int _maxSupportedYear;
    private bool _outOfRangeWarned;

    public MarketTimeService()
    {
        _holidays = new HashSet<string>();
        foreach (var list in HolidaysByYear.Values)
            foreach (var d in list) _holidays.Add(d);
        _maxSupportedYear = 2028;
    }

    private readonly record struct ShanghaiClock(int Year, int Month, int Day, int Hour, int Minute, int DayOfWeek);

    /// <summary>把输入时刻转换为东八区墙钟分量（不依赖系统时区，对应 getShanghaiParts）</summary>
    private ShanghaiClock ShanghaiParts(DateTime instant)
    {
        DateTime utc = instant.Kind switch
        {
            DateTimeKind.Utc => instant,
            DateTimeKind.Local => instant.ToUniversalTime(),
            _ => DateTime.SpecifyKind(instant, DateTimeKind.Local).ToUniversalTime()
        };
        var t = TimeZoneInfo.ConvertTimeFromUtc(utc, Shanghai);
        return new ShanghaiClock(t.Year, t.Month, t.Day, t.Hour, t.Minute, (int)t.DayOfWeek);
    }

    private string FormatNoArg(DateTime d) => FormatDate(d);

    public bool IsHoliday(DateTime d)
    {
        var dateStr = FormatDate(d);
        if (IsBeyondHolidayTable(d))
        {
            if (!_outOfRangeWarned)
            {
                _outOfRangeWarned = true;
                Log.Warning("[marketTime] 节假日表仅覆盖至 {Year} 年，当前日期 {Date} 已超出，自动按「仅周末休市」处理，请尽快更新 HOLIDAYS 表", _maxSupportedYear, dateStr);
            }
            return false;
        }
        return _holidays.Contains(dateStr);
    }

    private bool IsBeyondHolidayTable(DateTime d) => ShanghaiParts(d).Year > _maxSupportedYear;

    public bool IsWeekend(DateTime d) => ShanghaiParts(d).DayOfWeek % 6 == 0;

    public bool IsTradingDay(DateTime date) => !IsWeekend(date) && !IsHoliday(date);

    public DateTime GetNextTradingDay(DateTime date)
    {
        var next = date.AddDays(1);
        while (!IsTradingDay(next)) next = next.AddDays(1);
        return next;
    }

    public DateTime GetPreviousTradingDay(DateTime date)
    {
        var prev = date.AddDays(-1);
        while (!IsTradingDay(prev)) prev = prev.AddDays(-1);
        return prev;
    }

    public string FormatDate(DateTime date)
    {
        var p = ShanghaiParts(date);
        return $"{p.Year:D4}-{p.Month:D2}-{p.Day:D2}";
    }

    public decimal GetHours(DateTime date)
    {
        var p = ShanghaiParts(date);
        return p.Hour + p.Minute / 60m;
    }

    public int GetDay(DateTime date) => ShanghaiParts(date).DayOfWeek;

    public string? GetHolidayName()
    {
        var now = DateTime.Now;
        var dateStr = FormatDate(now);
        var p = ShanghaiParts(now);
        if (!HolidaysByYear.TryGetValue(p.Year, out var yearHolidays) || Array.IndexOf(yearHolidays, dateStr) < 0)
            return null;

        var monthDay = dateStr[5..];
        if (HolidayNames.TryGetValue(monthDay, out var named)) return named;

        var idx = Array.IndexOf(yearHolidays, dateStr);
        if (yearHolidays.Length > 0 && dateStr.StartsWith(yearHolidays[0][..8]))
            return idx == 0 ? "春节" : "春节假期";

        return "节假日";
    }

    public (IntradayPhase Phase, string Label) GetIntradayPhase(DateTime now)
    {
        var h = GetHours(now);
        // 先在交易日/时段层面做兜底：非交易日仍返回时段但标记 PreOpen（与 JS 语义一致，该值仅在交易日被有意义消费）
        if (h < 9.15m) return (IntradayPhase.PreOpen, "盘前");
        if (h < 9.25m) return (IntradayPhase.CallAuction, "开盘集合竞价");
        if (h < 9.5m) return (IntradayPhase.PreMatch, "开盘竞价");
        if (h < 11.5m) return (IntradayPhase.Morning, "上午");
        if (h < 13m) return (IntradayPhase.Lunch, "午休");
        if (h < 14.95m) return (IntradayPhase.Afternoon, "下午");
        if (h < 15m) return (IntradayPhase.CloseAuction, "收盘集合竞价");
        return (IntradayPhase.Closed, "已收盘");
    }

    public DateTime GetNow() => DateTime.Now;
}