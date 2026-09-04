// 技术指标因子(technicalIndicator, 建议2新增) 单元测试
// 验证因子层降级口径：<35 根返回 neutral；bear 优先于 bull；权重含 technicalIndicator 且归一
using System;
using System.Collections.Generic;
using System.Linq;
using StockReview.Core.Engines;
using Xunit;

namespace StockReview.Tests.MultiFactor;

public class ExtractTechnicalIndicatorFactorTests
{
    private static MultiFactorEngineService CreateService() => new();

    private static List<DailyKline> BuildDailyK(int n, double start, double step, double range = 0.3)
    {
        var list = new List<DailyKline>();
        for (var i = 0; i < n; i++)
        {
            var c = start + i * step;
            list.Add(new DailyKline
            {
                Date = DateTime.MinValue.AddDays(i),
                Open = c, Close = c, High = c + range, Low = c - range, Volume = 100000
            });
        }
        return list;
    }

    private static List<MarketSnapshot> MkSnaps(double[] prices, double avg)
    {
        var list = new List<MarketSnapshot>();
        foreach (var p in prices)
            list.Add(new MarketSnapshot
            {
                SnapshotAt = DateTime.MinValue, Price = p, Open = p, High = p, Low = p,
                AvgPrice = avg, Volume = 100, PreClose = avg, VolumeReliable = true
            });
        return list;
    }

    // ===== 降级 =====

    [Fact]
    public void Tech_NullKlines_Neutral()
    {
        var r = CreateService().ExtractTechnicalIndicatorFactor(null, 10);
        Assert.Equal(0, r.Score);
        Assert.Equal("neutral", r.Direction);
        Assert.Contains("日K不足", r.Detail);
    }

    [Fact]
    public void Tech_InsufficientKlines_Neutral()
    {
        var r = CreateService().ExtractTechnicalIndicatorFactor(BuildDailyK(20, 10, 0.5), 10);
        Assert.Equal("neutral", r.Direction);
        Assert.Contains("日K不足", r.Detail);
    }

    // ===== 方向判定（分值差决定，对冲时 neutral） =====

    private static List<DailyKline> BuildFlatThenSurge(int flat, int surge, double step)
    {
        // 前平后急变：KDJ 处于冲刺期（J 冲破 100/跌破 0），等差稳态到不了超买区
        var list = new List<DailyKline>();
        for (var i = 0; i < flat; i++)
            list.Add(new DailyKline
            {
                Date = DateTime.MinValue.AddDays(i),
                Open = 10, Close = 10, High = 10.3, Low = 9.7, Volume = 100000
            });
        for (var i = 0; i < surge; i++)
        {
            var c = 10 + (i + 1) * step;
            list.Add(new DailyKline
            {
                Date = DateTime.MinValue.AddDays(flat + i),
                Open = c, Close = c, High = c + 0.3, Low = c - 0.3, Volume = 100000
            });
        }
        return list;
    }

    [Fact]
    public void Tech_StrongUptrend_Bear()
    {
        // 平盘后急涨：KDJ J 冲破 100 + 价格破 BOLL 上轨 → 看空（卖点引擎语义）
        var klines = BuildFlatThenSurge(60, 6, 1.0);
        var last = klines[^1].Close;
        var r = CreateService().ExtractTechnicalIndicatorFactor(klines, last);
        Assert.Equal("bear", r.Direction);
        Assert.True(r.Score >= 35, $"Uptrend bear score should >= 35, got {r.Score}");
        Assert.Contains("KDJ超买", r.Detail);
    }

    [Fact]
    public void Tech_Downtrend_Bull()
    {
        // 平盘后急跌：KDJ J 跌破 0 + 触 BOLL 下轨（bull 65）压过 MACD 死叉（bear 30）→ bull
        var klines = BuildFlatThenSurge(60, 6, -1.0);
        var last = klines[^1].Close;
        var r = CreateService().ExtractTechnicalIndicatorFactor(klines, last);
        Assert.Equal("bull", r.Direction);
        Assert.True(r.Score >= 30, $"Downtrend bull score should >= 30, got {r.Score}");
    }

    [Fact]
    public void Tech_FlatData_Neutral()
    {
        // 平坦：MACD DIF==DEA 无死叉、KDJ=50 无超买卖、BOLL σ=0 轨道退化跳过 → 中性
        var klines = new List<DailyKline>();
        for (var i = 0; i < 60; i++)
            klines.Add(new DailyKline
            {
                Date = DateTime.MinValue.AddDays(i),
                Open = 10, Close = 10, High = 10, Low = 10, Volume = 100000
            });
        var r = CreateService().ExtractTechnicalIndicatorFactor(klines, 10);
        Assert.Equal("neutral", r.Direction);
        Assert.Equal(0, r.Score);
        Assert.Contains("指标中性", r.Detail);
    }

    // ===== Evaluate 集成 =====

    [Fact]
    public void Evaluate_TechnicalIndicatorFactor_Included()
    {
        var svc = CreateService();
        var snaps = MkSnaps(new[] { 10.0, 10.2, 10.4, 10.6, 10.8, 11.0 }, 10.5);
        var klines = BuildDailyK(80, 10, 0.3);
        var r = svc.Evaluate(snaps, 10.8, klines);
        var tech = r.Factors.FirstOrDefault(f => f.Key == "technicalIndicator");
        Assert.NotNull(tech);
        Assert.Equal("技术指标", tech!.Name);
        Assert.True(tech.Score is >= 0 and <= 100);
    }

    [Fact]
    public void Evaluate_WithoutKlines_TechFactorStillNeutral()
    {
        var svc = CreateService();
        var snaps = MkSnaps(new[] { 10.0, 10.2, 10.4, 10.6, 10.8, 11.0 }, 10.5);
        var r = svc.Evaluate(snaps, 10.8, null);
        var tech = r.Factors.FirstOrDefault(f => f.Key == "technicalIndicator");
        Assert.NotNull(tech);
        Assert.Equal("neutral", tech!.Direction);
    }

    // ===== 权重表 =====

    [Fact]
    public void DefaultWeights_ContainsTechnicalIndicator()
    {
        Assert.True(MultiFactorEngineService.DefaultWeights.ContainsKey("technicalIndicator"));
        Assert.Equal(0.10, MultiFactorEngineService.DefaultWeights["technicalIndicator"], 6);
    }

    [Fact]
    public void DefaultWeights_SumEqualsOne()
    {
        var sum = MultiFactorEngineService.DefaultWeights.Values.Sum();
        Assert.Equal(1.0, sum, 6);
    }
}
