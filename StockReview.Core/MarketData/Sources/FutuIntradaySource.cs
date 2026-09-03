using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Serilog;
using StockReview.Core.Futu;

namespace StockReview.Core.MarketData.Sources;

/// <summary>
/// 富途行情数据源 — 通过 FutuAdapter 向本机 OpenD 拉取全量行情数据：
/// · GetQuoteAsync：GetSecuritySnapshot 实时快照（无需订阅）
/// · GetDailyKLinesAsync：RequestHistoryKL 历史日K线（无需预下载，前复权）
/// · GetIntradayAsync：GetKL 当日1分钟K线（需先订阅）
/// OpenD 未连接/请求超时时返回空，由 MarketDataAggregator 降级到东财/腾讯。
/// </summary>
public class FutuIntradaySource : IMarketDataSource
{
    private readonly IFutuAdapter _futu;

    public string Name => "富途";

    public FutuIntradaySource(IFutuAdapter futu) => _futu = futu;

    public async Task<List<IntradayPoint>> GetIntradayAsync(string stockCode)
    {
        var result = new List<IntradayPoint>();
        try
        {
            if (!_futu.IsConnected)
            {
                // Information 级：富途是分时降级链首源，静默返回空会让问题表现为“莫名用了腾讯”而无从排查
                Log.Information("[富途分时] {Code} 未连接 OpenD，降级（连接见 [富途] 连接日志）", stockCode);
                return result;
            }

            // CurKL(实时K线)协议要求先订阅对应K线类型，否则 OpenD 返回错误、本源返回空，
            // 直接降级到东财/腾讯（富途轮询形同虚设的根因）。Subscribe 按股票代码去重，重复打开无额外开销。
            _futu.Subscribe(new List<string> { stockCode });
            await Task.Delay(300); // Sub 为异步指令，留出订阅在 OpenD 生效的时间

            var rsp = await _futu.GetKLAsync(stockCode, klType: 1, count: 300);
            if (rsp == null)
            {
                Log.Information("[富途分时] {Code} GetKL 超时/未连接，降级", stockCode);
                return result;
            }
            if (rsp.RetType != 0)
            {
                Log.Information("[富途分时] {Code} GetKL 错误 retType={RetType} msg={RetMsg}，降级",
                    stockCode, rsp.RetType, rsp.RetMsg);
                return result;
            }
            var klList = rsp.S2C?.KlListList;
            if (klList == null || klList.Count == 0)
            {
                Log.Information("[富途分时] {Code} GetKL 返回空 K线列表，降级", stockCode);
                return result;
            }

            // 目标交易日：盘前/周末/节假日取上一交易日（对应原版 getQuoteDateStr），
            // 否则富途按"今日"过滤在非交易时段永远为空，被迫无谓降级到东财/腾讯
            var today = IntradayTargetDate.Get();
            decimal preClose = 0, cumAmount = 0, cumVolume = 0;
            foreach (var kl in klList)
            {
                if (!kl.HasTimestamp || kl.Timestamp <= 0) continue;
                var time = DateTimeOffset.FromUnixTimeSeconds((long)kl.Timestamp).LocalDateTime;
                if (!kl.HasClosePrice) continue;

                var price = (decimal)kl.ClosePrice;
                if (time.Date < today)
                {
                    // 响应含昨日尾 bar（count=300），昨日最后一根分钟K收盘 = 昨收
                    preClose = price;
                    continue;
                }
                if (time.Date > today) continue;

                // 分钟线的 lastClosePrice 是前一分钟收盘而非昨收；
                // 今日首根 bar 的 lastClosePrice 恰为昨日收盘（其前一 bar 即昨日 15:00）
                if (preClose == 0 && kl.HasLastClosePrice) preClose = (decimal)kl.LastClosePrice;
                if (preClose == 0) preClose = price;

                var volume = kl.HasVolume ? (long)kl.Volume : 0;
                var amount = kl.HasTurnover ? (decimal)kl.Turnover : 0;
                cumAmount += amount;
                cumVolume += volume;

                result.Add(new IntradayPoint
                {
                    Time = time,
                    Price = price,
                    // 均价 = 累计成交额 / 累计成交量（富途分钟线含成交额）
                    AvgPrice = cumVolume > 0 ? JsMath.JsRound(cumAmount / cumVolume, 3) : price,
                    Volume = volume,
                    Amount = amount,
                    PreClose = preClose,
                    ChangePercent = preClose != 0 ? (price - preClose) / preClose * 100 : 0
                });
            }

            if (result.Count == 0)
                Log.Information("[富途分时] {Code} 时间过滤后为空（目标日={Target}，原始{Raw}根），降级",
                    stockCode, today.ToString("yyyy-MM-dd"), klList.Count);
            else
                Log.Information("[富途] 获取 {Code} 分时 {Count} 点 昨收={PreClose} 目标日={Target}",
                    stockCode, result.Count, preClose, today.ToString("yyyy-MM-dd"));
        }
        catch (Exception ex)
        {
            Log.Information(ex, "[富途] 获取 {Code} 分时异常，降级", stockCode);
        }
        return result;
    }

    // ===== 实时快照行情（GetSecuritySnapshot，无需订阅） =====

