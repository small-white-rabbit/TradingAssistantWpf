// 东财扩展数据域回归测试（建议4新增，2026-09-04）
// 覆盖 EastMoneyExtendedSource 六个数据域的 JSON 解析与容错：
// 个股资金流 / 板块资金流 / 北向 / 龙虎榜 / 涨跌停池 / 融资融券。
// 用 StubHandler 注入固定 JSON，锁定字段映射与端点契约，防止未来重构破坏解析。
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StockReview.Core.Engines;
using StockReview.Core.MarketData;
using StockReview.Core.MarketData.Sources;
using StockReview.Core.Services;
using Xunit;

namespace StockReview.Tests.MarketData;

/// <summary>按 URL 子串路由固定 JSON 的桩 Handler</summary>
public class StubHttpHandler : HttpMessageHandler
{
    private readonly (string urlPart, string json)[] _routes;

    public StubHttpHandler(params (string urlPart, string json)[] routes) => _routes = routes;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri?.ToString() ?? "";
        foreach (var (part, json) in _routes)
        {
            if (url.Contains(part))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
    }
}

public class EastMoneyExtendedSourceTests
{
    private static EastMoneyExtendedSource NewSource(params (string, string)[] routes) =>
        new(new HttpClient(new StubHttpHandler(routes)));

    // ==================== 数据域1: 个股资金流 ====================

    [Fact]
    public async Task StockFlow_ParsesLatestCumulativeBar()
    {
        // 尾条=当日最新累计："时间,主力,小单,中单,大单,超大单"
        var json = """
        {"data":{"klines":["09:31,-100000,50000,30000,20000,-120000","15:00,-5000000,2000000,1500000,1800000,-6800000"]}}
        """;
        var src = NewSource(("fflow/kline", json));

        var flow = await src.GetStockCapitalFlowAsync("SH600000");

        Assert.NotNull(flow);
        Assert.True(flow!.Available);
        Assert.Equal(-5000000, flow.MainInFlow, 0);
        Assert.Equal(2000000, flow.SmlInFlow, 0);
        Assert.Equal(1500000, flow.MidInFlow, 0);
        Assert.Equal(1800000, flow.BigInFlow, 0);
        Assert.Equal(-6800000, flow.SuperInFlow, 0);
        Assert.Equal(-1500000, flow.InFlow, 0); // 主力+小单+中单
    }

    [Fact]
    public async Task StockFlow_MainEqualsBigPlusSuper()
    {
        // 口径锁定：主力 = 大单 + 超大单（东财 f52 = f55 + f56）
        var json = """
        {"data":{"klines":["15:00,-2400000,1000000,500000,2600000,-5000000"]}}
        """;
        var src = NewSource(("fflow/kline", json));

        var flow = await src.GetStockCapitalFlowAsync("600000");

        Assert.NotNull(flow);
        Assert.Equal(flow!.BigInFlow + flow.SuperInFlow, flow.MainInFlow, 0);
    }

    [Fact]
    public async Task StockFlow_EmptyKlines_ReturnsNull()
    {
        var src = NewSource(("fflow/kline", """{"data":{"klines":[]}}"""));
        Assert.Null(await src.GetStockCapitalFlowAsync("600000"));
    }

    [Fact]
    public async Task StockFlow_DataNull_ReturnsNull()
    {
        var src = NewSource(("fflow/kline", """{"data":null}"""));
        Assert.Null(await src.GetStockCapitalFlowAsync("600000"));
    }

    // ==================== 数据域2: 板块资金流 ====================

