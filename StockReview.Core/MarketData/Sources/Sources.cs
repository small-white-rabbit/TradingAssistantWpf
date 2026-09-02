using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Serilog;

namespace StockReview.Core.MarketData.Sources;

/// <summary>
/// 东方财富数据源 - 对应原版 EastMoneySource
/// </summary>
public class EastMoneySource : IMarketDataSource
{
    private readonly HttpClient _http;

    public string Name => "东财";

    public EastMoneySource(HttpClient http) => _http = http;

    public async Task<StockQuote?> GetQuoteAsync(string stockCode)
    {
        try
        {
            // 东财实时行情接口
            var (market, code) = ParseStockCode(stockCode);
            var url = $"https://push2.eastmoney.com/api/qt/stock/get?secid={market}.{code}&fields=f43,f44,f45,f46,f47,f48,f57,f58,f170";
            var json = await _http.GetStringAsync(url);
            var data = JObject.Parse(json)?["data"];
            if (data == null) return null;

            return new StockQuote
            {
                Code = stockCode,
                Name = data["f58"]?.ToString() ?? "",
                CurrentPrice = decimal.TryParse(data["f43"]?.ToString(), out var p) ? p / 100 : 0,
                High = decimal.TryParse(data["f44"]?.ToString(), out var h) ? h / 100 : 0,
                Low = decimal.TryParse(data["f45"]?.ToString(), out var l) ? l / 100 : 0,
                Open = decimal.TryParse(data["f46"]?.ToString(), out var o) ? o / 100 : 0,
                Volume = long.TryParse(data["f47"]?.ToString(), out var v) ? v : 0,
                Amount = decimal.TryParse(data["f48"]?.ToString(), out var a) ? a : 0,
                ChangePercent = decimal.TryParse(data["f170"]?.ToString(), out var cp) ? cp / 100 : 0,
                DateTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Services.CnTimeZone.Get)
            };
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[东财] 获取 {Code} 失败", stockCode);
            return null;
        }
    }

    public async Task<List<KLineData>> GetDailyKLinesAsync(string stockCode, int count = 250)
    {
        var result = new List<KLineData>();
        try
        {
            var (market, code) = ParseStockCode(stockCode);
            var url = $"https://push2his.eastmoney.com/api/qt/stock/kline/get?secid={market}.{code}&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57,f58&klt=101&fqt=1&end=20500101&lmt={count}";
            var json = await _http.GetStringAsync(url);
            var klines = JObject.Parse(json)?["data"]?["klines"];
            if (klines == null) return result;

            foreach (var item in klines)
            {
                var parts = item.ToString().Split(',');
                if (parts.Length < 8) continue;
                result.Add(new KLineData
                {
                    Date = InvParse.Date(parts[0]),
                    Open = InvParse.Decimal(parts[1]),
                    Close = InvParse.Decimal(parts[2]),
                    High = InvParse.Decimal(parts[3]),
                    Low = InvParse.Decimal(parts[4]),
                    Volume = InvParse.Long(parts[5]),
                    Amount = InvParse.Decimal(parts[6]),
                    Turnover = InvParse.Decimal(parts[7])
                });
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[东财] 获取 {Code} K线失败", stockCode);
        }
        return result;
    }

    /// <summary>
    /// 解析股票代码为东财格式（市场.代码）
    /// 上证: 1.60xxxx / 1.68xxxx / 1.00xxxx
    /// 深证: 0.00xxxx / 0.30xxxx / 0.20xxxx
    /// </summary>
    private static (int market, string code) ParseStockCode(string stockCode)
    {
        var code = stockCode.Replace("SH", "").Replace("SZ", "").Replace("sh", "").Replace("sz", "");
        if (code.StartsWith("6") || code.StartsWith("5") || code.StartsWith("11") || code.StartsWith("13"))
            return (1, code);
        return (0, code);
    }

    public async Task<List<IntradayPoint>> GetIntradayAsync(string stockCode)
    {
        // 1) trends2 实时分时（push2 域名，与原版 fetchTrendsData 同款）：
        //    1 分钟点序列 + 真实分时均价 + preClose。push2his 的 kline 接口对本机
        //    TLS 指纹有间歇性拦截（连接被重置），trends2 是另一域名，成功率更高。
        var trends = await GetIntradayFromTrends2Async(stockCode);
        if (trends.Count > 0) return trends;

        // 2) 回退：push2his 1 分钟 K 线（响应含昨日尾 bar 可推算昨收）
        return await GetIntradayFromKline1mAsync(stockCode);
    }

    /// <summary>东财 trends2 实时分时（对应原版 EastMoneySource.fetchTrendsData）</summary>
    private async Task<List<IntradayPoint>> GetIntradayFromTrends2Async(string stockCode)
    {
        var result = new List<IntradayPoint>();
        try
        {
            var (market, code) = ParseStockCode(stockCode);
            var url = $"https://push2.eastmoney.com/api/qt/stock/trends2/get?secid={market}.{code}" +
                      "&fields1=f1,f2,f3,f4,f5,f6,f7,f8,f9,f10,f11,f12,f13&fields2=f51,f52,f53,f54,f55,f56,f57,f58";
            // trends2 偶发 socket hang up（服务端主动断开），重试一次提高成功率（对应原版 重试）
            string? json = null;
            for (var attempt = 1; attempt <= 2 && json == null; attempt++)
            {
                try { json = await _http.GetStringAsync(url); }
                catch (Exception ex) { Log.Debug(ex, "[东财分时] {Code} trends2 第{Attempt}次请求失败", stockCode, attempt); }
            }
            if (json == null)
            {
                Log.Information("[东财分时] {Code} trends2 请求失败（连接被断开），回退 kline 接口", stockCode);
                return result;
            }

            var data = JObject.Parse(json)?["data"];
            var trendsArr = data?["trends"];
            if (trendsArr == null || !trendsArr.HasValues)
            {
                Log.Information("[东财分时] {Code} trends2 响应无数据，回退 kline 接口", stockCode);
                return result;
            }

            // 昨收：响应含 preClose 字段（分时中轴与涨幅基准）；异常值（偏离首点 50%+）视为脏数据弃用
            decimal preClose = (decimal?)data?["preClose"] ?? 0;
            // 目标交易日：trends2 时间串无日期前缀（"09:30"），补目标交易日（对应原版 tPrefix 逻辑）
            var target = IntradayTargetDate.Get();

            decimal cumVol = 0, cumPv = 0;
            foreach (var item in trendsArr)
            {
                // trends2 点格式：time,open,close,high,low,volume,turnover[,avgPrice]
                var parts = item.ToString().Split(',');
                if (parts.Length < 7) continue;
                if (!decimal.TryParse(parts[2], out var close) || close <= 0) continue;

                var timeStr = parts[0].Trim();
                if (timeStr.Length <= 5) timeStr = $"{target:yyyy-MM-dd} {timeStr}";
                if (!DateTime.TryParse(timeStr, out var time)) continue;

                var volume = long.TryParse(parts[5], out var v) ? v : 0;
                var amount = decimal.TryParse(parts[6], out var amt) ? amt : 0;
                cumVol += volume;
                cumPv += amount > 0 ? amount : close * volume;

                if (preClose <= 0 || Math.Abs(preClose - close) / close > 0.5m) preClose = close;
                result.Add(new IntradayPoint
                {
                    Time = time,
                    Price = close,
                    // 字段8 avgPrice 为真实分时均价（累计额/量），缺失时用累计近似
                    AvgPrice = parts.Length > 7 && decimal.TryParse(parts[7], out var ap) && ap > 0
                        ? ap
                        : (cumVol > 0 ? cumPv / cumVol : close),
                    Volume = volume,
                    Amount = amount,
                    PreClose = preClose,
                    ChangePercent = preClose > 0 ? (close - preClose) / preClose * 100 : 0
                });
            }
            Log.Information("[东财] 获取 {Code} 分时 {Count} 点（trends2）昨收={PreClose}", stockCode, result.Count, preClose);
        }
        catch (Exception ex)
        {
            Log.Information(ex, "[东财分时] {Code} trends2 异常，回退 kline 接口", stockCode);
        }
        return result;
    }

    /// <summary>东财 push2his 1 分钟 K 线分时（trends2 失败后的回退路径）</summary>
    private async Task<List<IntradayPoint>> GetIntradayFromKline1mAsync(string stockCode)
    {
        var result = new List<IntradayPoint>();
        try
        {
            var (market, code) = ParseStockCode(stockCode);
            // 东财分时接口（1 分钟 K 线）。lmt=280 保证响应含昨日尾 bar 用于推算昨收
            var url = $"https://push2his.eastmoney.com/api/qt/stock/kline/get?secid={market}.{code}&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57,f58,f59,f60,f61&klt=1&fqt=1&end=20500101&lmt=280";
            var json = await _http.GetStringAsync(url);
            var klines = JObject.Parse(json)?["data"]?["klines"];
            if (klines == null) return result;

            // 目标交易日：盘前/周末/节假日取上一交易日（对应原版 getQuoteDateStr）
            var today = IntradayTargetDate.Get();
            decimal preClose = 0, cumVol = 0, cumAmount = 0;
            foreach (var item in klines)
            {
                var parts = item.ToString().Split(',');
                if (parts.Length < 7) continue;
                if (!DateTime.TryParse(parts[0], out var time)) continue;

                var close = decimal.TryParse(parts[2], out var p) ? p : 0;
                if (time.Date < today) { preClose = close; continue; } // 昨日最后一根分钟K收盘 = 昨收
                if (time.Date > today) continue;

                // 首个今日 bar 兜底：f60(parts[9]) 是相对上一分钟的涨跌额，恰等于 close - 昨收
                if (preClose == 0 && parts.Length > 9 && decimal.TryParse(parts[9], out var chg))
                    preClose = close - chg;
                if (preClose == 0) preClose = close;

                var volume = long.TryParse(parts[5], out var v) ? v : 0;
                var amount = decimal.TryParse(parts[6], out var amt) ? amt : 0;
                cumVol += volume;
                cumAmount += amount;
                result.Add(new IntradayPoint
                {
                    Time = time,
                    Price = close,
                    // 均价 = 累计成交额 / 累计成交量（东财分钟线含成交额 f57）
                    AvgPrice = cumVol > 0 ? cumAmount / cumVol : close,
                    Volume = volume,
                    Amount = amount,
                    PreClose = preClose,
                    ChangePercent = preClose != 0 ? (close - preClose) / preClose * 100 : 0
                });
            }
        }
        catch (Exception ex)
        {
            Log.Information(ex, "[东财] 获取 {Code} 分时失败（kline 回退路径）", stockCode);
        }
        return result;
    }
}

