// MarketTimeService 单元测试 - 验证上海时区/节假日/交易日判断
using System;
using StockReview.Core.Services;
using Xunit;

namespace StockReview.Tests.Core;

public class MarketTimeServiceTests
{
    private readonly MarketTimeService _svc = new();

    // ===== 时区转换 =====

    [Fact]
    public void CnTimeZone_IsShanghaiPlus8()
    {
        var tz = CnTimeZone.Get;
        Assert.Equal(TimeSpan.FromHours(8), tz.BaseUtcOffset);
    }

    [Fact]
    public void GetNow_ReturnsShanghaiTime()
    {
        var now = _svc.GetNow();
        // 应为东八区当前时间（不依赖系统时区）
        var expected = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, CnTimeZone.Get);
        Assert.Equal(expected.ToString("yyyy-MM-dd HH"), now.ToString("yyyy-MM-dd HH"));
    }

    [Fact]
    public void FormatDate_ReturnsShanghaiDate()
    {
        // UTC 2026-08-27 16:00 = 上海 2026-08-28 00:00
        var utc = new DateTime(2026, 8, 27, 16, 0, 0, DateTimeKind.Utc);
        Assert.Equal("2026-08-28", _svc.FormatDate(utc));
    }

    [Fact]
    public void FormatDate_Boundary_Midnight()
    {
        // UTC 2026-08-27 15:59 = 上海 2026-08-27 23:59
        var utc = new DateTime(2026, 8, 27, 15, 59, 0, DateTimeKind.Utc);
        Assert.Equal("2026-08-27", _svc.FormatDate(utc));

        // UTC 2026-08-27 16:00 = 上海 2026-08-28 00:00
        utc = new DateTime(2026, 8, 27, 16, 0, 0, DateTimeKind.Utc);
        Assert.Equal("2026-08-28", _svc.FormatDate(utc));
    }

    // ===== 周末判断 =====

    [Theory]
    // 2026-08-22 周六（UTC 02:00 = 上海 10:00）
    [InlineData(2026, 8, 22, 2, 0, true)]
    // 2026-08-23 周日
    [InlineData(2026, 8, 23, 2, 0, true)]
    // 2026-08-25 周二
    [InlineData(2026, 8, 25, 2, 0, false)]
    // 2026-08-27 周四
    [InlineData(2026, 8, 27, 2, 0, false)]
    public void IsWeekend_ShanghaiCalendar(int y, int m, int d, int h, int min, bool expected)
    {
        var utc = new DateTime(y, m, d, h, min, 0, DateTimeKind.Utc);
        Assert.Equal(expected, _svc.IsWeekend(utc));
    }

    // ===== 节假日判断 =====

    [Theory]
    [InlineData("2026-01-01", true)]   // 元旦
    [InlineData("2026-02-16", true)]   // 春节首日
    [InlineData("2026-05-01", true)]   // 劳动节
    [InlineData("2026-10-01", true)]   // 国庆
    [InlineData("2026-08-25", false)]  // 普通周二
    [InlineData("2026-08-27", false)]  // 普通周四
    public void IsHoliday_2026(string dateStr, bool expected)
    {
        var d = DateTime.Parse(dateStr, System.Globalization.CultureInfo.InvariantCulture);
        // 确保用上海时区解析（dateStr 是日历日，无时区分量）
        Assert.Equal(expected, _svc.IsHoliday(d));
    }

    [Fact]
    public void IsTradingDay_Holiday_NotTrading()
    {
        var d = DateTime.Parse("2026-10-01", System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(_svc.IsTradingDay(d));
    }

    [Fact]
    public void IsTradingDay_Weekend_NotTrading()
    {
        var d = DateTime.Parse("2026-08-22", System.Globalization.CultureInfo.InvariantCulture); // 周六
        Assert.False(_svc.IsTradingDay(d));
    }

    [Fact]
    public void IsTradingDay_Weekday_NonHoliday_Trading()
    {
        var d = DateTime.Parse("2026-08-27", System.Globalization.CultureInfo.InvariantCulture); // 周四
        Assert.True(_svc.IsTradingDay(d));
    }

    // ===== 交易日导航 =====

    [Fact]
    public void GetNextTradingDay_SkipsWeekend()
    {
        // 周五 → 下周一
        var fri = DateTime.Parse("2026-08-28", System.Globalization.CultureInfo.InvariantCulture);
        var next = _svc.GetNextTradingDay(fri);
        Assert.Equal("2026-08-31", _svc.FormatDate(next)); // 周一
    }

    [Fact]
    public void GetNextTradingDay_SkipsHoliday()
    {
        // 国庆前一天 → 国庆后第一个交易日
        var sep30 = DateTime.Parse("2026-09-30", System.Globalization.CultureInfo.InvariantCulture);
        var next = _svc.GetNextTradingDay(sep30);
        // 10-01~10-08 是国庆+中秋假期，10-09 是周五
        Assert.Equal("2026-10-09", _svc.FormatDate(next));
    }

    [Fact]
    public void GetPreviousTradingDay_SkipsWeekend()
    {
        // 周一 → 上周五
        var mon = DateTime.Parse("2026-08-31", System.Globalization.CultureInfo.InvariantCulture);
        var prev = _svc.GetPreviousTradingDay(mon);
        Assert.Equal("2026-08-28", _svc.FormatDate(prev)); // 周五
    }

    // ===== 盘中时段 =====

    [Theory]
    // 代码用十进制 h=Hour+Minute/60 比较：9.15m=9.15h(≈09:09), 9.25m=9.25h(≈09:15), 9.5m=9.5h(09:30)
    // 盘前: h<9.15 → 上海 <09:09 → UTC <01:09
    [InlineData(-1, 0, "盘前")]         // UTC 00:00 = 上海 08:00
    // 开盘集合竞价: 9.15<=h<9.25 → 上海 09:09-09:14 → UTC 01:09-01:14
    [InlineData(0, 10, "开盘集合竞价")]  // UTC 01:10 = 上海 09:10
    // 开盘竞价: 9.25<=h<9.5 → 上海 09:15-09:29 → UTC 01:15-01:29
    [InlineData(0, 20, "开盘竞价")]      // UTC 01:20 = 上海 09:20
    // 上午: 9.5<=h<11.5 → 上海 09:30-11:29 → UTC 01:30-03:29
    [InlineData(1, 0, "上午")]          // UTC 02:00 = 上海 10:00
    // 午休: 11.5<=h<13 → 上海 11:30-12:59 → UTC 03:30-04:59
    [InlineData(3, 40, "午休")]         // UTC 04:40 = 上海 12:40
    // 下午: 13<=h<14.95 → 上海 13:00-14:56 → UTC 05:00-06:56
    [InlineData(5, 0, "下午")]          // UTC 06:00 = 上海 14:00
    // 收盘集合竞价: 14.95<=h<15 → 上海 14:57-14:59 → UTC 06:57-06:59
    [InlineData(5, 58, "收盘集合竞价")]  // UTC 06:58 = 上海 14:58
    // 已收盘: h>=15 → 上海 15:00+ → UTC 07:00+
    [InlineData(7, 30, "已收盘")]        // UTC 08:30 = 上海 16:30
    public void GetIntradayPhase_ReturnsCorrectPhase(int utcHourOffset, int min, string expectedLabel)
    {
        var utc = new DateTime(2026, 8, 27, 1 + utcHourOffset, min, 0, DateTimeKind.Utc);
        var (_, label) = _svc.GetIntradayPhase(utc);
        Assert.Equal(expectedLabel, label);
    }
}