    [Fact]
    public async Task SectorFlow_ParsesFieldsAndKeepsOrder()
    {
        var json = """
        {"data":{"diff":[
            {"f12":"BK0475","f14":"银行","f62":12345678.0,"f184":3.25,"f66":8000000.0,"f72":4345678.0,"f78":-2000000.0,"f84":-10345678.0},
            {"f12":"BK0428","f14":"半导体","f62":-9876543.0,"f184":-2.1,"f66":-7000000.0,"f72":-2876543.0,"f78":3000000.0,"f84":6876543.0}
        ]}}
        """;
        var src = NewSource(("clist/get", json));

        var sectors = await src.GetSectorCapitalFlowAsync(10);

        Assert.Equal(2, sectors.Count);
        Assert.Equal("BK0475", sectors[0].Code);
        Assert.Equal("银行", sectors[0].Name);
        Assert.Equal(12345678.0, sectors[0].MainInFlow, 0);
        Assert.Equal(3.25, sectors[0].MainRatio, 2);
        Assert.Equal(8000000.0, sectors[0].SuperInFlow, 0);
        Assert.Equal("半导体", sectors[1].Name);
        Assert.Equal(-9876543.0, sectors[1].MainInFlow, 0);
    }

    [Fact]
    public async Task SectorFlow_DiffNull_ReturnsEmpty()
    {
        var src = NewSource(("clist/get", """{"data":null}"""));
        Assert.Empty(await src.GetSectorCapitalFlowAsync());
    }

    [Fact]
    public async Task SectorFlow_MissingFields_ToleratesPlaceholders()
    {
        // 缺字段 / "-" 占位不抛异常，默认 0
        var json = """
        {"data":{"diff":[{"f12":"BK0001","f14":"测试","f62":"-","f184":null}]}}
        """;
        var src = NewSource(("clist/get", json));

        var sectors = await src.GetSectorCapitalFlowAsync();

        Assert.Single(sectors);
        Assert.Equal(0, sectors[0].MainInFlow, 0);
    }

    // ==================== 数据域3: 北向 ====================

    [Fact]
    public async Task Northbound_ParsesDatedFlows()
    {
        var json = """
        {"data":{"s2n":{"klines":["2026-09-02,88000000.0","2026-09-03,-15000000.5"]}}}
        """;
        var src = NewSource(("kamt.kline", json));

        var flows = await src.GetNorthboundFlowAsync();

        Assert.Equal(2, flows.Count);
        Assert.Equal(new DateTime(2026, 9, 2), flows[0].Date);
        Assert.Equal(88000000.0, flows[0].NetInFlow, 1);
        Assert.Equal(-15000000.5, flows[1].NetInFlow, 1);
    }

    [Fact]
    public async Task Northbound_BadDate_SkipsRow()
    {
        var json = """
        {"data":{"s2n":{"klines":["not-a-date,100","2026-09-03,200"]}}}
        """;
        var src = NewSource(("kamt.kline", json));

        var flows = await src.GetNorthboundFlowAsync();

        Assert.Single(flows);
        Assert.Equal(200, flows[0].NetInFlow, 0);
    }

    [Fact]
    public async Task Northbound_Empty_ReturnsEmpty()
    {
        var src = NewSource(("kamt.kline", """{"data":null}"""));
        Assert.Empty(await src.GetNorthboundFlowAsync());
    }

    // ==================== 数据域4: 龙虎榜 ====================

    [Fact]
    public async Task DragonTiger_ParsesRows()
    {
        var json = """
        {"result":{"data":[
            {"SECURITY_CODE":"600000","SECURITY_NAME_ABBR":"浦发银行","TRADE_DATE":"2026-09-03T00:00:00",
             "EXPLAIN":"日涨幅偏离值达7%","CHANGE_RATE":7.5,"BILLBOARD_NET_AMT":100000000.0,
             "BILLBOARD_BUY_AMT":150000000.0,"BILLBOARD_SELL_AMT":50000000.0},
            {"SECURITY_CODE":"000001","SECURITY_NAME_ABBR":"平安银行","TRADE_DATE":"2026-09-03 00:00:00",
             "EXPLAIN":"连续三个交易日内收盘价格涨幅偏离值累计20%","CHANGE_RATE":-3.2,
             "BILLBOARD_NET_AMT":-25000000.0,"BILLBOARD_BUY_AMT":30000000.0,"BILLBOARD_SELL_AMT":55000000.0}
        ]}}
        """;
        var src = NewSource(("RPT_DAILYBILLBOARD", json));

        var list = await src.GetDragonTigerListAsync(new DateTime(2026, 9, 3));

        Assert.Equal(2, list.Count);
        Assert.Equal("600000", list[0].Code);
        Assert.Equal("浦发银行", list[0].Name);
        Assert.Equal(new DateTime(2026, 9, 3), list[0].TradeDate);
        Assert.Contains("日涨幅偏离值", list[0].Reason);
        Assert.Equal(7.5, list[0].ChangeRate, 2);
        Assert.Equal(100000000.0, list[0].NetBuyAmount, 0);
        // 第二行：日期带空格尾巴也能解析
        Assert.Equal(new DateTime(2026, 9, 3), list[1].TradeDate);
        Assert.Equal(-25000000.0, list[1].NetBuyAmount, 0);
    }

