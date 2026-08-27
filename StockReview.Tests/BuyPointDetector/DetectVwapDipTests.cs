// VWAP 回踩(vwap_dip) 翻译校验 —— C# StockReview.Core 侧（真实翻译代码）
// 与 CrossLanguageBaseline/verify_buy_vwap_js.mjs 跑同一组场景，做跨语言比对。
// 快照只设 Price/AvgPrice/Volume（不设 IntervalVolume -> GetIntervalVolume 回退 Volume），
// 与原 JS 侧「不设 intervalVolume -> getIntervalVolume 回退 volume」保持一致。
using System;
using System.Collections.Generic;
using StockReview.Core.Engines;
using Xunit;

namespace StockReview.Tests.BuyPointDetector;

public class DetectVwapDipTests
{
    private static IntradaySnapshot Mk(double price, double avg, double vol) => new()
    {
        SnapshotAt = DateTime.MinValue,
        Price = price,
        Open = price,
        High = price,
        Low = price,
        AvgPrice = avg,
        Volume = vol,
        PreClose = avg,
        VolumeReliable = true,
        // IntervalVolume 留默认(0) -> GetIntervalVolume 回退 Volume，与 JS 一致
    };

    private static List<IntradaySnapshot> Build(double[] prices, double avg, double[]? vols = null)
    {
        var list = new List<IntradaySnapshot>();
        for (var i = 0; i < prices.Length; i++)
            list.Add(Mk(prices[i], avg, vols != null ? vols[i] : 100));
        return list;
    }

    private static BuyPointDetectorService CreateService()
    {
        var svc = new BuyPointDetectorService();
        // 显式对齐默认 BuyConfig 的 VWAP_DIP 相关阈值
        svc.Config.DipToVwapMin = 0.08;
        svc.Config.DipToVwapThreshold = 0.5;
        svc.Config.DipVolumeShrink = 0.8;
        svc.Config.DipBelowConfirm = 2;
        return svc;
    }

    [Fact]
    public void VwapDip_Fires_OnPullbackToVwap()
    {
        var svc = CreateService();
        var vols = new double[] { 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 50 };
        var snaps = Build(new double[] { 100, 100, 100, 100, 100, 100, 100, 100, 100, 99.9, 99.8, 99.9 }, 100, vols);
        var r = svc.DetectVwapDip(snaps, 99.9);
        Assert.NotNull(r);
        Assert.Equal(100d, r.AvgPrice, 4);
        Assert.Equal(0.1, r.DeviationPct, 4);
        Assert.Equal(0.5, r.VolumeRatio, 4);
    }

    [Fact]
    public void VwapDip_NoFire_DeviationTooSmall()
    {
        var svc = CreateService();
        var snaps = Build(new double[] { 100, 100, 100, 100, 100, 100, 100, 100, 100, 99.99, 99.98, 99.99 }, 100);
        Assert.Null(svc.DetectVwapDip(snaps, 99.99));
    }

    [Fact]
    public void VwapDip_NoFire_VolumeExpanded()
    {
        var svc = CreateService();
        var vols = new double[] { 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 200 };
        var snaps = Build(new double[] { 100, 100, 100, 100, 100, 100, 100, 100, 100, 99.9, 99.8, 99.9 }, 100, vols);
        Assert.Null(svc.DetectVwapDip(snaps, 99.9));
    }
}