    public async Task<StockQuote?> GetQuoteAsync(string stockCode)
    {
        try
        {
            if (!_futu.IsConnected)
            {
                Log.Debug("[富途行情] {Code} 未连接 OpenD，降级", stockCode);
                return null;
            }

            var rsp = await _futu.GetSecuritySnapshotAsync(stockCode);
            if (rsp == null || rsp.RetType != 0)
            {
                Log.Debug("[富途行情] {Code} 快照请求失败 retType={RetType}，降级", stockCode, rsp?.RetType ?? -1);
                return null;
            }

            var snapshotList = rsp.S2C?.SnapshotListList;
            if (snapshotList == null || snapshotList.Count == 0)
            {
                Log.Debug("[富途行情] {Code} 快照返回空列表，降级", stockCode);
                return null;
            }

            var snap = snapshotList[0];
            if (!snap.HasBasic)
            {
                Log.Debug("[富途行情] {Code} 快照无 Basic 数据，降级", stockCode);
                return null;
            }
            var basic = snap.Basic;
            var curPrice = basic.HasCurPrice ? (decimal)basic.CurPrice : 0m;
            var preClose = basic.HasLastClosePrice ? (decimal)basic.LastClosePrice : 0m;

            return new StockQuote
            {
                Code = stockCode,
                Name = basic.HasName ? basic.Name : "",
                CurrentPrice = curPrice,
                Open = basic.HasOpenPrice ? (decimal)basic.OpenPrice : 0m,
                High = basic.HasHighPrice ? (decimal)basic.HighPrice : 0m,
                Low = basic.HasLowPrice ? (decimal)basic.LowPrice : 0m,
                PreClose = preClose,
                Volume = basic.HasVolume ? (long)basic.Volume : 0,
                Amount = basic.HasTurnover ? (decimal)basic.Turnover : 0m,
                Change = curPrice - preClose,
                ChangePercent = preClose > 0 ? (curPrice - preClose) / preClose * 100 : 0m,
                DateTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Services.CnTimeZone.Get)
            };
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[富途行情] 获取 {Code} 快照异常，降级", stockCode);
            return null;
        }
    }

    // ===== 历史日K线（RequestHistoryKL，无需预下载） =====

    public async Task<List<KLineData>> GetDailyKLinesAsync(string stockCode, int count = 250)
    {
        var result = new List<KLineData>();
        try
        {
            if (!_futu.IsConnected)
            {
                Log.Debug("[富途日K] {Code} 未连接 OpenD，降级", stockCode);
                return result;
            }

            // 富途 MaxAckKLNum 是分页大小：区间(730天≈490根)多于 250 根时单次请求只返回
            // 最前面一页（一年前的K线）。必须用 nextReqKey 翻页到最后一页才是最新数据。
            byte[]? nextKey = null;
            for (var page = 0; page < 8; page++)
            {
                var rsp = await _futu.RequestHistoryKLAsync(stockCode, klType: 2, count: count, nextReqKey: nextKey);
                if (rsp == null || rsp.RetType != 0)
                {
                    Log.Debug("[富途日K] {Code} 历史K线请求失败 retType={RetType}（第{Page}页），降级", stockCode, rsp?.RetType ?? -1, page + 1);
                    // 已翻到部分页面数据仍然可用（比整段降级到东财更快），仅在零数据时放弃
                    if (result.Count == 0) return result;
                    break;
                }

                var klList = rsp.S2C?.KlListList;
                if (klList == null || klList.Count == 0) break;

                foreach (var kl in klList)
                {
                    if (!kl.HasTimestamp || kl.Timestamp <= 0) continue;
                    var date = DateTimeOffset.FromUnixTimeSeconds((long)kl.Timestamp).ToOffset(TimeSpan.FromHours(8)).DateTime.Date;

                    result.Add(new KLineData
                    {
                        Date = date,
                        Open = kl.HasOpenPrice ? (decimal)kl.OpenPrice : 0m,
                        Close = kl.HasClosePrice ? (decimal)kl.ClosePrice : 0m,
                        High = kl.HasHighPrice ? (decimal)kl.HighPrice : 0m,
                        Low = kl.HasLowPrice ? (decimal)kl.LowPrice : 0m,
                        Volume = kl.HasVolume ? (long)kl.Volume : 0,
                        Amount = kl.HasTurnover ? (decimal)kl.Turnover : 0m,
                        Turnover = 0m,
                        ChangePercent = kl.HasChangeRate ? (decimal)kl.ChangeRate : 0m
                    });
                }

                // 无分页键 = 已是最后一页
                var key = rsp.S2C?.NextReqKey;
                if (key == null || key.IsEmpty || key.Length == 0) break;
                nextKey = key.ToByteArray();
            }

            result.Sort((a, b) => a.Date.CompareTo(b.Date));

            // 只保留最新 count 根（翻页可能累计超过 count）
            if (result.Count > count)
                result.RemoveRange(0, result.Count - count);

            // 补当日实时K线：富途历史接口盘中不含当日未收盘K线。MA5/MA10/MA30 口径
            // 必须含今日（close=当前最新价），对齐东财/腾讯源行为与行情软件均线位。
            var today = IntradayTargetDate.Get().Date;
            var cnToday = System.TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Services.CnTimeZone.Get).Date;
            if (today == cnToday && (result.Count == 0 || result[^1].Date < today))
            {
                var q = await GetQuoteAsync(stockCode);
                if (q != null && q.CurrentPrice > 0)
                {
                    result.Add(new KLineData
                    {
                        Date = today,
                        Open = q.Open,
                        High = q.High,
                        Low = q.Low,
                        Close = q.CurrentPrice,
                        Volume = q.Volume,
                        Amount = q.Amount
                    });
                    Log.Information("[富途日K] {Code} 历史K线缺今日数据，已用实时快照合成当日K线 close={Close}", stockCode, q.CurrentPrice);
                }
            }

            Log.Information("[富途日K] 获取 {Code} {Count} 根日K线（最新 {Last:yyyy-MM-dd}）", stockCode, result.Count, result.Count > 0 ? result[^1].Date : DateTime.MinValue);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[富途日K] 获取 {Code} 历史K线异常，降级", stockCode);
        }
        return result;
    }
}
