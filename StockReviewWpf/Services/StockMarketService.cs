using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StockReview.Core.MarketData;

namespace StockReviewWpf.Services;

/// <summary>行情自动回填结果（字符串字段，便于直接写回表单）</summary>
public sealed class StockData
{
    public string Source { get; set; } = "";
    public string Name { get; set; } = "";
    public string Close { get; set; } = "";
    public string PrevClose { get; set; } = "";
    public string High { get; set; } = "";
    public string ChangePct { get; set; } = "";
    public string MaxChangePct { get; set; } = "";
}

/// <summary>
/// 共享的行情获取逻辑（对应原版 stockApi.fetchStockData 的 WPF 落地）。
/// 同一只股票，表单日期=今天拉实时，否则拉该日历史 K 线。
/// </summary>
public static class StockMarketService
{
    /// <summary>获取行情。参数 date 为目标交易日(yyyy-MM-dd)，等于今天或为空时走实时。</summary>
    public static async Task<StockData?> Fetch(StockOcrService ocr, MarketDataAggregator market, string code, string date)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (date == today || string.IsNullOrWhiteSpace(date))
        {
            // 实时行情
            var q = await Task.Run(() => market.GetQuoteAsync(code));
            if (q != null && q.CurrentPrice > 0)
            {
                var prev = q.PreClose;
                if (prev <= 0 && q.ChangePercent != 0) prev = q.CurrentPrice / (1 + q.ChangePercent);
                return new StockData
                {
                    Source = "实时行情",
                    Name = q.Name,
                    Close = Fmt(q.CurrentPrice),
                    PrevClose = prev > 0 ? Fmt(prev) : "",
                    High = Fmt(q.High),
                    ChangePct = prev > 0 ? Fmt((q.CurrentPrice - prev) / prev * 100) : "",
                    MaxChangePct = prev > 0 && q.High > 0 ? Fmt((q.High - prev) / prev * 100) : ""
                };
            }
        }
        else
        {
            // 历史数据：东财日K线，取目标日期当日收盘/最高 + 昨收
            var klines = await Task.Run(() => market.GetDailyKLinesAsync(code, 500));
            if (!DateTime.TryParse(date, out var target)) target = DateTime.Today;
            var found = BuildFromKlines(klines, target);
            if (found != null)
            {
                found.Source = "历史K线";
                if (string.IsNullOrEmpty(found.Name))
                    found.Name = await Task.Run(() => ocr.GetNameByCode(code));
                return found;
            }
        }
        return null;
    }

    /// <summary>从日K线序列计算目标日期的行情字段（纯函数，供自检）</summary>
    public static StockData? BuildFromKlines(List<KLineData> klines, DateTime target)
    {
        if (klines == null || klines.Count == 0) return null;
        for (var i = klines.Count - 1; i >= 0; i--)
        {
            if (klines[i].Date.Date <= target.Date)
            {
                var close = klines[i].Close;
                var high = klines[i].High;
                var prev = i > 0 ? klines[i - 1].Close : close;
                return new StockData
                {
                    Name = "",
                    Close = Fmt(close),
                    PrevClose = prev > 0 ? Fmt(prev) : "",
                    High = Fmt(high),
                    ChangePct = prev > 0 ? Fmt((close - prev) / prev * 100) : "",
                    MaxChangePct = prev > 0 && high > 0 ? Fmt((high - prev) / prev * 100) : ""
                };
            }
        }
        return null;
    }

    private static string Fmt(decimal v) => v.ToString("0.00");
}