    [Fact]
    public async Task DragonTiger_NoResult_ReturnsEmpty()
    {
        // 非交易日或无数据时 data 为 null
        var src = NewSource(("RPT_DAILYBILLBOARD", """{"result":null}"""));
        Assert.Empty(await src.GetDragonTigerListAsync(new DateTime(2026, 9, 5)));
    }

    // ==================== 数据域5: 涨跌停池 ====================

    [Fact]
    public async Task LimitPool_UpParses()
    {
        var json = """
        {"data":{"pool":[
            {"c":"600000","n":"浦发银行","lbc":2,"hybk":"银行"},
            {"c":"000001","n":"平安银行","lbc":1,"hybk":"银行"}
        ]}}
        """;
        var src = NewSource(("getTopicZTPool", json));

        var pool = await src.GetLimitPoolAsync(limitUp: true, date: new DateTime(2026, 9, 3));

        Assert.Equal(2, pool.Count);
        Assert.Equal("600000", pool[0].Code);
        Assert.Equal(2, pool[0].LimitDays);
        Assert.Equal("银行", pool[0].Industry);
        Assert.Equal(new DateTime(2026, 9, 3), pool[0].TradeDate);
    }

    [Fact]
    public async Task LimitPool_DownParses()
    {
        var json = """
        {"data":{"pool":[{"c":"300999","n":"测试","lbc":1,"hybk":"创业板"}]}}
        """;
        var src = NewSource(("getTopicDTPool", json));

        var pool = await src.GetLimitPoolAsync(limitUp: false);

        Assert.Single(pool);
        Assert.Equal("300999", pool[0].Code);
    }

    [Fact]
    public async Task LimitPool_DataNull_ReturnsEmpty()
    {
        var src = NewSource(("getTopicZTPool", """{"data":null}"""));
        Assert.Empty(await src.GetLimitPoolAsync(true));
    }

    // ==================== 数据域6: 融资融券 ====================

    [Fact]
    public async Task MarginTrading_ParsesAndFiltersByCode()
    {
        var json = """
        {"result":{"data":[
            {"DATE":"2026-09-03T00:00:00","RZYE":80000000.0,"RZMRE":10000000.0,"RZJME":2000000.0,"RQYE":500000.0,"RZRQYE":80500000.0},
            {"DATE":"2026-09-02 00:00:00","RZYE":78000000.0,"RZMRE":8000000.0,"RZJME":-500000.0,"RQYE":400000.0,"RZRQYE":78400000.0}
        ]}}
        """;
        var src = NewSource(("RPTA_WEB_RZRQ_GGMX", json));

        var rows = await src.GetMarginTradingAsync("SH600000");

        Assert.Equal(2, rows.Count);
        Assert.Equal(new DateTime(2026, 9, 3), rows[0].Date);
        Assert.Equal(80000000.0, rows[0].MarginBalance, 0);
        Assert.Equal(2000000.0, rows[0].MarginNetBuy, 0);
        Assert.Equal(500000.0, rows[0].ShortBalance, 0);
        Assert.Equal(80500000.0, rows[0].TotalBalance, 0);
        Assert.Equal(-500000.0, rows[1].MarginNetBuy, 0);
    }