/// <summary>
/// 腾讯数据源
/// </summary>
public class TencentSource : IMarketDataSource
{
    private readonly HttpClient _http;
    public string Name => "腾讯";

    public TencentSource(HttpClient http) => _http = http;

    public async Task<StockQuote?> GetQuoteAsync(string stockCode)
    {
        try
        {
            var (prefix, code) = ParseCode(stockCode);
            var url = $"https://qt.gtimg.cn/q={prefix}{code}";
            var text = await _http.GetStringAsync(url);
            var match = Regex.Match(text, @"""([^""]+)""");
            if (!match.Success) return null;

            var parts = match.Groups[1].Value.Split('~');
            if (parts.Length < 50) return null;

            return new StockQuote
            {
                Code = stockCode,
                Name = parts[1],
                CurrentPrice = InvParse.Decimal(parts[3]),
                PreClose = InvParse.Decimal(parts[4]),
                Open = InvParse.Decimal(parts[5]),
                Volume = InvParse.Long(parts[6]),
                High = InvParse.Decimal(parts[33]),
                Low = InvParse.Decimal(parts[34]),
                Amount = InvParse.Decimal(parts[37]),
                ChangePercent = InvParse.Decimal(parts[32]) / 100,
                DateTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Services.CnTimeZone.Get)
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// 代码 → (腾讯前缀, 纯数字代码)（6/5/9 开头为沪市，其余深市）。
    /// 输入兼容 SH/SZ 前缀（计划列表代码带前缀），拼接 URL 前必须剥掉前缀，
    /// 否则出现 szSZ301013 双前缀导致接口静默返回空。
    /// </summary>
    private static (string prefix, string code) ParseCode(string stockCode)
    {
        var code = stockCode.ToUpperInvariant().Replace("SH", "").Replace("SZ", "");
        var prefix = code.StartsWith("6") || code.StartsWith("5") || code.StartsWith("9") ? "sh" : "sz";
        return (prefix, code);
    }

    public async Task<List<KLineData>> GetDailyKLinesAsync(string stockCode, int count = 250)
    {
        // 腾讯日K来源：
        // web.ifzq.gtimg.cn（fqkline 端点不重定向），前复权，请求数量放大一倍再截尾
        var result = new List<KLineData>();
        try
        {
            var (prefix, code) = ParseCode(stockCode);
            var url = $"https://web.ifzq.gtimg.cn/appstock/app/fqkline/get?param={prefix}{code},day,,,{count * 2},qfq";
            var json = await _http.GetStringAsync(url);
            var stockData = JObject.Parse(json)?["data"]?[ $"{prefix}{code}"];
            var klines = stockData?["qfqday"] ?? stockData?["day"] ?? stockData?["hfqday"];
            if (klines == null) return result;

            // 腾讯格式：[date, open, close, high, low, volume, ...]
            foreach (var item in klines)
            {
                if (item is not JArray arr || arr.Count < 6) continue;
                if (!DateTime.TryParseExact(arr[0]?.ToString(), "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var date)) continue;
                result.Add(new KLineData
                {
                    Date = date,
                    Open = (decimal?)arr[1] ?? 0,
                    Close = (decimal?)arr[2] ?? 0,
                    High = (decimal?)arr[3] ?? 0,
                    Low = (decimal?)arr[4] ?? 0,
                    Volume = (long?)arr[5] ?? 0
                });
            }
            if (result.Count > count) result = result.Skip(result.Count - count).ToList();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[腾讯] 获取 {Code} 日K失败", stockCode);
        }
        return result;
    }

    public async Task<List<IntradayPoint>> GetIntradayAsync(string stockCode)
    {
        // 腾讯分时来源（历史分钟线 + 分时走势）：
        // ifzq.gtimg.cn（mkline 端点；web.ifzq 会重定向 web3 导致 DNS 失败），
        // 返回最近 320 根 1 分钟 K（跨 1-2 日），按东八区当日过滤并累计均价
        var result = new List<IntradayPoint>();
        try
        {
            var (prefix, code) = ParseCode(stockCode);
            var url = $"https://ifzq.gtimg.cn/appstock/app/kline/mkline?param={prefix}{code},m1,,320";
            var json = await _http.GetStringAsync(url);
            var stockData = JObject.Parse(json)?["data"]?[ $"{prefix}{code}"];
            var klines = stockData?["m1"] ?? stockData?["qfqm1"];
            if (klines == null) return result;

            // 目标交易日：盘前/周末/节假日取上一交易日（对应原版 getQuoteDateStr）
            var today = IntradayTargetDate.Get().ToString("yyyyMMdd");

            decimal preClose = 0, cumVol = 0, cumPv = 0;
            foreach (var item in klines)
            {
                // 腾讯格式：['YYYYMMDDHHmm', open, close, high, low, volume]
                if (item is not JArray arr || arr.Count < 6) continue;
                var rawTime = arr[0]?.ToString() ?? "";
                if (rawTime.Length < 12) continue;

                var open = (decimal?)arr[1] ?? 0;
                var close = (decimal?)arr[2] ?? 0;
                // 腾讯成交量可能是 "176.00" 这类带小数形式，用 TryParse + 截断，避免强转 long 抛 FormatException
                var volume = decimal.TryParse(arr[5]?.ToString(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var v) ? (long)v : 0;
                if (rawTime.Substring(0, 8) != today)
                {
                    // 昨日尾 bar：遍历中最后出现的昨日收盘即昨收
                    if (close > 0) preClose = close;
                    continue;
                }
                if (preClose == 0) preClose = open != 0 ? open : close;
                cumVol += volume;
                cumPv += close * volume;
                result.Add(new IntradayPoint
                {
                    Time = DateTime.ParseExact(rawTime, "yyyyMMddHHmm",
                        System.Globalization.CultureInfo.InvariantCulture),
                    Price = close,
                    // 腾讯分钟线无成交额，均价用量×价累计近似
                    AvgPrice = cumVol > 0 ? cumPv / cumVol : close,
                    Volume = volume,
                    PreClose = preClose,
                    ChangePercent = preClose != 0 ? (close - preClose) / preClose * 100 : 0
                });
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[腾讯] 获取 {Code} 分时失败", stockCode);
        }
        return result;
    }
}

/// <summary>
/// 新浪数据源
/// </summary>
public class SinaSource : IMarketDataSource
{
    private readonly HttpClient _http;
    public string Name => "新浪";

    public SinaSource(HttpClient http) => _http = http;

    public async Task<StockQuote?> GetQuoteAsync(string stockCode)
    {
        try
        {
            var (prefix, code) = ParseCode(stockCode);
            var url = $"https://hq.sinajs.cn/list={prefix}{code}";
            // hq.sinajs.cn 自 2022 起强制校验 Referer；用 per-request 头，
            // 不改共享 HttpClient 的 DefaultRequestHeaders（会波及其他数据源且有并发写问题）
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://finance.sina.com.cn");
            using var resp = await _http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            var match = Regex.Match(text, @"""([^""]+)""");
            if (!match.Success) return null;

            var parts = match.Groups[1].Value.Split(',');
            if (parts.Length < 32) return null;

            return new StockQuote
            {
                Code = stockCode,
                Name = parts[0],
                Open = InvParse.Decimal(parts[1]),
                PreClose = InvParse.Decimal(parts[2]),
                CurrentPrice = InvParse.Decimal(parts[3]),
                High = InvParse.Decimal(parts[4]),
                Low = InvParse.Decimal(parts[5]),
                Volume = InvParse.Long(parts[8]),
                Amount = InvParse.Decimal(parts[9]),
                DateTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Services.CnTimeZone.Get)
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// 代码 → (新浪前缀, 纯数字代码)（6/5/9 开头为沪市，其余深市）。
    /// 输入兼容 SH/SZ 前缀（计划列表代码带前缀），拼接 URL 前必须剥掉前缀，
    /// 否则出现 szSZ301013 双前缀导致接口静默返回空。
    /// </summary>
    private static (string prefix, string code) ParseCode(string stockCode)
    {
        var code = stockCode.ToUpperInvariant().Replace("SH", "").Replace("SZ", "");
        var prefix = code.StartsWith("6") || code.StartsWith("5") || code.StartsWith("9") ? "sh" : "sz";
        return (prefix, code);
    }

    public async Task<List<KLineData>> GetDailyKLinesAsync(string stockCode, int count = 250)
    {
        // 新浪K线接口（scale=240 日K，不复权），作为东财/腾讯之后的降级源
        var result = new List<KLineData>();
        try
        {
            var (prefix, code) = ParseCode(stockCode);
            var url = $"https://quotes.sina.cn/cn/api/json_v2.php/CN_MarketDataService.getKLineData?symbol={prefix}{code}&scale=240&ma=no&datalen={count}";
            var json = await _http.GetStringAsync(url);
            var arr = JArray.Parse(json);
            // 新浪格式：{day, open, high, low, close, volume}
            foreach (var item in arr)
            {
                var day = item["day"]?.ToString() ?? "";
                if (day.Length < 10) continue;
                if (!DateTime.TryParseExact(day.Substring(0, 10), "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var date)) continue;
                result.Add(new KLineData
                {
                    Date = date,
                    Open = (decimal?)item["open"] ?? 0,
                    Close = (decimal?)item["close"] ?? 0,
                    High = (decimal?)item["high"] ?? 0,
                    Low = (decimal?)item["low"] ?? 0,
                    Volume = (long?)item["volume"] ?? 0
                });
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[新浪] 获取 {Code} 日K失败", stockCode);
        }
        return result;
    }

    public async Task<List<IntradayPoint>> GetIntradayAsync(string stockCode)
    {
        // 新浪分时（scale=5 为最小粒度，5分钟K线），东财/腾讯之后的末级降级源
        var result = new List<IntradayPoint>();
        try
        {
            var (prefix, code) = ParseCode(stockCode);
            // datalen=60：覆盖全日（约49根）+ 昨日尾 bar，用于推算昨收
            var url = $"https://quotes.sina.cn/cn/api/json_v2.php/CN_MarketDataService.getKLineData?symbol={prefix}{code}&scale=5&ma=no&datalen=60";
            var json = await _http.GetStringAsync(url);
            var arr = JArray.Parse(json);

            // 目标交易日：盘前/周末/节假日取上一交易日（对应原版 getQuoteDateStr）
            var today = IntradayTargetDate.Get().ToString("yyyy-MM-dd");

            decimal preClose = 0, cumVol = 0, cumPv = 0;
            foreach (var item in arr)
            {
                // 新浪格式：{day:"yyyy-MM-dd HH:mm:ss", open, high, low, close, volume}
                var day = item["day"]?.ToString() ?? "";
                if (day.Length < 19) continue;
                if (!DateTime.TryParseExact(day, "yyyy-MM-dd HH:mm:ss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var time)) continue;

                var close = (decimal?)item["close"] ?? 0;
                if (!day.StartsWith(today))
                {
                    // 昨日尾 bar：最后出现的昨日收盘即昨收
                    if (close > 0) preClose = close;
                    continue;
                }
                var open = (decimal?)item["open"] ?? 0;
                var volume = (long?)item["volume"] ?? 0;
                if (preClose == 0) preClose = open != 0 ? open : close;
                cumVol += volume;
                cumPv += close * volume;
                result.Add(new IntradayPoint
                {
                    Time = time,
                    Price = close,
                    // 新浪5分钟线无成交额，均价用量×价累计近似
                    AvgPrice = cumVol > 0 ? cumPv / cumVol : close,
                    Volume = volume,
                    PreClose = preClose,
                    ChangePercent = preClose != 0 ? (close - preClose) / preClose * 100 : 0
                });
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[新浪] 获取 {Code} 分时失败", stockCode);
        }
        return result;
    }
}


