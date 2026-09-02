// 跌破平台(platform_breakdown) 平台认定回归测试
// 覆盖 StockReview.Core 中真实翻译代码 SellPointDetectorService.DetectPlatformBreakdown。
// 背景（2026-09-02 用户实测 301148）：10:35:09 现价 51.1 仍处于真实平台 51-51.2 内部，
// 但检测器把跳水前仅存约 7 分钟的高位小台阶 51.30-51.42 当成"平台"，以 51.30 为平台下沿
// 误报"跌破平台"；太极实业、利和兴 10:43 同类误报。平台下沿必须是近期已确立的地板，
// 而不能是更大平台内部新形成的高位台阶。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using StockReview.Core.Data;
using StockReview.Core.Engines;
using StockReview.Core.MarketData;
using Xunit;

namespace StockReview.Tests.SellPointDetector;

public class DetectPlatformBreakdownTests
{
    private static IntradaySnapshot Mk(double price, DateTime t) => new()
    {
        SnapshotAt = t,
        Price = price, Open = price, High = price, Low = price,
        AvgPrice = price,
        Volume = 100, IntervalVolume = 100,
        PreClose = 50.0,
        VolumeReliable = true,
    };

    private static SellPointDetectorService CreateService()
    {
        var db = new DatabaseService();
        var tmp = Path.Combine(Path.GetTempPath(), "platformBreak_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        db.SetDataDir(tmp);
        db.Initialize();
        var market = new MarketDataAggregator(new HttpClient());
        var svc = new SellPointDetectorService(db, market);
        svc.UpdateConfig(new SellPointDetectorConfig
        {
            EnablePatternSimilarity = false,
        });
        return svc;
    }

    // 301148 复现场景（10s 快照节奏，数字对应用户实测）：
    // i0-2    开盘下探 50.80（日内低点，抬高价格位置占比）
    // i3-242  真实平台 51.00-51.20 震荡约 40 分钟（对应短期平台 51-51.2）
    // i243-282 平台内部高位小台阶 51.30-51.42（仅约 6.7 分钟，远短于 5 分钟-1 小时的真实平台经验区间）
    // i283-285 台阶上回落 51.15→51.10，现价 51.10 仍在真实平台内
    // 期望：不触发跌破平台（旧行为会以 51.30 为平台下沿误报，breakdownPct≈0.39%）。
    [Fact]
    public void PriceInsideRealPlatform_ShouldNotFire()
    {
        var svc = CreateService();
        var baseTime = new DateTime(2026, 9, 2, 9, 30, 0);
        var snaps = new List<IntradaySnapshot>();
        for (var i = 0; i < 3; i++)
            snaps.Add(Mk(50.80, baseTime.AddSeconds(10 * i)));
        var upCycle = new[] { 51.00, 51.05, 51.10, 51.15, 51.20 };
        for (var i = 3; i <= 242; i++)
            snaps.Add(Mk(upCycle[i % 5], baseTime.AddSeconds(10 * i)));
        var shelfCycle = new[] { 51.30, 51.34, 51.38, 51.42 };
        for (var i = 243; i <= 282; i++)
            snaps.Add(Mk(shelfCycle[(i - 243) % 4], baseTime.AddSeconds(10 * i)));
        var dip = new[] { 51.15, 51.12, 51.10 };
        for (var i = 0; i < 3; i++)
            snaps.Add(Mk(dip[i], baseTime.AddSeconds(10 * (283 + i))));

        var sig = svc.DetectPlatformBreakdown(snaps, 51.10);

        Assert.Null(sig);
    }

    // 真实跌破仍须触发：台阶 51.30-51.42 已确立约 3.5 小时（台阶前仅 30 根爬升），
    // 价格跌破台阶下沿至 51.05（breakdownPct≈0.49%）→ 应以 51.30 为平台下沿触发。
    [Fact]
    public void EstablishedPlatformBreakdown_ShouldFire()
    {
        var svc = CreateService();
        var baseTime = new DateTime(2026, 9, 2, 9, 30, 0);
        var snaps = new List<IntradaySnapshot>();
        for (var i = 0; i <= 29; i++)
            snaps.Add(Mk(50.80 + (51.35 - 50.80) * i / 29, baseTime.AddSeconds(10 * i)));
        var shelfCycle = new[] { 51.30, 51.34, 51.38, 51.42 };
        for (var i = 30; i <= 279; i++)
            snaps.Add(Mk(shelfCycle[(i - 30) % 4], baseTime.AddSeconds(10 * i)));
        // 跌破后须持续低于平台下轨 ≥ PlatformConfirmSnaps(18) 个快照（约 3 分钟）才确认
        var dip = new[] { 51.12, 51.08, 51.05 };
        for (var i = 0; i < 18; i++)
            snaps.Add(Mk(dip[i % 3], baseTime.AddSeconds(10 * (280 + i))));

        var sig = svc.DetectPlatformBreakdown(snaps, 51.05);

        Assert.NotNull(sig);
        Assert.Equal("跌破平台", sig!.LevelName);
        Assert.Equal(51.30, sig.LevelPrice, 6);
        Assert.True(sig.GetDouble("breakdownPct") >= 0.25, $"breakdownPct={sig.GetDouble("breakdownPct")}");
    }
}
