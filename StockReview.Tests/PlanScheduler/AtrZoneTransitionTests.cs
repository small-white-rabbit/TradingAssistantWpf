using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using StockReview.Core.Services;

namespace StockReview.Tests.PlanScheduler;

/// <summary>
/// FilterAtrZoneTransitionSignals ATR 类区间信号状态转换门控测试。
/// 根因复现：止损条件是持续状态（价格在线下一直为真），旧逻辑"条件为真即触发+15分钟冷却"
/// 导致同批股票在快照预热完成的同一 tick 集体爆发、且冷却到期后周期性再爆发。
/// 状态转换语义：新穿越提醒一次；存量静默初始化；持续在区间内静默（恶化≥1%穿透）；离开区间重置。
/// </summary>
public class AtrZoneTransitionTests
{
    private static PlanSchedulerService NewService()
    {
        return new PlanSchedulerService(
            null!, null!, null, null!, null!, null!, null!, null!, null!, null!, null!, null!);
    }

    private static SellSignalInfo AtrStop(decimal current, decimal line)
        => new() { Type = "atr_stop_loss", Label = "ATR止损", CurrentPrice = current, LevelPrice = line };

    private static void FeedTrail(PlanSchedulerService svc, string code, DateTime start,
        params (decimal price, int secOffset)[] points)
    {
        foreach (var (price, offset) in points)
            svc.RecordLiveTrail(code, price, start.AddSeconds(offset));
    }

    [Fact]
    public void T1_NewCrossDown_Triggered()
    {
        // 新跌破：轨迹中先有高于止损线的价格（10.2 > 线 10.0），随后跌破 → 提醒
        var svc = NewService();
        var t = new DateTime(2026, 1, 5, 10, 0, 0);
        FeedTrail(svc, "600000", t, (10.2m, 0), (10.1m, 30), (9.9m, 60));

        var out1 = svc.FilterAtrZoneTransitionSignals("p1", "600000", new List<SellSignalInfo> { AtrStop(9.9m, 10.0m) });

        Assert.Single(out1);
        Assert.Equal("atr_stop_loss", out1[0].Type);
    }

    [Fact]
    public void T2_ExistingBelowAtStartup_SilentInit()
    {
        // 存量：启动/预热时已在线下（轨迹从未高于止损线）→ 静默初始化，不提醒
        var svc = NewService();
        var t = new DateTime(2026, 1, 5, 10, 0, 0);
        FeedTrail(svc, "600000", t, (9.9m, 0), (9.8m, 30), (9.85m, 60));

        var out1 = svc.FilterAtrZoneTransitionSignals("p1", "600000", new List<SellSignalInfo> { AtrStop(9.85m, 10.0m) });

        Assert.Empty(out1);
    }

    [Fact]
    public void T3_StayingInZone_NoWorsening_Silent()
    {
        // 持续在区间内且未恶化（现价 9.89 vs 上次提醒价 9.90，仅 -0.1%）→ 静默
        var svc = NewService();
        var t = new DateTime(2026, 1, 5, 10, 0, 0);
        FeedTrail(svc, "600000", t, (10.2m, 0), (9.9m, 60));

        var out1 = svc.FilterAtrZoneTransitionSignals("p1", "600000", new List<SellSignalInfo> { AtrStop(9.9m, 10.0m) });
        Assert.Single(out1); // 首次跌破提醒

        var out2 = svc.FilterAtrZoneTransitionSignals("p1", "600000", new List<SellSignalInfo> { AtrStop(9.89m, 10.0m) });
        Assert.Empty(out2); // 持续在线下、幅度几乎未变 → 静默
    }

    [Fact]
    public void T4_Worsening_Penetrate()
    {
        // 持续在区间内且较上次提醒价再跌 ≥1%（9.9 → 9.78 = -1.21%）→ 穿透再提醒
        var svc = NewService();
        var t = new DateTime(2026, 1, 5, 10, 0, 0);
        FeedTrail(svc, "600000", t, (10.2m, 0), (9.9m, 60));

        var out1 = svc.FilterAtrZoneTransitionSignals("p1", "600000", new List<SellSignalInfo> { AtrStop(9.9m, 10.0m) });
        Assert.Single(out1);

        var out2 = svc.FilterAtrZoneTransitionSignals("p1", "600000", new List<SellSignalInfo> { AtrStop(9.78m, 9.98m) });
        Assert.Single(out2); // 恶化穿透
    }

