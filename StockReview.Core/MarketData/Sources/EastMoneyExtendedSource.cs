using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Serilog;
using StockReview.Core.Engines;

namespace StockReview.Core.MarketData.Sources;

/// <summary>
/// 东方财富扩展数据域源（建议4新增）- 对照 cn-financial-mcp 的东财端点清单
/// 6 个数据域：个股资金流 / 板块资金流 / 北向净流入 / 龙虎榜 / 涨跌停池 / 融资融券
/// 定位：与富途管道（A1 高质量、有权限门槛）互为降级源，本源免费无门槛走 HTTP 兜底；
/// 端点与 Sources.cs 的 push2 域名族同源，datacenter-web / push2ex 为补充域名。
/// 所有方法失败不抛异常，返回 null / 空列表，由调用方降级处理。
/// </summary>
public class EastMoneyExtendedSource
{
    private readonly HttpClient _http;

    public string Name => "东财扩展";

    public EastMoneyExtendedSource(HttpClient http) => _http = http;

    // ==================== 数据域1: 个股资金流 ====================

    /// <summary>
    /// 个股当日资金流（分钟级累计序列，取尾条=最新累计口径，单位元）
    /// 富途资金流失败时的兜底源，字段语义与富途 CapitalFlowData 对齐
    /// </summary>
    public async Task<CapitalFlowData?> GetStockCapitalFlowAsync(string stockCode)
    {
        try
        {
            var (market, code) = ParseStockCode(stockCode);
            var url = $"https://push2.eastmoney.com/api/qt/stock/fflow/kline/get?secid={market}.{code}&fields1=f1,f2,f3,f7&fields2=f51,f52,f53,f54,f55,f56&klt=1&lmt=0";
            var json = await _http.GetStringAsync(url);
            var klines = JObject.Parse(json)?["data"]?["klines"] as JArray;
            if (klines == null || klines.Count == 0) return null;

            // 尾条为当日最新累计："时间,主力,小单,中单,大单,超大单"
            var parts = klines[klines.Count - 1].ToString().Split(',');
            if (parts.Length < 6) return null;
            var main = ParseFlow(parts[1]);   // f52 主力净流入(=大单+超大单)
            var sml = ParseFlow(parts[2]);    // f53 小单净流入
            var mid = ParseFlow(parts[3]);    // f54 中单净流入
            var big = ParseFlow(parts[4]);    // f55 大单净流入
            var super = ParseFlow(parts[5]);  // f56 超大单净流入

            return new CapitalFlowData
            {
                Available = true,
                InFlow = main + sml + mid,    // 全单类型净流入合计
                MainInFlow = main,
                SuperInFlow = super,
                BigInFlow = big,
                MidInFlow = mid,
                SmlInFlow = sml
            };
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[东财扩展] {Code} 个股资金流失败", stockCode);
            return null;
        }
    }

    // ==================== 数据域2: 板块资金流 ====================

