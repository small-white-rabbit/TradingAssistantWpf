using System;
using System.Threading;
using Futu.OpenApi;
using Futu.OpenApi.Pb;
using Serilog;

// 实测 OpenD 是否推送 BasicQot：连接 → 订阅一只活跃股 → 打印 30 秒内所有推送
Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();

var callback = new TestQotCallback();
var conn = new FTAPI_Qot();
conn.SetQotCallback(callback);
var ok = conn.InitConnect("127.0.0.1", 11111, false);
Console.WriteLine($"InitConnect 调用: {ok}");
Thread.Sleep(1500);

if (!ok) { Console.WriteLine("连接失败"); return; }

// 订阅 300795（深市） Basic 推送
var sec = new QotCommon.Security.Builder
{
    Code = "300795",
    Market = (int)QotCommon.QotMarket.QotMarket_CNSZ_Security
}.BuildPartial();

var c2s = new QotSub.C2S.Builder { IsSubOrUnSub = true, IsRegOrUnRegPush = true };
c2s.SecurityListList.Add(sec);
c2s.SubTypeListList.Add((int)QotCommon.SubType.SubType_Basic);
var req = new QotSub.Request.Builder { C2S = c2s.BuildPartial() }.BuildPartial();
var serial = conn.Sub(req);
Console.WriteLine($"Sub serial={serial}（0=失败）");

Console.WriteLine("等待 30 秒观察推送...");
Thread.Sleep(30000);
Console.WriteLine($"共收到 BasicQot 推送批次: {TestQotCallback.PushCount}");

class TestQotCallback : FTSPI_Qot
{
    public static int PushCount;

