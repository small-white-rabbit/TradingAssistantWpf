// JsCompat 单元测试 - 验证 JS 语义兼容助手
// 覆盖：JsMath.JsRound（对齐 JS Math.round half-up）+ InvParse（InvariantCulture）
using System;
using System.Globalization;
using StockReview.Core;
using Xunit;

namespace StockReview.Tests.Core;

public class JsCompatTests
{
    // ===== JsMath.JsRound =====

    [Theory]
    [InlineData(2.5, 3)]    // JS: Math.round(2.5) = 3（half-up）；C# 默认 Math.Round(2.5) = 2（银行家）
    [InlineData(3.5, 4)]    // JS: Math.round(3.5) = 4；C# 默认 = 4（恰好对，但原因不同）
    [InlineData(1.5, 2)]    // JS: 2；C# 默认 = 2（偶数）
    [InlineData(0.5, 1)]    // JS: 1；C# 默认 = 0
    [InlineData(-0.5, 0)]   // JS: Math.round(-0.5) = 0（向 +∞）；C# 默认 = 0
    [InlineData(-1.5, -1)]  // JS: -1；C# 默认 = -2
    [InlineData(-2.5, -2)]  // JS: -2；C# 默认 = -2
    public void JsRound_HalfUp_AlignsJS(double input, double expected)
    {
        Assert.Equal(expected, JsMath.JsRound(input));
    }

    [Theory]
    [InlineData(2.555, 2, 2.56)]   // half-up 到 2 位
    [InlineData(0.135, 2, 0.14)]   // half-up
    public void JsRound_WithDigits_HalfUp(double input, int digits, double expected)
    {
        // 用容差比较避免浮点精度问题
        Assert.Equal(expected, JsMath.JsRound(input, digits), precision: 2);
    }

    [Fact]
    public void JsRound_DecimalOverload()
    {
        Assert.Equal(3m, JsMath.JsRound(2.5m));
        Assert.Equal(2.56m, JsMath.JsRound(2.555m, 2));
        Assert.Equal(1m, JsMath.JsRound(0.5m));
        // JS Math.round(-1.5) = -1（向 +∞），不是 -2（AwayFromZero）
        Assert.Equal(-1m, JsMath.JsRound(-1.5m));
        Assert.Equal(-2m, JsMath.JsRound(-2.5m));
    }

    [Fact]
    public void JsRound_IntegerInput_NoChange()
    {
        Assert.Equal(5.0, JsMath.JsRound(5.0));
        Assert.Equal(0.0, JsMath.JsRound(0.0));
        Assert.Equal(-3.0, JsMath.JsRound(-3.0));
    }

    // ===== InvParse =====

    [Theory]
    [InlineData("1234.56", 1234.56)]
    [InlineData("0.001", 0.001)]
    [InlineData("999999.99", 999999.99)]
    public void InvParse_Decimal_ParsesInvariant(string input, decimal expected)
    {
        Assert.Equal(expected, InvParse.Decimal(input));
    }

    [Theory]
    [InlineData("1234567890", 1234567890L)]
    [InlineData("0", 0L)]
    public void InvParse_Long_ParsesInvariant(string input, long expected)
    {
        Assert.Equal(expected, InvParse.Long(input));
    }

    [Fact]
    public void InvParse_Date_ParsesInvariant()
    {
        var d = InvParse.Date("2026-08-27");
        Assert.Equal(2026, d.Year);
        Assert.Equal(8, d.Month);
        Assert.Equal(27, d.Day);
    }

    /// <summary>
    /// 关键回归：确保在非 en-US 文化区下仍正确解析。
    /// 旧代码用裸 decimal.Parse("12.34")，在 de-DE 下会抛 FormatException。
    /// </summary>
    [Fact]
    public void InvParse_Decimal_WorksUnderNonEnUsCulture()
    {
        var orig = CultureInfo.CurrentCulture;
        try
        {
            // 模拟德语区域（小数点为逗号）
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            // 旧代码 decimal.Parse("12.34") 在此处会崩
            Assert.Equal(12.34m, InvParse.Decimal("12.34"));
            Assert.Equal(100L, InvParse.Long("100"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = orig;
        }
    }
}
