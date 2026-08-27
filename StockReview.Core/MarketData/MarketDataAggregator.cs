using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Serilog;

namespace StockReview.Core.MarketData;

/// <summary>
/// 行情数据聚合器 - 对应 Electron 版的 DataAggregator
/// HttpClient + 多源降级链 + 源健康追踪
/// 数据源：东财 / 腾讯 / 新浪；富途由 FutuAdapter 单独注入
/// </summary>
public class MarketDataAggregator
{
    private readonly List<IMarketDataSource> _sources = new();
    // 分时专用降级链（富途轮询源，不参与通用行情/K线链）
    private readonly List<IMarketDataSource> _intradaySources = new();
    private readonly System.Net.Http.HttpClient _httpClient;

    // 源健康追踪
    private readonly Dictionary<string, SourceHealth> _health = new();
    private readonly object _healthLock = new();
    private static readonly TimeSpan UnhealthyCooldown = TimeSpan.FromSeconds(60);
    private const double MinSuccessRate = 0.3;
    private const int MinSamples = 10;
    private const int MaxSamples = 20;

    public MarketDataAggregator(System.Net.Http.HttpClient httpClient)
    {
        _httpClient = httpClient;

        // 注册多源降级链（按优先级排序）
        _sources.Add(new Sources.EastMoneySource(_httpClient));
        _sources.Add(new Sources.TencentSource(_httpClient));
        _sources.Add(new Sources.SinaSource(_httpClient));
    }

    /// <summary>
    /// 获取实时行情（多源降级，对应 DataAggregator.getQuote）
    /// 含源健康追踪：连续失败率过高的数据源自动冷却 60 秒
    /// </summary>
    public async Task<StockQuote?> GetQuoteAsync(string stockCode)
    {
        for (int i = 0; i < _sources.Count; i++)
        {
            var source = _sources[i];

            if (IsSourceUnhealthy(source.Name))
            {
                Log.Debug("[行情] {Source} 最近成功率过低，暂时跳过", source.Name);
                continue;
            }

            try
            {
                var quote = await source.GetQuoteAsync(stockCode);
                if (quote != null)
                {
                    RecordResult(source.Name, success: true);
                    Log.Debug("[行情] {Source} 获取 {Code} 成功: {Price}",
                        source.Name, stockCode, quote.CurrentPrice);
                    return quote;
                }
                RecordResult(source.Name, success: false);
            }
            catch (Exception ex)
            {
                RecordResult(source.Name, success: false);
                Log.Warning(ex, "[行情] {Source} 获取 {Code} 失败，降级到下一个数据源",
                    source.Name, stockCode);
            }

            // 源间降级短暂停顿，避免对下一个源造成突发压力
            if (i < _sources.Count - 1)
                await Task.Delay(200);
        }

        Log.Error("[行情] 所有数据源均无法获取 {Code} 的行情", stockCode);
        return null;
    }

    /// <summary>
    /// 批量获取实时行情
    /// </summary>
    public async Task<List<StockQuote>> GetQuotesAsync(IEnumerable<string> stockCodes)
    {
        var results = new List<StockQuote>();
        foreach (var code in stockCodes)
        {
            var quote = await GetQuoteAsync(code);
            if (quote != null) results.Add(quote);
        }
        return results;
    }

    /// <summary>
    /// 获取日K线数据
    /// </summary>
    public async Task<List<KLineData>> GetDailyKLinesAsync(string stockCode, int count = 250)
    {
        for (int i = 0; i < _sources.Count; i++)
        {
            var source = _sources[i];
            try
            {
                var klines = await source.GetDailyKLinesAsync(stockCode, count);
                if (klines.Count > 0) return klines;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[行情] {Source} 获取 {Code} K线失败", source.Name, stockCode);
            }

            if (i < _sources.Count - 1)
                await Task.Delay(200);
        }
        return new List<KLineData>();
    }

    /// <summary>
    /// 注册额外数据源（如 Futu）
    /// </summary>
    public void AddSource(IMarketDataSource source)
    {
        _sources.Add(source);
    }

    /// <summary>
    /// 注册仅参与分时降级链的数据源（如富途轮询源）。
    /// 不进入通用 _sources，避免 GetQuoteAsync/K线链被空结果拖慢（每次降级有 200ms 停顿）。
    /// 分时链顺序：_intradaySources（富途）→ _sources（东财→腾讯→新浪）。
    /// </summary>
    public void AddIntradaySource(IMarketDataSource source)
    {
        _intradaySources.Add(source);
    }

