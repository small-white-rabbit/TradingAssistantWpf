using System;
using System.Collections.Generic;
using Xunit;
using StockReview.Core.Services;

namespace StockReview.Tests.PlanScheduler;

/// <summary>
/// DetectRapidByTimeTrail 时间窗口快速涨跌检测测试。
/// 与快照计数窗口（DetectMultiWindowRapid）的区别：
/// - 按真实时间戳划定窗口，无 18 根快照预热盲区（开盘/订阅启动 30 秒后即可判定）；
/// - 午休连贯：窗口基于交易连续时间（剥离 11:30-13:00 空白），跨午休涨跌幅正常判定；
/// - CanEmitRapidSignal 支持恶化升级穿透冷却（幅度 ≥ 上次 1.5 倍时穿透）。
/// 使用 C# 默认窗口（18/60/90 bars × 10s = 3/10/15 分钟，1%/2%/3%）。
/// </summary>
public class DetectRapidByTimeTrailTests
{
    private static PlanSchedulerService NewService()
    {
        return new PlanSchedulerService(
            null!, null!, null, null!, null!, null!, null!, null!, null!, null!, null!, null!);
    }

    private static void Feed(PlanSchedulerService svc, string code, DateTime start,
        params (decimal price, int secOffset)[] points)
    {
        foreach (var (price, offset) in points)
        {
            svc.RecordLiveTrail(code, price, start.AddSeconds(offset));
        }
    }

    [Fact]
    public void T1_ThreeMinDrop_TriggeredWithoutSnapshotWarmup()
    {
        // 用户场景复现：3 分钟内跌 1.2%。
        // 旧快照窗口需 18 根（3 分钟预热）才开始判定；时间窗口用现有轨迹即时判定。
        var svc = NewService();
        var t = new DateTime(2026, 1, 5, 10, 0, 0);
        var points = new List<(decimal, int)>();
        for (var i = 0; i < 12; i++) points.Add((100m, i * 10));          // 前 2 分钟平稳
        for (var i = 0; i <= 6; i++) points.Add((100m - i * 0.2m, 120 + i * 10)); // 后 1 分钟滑落至 98.8
        Feed(svc, "600000", t, points.ToArray());

        var r = svc.DetectRapidByTimeTrail("600000");

        Assert.NotNull(r);
        Assert.Equal("down", r!.Direction);
        Assert.Equal(18, r.WindowBars);
        Assert.Equal("脉冲", r.WindowLabel);
        Assert.Equal(-1.2m, r.ChangePct, 4);
    }

    [Fact]
    public void T2_OpeningWarmup_TriggerAfter30Seconds()
    {
        // 开盘预热消除：9:30 开盘 40 秒内跌 1.6% → 立即触发（旧机制需等 3 分钟快照）
        var svc = NewService();
        var open = new DateTime(2026, 1, 5, 9, 30, 0);
        Feed(svc, "600000", open, (100m, 0), (99.5m, 5), (98.4m, 40));

        var r = svc.DetectRapidByTimeTrail("600000");

        Assert.NotNull(r);
        Assert.Equal("down", r!.Direction);
        Assert.Equal(-1.6m, r.ChangePct, 4);
    }

    [Fact]
    public void T3_RapidDropWithin30Seconds_Triggered()
    {
        // 滑动窗口任意子区间语义：30 秒内直接跌 1% 立即触发（无需等满 3 分钟）
        var svc = NewService();
        var t = new DateTime(2026, 1, 5, 10, 0, 0);
        Feed(svc, "600000", t, (100m, 0), (100m, 5), (99m, 30));

        var r = svc.DetectRapidByTimeTrail("600000");

        Assert.NotNull(r);
        Assert.Equal("down", r!.Direction);
        Assert.Equal(-1.0m, r.ChangePct, 4);
    }

    [Fact]
    public void T3b_RiseThenDrop_HeadTailOffsetStillTriggered()
    {
        // 先涨后跌首尾抵消：3 分钟窗口首尾均为 100（首尾比较 = 0%），
        // 但窗口内 102（高点）→ 100（末点）= -1.96% 回撤 ≥ 1% → 触发 down
        var svc = NewService();
        var t = new DateTime(2026, 1, 5, 10, 0, 0);
        var points = new List<(decimal, int)> { (100m, 0), (101m, 30), (102m, 60), (101.5m, 90), (100.5m, 120), (100m, 150) };
        Feed(svc, "600000", t, points.ToArray());

        var r = svc.DetectRapidByTimeTrail("600000");

        Assert.NotNull(r);
        Assert.Equal("down", r!.Direction);
        Assert.Equal(-1.9608m, r.ChangePct, 3);
    }

    [Fact]
    public void T3c_SinglePointWindow_NoTrigger()
    {
        // 窗口内仅 1 个有效轨迹点：无法判定
        var svc = NewService();
        var t = new DateTime(2026, 1, 5, 10, 0, 0);
        Feed(svc, "600000", t, (100m, 0));

        Assert.Null(svc.DetectRapidByTimeTrail("600000"));
    }

