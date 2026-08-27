using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Serilog;
using StockReview.Core.Futu;

namespace StockReview.Core.MarketData.Sources;

/// <summary>
/// 富途分时数据源（富途轮询模式）- 通过 FutuAdapter 向本机 OpenD 拉取当日 1 分钟 K 线。
/// OpenD 未连接/请求超时时返回空，由 MarketDataAggregator 降级到东财/腾讯。
/// 仅 GetIntradayAsync 有实际实现；实时行情/K线走其它源（富途订阅推送由上层单独消费）。
/// </summary>
public class FutuIntradaySource : IMarketDataSource
{
    private readonly FutuAdapter _futu;

    public string Name => "富途";

    public FutuIntradaySource(FutuAdapter futu) => _futu = futu;

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

            // 目标交易日：盘前/周末/节假日取上一交易日（对应 Electron getQuoteDateStr），
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
                    AvgPrice = cumVolume > 0 ? Math.Round(cumAmount / cumVolume, 3) : price,
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

    // 实时行情与日K线不在本源职责内（富途订阅推送由 PlanScheduler 消费），返回空触发降级
    public Task<StockQuote?> GetQuoteAsync(string stockCode) => Task.FromResult<StockQuote?>(null);
    public Task<List<KLineData>> GetDailyKLinesAsync(string stockCode, int count = 250) => Task.FromResult(new List<KLineData>());
}