    /// <summary>最近一次分时获取成功的数据源名（供 UI 展示"数据源：富途/东财/腾讯"）</summary>
    public string? LastIntradaySource { get; private set; }

    /// <summary>
    /// 获取分时数据（多源降级，对应 DataAggregator.getIntraday）：
    /// 富途轮询 → 东财 → 腾讯 → 新浪
    /// </summary>
    public async Task<List<IntradayPoint>> GetIntradayAsync(string stockCode)
    {
        foreach (var source in _intradaySources.Concat(_sources))
        {
            try
            {
                var points = await source.GetIntradayAsync(stockCode);
                if (points.Count > 0)
                {
                    LastIntradaySource = source.Name;
                    return points;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[行情] {Source} 获取 {Code} 分时失败", source.Name, stockCode);
            }
        }
        LastIntradaySource = null;
        return new List<IntradayPoint>();
    }

    // ============ 源健康追踪 ============

    private bool IsSourceUnhealthy(string sourceName)
    {
        lock (_healthLock)
        {
            if (!_health.TryGetValue(sourceName, out var h) || !h.UnhealthyUntil.HasValue)
                return false;
            if (DateTime.UtcNow < h.UnhealthyUntil.Value)
                return true;
            h.UnhealthyUntil = null;
            return false;
        }
    }

    private void RecordResult(string sourceName, bool success)
    {
        lock (_healthLock)
        {
            if (!_health.TryGetValue(sourceName, out var h))
            {
                h = new SourceHealth();
                _health[sourceName] = h;
            }

            h.RecentResults.Enqueue(success);
            if (h.RecentResults.Count > MaxSamples)
                h.RecentResults.Dequeue();

            if (h.RecentResults.Count >= MinSamples)
            {
                var rate = (double)h.RecentResults.Count(r => r) / h.RecentResults.Count;
                if (rate < MinSuccessRate)
                {
                    h.UnhealthyUntil = DateTime.UtcNow.Add(UnhealthyCooldown);
                    Log.Warning("[行情] {Source} 成功率 {Rate:P0} 低于阈值，冷却 {Seconds}s",
                        sourceName, rate, UnhealthyCooldown.TotalSeconds);
                }
            }
        }
    }

    private class SourceHealth
    {
        public Queue<bool> RecentResults { get; } = new();
        public DateTime? UnhealthyUntil { get; set; }
    }
}

/// <summary>
/// 股票行情数据模型
/// </summary>
public class StockQuote
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal CurrentPrice { get; set; }
    public decimal Change { get; set; }
    public decimal ChangePercent { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal PreClose { get; set; }
    public long Volume { get; set; }
    public decimal Amount { get; set; }
    public DateTime DateTime { get; set; }
}

/// <summary>
/// K线数据模型
/// </summary>
public class KLineData
{
    public DateTime Date { get; set; }
    public decimal Open { get; set; }
    public decimal Close { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public long Volume { get; set; }
    public decimal Amount { get; set; }
    public decimal Turnover { get; set; }
    public decimal ChangePercent { get; set; }
}

/// <summary>
/// 分时数据的目标交易日（对应 Electron marketTime.getQuoteDateStr）：
/// 交易日盘中/午休/盘后 → 今日；盘前/周末/节假日 → 上一交易日。
/// 非交易时段按"今日"过滤当日分时永远为空，导致无谓降级甚至全链无数据。
/// </summary>
public static class IntradayTargetDate
{
    public static DateTime Get()
    {
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Services.CnTimeZone.Get);
        var mt = new Services.MarketTimeService();
        if (mt.IsTradingDay(now) && mt.GetIntradayPhase(now).Phase != Services.IntradayPhase.PreOpen)
            return now.Date;
        return mt.GetPreviousTradingDay(now).Date;
    }
}

/// <summary>
/// 分时数据点（对应 Electron 分时图）
/// </summary>
public class IntradayPoint
{
    public DateTime Time { get; set; }
    public decimal Price { get; set; }
    public decimal AvgPrice { get; set; }
    public long Volume { get; set; }
    /// <summary>分钟成交额（可得时用于精确均价线）</summary>
    public decimal Amount { get; set; }
    /// <summary>昨收（涨幅基准，分时图中轴线）</summary>
    public decimal PreClose { get; set; }
    public decimal ChangePercent { get; set; }
}

/// <summary>
/// 行情数据源接口
/// </summary>
public interface IMarketDataSource
{
    string Name { get; }
    Task<StockQuote?> GetQuoteAsync(string stockCode);
    Task<List<KLineData>> GetDailyKLinesAsync(string stockCode, int count = 250);
    Task<List<IntradayPoint>> GetIntradayAsync(string stockCode);
}
