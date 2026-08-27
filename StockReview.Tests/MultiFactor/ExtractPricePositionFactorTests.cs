// 价格位置因子(pricePosition) 翻译校验 —— C# StockReview.Core 侧（真实翻译代码）
// 与 CrossLanguageBaseline/verify_mfe_pricepos_js.mjs 跑同一组场景，做跨语言比对。
// MarketSnapshot 继承自 IntradaySnapshot，构造时只设 Price/AvgPrice 等必要字段。
using System;
using System.Collections.Generic;
using StockReview.Core.Engines;
using Xunit;

namespace StockReview.Tests.MultiFactor;

public class ExtractPricePositionFactorTests
{
    private static MarketSnapshot Mk(double price, double avg) => new()
    {
        SnapshotAt = DateTime.MinValue,
        Price = price,
        Open = price,
        High = price,
        Low = price,
        AvgPrice = avg,
        Volume = 100,
        PreClose = avg,
        VolumeReliable = true,
    };

    private static List<MarketSnapshot> Build(double[] prices, double avg)
    {
        var list = new List<MarketSnapshot>();
        foreach (var p in prices) list.Add(Mk(p, avg));
        return list;
    }

    private static MultiFactorEngineService CreateService() => new();

    [Fact]
    public void PricePosition_HighPosition_HighDeviation_Bear()
    {
        var svc = CreateService();
        var snaps = Build(new double[] { 10, 15, 20, 19 }, 18);
        var r = svc.ExtractPricePositionFactor(snaps, 19);
        Assert.Equal(75, r.Score);
        Assert.Equal("bear", r.Direction);
        Assert.Contains("日内高位", r.Detail);
        Assert.Contains("高乖离", r.Detail);
    }

    [Fact]
    public void PricePosition_LowPosition_Bull()
    {
        var svc = CreateService();
        var snaps = Build(new double[] { 10, 12, 11, 10.2 }, 11);
        var r = svc.ExtractPricePositionFactor(snaps, 10.2);
        Assert.Equal(30, r.Score);
        Assert.Equal("bull", r.Direction);
        Assert.Contains("日内低位", r.Detail);
    }

    [Fact]
    public void PricePosition_InsufficientData_Neutral()
    {
        var svc = CreateService();
        var snaps = Build(new double[] { 10 }, 11);
        var r = svc.ExtractPricePositionFactor(snaps, 10);
        Assert.Equal(0, r.Score);
        Assert.Equal("neutral", r.Direction);
        Assert.Contains("数据不足", r.Detail);
    }
}
