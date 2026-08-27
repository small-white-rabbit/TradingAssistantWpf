using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using StockReview.Core.Services;
using StockReview.Core.Data;
using StockReview.Core.MarketData;
using StockReview.Core.Futu;

namespace StockReview.Tests.PlanScheduler;

/// <summary>
/// DetectMultiWindowRapid 跨语言回归测试。
/// 算法逻辑对应原 Electron 源码 src/stores/planScheduler.js:3503 detectMultiWindowRapid（逐行移植）。
/// 本测试隔离验证「算法翻译正确性」：两边统一使用 JS 原版默认窗口配置。
/// 注意：C# 现版 MonitorConfig.RapidWindows 默认值与 JS 原版不同（见末尾 DivergesFromJs 测试）。
/// </summary>
public class DetectMultiWindowRapidTests
{
    // JS 原版默认窗口（src/stores/planScheduler.js:57），按分钟设计
    private static readonly RapidWindow[] JsDefaultWindows =
    {
        new() { Bars = 9,   Pct = 1.0m, Label = "脉冲",     CooldownMs = 2 * 60 * 1000 },
        new() { Bars = 30,  Pct = 2.0m, Label = "中速",     CooldownMs = 3 * 60 * 1000 },
        new() { Bars = 60,  Pct = 3.0m, Label = "慢牛",     CooldownMs = 5 * 60 * 1000 },
        new() { Bars = 120, Pct = 4.0m, Label = "持续推升", CooldownMs = 10 * 60 * 1000 },
    };

    /// <summary>
    /// 构造函数所有依赖传 null（构造函数仅做字段赋值，不立即解引用），
    /// 随后用 JS 原版窗口覆盖 Config.RapidWindows，隔离默认配置差异对算法验证的干扰。
    /// </summary>
    private static PlanSchedulerService NewServiceWithJsWindows()
    {
        var svc = new PlanSchedulerService(
            null!, null!, null, null!, null!, null!, null!, null!, null!, null!, null!, null!);
        svc.Config.RapidWindows.Clear();
        svc.Config.RapidWindows.AddRange(JsDefaultWindows);
        return svc;
    }

    private static List<PriceSnapshot> Snaps(params decimal[] prices)
    {
        var t = new DateTime(2026, 1, 1, 9, 30, 0);
        return prices.Select((p, i) => new PriceSnapshot
        {
            Price = p,
            Timestamp = t.AddSeconds(i * 10)
        }).ToList();
    }

    [Fact]
    public void S1_Up_MidWindow()
    {
        // 35 快照：前 30 平稳 @100，末 5 根 100→103（+3%），命中「中速」窗口(30 bars, pct2%)
        var svc = NewServiceWithJsWindows();
        var prices = Enumerable.Repeat(100m, 30)
            .Concat(new decimal[] { 100.5m, 101m, 101.5m, 102m, 102.5m, 103m })
            .ToArray();
        var r = svc.DetectMultiWindowRapid(Snaps(prices));

        Assert.NotNull(r);
        Assert.Equal("up", r!.Direction);
        Assert.Equal(30, r.WindowBars);
        Assert.Equal("中速", r.WindowLabel);
        Assert.Equal(3.0m, r.ChangePct);
        Assert.Equal(180000, r.CooldownMs);
    }

    [Fact]
    public void S2_Down_PulseShortWindow()
    {
        // 12 快照：末 9 根 100→90（-10%），命中「脉冲」窗口(9 bars, pct1%)；
        // ratio=10>2 → 即使更长窗口命中也优先选最短窗口（更及时）
        var svc = NewServiceWithJsWindows();
        var r = svc.DetectMultiWindowRapid(Snaps(
            100, 100, 100, 100, 99, 98, 97, 96, 95, 94, 93, 90));

        Assert.NotNull(r);
        Assert.Equal("down", r!.Direction);
        Assert.Equal(9, r.WindowBars);
        Assert.Equal("脉冲", r.WindowLabel);
        Assert.Equal(-10.0m, r.ChangePct);
        Assert.Equal(120000, r.CooldownMs);
    }

    [Fact]
    public void S3_NoTrigger_OnSmallFluctuation()
    {
        // 20 快照小幅波动（±0.2），所有窗口 changePct 与 volatilityPct 均不足阈值 → null
        var svc = NewServiceWithJsWindows();
        var r = svc.DetectMultiWindowRapid(Snaps(
            100, 100.1m, 99.9m, 100.05m, 99.95m, 100.1m, 99.92m, 100.03m, 99.97m, 100.08m,
            99.94m, 100.02m, 99.98m, 100.06m, 99.93m, 100.01m, 99.99m, 100.04m, 99.96m, 100));

        Assert.Null(r);
    }

    [Fact]
    public void S4_VolatilityFallbackDirection()
    {
        // 12 快照：末 9 根首尾 100→100.3（+0.3% 不足 pct1%），但中间剧烈波动(80~120)
        // → volatilityPct≥1 触发兜底方向判定（用 lastPrice vs firstPrice 定方向 = up）
        var svc = NewServiceWithJsWindows();
        var r = svc.DetectMultiWindowRapid(Snaps(
            100, 100, 100, 100, 120, 80, 100.3m, 100.3m, 100.3m, 100.3m, 100.3m, 100.3m));

        Assert.NotNull(r);
        Assert.Equal("up", r!.Direction);
        Assert.Equal(9, r.WindowBars);
        Assert.Equal("脉冲", r.WindowLabel);
        Assert.Equal(0.3m, r.ChangePct);
        Assert.Equal(120000, r.CooldownMs);
    }

    /// <summary>
    /// 已知分歧锁定：C# MonitorConfig 默认 rapidWindows 与 JS MONITOR_CONFIG.rapidWindows 不同
    /// （C# bars=3/10/20/40 pct=1/2/3/3；JS bars=9/30/60/120 pct=1/2/3/4）。
    /// 这是「配置重写」，未必是 bug，但必须显式固化，防止被无意改回 JS 原值而不知情。
    /// </summary>
    [Fact]
    public void CSharpDefaultRapidWindows_DivergesFromJsOriginal()
    {
        var svc = new PlanSchedulerService(
            null!, null!, null, null!, null!, null!, null!, null!, null!, null!, null!, null!);
        var def = svc.Config.RapidWindows;

        Assert.Equal(4, def.Count);
        Assert.Equal(3, def[0].Bars);
        Assert.Equal(1.0m, def[0].Pct);
        Assert.Equal(10, def[1].Bars);
        Assert.Equal(2.0m, def[1].Pct);
        Assert.Equal(40, def[3].Bars);
        Assert.Equal(3.0m, def[3].Pct); // JS 原版第4窗口 pct=4.0（此处 C# 为 3.0，是分歧点）
    }
}