    public void OnReply_UpdateBasicQot(FTAPI_Conn client, uint nSerialNo, QotUpdateBasicQot.Response rsp)
    {
        PushCount++;
        foreach (var qot in rsp.S2C.BasicQotListList)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 推送 {qot.Security.Code} price={qot.CurPrice} vol={qot.Volume}");
        }
    }

    public void OnReply_Sub(FTAPI_Conn client, uint nSerialNo, QotSub.Response rsp)
    {
        Console.WriteLine($"订阅应答 serial={nSerialNo} retType={rsp.RetType} msg={rsp.RetMsg}");
    }

    // 其余接口必需空实现
    public void OnReply_GetSecuritySnapshot(FTAPI_Conn c, uint s, QotGetSecuritySnapshot.Response r) { }
    public void OnReply_GetKL(FTAPI_Conn c, uint s, QotGetKL.Response r) { }
    public void OnReply_UpdateRT(FTAPI_Conn c, uint s, QotUpdateRT.Response r) { }
    public void OnReply_FilterCompetition(FTAPI_Conn c, uint s, QotFilterCompetition.Response r) { }
    public void OnReply_GetArkActiveTransaction(FTAPI_Conn c, uint s, QotGetArkActiveTransaction.Response r) { }
    public void OnReply_GetArkFundHolding(FTAPI_Conn c, uint s, QotGetArkFundHolding.Response r) { }
    public void OnReply_GetArkStockDynamic(FTAPI_Conn c, uint s, QotGetArkStockDynamic.Response r) { }
    public void OnReply_GetBasicQot(FTAPI_Conn c, uint s, QotGetBasicQot.Response r) { }
    public void OnReply_GetBroker(FTAPI_Conn c, uint s, QotGetBroker.Response r) { }
    public void OnReply_GetCapitalDistribution(FTAPI_Conn c, uint s, QotGetCapitalDistribution.Response r) { }
    public void OnReply_GetCapitalFlow(FTAPI_Conn c, uint s, QotGetCapitalFlow.Response r) { }
    public void OnReply_GetCodeChange(FTAPI_Conn c, uint s, QotGetCodeChange.Response r) { }
    public void OnReply_GetCompanyExecutiveBackground(FTAPI_Conn c, uint s, QotGetCompanyExecutiveBackground.Response r) { }
    public void OnReply_GetCompanyExecutives(FTAPI_Conn c, uint s, QotGetCompanyExecutives.Response r) { }
    public void OnReply_GetCompanyOperationalEfficiency(FTAPI_Conn c, uint s, QotGetCompanyOperationalEfficiency.Response r) { }
    public void OnReply_GetCompanyProfile(FTAPI_Conn c, uint s, QotGetCompanyProfile.Response r) { }
    public void OnReply_GetCorporateActionsBuybacks(FTAPI_Conn c, uint s, QotGetCorporateActionsBuybacks.Response r) { }
    public void OnReply_GetCorporateActionsDividends(FTAPI_Conn c, uint s, QotGetCorporateActionsDividends.Response r) { }
    public void OnReply_GetCorporateActionsStockSplits(FTAPI_Conn c, uint s, QotGetCorporateActionsStockSplits.Response r) { }
    public void OnReply_GetDailyShortVolume(FTAPI_Conn c, uint s, QotGetDailyShortVolume.Response r) { }
    public void OnReply_GetDerivativeUnusual(FTAPI_Conn c, uint s, SkillWrapAPI.DerivativeUnusualRsp r) { }
    public void OnReply_GetDividendCalendar(FTAPI_Conn c, uint s, QotGetDividendCalendar.Response r) { }
    public void OnReply_GetDividendRank(FTAPI_Conn c, uint s, QotGetDividendRank.Response r) { }
    public void OnReply_GetEarningsBeatRank(FTAPI_Conn c, uint s, QotGetEarningsBeatRank.Response r) { }
    public void OnReply_GetEarningsCalendar(FTAPI_Conn c, uint s, QotGetEarningsCalendar.Response r) { }
    public void OnReply_GetEconomicCalendar(FTAPI_Conn c, uint s, QotGetEconomicCalendar.Response r) { }
    public void OnReply_GetEventContract(FTAPI_Conn c, uint s, QotGetEventContract.Response r) { }
    public void OnReply_GetEventContractCategory(FTAPI_Conn c, uint s, QotGetEventContractCategory.Response r) { }
    public void OnReply_GetEventContractComboList(FTAPI_Conn c, uint s, QotGetEventContractComboList.Response r) { }
    public void OnReply_GetEventContractComboRfq(FTAPI_Conn c, uint s, QotGetEventContractComboRfq.Response r) { }
    public void OnReply_GetEventContractEventList(FTAPI_Conn c, uint s, QotGetEventContractEventList.Response r) { }
    public void OnReply_GetEventContractKline(FTAPI_Conn c, uint s, QotGetEventContractKline.Response r) { }
    public void OnReply_GetEventContractMilestoneList(FTAPI_Conn c, uint s, QotGetEventContractMilestoneList.Response r) { }
    public void OnReply_GetEventContractOrderBook(FTAPI_Conn c, uint s, QotGetEventContractOrderBook.Response r) { }
    public void OnReply_GetEventContractSeriesList(FTAPI_Conn c, uint s, QotGetEventContractSeriesList.Response r) { }
    public void OnReply_GetEventContractSnapshot(FTAPI_Conn c, uint s, QotGetEventContractSnapshot.Response r) { }
    public void OnReply_GetEventContractTicker(FTAPI_Conn c, uint s, QotGetEventContractTicker.Response r) { }
    public void OnReply_GetFedWatchDotPlot(FTAPI_Conn c, uint s, QotGetFedWatchDotPlot.Response r) { }
    public void OnReply_GetFedWatchTargetRate(FTAPI_Conn c, uint s, QotGetFedWatchTargetRate.Response r) { }
    public void OnReply_GetFinancialsEarningsPriceHistory(FTAPI_Conn c, uint s, QotGetFinancialsEarningsPriceHistory.Response r) { }
    public void OnReply_GetFinancialsEarningsPriceMove(FTAPI_Conn c, uint s, QotGetFinancialsEarningsPriceMove.Response r) { }
    public void OnReply_GetFinancialsRevenueBreakdown(FTAPI_Conn c, uint s, QotGetFinancialsRevenueBreakdown.Response r) { }
    public void OnReply_GetFinancialsStatements(FTAPI_Conn c, uint s, QotGetFinancialsStatements.Response r) { }
    public void OnReply_GetFinancialUnusual(FTAPI_Conn c, uint s, SkillWrapAPI.FinancialUnusualRsp r) { }
    public void OnReply_GetFutureInfo(FTAPI_Conn c, uint s, QotGetFutureInfo.Response r) { }
    public void OnReply_GetGlobalState(FTAPI_Conn c, uint s, GetGlobalState.Response r) { }
    public void OnReply_GetHeatMapData(FTAPI_Conn c, uint s, QotGetHeatMapData.Response r) { }
    public void OnReply_GetHighDividendSOERank(FTAPI_Conn c, uint s, QotGetHighDividendSOERank.Response r) { }
    public void OnReply_GetHoldingChangeList(FTAPI_Conn c, uint s, QotGetHoldingChangeList.Response r) { }
    public void OnReply_GetHotList(FTAPI_Conn c, uint s, QotGetHotList.Response r) { }
    public void OnReply_GetIndicatorList(FTAPI_Conn c, uint s, QotGetIndicatorList.Response r) { }
    public void OnReply_GetIndustrialChainByPlate(FTAPI_Conn c, uint s, QotGetIndustrialChainByPlate.Response r) { }
    public void OnReply_GetIndustrialChainDetail(FTAPI_Conn c, uint s, QotGetIndustrialChainDetail.Response r) { }
    public void OnReply_GetIndustrialChainList(FTAPI_Conn c, uint s, QotGetIndustrialChainList.Response r) { }
    public void OnReply_GetIndustrialPlateInfo(FTAPI_Conn c, uint s, QotGetIndustrialPlateInfo.Response r) { }
    public void OnReply_GetIndustrialPlateStock(FTAPI_Conn c, uint s, QotGetIndustrialPlateStock.Response r) { }
    public void OnReply_GetInsiderHolderList(FTAPI_Conn c, uint s, QotGetInsiderHolderList.Response r) { }
    public void OnReply_GetInsiderTradeList(FTAPI_Conn c, uint s, QotGetInsiderTradeList.Response r) { }
    public void OnReply_GetInstitutionDistribution(FTAPI_Conn c, uint s, QotGetInstitutionDistribution.Response r) { }
    public void OnReply_GetInstitutionHoldingChange(FTAPI_Conn c, uint s, QotGetInstitutionHoldingChange.Response r) { }
    public void OnReply_GetInstitutionHoldingList(FTAPI_Conn c, uint s, QotGetInstitutionHoldingList.Response r) { }
    public void OnReply_GetInstitutionList(FTAPI_Conn c, uint s, QotGetInstitutionList.Response r) { }
    public void OnReply_GetInstitutionProfile(FTAPI_Conn c, uint s, QotGetInstitutionProfile.Response r) { }
    public void OnReply_GetIpoList(FTAPI_Conn c, uint s, QotGetIpoList.Response r) { }
    public void OnReply_GetMacroIndicatorHistory(FTAPI_Conn c, uint s, QotGetMacroIndicatorHistory.Response r) { }
    public void OnReply_GetMacroIndicatorList(FTAPI_Conn c, uint s, QotGetMacroIndicatorList.Response r) { }
    public void OnReply_GetMarketState(FTAPI_Conn c, uint s, QotGetMarketState.Response r) { }
    public void OnReply_GetOptionChain(FTAPI_Conn c, uint s, QotGetOptionChain.Response r) { }
    public void OnReply_GetOptionEarnings(FTAPI_Conn c, uint s, QotGetOptionEarningsScreener.Response r) { }
    public void OnReply_GetOptionEvent(FTAPI_Conn c, uint s, QotGetOptionEvent.Response r) { }
    public void OnReply_GetOptionEventAlert(FTAPI_Conn c, uint s, QotGetOptionEventAlert.Response r) { }
    public void OnReply_GetOptionExerciseProbability(FTAPI_Conn c, uint s, QotGetOptionExerciseProbability.Response r) { }
    public void OnReply_GetOptionMarketStatistic(FTAPI_Conn c, uint s, QotGetOptionMarketStatistic.Response r) { }
    public void OnReply_GetOptionQuote(FTAPI_Conn c, uint s, QotGetOptionQuote.Response r) { }
    public void OnReply_GetOptionRank(FTAPI_Conn c, uint s, QotGetOptionRank.Response r) { }
    public void OnReply_GetOptionScreen(FTAPI_Conn c, uint s, QotOptionScreen.Response r) { }
    public void OnReply_GetOptionSellerScreener(FTAPI_Conn c, uint s, QotGetOptionSellerScreener.Response r) { }
    public void OnReply_GetOptionStrategy(FTAPI_Conn c, uint s, QotGetOptionStrategy.Response r) { }
    public void OnReply_GetOptionStrategyAnalysis(FTAPI_Conn c, uint s, QotGetOptionStrategyAnalysis.Response r) { }
    public void OnReply_GetOptionStrategySpread(FTAPI_Conn c, uint s, QotGetOptionStrategySpread.Response r) { }
    public void OnReply_GetOptionUnderlyingHisStatistic(FTAPI_Conn c, uint s, QotGetOptionUnderlyingHisStatistic.Response r) { }
    public void OnReply_GetOptionUnderlyingHisVolatility(FTAPI_Conn c, uint s, QotGetOptionUnderlyingHisVolatility.Response r) { }
    public void OnReply_GetOptionUnderlyingOverview(FTAPI_Conn c, uint s, QotGetOptionUnderlyingOverview.Response r) { }
    public void OnReply_GetOptionUnderlyingRank(FTAPI_Conn c, uint s, QotGetOptionUnderlyingRank.Response r) { }
    public void OnReply_GetOptionVolatility(FTAPI_Conn c, uint s, QotGetOptionVolatility.Response r) { }
    public void OnReply_GetOptionZeroDteContract(FTAPI_Conn c, uint s, QotGetOptionZeroDteContract.Response r) { }
    public void OnReply_GetOptionZeroDteScreener(FTAPI_Conn c, uint s, QotGetOptionZeroDteScreener.Response r) { }
    public void OnReply_GetOrderBook(FTAPI_Conn c, uint s, QotGetOrderBook.Response r) { }
    public void OnReply_GetOwnerPlate(FTAPI_Conn c, uint s, QotGetOwnerPlate.Response r) { }
    public void OnReply_GetPeriodChangeRank(FTAPI_Conn c, uint s, QotGetPeriodChangeRank.Response r) { }
    public void OnReply_GetPlateSecurity(FTAPI_Conn c, uint s, QotGetPlateSecurity.Response r) { }
    public void OnReply_GetPlateSet(FTAPI_Conn c, uint s, QotGetPlateSet.Response r) { }
    public void OnReply_GetPriceReminder(FTAPI_Conn c, uint s, QotGetPriceReminder.Response r) { }
    public void OnReply_GetRatingChange(FTAPI_Conn c, uint s, QotGetRatingChange.Response r) { }
    public void OnReply_GetReference(FTAPI_Conn c, uint s, QotGetReference.Response r) { }
    public void OnReply_GetResearchAnalystConsensus(FTAPI_Conn c, uint s, QotGetResearchAnalystConsensus.Response r) { }
    public void OnReply_GetResearchMorningstarReport(FTAPI_Conn c, uint s, QotGetResearchMorningstarReport.Response r) { }
    public void OnReply_GetResearchRatingSummary(FTAPI_Conn c, uint s, QotGetResearchRatingSummary.Response r) { }
    public void OnReply_GetRiseFallDistribution(FTAPI_Conn c, uint s, QotGetRiseFallDistribution.Response r) { }
    public void OnReply_GetRT(FTAPI_Conn c, uint s, QotGetRT.Response r) { }
    public void OnReply_GetSearchNews(FTAPI_Conn c, uint s, QotGetSearchNews.Response r) { }
    public void OnReply_GetSearchQuote(FTAPI_Conn c, uint s, QotGetSearchQuote.Response r) { }
    public void OnReply_GetShareholdersHolderDetail(FTAPI_Conn c, uint s, QotGetShareholdersHolderDetail.Response r) { }
    public void OnReply_GetShareholdersHoldingChanges(FTAPI_Conn c, uint s, QotGetShareholdersHoldingChanges.Response r) { }
    public void OnReply_GetShareholdersInstitutional(FTAPI_Conn c, uint s, QotGetShareholdersInstitutional.Response r) { }
    public void OnReply_GetShareholdersOverview(FTAPI_Conn c, uint s, QotGetShareholdersOverview.Response r) { }
    public void OnReply_GetShortInterest(FTAPI_Conn c, uint s, QotGetShortInterest.Response r) { }
    public void OnReply_GetShortSellingRank(FTAPI_Conn c, uint s, QotGetShortSellingRank.Response r) { }
    public void OnReply_GetStaticInfo(FTAPI_Conn c, uint s, QotGetStaticInfo.Response r) { }
    public void OnReply_GetStockScreen(FTAPI_Conn c, uint s, QotStockScreen.Response r) { }
    public void OnReply_GetTechnicalUnusual(FTAPI_Conn c, uint s, SkillWrapAPI.TechnicalUnusualRsp r) { }
    public void OnReply_GetTicker(FTAPI_Conn c, uint s, QotGetTicker.Response r) { }
    public void OnReply_GetTopMoversRank(FTAPI_Conn c, uint s, QotGetTopMoversRank.Response r) { }
    public void OnReply_GetTopTenBuySellBrokers(FTAPI_Conn c, uint s, QotGetTopTenBuySellBrokers.Response r) { }
    public void OnReply_GetUserSecurity(FTAPI_Conn c, uint s, QotGetUserSecurity.Response r) { }
    public void OnReply_GetUserSecurityGroup(FTAPI_Conn c, uint s, QotGetUserSecurityGroup.Response r) { }
    public void OnReply_GetUSAfterHoursRank(FTAPI_Conn c, uint s, QotGetUSAfterHoursRank.Response r) { }
    public void OnReply_GetValuationDetail(FTAPI_Conn c, uint s, QotGetValuationDetail.Response r) { }
    public void OnReply_GetValuationPlateStockList(FTAPI_Conn c, uint s, QotGetValuationPlateStockList.Response r) { }
    public void OnReply_GetWarrant(FTAPI_Conn c, uint s, QotGetWarrant.Response r) { }
    public void OnReply_GetWarrantScreen(FTAPI_Conn c, uint s, QotWarrantScreen.Response r) { }
    public void OnReply_ModifyUserSecurity(FTAPI_Conn c, uint s, QotModifyUserSecurity.Response r) { }
    public void OnReply_Notify(FTAPI_Conn c, uint s, Notify.Response r) { }
    public void OnReply_PushIndicatorCalc(FTAPI_Conn c, uint s, QotPushIndicatorCalc.Response r) { }
    public void OnReply_RegQotPush(FTAPI_Conn c, uint s, QotRegQotPush.Response r) { }
    public void OnReply_RequestHistoryEventContractKL(FTAPI_Conn c, uint s, QotRequestHistoryEventContractKL.Response r) { }
    public void OnReply_RequestHistoryKL(FTAPI_Conn c, uint s, QotRequestHistoryKL.Response r) { }
    public void OnReply_RequestHistoryKLQuota(FTAPI_Conn c, uint s, QotRequestHistoryKLQuota.Response r) { }
    public void OnReply_RequestIndicatorCalc(FTAPI_Conn c, uint s, QotRequestIndicatorCalc.Response r) { }
    public void OnReply_RequestRehab(FTAPI_Conn c, uint s, QotRequestRehab.Response r) { }
    public void OnReply_RequestTradeDate(FTAPI_Conn c, uint s, QotRequestTradeDate.Response r) { }
    public void OnReply_SetOptionEventAlert(FTAPI_Conn c, uint s, QotSetOptionEventAlert.Response r) { }
    public void OnReply_SetPriceReminder(FTAPI_Conn c, uint s, QotSetPriceReminder.Response r) { }
    public void OnReply_StockFilter(FTAPI_Conn c, uint s, QotStockFilter.Response r) { }
    public void OnReply_SubEventContract(FTAPI_Conn c, uint s, QotSubEventContract.Response r) { }
    public void OnReply_UpdateBroker(FTAPI_Conn c, uint s, QotUpdateBroker.Response r) { }
    public void OnReply_UpdateEventContractKline(FTAPI_Conn c, uint s, QotUpdateEventContractKline.Response r) { }
    public void OnReply_UpdateEventContractOrderBook(FTAPI_Conn c, uint s, QotUpdateEventContractOrderBook.Response r) { }
    public void OnReply_UpdateEventContractTicker(FTAPI_Conn c, uint s, QotUpdateEventContractTicker.Response r) { }
    public void OnReply_UpdateKL(FTAPI_Conn c, uint s, QotUpdateKL.Response r) { }
    public void OnReply_UpdateOptionEvent(FTAPI_Conn c, uint s, QotUpdateOptionEvent.Response r) { }
    public void OnReply_UpdateOrderBook(FTAPI_Conn c, uint s, QotUpdateOrderBook.Response r) { }
    public void OnReply_UpdatePriceReminder(FTAPI_Conn c, uint s, QotUpdatePriceReminder.Response r) { }
    public void OnReply_UpdateTicker(FTAPI_Conn c, uint s, QotUpdateTicker.Response r) { }
    public void OnReply_GetSubInfo(FTAPI_Conn c, uint s, QotGetSubInfo.Response r) { }
    public void OnReply_GetOptionExpirationDate(FTAPI_Conn c, uint s, QotGetOptionExpirationDate.Response r) { }
    public void OnReply_GetUSPreMarketRank(FTAPI_Conn c, uint s, QotGetUSPreMarketRank.Response r) { }
    public void OnReply_GetUSOvernightRank(FTAPI_Conn c, uint s, QotGetUSOvernightRank.Response r) { }
}
