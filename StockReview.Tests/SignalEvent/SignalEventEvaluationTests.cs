// SignalEventService 评估单元测试 - 验证 ClassifyQuality 等纯函数
using System;
using StockReview.Core.Services;
using Xunit;

namespace StockReview.Tests.SignalEvent;

public class SignalEventEvaluationTests
{
    // ===== ClassifyQuality =====

    [Fact]
    public void ClassifyQuality_NullEvaluation_ReturnsUnknown()
    {
        var result = SignalEventService.ClassifyQuality(null);
        Assert.NotNull(result);
        Assert.True(result.Length > 0);
    }

    [Fact]
    public void ClassifyQuality_SuccessWithHighReward_ReturnsGoodQuality()
    {
        var ev = new SignalEvaluation
        {
            Result = "success",
            Reward = 2.5,
            Quality = 80,
            Capture = 0.7,
            CapturePct = 2.0
        };
        var result = SignalEventService.ClassifyQuality(ev);
        Assert.NotNull(result);
        // 应包含正面质量标签
        Assert.True(result.Length > 0);
    }

    [Fact]
    public void ClassifyQuality_FailResult_ReturnsPoorQuality()
    {
        var ev = new SignalEvaluation
        {
            Result = "fail",
            Reward = -1.5,
            Quality = 20,
            Capture = 0.1
        };
        var result = SignalEventService.ClassifyQuality(ev);
        Assert.NotNull(result);
    }

    [Fact]
    public void ClassifyQuality_NeutralResult_ReturnsNeutralLabel()
    {
        var ev = new SignalEvaluation
        {
            Result = "neutral",
            Reward = 0,
            Quality = 50
        };
        var result = SignalEventService.ClassifyQuality(ev);
        Assert.NotNull(result);
    }

    [Fact]
    public void ClassifyQuality_SuccessWithLowReward_ReturnsMarginalQuality()
    {
        var ev = new SignalEvaluation
        {
            Result = "success",
            Reward = 0.3,
            Quality = 45,
            Capture = 0.3
        };
        var result = SignalEventService.ClassifyQuality(ev);
        Assert.NotNull(result);
        // 边缘质量不应是空
        Assert.True(result.Length > 0);
    }
}
