using System;
using System.Globalization;

namespace StockReview.Core;

/// <summary>
/// JS 数值语义兼容助手。
/// 沿用 JS 数值语义的算法里，凡取整行为必须与 JS 基准一致的地方，
/// 统一走 <see cref="JsRound"/>，禁止直接调用默认 <see cref="Math.Round(double)"/>。
/// </summary>
public static class JsMath
{
    /// <summary>
    /// 等价 JS <c>Math.round</c>：half-up 向 +∞（JS 规范 = Math.floor(x + 0.5)）。
    /// .NET <see cref="Math.Round(double)"/> 默认是银行家舍入（ToEven），
    /// <see cref="MidpointRounding.AwayFromZero"/> 对正数等价但对负数不同
    /// （-2.5 → -3，而 JS → -2）。此处用 floor(x+0.5) 精确对齐 JS。
    /// </summary>
    public static double JsRound(double value, int digits = 0)
    {
        var factor = Math.Pow(10, digits);
        return Math.Floor(value * factor + 0.5) / factor;
    }

    /// <summary>decimal 版（K线/价格字段多为 decimal）：语义同上。</summary>
    public static decimal JsRound(decimal value, int digits = 0)
    {
        var factor = (decimal)Math.Pow(10, digits);
        return Math.Floor(value * factor + 0.5m) / factor;
    }
}

/// <summary>
/// 行情字符串数值解析（固定不变文化区 / en-US）。
/// 腾讯 / 新浪 / 东财接口返回的数字均为 en-US 格式（"1234.56"）；按当前文化区解析，
/// 在以逗号作小数位的区域（de-DE / fr-FR 等）会抛 <see cref="FormatException"/>
/// 或静默解析为 0。行情解析一律用这里，禁止裸 <c>decimal.Parse</c>。
/// </summary>
public static class InvParse
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static decimal Decimal(string s) => decimal.Parse(s, Inv);

    public static long Long(string s) => long.Parse(s, Inv);

    public static DateTime Date(string s) => DateTime.Parse(s, Inv);
}
