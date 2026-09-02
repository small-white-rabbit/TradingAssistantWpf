// A5 拆分回归测试（2026-09-02）。
// MarketDataCache / SignalStateStore 从 PlanSchedulerService 提取后的行为锁定：
// TTL 过期清理、分时缓存 10 分钟陈旧清理、跨天重置范围。
using System;
using System.Collections.Generic;
using StockReview.Core.MarketData;
using StockReview.Core.Services;
using Xunit;

namespace StockReview.Tests.MarketData;

public class MarketDataCacheTests
{
    [Fact]
    public void CleanupExpired_RemovesExpiredQuotesAndCapitalFlow_KeepsFresh()
    {
        var cache = new MarketDataCache();
        var now = new DateTime(2026, 9, 2, 14, 0, 0);
        cache.BatchQuoteCache["600000"] = (new StockQuote { Code = "600000", CurrentPrice = 10m }, now.AddSeconds(-1));   // 过期
        cache.BatchQuoteCache["600001"] = (new StockQuote { Code = "600001", CurrentPrice = 20m }, now.AddSeconds(3));    // 新鲜
        cache.CapitalFlowCache["600000"] = (null, now.AddSeconds(-1));                                                     // 过期

        cache.CleanupExpired(now);

        Assert.False(cache.BatchQuoteCache.ContainsKey("600000"));
        Assert.True(cache.BatchQuoteCache.ContainsKey("600001"));
        Assert.False(cache.CapitalFlowCache.ContainsKey("600000"));
    }

    [Fact]
    public void CleanupExpired_TrendsCache_RemovesEntriesOlderThan10Minutes()
    {
        var cache = new MarketDataCache();
        var now = new DateTime(2026, 9, 2, 14, 0, 0);
        cache.TrendsCache["600000"] = (new List<IntradayPoint>(), now.AddMinutes(-11)); // 陈旧，清
        cache.TrendsCache["600001"] = (new List<IntradayPoint>(), now.AddMinutes(-9));  // 未超 10 分钟，留

        cache.CleanupExpired(now);

        Assert.False(cache.TrendsCache.ContainsKey("600000"));
        Assert.True(cache.TrendsCache.ContainsKey("600001"));
    }

    [Fact]
    public void ResetForNewDay_ClearsLiveTrailAndDailyKline_KeepsTtlCaches()
    {
        var cache = new MarketDataCache();
        cache.LiveTrail["600000"] = new List<LiveTrailPoint>();
        cache.DailyKlineCache["600000"] = (new List<KLineData>(), DateTime.Now.AddMinutes(5));
        cache.BatchQuoteCache["600000"] = (new StockQuote(), DateTime.Now.AddSeconds(5));
        cache.SnapshotCache["600000"] = new List<PriceSnapshot>();

        cache.ResetForNewDay();

        Assert.Empty(cache.LiveTrail);
        Assert.Empty(cache.DailyKlineCache);
        // 其余缓存靠 TTL 自然过期，跨天不强制清空（与原 OnDayChanged 行为一致）
        Assert.Single(cache.BatchQuoteCache);
        Assert.Single(cache.SnapshotCache);
    }
}

public class SignalStateStoreTests
{
    [Fact]
    public void ResetForNewDay_ClearsAllFiveDictionaries()
    {
        var store = new SignalStateStore();
        store.SignalStates["p1:sell"] = new SignalStateEntry();
        store.RateLimiter["600000:type"] = new RateLimitRecord();
        store.WaveGateStates["600000"] = new WaveGateState();
        store.LevelHitNotified["p1:level1"] = true;
        store.ActionEmittedToday["p1:notify"] = true;

        store.ResetForNewDay();

        Assert.Empty(store.SignalStates);
        Assert.Empty(store.RateLimiter);
        Assert.Empty(store.WaveGateStates);
        Assert.Empty(store.LevelHitNotified);
        Assert.Empty(store.ActionEmittedToday);
    }
}