    [Fact]
    public void T4_LunchBreak_ContinuousTimeline()
    {
        // 午休连贯：行情数据本身连贯，剥离午休空白（11:30-13:00）后上午尾与下午首相邻。
        // 上午收盘最后一笔 11:29:59 的 105 元与下午首笔 13:00:00 的 103.5 元，
        // 在交易连续时间轴上仅隔 1 秒（相邻数据点），3 分钟窗口包含上午尾点：
        // 105 → 103.5 = -1.43%，跨午休下跌不漏检
        var svc = NewService();
        var t = new DateTime(2026, 1, 5, 11, 29, 59);
        Feed(svc, "600000", t,
            (105m, 0),        // 11:29:59 上午收盘尾点（sessionTime 11:29:59）
            (103.5m, 5461),   // 13:00:00 下午首点（sessionTime 11:30:00，与上午尾仅隔 1 秒）
            (103.5m, 5491));  // 13:00:30（sessionTime 11:30:30）

        var r = svc.DetectRapidByTimeTrail("600000");

        Assert.NotNull(r);
        Assert.Equal("down", r!.Direction);
        Assert.Equal("脉冲", r.WindowLabel);
        Assert.Equal(-1.4286m, r.ChangePct, 3); // 基于 11:29:59 上午尾点（剥离午休后相邻）
    }

    [Fact]
    public void T4b_MiddayPoint_DoesNotBreakOrdering()
    {
        // 午休中段（12:xx）的点贴到 11:30，不破坏交易连续时间轴的升序：
        // 若保持 12:30 原值，会晚于 13:00 映射出的 11:30+，导致窗口定位错乱
        var svc = NewService();
        var t = new DateTime(2026, 1, 5, 11, 29, 59);
        Feed(svc, "600000", t,
            (100m, 0),        // 11:29:59 上午尾（sessionTime 11:29:59）
            (100m, 1801),     // 12:00:00 午休中（sessionTime 11:30:00）
            (98.5m, 5461),    // 13:00:00 下午首（sessionTime 11:30:00，-1.5%）
            (98.5m, 5491));   // 13:00:30（sessionTime 11:30:30）

        var r = svc.DetectRapidByTimeTrail("600000");

        Assert.NotNull(r);
        Assert.Equal("down", r!.Direction);
        Assert.Equal(-1.5m, r.ChangePct, 4);
    }

    [Fact]
    public void T5_SmallFluctuation_NoTrigger()
    {
        // 4 分钟小幅波动（±0.2%）：所有窗口首尾与波动率均不足阈值 → null
        var svc = NewService();
        var t = new DateTime(2026, 1, 5, 10, 0, 0);
        var prices = new decimal[]
        {
            100, 100.1m, 99.9m, 100.05m, 99.95m, 100.1m, 99.92m, 100.03m,
            99.97m, 100.08m, 99.94m, 100.02m, 99.98m, 100.06m, 99.93m, 100.01m,
            99.99m, 100.04m, 99.96m, 100, 100.05m, 99.95m, 100.02m, 99.98m, 100
        };
        var points = new List<(decimal, int)>();
        for (var i = 0; i < prices.Length; i++) points.Add((prices[i], i * 10));
        Feed(svc, "600000", t, points.ToArray());

        Assert.Null(svc.DetectRapidByTimeTrail("600000"));
    }

    [Fact]
    public void T6_EscalationPiercesCooldown()
    {
        // 恶化升级穿透冷却：冷却期内幅度 ≥ 上次 1.5 倍 → 穿透；不足 → 拦截
        var svc = NewService();
        var first = new RapidMatch { Direction = "down", ChangePct = -1.0m, WindowBars = 18, WindowLabel = "脉冲", CooldownMs = 5 * 60 * 1000, WindowMinutes = 3 };
        svc.CommitRapidSignalState("p1", "down", first);

        var worse16 = new RapidMatch { Direction = "down", ChangePct = -1.6m, WindowBars = 18, WindowLabel = "脉冲", CooldownMs = 5 * 60 * 1000, WindowMinutes = 3 };
        var worse14 = new RapidMatch { Direction = "down", ChangePct = -1.4m, WindowBars = 18, WindowLabel = "脉冲", CooldownMs = 5 * 60 * 1000, WindowMinutes = 3 };

        Assert.True(svc.CanEmitRapidSignal("p1", "down", worse16));   // 1.6 ≥ 1.0×1.5 → 穿透
        Assert.False(svc.CanEmitRapidSignal("p1", "down", worse14));  // 1.4 < 1.5 → 冷却拦截
        Assert.True(svc.CanEmitRapidSignal("p2", "down", worse14));   // 新计划无冷却记录 → 放行
    }

    [Fact]
    public void T7_TrailDedupe_SamePriceWithin5sIgnored()
    {
        // 价格不变且距上点不足 5 秒：不追加轨迹点（防内存膨胀，无增量信息）
        var svc = NewService();
        var t = new DateTime(2026, 1, 5, 10, 0, 0);
        Feed(svc, "600000", t, (100m, 0), (100m, 1), (100m, 2), (100m, 3));

        // 仅 1 个有效轨迹点 → 检测返回 null
        Assert.Null(svc.DetectRapidByTimeTrail("600000"));
    }

    [Fact]
    public void T8_PreMatchAuctionData_CountsTowardWindow()
    {
        // 开盘盲区消除：9:25-9:30 集合竞价尾段数据直接计入轨迹。
        // 9:25 竞价匹配价 100 → 9:26 跌至 98.9（-1.1%）：竞价阶段即可触发，无需等 9:30
        var svc = NewService();
        var auction = new DateTime(2026, 1, 5, 9, 25, 0);
        Feed(svc, "600000", auction, (100m, 0), (100m, 30), (98.9m, 60));

        var r = svc.DetectRapidByTimeTrail("600000");

        Assert.NotNull(r);
        Assert.Equal("down", r!.Direction);
        Assert.Equal(-1.1m, r.ChangePct, 4);
    }
}