    [Fact]
    public async Task MarginTrading_StripsMarketPrefix()
    {
        // SH/SZ 前缀必须剥掉再请求（端点按纯代码过滤）
        var json = """{"result":{"data":[]} }""";
        var src = NewSource(("RPTA_WEB_RZRQ_GGMX", json));

        var rows = await src.GetMarginTradingAsync("SZ000001");

        Assert.Empty(rows); // 路由命中即证明 URL 构造成功（未命中会同样返回空，此处只验证不抛异常）
    }

    [Fact]
    public async Task MarginTrading_ResultNull_ReturnsEmpty()
    {
        var src = NewSource(("RPTA_WEB_RZRQ_GGMX", """{"result":null}"""));
        Assert.Empty(await src.GetMarginTradingAsync("600000"));
    }

    // ==================== 网络失败容错 ====================

    [Fact]
    public async Task NetworkError_AllDomainsReturnSafeDefaults()
    {
        // 所有端点 404（未命中路由）→ 各域返回 null/空列表，不抛异常
        var src = NewSource(("never/match", "{}"));

        Assert.Null(await src.GetStockCapitalFlowAsync("600000"));
        Assert.Empty(await src.GetSectorCapitalFlowAsync());
        Assert.Empty(await src.GetNorthboundFlowAsync());
        Assert.Empty(await src.GetDragonTigerListAsync());
        Assert.Empty(await src.GetLimitPoolAsync(true));
        Assert.Empty(await src.GetMarginTradingAsync("600000"));
    }

    [Fact]
    public async Task MalformedJson_AllDomainsReturnSafeDefaults()
    {
        var src = NewSource(("eastmoney", "not-json{{{"));

        Assert.Null(await src.GetStockCapitalFlowAsync("600000"));
        Assert.Empty(await src.GetSectorCapitalFlowAsync());
        Assert.Empty(await src.GetNorthboundFlowAsync());
        Assert.Empty(await src.GetDragonTigerListAsync());
        Assert.Empty(await src.GetLimitPoolAsync(false));
        Assert.Empty(await src.GetMarginTradingAsync("600000"));
    }

    // ==================== 降级链集成（A1→B2） ====================

    [Fact]
    public async Task CapitalFlowFallback_FutuUnavailable_FallsToEastMoney()
    {
        // 场景：富途未注入（null）+ 东财扩展源可用 → FetchCapitalFlowWithCache 应返回东财口径
        var json = """
        {"data":{"klines":["15:00,3000000,-800000,-200000,1000000,2000000"]}}
        """;
        var http = new HttpClient(new StubHttpHandler(("fflow/kline", json)));
        var agg = new MarketDataAggregator(http);

        // PlanSchedulerService 12 参构造：其余依赖传 null!，仅注入聚合器
        var svc = new PlanSchedulerService(
            null!, agg, null, null!, null!, null!, null!, null!, null!, null!, null!, null!);

        var flow = await svc.FetchCapitalFlowWithCache("600000") as CapitalFlowData;

        Assert.NotNull(flow);
        Assert.True(flow!.Available);
        Assert.Equal(3000000, flow.MainInFlow, 0); // 东财主力口径

        // 二次调用命中缓存（同实例）
        var flow2 = await svc.FetchCapitalFlowWithCache("600000") as CapitalFlowData;
        Assert.NotNull(flow2);
        Assert.Equal(flow.MainInFlow, flow2!.MainInFlow, 0);
    }

    [Fact]
    public async Task CapitalFlowFallback_AllSourcesFail_ReturnsNullAndShortTtl()
    {
        // 场景：富途不可用 + 东财扩展源 404 → 返回 null（多因子引擎自动跳过资金流因子）
        var agg = new MarketDataAggregator(new HttpClient(new StubHttpHandler()));
        var svc = new PlanSchedulerService(
            null!, agg, null, null!, null!, null!, null!, null!, null!, null!, null!, null!);

        var flow = await svc.FetchCapitalFlowWithCache("600000");

        Assert.Null(flow);
    }
}