    /// <summary>行业板块资金流排行（按主力净流入降序，单位元）</summary>
    public async Task<List<SectorFlowItem>> GetSectorCapitalFlowAsync(int top = 10)
    {
        var result = new List<SectorFlowItem>();
        try
        {
            var url = $"https://push2.eastmoney.com/api/qt/clist/get?pn=1&pz={top}&po=1&np=1&fltt=2&invt=2&fid=f62&fs=m:90+t:2&fields=f12,f14,f62,f184,f66,f72,f78,f84";
            var json = await _http.GetStringAsync(url);
            var diff = JObject.Parse(json)?["data"]?["diff"];
            if (diff == null) return result;

            foreach (var item in diff)
            {
                result.Add(new SectorFlowItem
                {
                    Code = item["f12"]?.ToString() ?? "",
                    Name = item["f14"]?.ToString() ?? "",
                    MainInFlow = ToDouble(item["f62"]),
                    MainRatio = ToDouble(item["f184"]),
                    SuperInFlow = ToDouble(item["f66"]),
                    BigInFlow = ToDouble(item["f72"]),
                    MidInFlow = ToDouble(item["f78"]),
                    SmlInFlow = ToDouble(item["f84"])
                });
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[东财扩展] 板块资金流失败");
        }
        return result;
    }

    // ==================== 数据域3: 北向净流入 ====================

    /// <summary>
    /// 北向资金日净流入序列（单位元）。注：2024-08 起交易所停止盘中实时披露，
    /// 端点仍返回历史日频数据，最新条可能滞后——调用方需自行判断时效
    /// </summary>
    public async Task<List<DatedFlowItem>> GetNorthboundFlowAsync(int days = 30)
    {
        var result = new List<DatedFlowItem>();
        try
        {
            var url = $"https://push2his.eastmoney.com/api/qt/kamt.kline/get?fields1=f1,f3,f5&fields2=f51,f52&klt=101&lmt={days}";
            var json = await _http.GetStringAsync(url);
            var klines = JObject.Parse(json)?["data"]?["s2n"]?["klines"];
            if (klines == null) return result;

            foreach (var item in klines)
            {
                // "日期,当日净买入额"
                var parts = item.ToString().Split(',');
                if (parts.Length < 2) continue;
                if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
                result.Add(new DatedFlowItem { Date = d, NetInFlow = ParseFlow(parts[1]) });
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[东财扩展] 北向资金失败");
        }
        return result;
    }

    // ==================== 数据域4: 龙虎榜 ====================

    /// <summary>
    /// 龙虎榜明细（按净买额降序，单位元）。date 为空默认当日（非交易日返回空列表，
    /// 调用方可回退到前一交易日重查）
    /// </summary>
    public async Task<List<DragonTigerItem>> GetDragonTigerListAsync(DateTime? date = null, int top = 50)
    {
        var result = new List<DragonTigerItem>();
        try
        {
            var tradeDate = (date ?? DateTime.Today).ToString("yyyy-MM-dd");
            var url = $"https://datacenter-web.eastmoney.com/api/data/v1/get?reportName=RPT_DAILYBILLBOARD_DETAILSNEW&columns=ALL&filter=(TRADE_DATE='{tradeDate}')&pageNumber=1&pageSize={top}&sortTypes=-1&sortColumns=BILLBOARD_NET_AMT&source=WEB&client=WEB";
            var json = await _http.GetStringAsync(url);
            var rows = JObject.Parse(json)?["result"]?["data"];
            if (rows == null) return result;

            foreach (var r in rows)
            {
                result.Add(new DragonTigerItem
                {
                    Code = r["SECURITY_CODE"]?.ToString() ?? "",
                    Name = r["SECURITY_NAME_ABBR"]?.ToString() ?? "",
                    TradeDate = ParseDate(r["TRADE_DATE"]),
                    Reason = r["EXPLAIN"]?.ToString() ?? "",
                    ChangeRate = ToDouble(r["CHANGE_RATE"]),
                    NetBuyAmount = ToDouble(r["BILLBOARD_NET_AMT"]),
                    BuyAmount = ToDouble(r["BILLBOARD_BUY_AMT"]),
                    SellAmount = ToDouble(r["BILLBOARD_SELL_AMT"])
                });
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[东财扩展] 龙虎榜失败");
        }
        return result;
    }

    // ==================== 数据域5: 涨跌停池 ====================

    /// <summary>涨跌停池（limitUp=true 涨停池，false 跌停池；date 为空默认当日）</summary>
    public async Task<List<LimitPoolItem>> GetLimitPoolAsync(bool limitUp = true, DateTime? date = null)
    {
        var result = new List<LimitPoolItem>();
        try
        {
            var d = (date ?? DateTime.Today).ToString("yyyyMMdd");
            var endpoint = limitUp ? "getTopicZTPool" : "getTopicDTPool";
            var url = $"https://push2ex.eastmoney.com/{endpoint}?ut=7eea3edcaed734bea9cbfc24409ed989&dpt=wz.ztzt&Pageindex=0&pagesize=300&sort=fbt:asc&date={d}";
            var json = await _http.GetStringAsync(url);
            var pool = JObject.Parse(json)?["data"]?["pool"];
            if (pool == null) return result;

            foreach (var p in pool)
            {
                result.Add(new LimitPoolItem
                {
                    Code = p["c"]?.ToString() ?? "",
                    Name = p["n"]?.ToString() ?? "",
                    LimitDays = (int)ToDouble(p["lbc"]),
                    Industry = p["hybk"]?.ToString() ?? "",
                    TradeDate = date ?? DateTime.Today
                });
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[东财扩展] {Pool} 获取失败", limitUp ? "涨停池" : "跌停池");
        }
        return result;
    }

    // ==================== 数据域6: 融资融券 ====================

    /// <summary>个股融资融券明细（日频，最新在前，单位元）</summary>
    public async Task<List<MarginTradingItem>> GetMarginTradingAsync(string stockCode, int days = 10)
    {
        var result = new List<MarginTradingItem>();
        try
        {
            var code = stockCode.Replace("SH", "").Replace("SZ", "").Replace("sh", "").Replace("sz", "");
            var url = $"https://datacenter-web.eastmoney.com/api/data/v1/get?reportName=RPTA_WEB_RZRQ_GGMX&columns=ALL&filter=(scode=\"{code}\")&pageNumber=1&pageSize={days}&sortTypes=-1&sortColumns=DATE&source=WEB&client=WEB";
            var json = await _http.GetStringAsync(url);
            var rows = JObject.Parse(json)?["result"]?["data"];
            if (rows == null) return result;

            foreach (var r in rows)
            {
                result.Add(new MarginTradingItem
                {
                    Date = ParseDate(r["DATE"]),
                    MarginBalance = ToDouble(r["RZYE"]),
                    MarginBuyAmount = ToDouble(r["RZMRE"]),
                    MarginNetBuy = ToDouble(r["RZJME"]),
                    ShortBalance = ToDouble(r["RQYE"]),
                    TotalBalance = ToDouble(r["RZRQYE"])
                });
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[东财扩展] {Code} 融资融券失败", stockCode);
        }
        return result;
    }

    // ==================== 解析辅助 ====================

    /// <summary>解析股票代码为东财 secid（与 EastMoneySource.ParseStockCode 同规则）</summary>
    private static (int market, string code) ParseStockCode(string stockCode)
    {
        var code = stockCode.Replace("SH", "").Replace("SZ", "").Replace("sh", "").Replace("sz", "");
        if (code.StartsWith("6") || code.StartsWith("5") || code.StartsWith("11") || code.StartsWith("13"))
            return (1, code);
        return (0, code);
    }

    /// <summary>容错浮点解析（东财 JSON 数值可能为 "-" 占位；必须用 InvariantCulture）</summary>
    private static double ToDouble(JToken? token)
    {
        if (token == null) return 0;
        var s = token.ToString();
        if (string.IsNullOrWhiteSpace(s) || s == "-" || s == "—") return 0;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>资金流字段解析（fflow/kamt 的 klines 字符串 split 后的单位元数值）</summary>
    private static double ParseFlow(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>日期解析（东财 datacenter 日期常带 "T00:00:00" / " 00:00:00" 尾巴）</summary>
    private static DateTime ParseDate(JToken? token)
    {
        var s = token?.ToString();
        if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;
        s = s.Split('T')[0].Split(' ')[0];
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.MinValue;
    }
}

// ==================== 扩展数据域 DTO ====================

/// <summary>板块资金流条目（单位元）</summary>
public class SectorFlowItem
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public double MainInFlow { get; set; }      // f62 主力净流入(元)
    public double MainRatio { get; set; }       // f184 主力净占比(%)
    public double SuperInFlow { get; set; }     // f66 超大单净流入(元)
    public double BigInFlow { get; set; }       // f72 大单净流入(元)
    public double MidInFlow { get; set; }       // f78 中单净流入(元)
    public double SmlInFlow { get; set; }       // f84 小单净流入(元)
}

/// <summary>带日期的资金流条目（北向日净买入等，单位元）</summary>
public class DatedFlowItem
{
    public DateTime Date { get; set; }
    public double NetInFlow { get; set; }
}

/// <summary>龙虎榜明细条目（单位元）</summary>
public class DragonTigerItem
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime TradeDate { get; set; }
    public string Reason { get; set; } = "";        // 上榜原因
    public double ChangeRate { get; set; }          // 当日涨跌幅(%)
    public double NetBuyAmount { get; set; }        // 龙虎榜净买额(元)
    public double BuyAmount { get; set; }           // 龙虎榜买入额(元)
    public double SellAmount { get; set; }          // 龙虎榜卖出额(元)
}

/// <summary>涨跌停池条目</summary>
public class LimitPoolItem
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int LimitDays { get; set; }              // 涨停:连板数 / 跌停:连续跌停天数
    public string Industry { get; set; } = "";      // 所属行业板块
    public DateTime TradeDate { get; set; }
}

/// <summary>融资融券明细条目（单位元）</summary>
public class MarginTradingItem
{
    public DateTime Date { get; set; }
    public double MarginBalance { get; set; }       // RZYE 融资余额(元)
    public double MarginBuyAmount { get; set; }     // RZMRE 融资买入额(元)
    public double MarginNetBuy { get; set; }        // RZJME 融资净买入(元)
    public double ShortBalance { get; set; }        // RQYE 融券余额(元)
    public double TotalBalance { get; set; }        // RZRQYE 两融余额(元)
}
