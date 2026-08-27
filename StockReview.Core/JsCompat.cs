using System;
using System.Globalization;

namespace StockReview.Core;

/// <summary>
/// JS 数值语义兼容助手。
/// 迁移自 Electron（src/stores/*.js）的算法里，凡取整行为必须与 JS 基准一致的地方，
/// 统一走 <see cref="JsRound"/>，禁止直接调用默认 <see cref="Math.Round(double)"/>。
/// </summary>
public static class JsMath
{
    /// <summary>
    /// 等价 JS <c>Math.round</c>：四舍五入（half-up，向 +∞）。
    /// .NET <see cref="Math.Round(double)"/> 默认是银行家舍入（MidpointRounding.ToEven），
    /// 精确 .5 的临界值会比 JS 少 1（如 <c>Math.Round(2.5)=2</c>，JS 为 3）。
    /// </summary>
    public static double JsRound(double value, int digits = 0)
        => Math.Round(value, digits, MidpointRounding.AwayFromZero);

    /// <summary>decimal 版（K线/价格字段多为 decimal）：语义同上。</summary>
    public static decimal JsRound(decimal value, int digits = 0)
        => Math.Round(value, digits, MidpointRounding.AwayFromZero);
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