    [Fact]
    public void T5_LeaveZoneThenReenter_TriggeredAgain()
    {
        // 回升离开区间（信号消失）→ 状态重置；再次跌破 → 重新提醒
        var svc = NewService();
        var t = new DateTime(2026, 1, 5, 10, 0, 0);
        FeedTrail(svc, "600000", t, (10.2m, 0), (9.9m, 60), (10.1m, 120), (9.95m, 180));

        var out1 = svc.FilterAtrZoneTransitionSignals("p1", "600000", new List<SellSignalInfo> { AtrStop(9.9m, 10.0m) });
        Assert.Single(out1); // 首次跌破

        var out2 = svc.FilterAtrZoneTransitionSignals("p1", "600000", new List<SellSignalInfo>()); // 信号消失（回升）
        Assert.Empty(out2);

        var out3 = svc.FilterAtrZoneTransitionSignals("p1", "600000", new List<SellSignalInfo> { AtrStop(9.95m, 10.0m) });
        Assert.Single(out3); // 重置后再次跌破 → 重新提醒
    }

    [Fact]
    public void T6_NonAtrSignals_Untouched()
    {
        // 非 ATR 类信号不受门控影响，原样保留
        var svc = NewService();
        var signals = new List<SellSignalInfo>
        {
            new() { Type = "vwap_rejection", Label = "均价线 rejection", CurrentPrice = 10m },
            new() { Type = "break_ma5", Label = "破MA5", CurrentPrice = 10m }
        };

        var out1 = svc.FilterAtrZoneTransitionSignals("p1", "600000", signals);

        Assert.Equal(2, out1.Count);
    }

    [Fact]
    public void T7_TakeProfit_CrossUpTriggered_WorsenUpward()
    {
        // ATR 止盈（区间在线上方）：轨迹先有低于线的点（从下方穿越上来）→ 提醒；
        // 持续在线上方且再涨 ≥1% → 穿透再提醒
        var svc = NewService();
        var t = new DateTime(2026, 1, 5, 10, 0, 0);
        FeedTrail(svc, "600000", t, (10.0m, 0), (10.3m, 60), (10.42m, 120));

        var up = new SellSignalInfo { Type = "atr_take_profit", Label = "ATR止盈", CurrentPrice = 10.3m, LevelPrice = 10.2m };
        var out1 = svc.FilterAtrZoneTransitionSignals("p1", "600000", new List<SellSignalInfo> { up });
        Assert.Single(out1); // 新上穿提醒

        var up2 = new SellSignalInfo { Type = "atr_take_profit", Label = "ATR止盈", CurrentPrice = 10.42m, LevelPrice = 10.2m };
        var out2 = svc.FilterAtrZoneTransitionSignals("p1", "600000", new List<SellSignalInfo> { up2 });
        Assert.Single(out2); // 再涨 1.16% → 穿透
    }

    [Fact]
    public void T8_MultiplePlans_SameStock_IndependentStates()
    {
        // 同一股票两个计划（不同 planId）各自独立记录状态，互不干扰
        var svc = NewService();
        var t = new DateTime(2026, 1, 5, 10, 0, 0);
        FeedTrail(svc, "600000", t, (10.2m, 0), (9.9m, 60));

        var o1 = svc.FilterAtrZoneTransitionSignals("p1", "600000", new List<SellSignalInfo> { AtrStop(9.9m, 10.0m) });
        var o2 = svc.FilterAtrZoneTransitionSignals("p2", "600000", new List<SellSignalInfo> { AtrStop(9.9m, 10.0m) });

        Assert.Single(o1);
        Assert.Single(o2); // 计划 p2 同样是新跌破，各自提醒一次

        // p1 已在区间内：再次检测静默；p2 也已在区间内：静默
        var o1b = svc.FilterAtrZoneTransitionSignals("p1", "600000", new List<SellSignalInfo> { AtrStop(9.9m, 10.0m) });
        var o2b = svc.FilterAtrZoneTransitionSignals("p2", "600000", new List<SellSignalInfo> { AtrStop(9.9m, 10.0m) });
        Assert.Empty(o1b);
        Assert.Empty(o2b);
    }
}
