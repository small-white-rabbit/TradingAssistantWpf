using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Futu.OpenApi;
using Futu.OpenApi.Pb;
using Serilog;
using StockReview.Core.MarketData;

namespace StockReview.Core.Futu;

/// <summary>
/// 富途适配器 - 通过 futu-api NuGet 包直连本机 FutuOpenD（127.0.0.1:11111）。
/// 实现 OpenD 连接 + 股票订阅 + 实时推送回调，将秒级行情推送到上层。
/// 推送数据链路：FutuOpenD → FTAPI_Qot.OnReply_UpdateBasicQot/UpdateRT → OnQuotePush 事件
/// </summary>
public class FutuAdapter : IFutuAdapter
{
    private const string DefaultHost = "127.0.0.1";
    private const ushort DefaultPort = 11111;

    private FTAPI_Qot? _qot;
    private QotCallback? _callback;
    private bool _connected;
    /// <summary>OnInitConnect 回调错误码：-1=未收到, 0=成功</summary>
    private long _connectErrCode = -1;
    /// <summary>OnInitConnect / OnDisconnect 回调信号：Connect 等待它而非固定 Thread.Sleep</summary>
    private readonly ManualResetEventSlim _connectDone = new(false);

    private readonly HashSet<string> _subscribedCodes = new();
    private readonly object _lock = new();

    /// <summary>
    /// 实时行情推送事件。
    /// 参数: stockCode(纯6位数字代码，如 600519 —— 已归一化，
    /// 与计划/快照/行情缓存的键格式一致), lastPrice, volume, turnover
    /// </summary>
    public event Action<string, decimal, long, decimal>? OnQuotePush;

    /// <summary>连接/订阅状态变更事件（false=断开或订阅失败，上层应重连重订）</summary>
    public event Action<bool>? OnConnectionChanged;

    public bool IsConnected => _connected;

    // ===== 推送健康心跳（Info 级，5 分钟一条，暴露推送链路是否真实工作） =====
    private long _pushCount;
    private long _pushLastReportAt;

    /// <summary>每次收到有效推送调用；每 5 分钟输出一次汇总心跳日志</summary>
    private void RecordPushAlive()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Interlocked.Increment(ref _pushCount);
        if (Interlocked.Exchange(ref _pushLastReportAt, _pushLastReportAt) == 0) _pushLastReportAt = now;
        if (now - Interlocked.Read(ref _pushLastReportAt) > 5 * 60 * 1000)
        {
            Interlocked.Exchange(ref _pushLastReportAt, now);
            Log.Information("[富途] 推送心跳：近5分钟收到 {Count} 次报价推送（0=推送链路异常）", Interlocked.Exchange(ref _pushCount, 0));
        }
    }

    // ===== 连接 =====

    public bool Connect(string host = DefaultHost, ushort port = DefaultPort)
    {
        try
        {
            _callback = new QotCallback(this);
            // FTAPI_Qot 继承自 FTAPI_Conn，本身就是行情连接，必须由它发起 InitConnect。
            // 旧实现连接的是无关的 _conn 实例，_qot 从未连接，所有 Qot 请求(Sub/GetKL) serial=0 静默失败。
            _qot = new FTAPI_Qot();
            _qot.SetConnCallback(new ConnCallback(this));
            _qot.SetQotCallback(_callback);

            _connectErrCode = -1;
            _connectDone.Reset();
            var ok = _qot.InitConnect(host, port, false);
            if (!ok)
            {
                Log.Warning("[富途] InitConnect 调用失败 {Host}:{Port}", host, port);
                _connected = false;
                OnConnectionChanged?.Invoke(false);
                return false;
            }

            // 等待 OnInitConnect 回调（最多 5 秒）：回调到达即放行，不再固定阻塞 1 秒；
            // 超时则按当前错误码判定（-1 仍视为失败）
            if (!_connectDone.Wait(TimeSpan.FromSeconds(5)))
                Log.Warning("[富途] 等待 OnInitConnect 回调超时(5s)，errCode={ErrCode}", _connectErrCode);

            if (_connectErrCode != 0)
            {
                Log.Warning("[富途] OnInitConnect 失败 errCode={ErrCode}", _connectErrCode);
                _connected = false;
                OnConnectionChanged?.Invoke(false);
                return false;
            }

            _connected = true;
            OnConnectionChanged?.Invoke(true);
            // 新连接上旧订阅不成立：清空本地订阅记录，确保重连后重新订阅
            // （否则 Subscribe 会误以为已订阅而跳过，重连后收不到任何推送）
            lock (_lock) { _subscribedCodes.Clear(); }
            Log.Information("[富途] 连接 {Host}:{Port} 成功", host, port);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[富途] 连接失败");
            _connected = false;
            OnConnectionChanged?.Invoke(false);
            return false;
        }
    }

    // ===== 订阅 =====

    public bool Subscribe(List<string> stockCodes)
    {
        if (!_connected || _qot == null || _callback == null) return false;

        List<string> toSubscribe;
        lock (_lock)
        {
            toSubscribe = stockCodes.Where(c => !_subscribedCodes.Contains(c)).ToList();
            if (toSubscribe.Count == 0) return true;
            foreach (var c in stockCodes) _subscribedCodes.Add(c);
        }

        try
        {
            // 注意：消息 Build 后重复字段为只读列表（PopsicleList），必须在 Builder 上完成全部填充再 BuildPartial
            var c2sBuilder = new QotSub.C2S.Builder { IsSubOrUnSub = true, IsRegOrUnRegPush = true };
            foreach (var code in toSubscribe)
            {
                c2sBuilder.SecurityListList.Add(MakeSecurity(code));
            }
            // 注意本 SDK 的 SubType 枚举值：Basic=1, Ticker=4, RT=5, KL_1Min=11（旧代码把 4 误当 RT，实际订阅的是逐笔成交）
            c2sBuilder.SubTypeListList.Add((int)QotCommon.SubType.SubType_Basic);    // 实时报价（OnQuotePush 主推送源）
            c2sBuilder.SubTypeListList.Add((int)QotCommon.SubType.SubType_RT);       // 分时明细推送
            c2sBuilder.SubTypeListList.Add((int)QotCommon.SubType.SubType_KL_1Min);  // 1分钟K线（CurKL 协议要求先订阅对应K线类型才能拉取）
            var subReq = new QotSub.Request.Builder { C2S = c2sBuilder.BuildPartial() }.BuildPartial();
            var serialNo = _qot.Sub(subReq);
            if (serialNo == 0)
            {
                Log.Warning("[富途] 订阅请求发送失败（连接未就绪）");
                return false;
            }
            Log.Information("[富途] 订阅 {Count} 只股票, serial={Serial}, codes={Codes}",
                toSubscribe.Count, serialNo, string.Join(",", toSubscribe));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[富途] 订阅失败");
            return false;
        }
    }

    public List<string> GetSubscribedCodes()
    {
        lock (_lock) { return _subscribedCodes.ToList(); }
    }

    /// <summary>清空本地订阅记录（订阅整体失败后允许上层重发）</summary>
    private void ResetSubscription()
    {
        lock (_lock) { _subscribedCodes.Clear(); }
    }

    // ===== 分时 K 线拉取（富途轮询） =====

    // GetKL 请求-响应等待表：serialNo → TaskCompletionSource
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<QotGetKL.Response?>> _klWaiters = new();

    /// <summary>
    /// 拉取分时 K 线（富途轮询模式）。klType=1 为 1 分钟线。
    /// 未连接 OpenD 或超时（默认 5s）时返回 null，由上层降级到东财/腾讯。
    /// </summary>
    public Task<QotGetKL.Response?> GetKLAsync(string stockCode, int klType = 1, int count = 300, int timeoutMs = 5000)
    {
        var tcs = new TaskCompletionSource<QotGetKL.Response?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_connected || _qot == null)
        {
            tcs.SetResult(null);
            return tcs.Task;
        }

        try
        {
            var c2s = new QotGetKL.C2S.Builder
            {
                Security = MakeSecurity(stockCode),
                KlType = klType,
                RehabType = 0,   // 不复权（分时场景）
                ReqNum = count
            }.BuildPartial();
            var req = new QotGetKL.Request.Builder { C2S = c2s }.BuildPartial();

            var serialNo = _qot.GetKL(req);
            if (serialNo == 0)
            {
                Log.Debug("[富途] GetKL 请求发送失败 {Code}（连接未就绪）", stockCode);
                tcs.TrySetResult(null);
                return tcs.Task;
            }
            _klWaiters[serialNo] = tcs;
            Log.Debug("[富途] GetKL 请求 {Code} klType={KlType} serial={Serial}", stockCode, klType, serialNo);

            // 超时保护：未在时限内收到响应则置空，触发上层降级
            _ = Task.Delay(timeoutMs).ContinueWith(_ =>
            {
                if (_klWaiters.TryRemove(serialNo, out var waiter))
                    waiter.TrySetResult(null);
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[富途] GetKL 请求失败 {Code}", stockCode);
            tcs.TrySetResult(null);
        }
        return tcs.Task;
    }

    /// <summary>GetKL 响应完成回调（由内部 QotCallback 调用）</summary>
    private void CompleteGetKL(uint serialNo, QotGetKL.Response rsp)
    {
        if (_klWaiters.TryRemove(serialNo, out var waiter))
            waiter.TrySetResult(rsp);
    }

    // ===== 实时快照拉取（无需订阅，GetSecuritySnapshot） =====

    private readonly ConcurrentDictionary<uint, TaskCompletionSource<QotGetSecuritySnapshot.Response?>> _snapshotWaiters = new();

    /// <summary>
    /// 拉取实时行情快照（无需订阅）。返回完整行情字段（开高低收/量额/换手率等）。
    /// 未连接 OpenD 或超时（默认 5s）时返回 null，由上层降级到东财/腾讯。
    /// </summary>
    public Task<QotGetSecuritySnapshot.Response?> GetSecuritySnapshotAsync(string stockCode, int timeoutMs = 5000)
    {
        var tcs = new TaskCompletionSource<QotGetSecuritySnapshot.Response?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_connected || _qot == null)
        {
            tcs.SetResult(null);
            return tcs.Task;
        }

        try
        {
            var c2sBuilder = new QotGetSecuritySnapshot.C2S.Builder();
            c2sBuilder.SecurityListList.Add(MakeSecurity(stockCode));
            var req = new QotGetSecuritySnapshot.Request.Builder { C2S = c2sBuilder.BuildPartial() }.BuildPartial();

            var serialNo = _qot.GetSecuritySnapshot(req);
            if (serialNo == 0)
            {
                Log.Debug("[富途] GetSecuritySnapshot 请求发送失败 {Code}（连接未就绪）", stockCode);
                tcs.TrySetResult(null);
                return tcs.Task;
            }
            _snapshotWaiters[serialNo] = tcs;
            Log.Debug("[富途] GetSecuritySnapshot 请求 {Code} serial={Serial}", stockCode, serialNo);

            _ = Task.Delay(timeoutMs).ContinueWith(_ =>
            {
                if (_snapshotWaiters.TryRemove(serialNo, out var waiter))
                    waiter.TrySetResult(null);
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[富途] GetSecuritySnapshot 请求失败 {Code}", stockCode);
            tcs.TrySetResult(null);
        }
        return tcs.Task;
    }

    private void CompleteGetSecuritySnapshot(uint serialNo, QotGetSecuritySnapshot.Response rsp)
    {
        if (_snapshotWaiters.TryRemove(serialNo, out var waiter))
            waiter.TrySetResult(rsp);
    }

    // ===== 历史日K线拉取（RequestHistoryKL） =====

    private readonly ConcurrentDictionary<uint, TaskCompletionSource<QotRequestHistoryKL.Response?>> _historyKlWaiters = new();

    /// <summary>
    /// 拉取历史日K线（无需预下载）。klType=2 为日线，auType=1 为前复权（与东财 fqt=1 对齐）。
    /// 未连接 OpenD 或超时（默认 10s）时返回 null，由上层降级到东财。
    /// </summary>
    public Task<QotRequestHistoryKL.Response?> RequestHistoryKLAsync(string stockCode, int klType = 2, int count = 250, int timeoutMs = 10000)
    {
        var tcs = new TaskCompletionSource<QotRequestHistoryKL.Response?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_connected || _qot == null)
        {
            tcs.SetResult(null);
            return tcs.Task;
        }

        try
        {
            var nowDate = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, StockReview.Core.Services.CnTimeZone.Get);
            var beginDate = nowDate.AddDays(-730);
            var c2s = new QotRequestHistoryKL.C2S.Builder
            {
                Security = MakeSecurity(stockCode),
                KlType = klType,
                RehabType = 1,   // 前复权
                BeginTime = beginDate.ToString("yyyy-MM-dd"),
                EndTime = nowDate.ToString("yyyy-MM-dd"),
                MaxAckKLNum = count,
                NeedKLFieldsFlag = 0x3FF
            }.BuildPartial();
            var req = new QotRequestHistoryKL.Request.Builder { C2S = c2s }.BuildPartial();

            var serialNo = _qot.RequestHistoryKL(req);
            if (serialNo == 0)
            {
                Log.Debug("[富途] RequestHistoryKL 请求发送失败 {Code}（连接未就绪）", stockCode);
                tcs.TrySetResult(null);
                return tcs.Task;
            }
            _historyKlWaiters[serialNo] = tcs;
            Log.Debug("[富途] RequestHistoryKL 请求 {Code} klType={KlType} count={Count} serial={Serial}", stockCode, klType, count, serialNo);

            _ = Task.Delay(timeoutMs).ContinueWith(_ =>
            {
                if (_historyKlWaiters.TryRemove(serialNo, out var waiter))
                    waiter.TrySetResult(null);
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[富途] RequestHistoryKL 请求失败 {Code}", stockCode);
            tcs.TrySetResult(null);
        }
        return tcs.Task;
    }

    private void CompleteRequestHistoryKL(uint serialNo, QotRequestHistoryKL.Response rsp)
    {
        if (_historyKlWaiters.TryRemove(serialNo, out var waiter))
            waiter.TrySetResult(rsp);
    }

    // ===== 断开 =====

    public void Disconnect()
    {
        try { _qot?.Close(); } catch { }
        _connected = false;
        lock (_lock) { _subscribedCodes.Clear(); }
        OnConnectionChanged?.Invoke(false);
        Log.Information("[富途] 连接已断开");
    }

    // ===== 辅助 =====

    private static QotCommon.Security MakeSecurity(string stockCode)
    {
        var code = stockCode.Replace("SH", "").Replace("SZ", "").Replace("sh", "").Replace("sz", "");
        // A股市场枚举：沪市=21, 深市=22（旧代码误用 1/2，实为港股/港股期货，导致所有A股被报『未知股票』）
        var market = code.StartsWith("6") || code.StartsWith("5") || code.StartsWith("9")
            ? (int)QotCommon.QotMarket.QotMarket_CNSH_Security
            : (int)QotCommon.QotMarket.QotMarket_CNSZ_Security;
        return new QotCommon.Security.Builder { Code = code, Market = market }.BuildPartial();
    }

    // ========================================================================
    //  内部回调
    // ========================================================================

    private class ConnCallback : FTSPI_Conn
    {
        private readonly FutuAdapter _adapter;
        public ConnCallback(FutuAdapter adapter) => _adapter = adapter;

        public void OnInitConnect(FTAPI_Conn client, long errCode, string desc)
        {
            _adapter._connectErrCode = errCode;
            _adapter._connectDone.Set();
            Log.Information("[富途] OnInitConnect errCode={ErrCode} desc={Desc}", errCode, desc);
        }

        public void OnDisconnect(FTAPI_Conn client, long errCode)
        {
            Log.Warning("[富途] 连接断开 errCode={ErrCode}", errCode);
            _adapter._connected = false;
            // 断开也要放行等待中的 Connect，避免其干等 5 秒超时
            _adapter._connectDone.Set();
            _adapter.OnConnectionChanged?.Invoke(false);
        }
    }

    private class QotCallback : FTSPI_Qot
    {
        private readonly FutuAdapter _adapter;

        public QotCallback(FutuAdapter adapter) => _adapter = adapter;

        // === 有实际逻辑的回调 ===

        public void OnReply_GetSecuritySnapshot(FTAPI_Conn client, uint nSerialNo, QotGetSecuritySnapshot.Response rsp)
        {
            try
            {
                _adapter.CompleteGetSecuritySnapshot(nSerialNo, rsp);
            }
            catch (Exception ex) { Log.Warning(ex, "[富途] OnReply_GetSecuritySnapshot 解析失败"); }
        }

        public void OnReply_UpdateBasicQot(FTAPI_Conn client, uint nSerialNo, QotUpdateBasicQot.Response rsp)
        {
            try
            {
                if (rsp?.S2C?.BasicQotListList == null) return;

                foreach (var qot in rsp.S2C.BasicQotListList)
                {
                    var stockCode = qot.HasSecurity ? ToAppCode(qot.Security) : "";
                    var price = (decimal)(qot.HasCurPrice ? qot.CurPrice : 0);
                    var volume = qot.HasVolume ? (long)qot.Volume : 0;
                    var turnover = (decimal)(qot.HasTurnover ? qot.Turnover : 0);

                    if (price > 0)
                    {
                        Log.Debug("[富途推送] {Code} price={Price} vol={Vol}", stockCode, price, volume);
                        _adapter.OnQuotePush?.Invoke(stockCode, price, volume, turnover);
                        _adapter.RecordPushAlive();
                    }
                }
            }
            catch (Exception ex) { Log.Warning(ex, "[富途] OnReply_UpdateBasicQot 解析失败"); }
        }

        public void OnReply_GetKL(FTAPI_Conn client, uint nSerialNo, QotGetKL.Response rsp)
        {
            try
            {
                _adapter.CompleteGetKL(nSerialNo, rsp);
            }
            catch (Exception ex) { Log.Warning(ex, "[富途] OnReply_GetKL 解析失败"); }
        }

        public void OnReply_UpdateRT(FTAPI_Conn client, uint nSerialNo, QotUpdateRT.Response rsp)
        {
            try
            {
                if (rsp?.S2C?.RtListList == null || rsp.S2C.RtListList.Count == 0) return;

                var sec = rsp.S2C.HasSecurity ? rsp.S2C.Security : null;
                var stockCode = sec != null ? ToAppCode(sec) : "";

                // 取最后一条 RT 记录（最新价格）
                var lastRt = rsp.S2C.RtListList[rsp.S2C.RtListList.Count - 1];
                var price = (decimal)(lastRt.HasPrice ? lastRt.Price : 0);
                var volume = lastRt.HasVolume ? (long)lastRt.Volume : 0;
                var turnover = (decimal)(lastRt.HasTurnover ? lastRt.Turnover : 0);

                if (price > 0)
                {
                    Log.Debug("[富途RT推送] {Code} price={Price}", stockCode, price);
                    _adapter.OnQuotePush?.Invoke(stockCode, price, volume, turnover);
                }
            }
            catch (Exception ex) { Log.Warning(ex, "[富途] OnReply_UpdateRT 解析失败"); }
        }

        /// <summary>
        /// 富途 Security → app 代码（剥离 SH/SZ 市场前缀，返回纯6位数字码，
        /// 推送键与计划/快照/行情缓存键保持同构，
        /// 否则带前缀码匹配不到任何计划，秒级推送形同虚设）
        /// </summary>
        private static string ToAppCode(QotCommon.Security sec)
        {
            return sec.HasCode ? sec.Code : "";
        }

        // === 以下为 FTSPI_Qot 接口要求实现的空方法（auto-generated） ===

        public void OnReply_FilterCompetition(FTAPI_Conn client, uint nSerialNo, QotFilterCompetition.Response rsp) { }
        public void OnReply_GetArkActiveTransaction(FTAPI_Conn client, uint nSerialNo, QotGetArkActiveTransaction.Response rsp) { }
        public void OnReply_GetArkFundHolding(FTAPI_Conn client, uint nSerialNo, QotGetArkFundHolding.Response rsp) { }
        public void OnReply_GetArkStockDynamic(FTAPI_Conn client, uint nSerialNo, QotGetArkStockDynamic.Response rsp) { }
        public void OnReply_GetBasicQot(FTAPI_Conn client, uint nSerialNo, QotGetBasicQot.Response rsp) { }
        public void OnReply_GetBroker(FTAPI_Conn client, uint nSerialNo, QotGetBroker.Response rsp) { }
        public void OnReply_GetCapitalDistribution(FTAPI_Conn client, uint nSerialNo, QotGetCapitalDistribution.Response rsp) { }
        public void OnReply_GetCapitalFlow(FTAPI_Conn client, uint nSerialNo, QotGetCapitalFlow.Response rsp) { }
        public void OnReply_GetCodeChange(FTAPI_Conn client, uint nSerialNo, QotGetCodeChange.Response rsp) { }
        public void OnReply_GetCompanyExecutiveBackground(FTAPI_Conn client, uint nSerialNo, QotGetCompanyExecutiveBackground.Response rsp) { }
        public void OnReply_GetCompanyExecutives(FTAPI_Conn client, uint nSerialNo, QotGetCompanyExecutives.Response rsp) { }
        public void OnReply_GetCompanyOperationalEfficiency(FTAPI_Conn client, uint nSerialNo, QotGetCompanyOperationalEfficiency.Response rsp) { }
        public void OnReply_GetCompanyProfile(FTAPI_Conn client, uint nSerialNo, QotGetCompanyProfile.Response rsp) { }
        public void OnReply_GetCorporateActionsBuybacks(FTAPI_Conn client, uint nSerialNo, QotGetCorporateActionsBuybacks.Response rsp) { }
        public void OnReply_GetCorporateActionsDividends(FTAPI_Conn client, uint nSerialNo, QotGetCorporateActionsDividends.Response rsp) { }
        public void OnReply_GetCorporateActionsStockSplits(FTAPI_Conn client, uint nSerialNo, QotGetCorporateActionsStockSplits.Response rsp) { }
        public void OnReply_GetDailyShortVolume(FTAPI_Conn client, uint nSerialNo, QotGetDailyShortVolume.Response rsp) { }
        public void OnReply_GetDerivativeUnusual(FTAPI_Conn client, uint nSerialNo, SkillWrapAPI.DerivativeUnusualRsp rsp) { }
        public void OnReply_GetDividendCalendar(FTAPI_Conn client, uint nSerialNo, QotGetDividendCalendar.Response rsp) { }
        public void OnReply_GetDividendRank(FTAPI_Conn client, uint nSerialNo, QotGetDividendRank.Response rsp) { }
        public void OnReply_GetEarningsBeatRank(FTAPI_Conn client, uint nSerialNo, QotGetEarningsBeatRank.Response rsp) { }
        public void OnReply_GetEarningsCalendar(FTAPI_Conn client, uint nSerialNo, QotGetEarningsCalendar.Response rsp) { }
        public void OnReply_GetEconomicCalendar(FTAPI_Conn client, uint nSerialNo, QotGetEconomicCalendar.Response rsp) { }
        public void OnReply_GetEventContract(FTAPI_Conn client, uint nSerialNo, QotGetEventContract.Response rsp) { }
        public void OnReply_GetEventContractCategory(FTAPI_Conn client, uint nSerialNo, QotGetEventContractCategory.Response rsp) { }
        public void OnReply_GetEventContractComboList(FTAPI_Conn client, uint nSerialNo, QotGetEventContractComboList.Response rsp) { }
        public void OnReply_GetEventContractComboRfq(FTAPI_Conn client, uint nSerialNo, QotGetEventContractComboRfq.Response rsp) { }
        public void OnReply_GetEventContractEventList(FTAPI_Conn client, uint nSerialNo, QotGetEventContractEventList.Response rsp) { }
        public void OnReply_GetEventContractKline(FTAPI_Conn client, uint nSerialNo, QotGetEventContractKline.Response rsp) { }
        public void OnReply_GetEventContractMilestoneList(FTAPI_Conn client, uint nSerialNo, QotGetEventContractMilestoneList.Response rsp) { }
        public void OnReply_GetEventContractOrderBook(FTAPI_Conn client, uint nSerialNo, QotGetEventContractOrderBook.Response rsp) { }
        public void OnReply_GetEventContractSeriesList(FTAPI_Conn client, uint nSerialNo, QotGetEventContractSeriesList.Response rsp) { }
        public void OnReply_GetEventContractSnapshot(FTAPI_Conn client, uint nSerialNo, QotGetEventContractSnapshot.Response rsp) { }
        public void OnReply_GetEventContractTicker(FTAPI_Conn client, uint nSerialNo, QotGetEventContractTicker.Response rsp) { }
        public void OnReply_GetFedWatchDotPlot(FTAPI_Conn client, uint nSerialNo, QotGetFedWatchDotPlot.Response rsp) { }
        public void OnReply_GetFedWatchTargetRate(FTAPI_Conn client, uint nSerialNo, QotGetFedWatchTargetRate.Response rsp) { }
        public void OnReply_GetFinancialsEarningsPriceHistory(FTAPI_Conn client, uint nSerialNo, QotGetFinancialsEarningsPriceHistory.Response rsp) { }
        public void OnReply_GetFinancialsEarningsPriceMove(FTAPI_Conn client, uint nSerialNo, QotGetFinancialsEarningsPriceMove.Response rsp) { }
        public void OnReply_GetFinancialsRevenueBreakdown(FTAPI_Conn client, uint nSerialNo, QotGetFinancialsRevenueBreakdown.Response rsp) { }
        public void OnReply_GetFinancialsStatements(FTAPI_Conn client, uint nSerialNo, QotGetFinancialsStatements.Response rsp) { }
        public void OnReply_GetFinancialUnusual(FTAPI_Conn client, uint nSerialNo, SkillWrapAPI.FinancialUnusualRsp rsp) { }
        public void OnReply_GetFutureInfo(FTAPI_Conn client, uint nSerialNo, QotGetFutureInfo.Response rsp) { }
        public void OnReply_GetGlobalState(FTAPI_Conn client, uint nSerialNo, GetGlobalState.Response rsp) { }
        public void OnReply_GetHeatMapData(FTAPI_Conn client, uint nSerialNo, QotGetHeatMapData.Response rsp) { }
        public void OnReply_GetHighDividendSOERank(FTAPI_Conn client, uint nSerialNo, QotGetHighDividendSOERank.Response rsp) { }
        public void OnReply_GetHoldingChangeList(FTAPI_Conn client, uint nSerialNo, QotGetHoldingChangeList.Response rsp) { }
        public void OnReply_GetHotList(FTAPI_Conn client, uint nSerialNo, QotGetHotList.Response rsp) { }
        public void OnReply_GetIndicatorList(FTAPI_Conn client, uint nSerialNo, QotGetIndicatorList.Response rsp) { }
        public void OnReply_GetIndustrialChainByPlate(FTAPI_Conn client, uint nSerialNo, QotGetIndustrialChainByPlate.Response rsp) { }
        public void OnReply_GetIndustrialChainDetail(FTAPI_Conn client, uint nSerialNo, QotGetIndustrialChainDetail.Response rsp) { }
        public void OnReply_GetIndustrialChainList(FTAPI_Conn client, uint nSerialNo, QotGetIndustrialChainList.Response rsp) { }
        public void OnReply_GetIndustrialPlateInfo(FTAPI_Conn client, uint nSerialNo, QotGetIndustrialPlateInfo.Response rsp) { }
        public void OnReply_GetIndustrialPlateStock(FTAPI_Conn client, uint nSerialNo, QotGetIndustrialPlateStock.Response rsp) { }
        public void OnReply_GetInsiderHolderList(FTAPI_Conn client, uint nSerialNo, QotGetInsiderHolderList.Response rsp) { }
        public void OnReply_GetInsiderTradeList(FTAPI_Conn client, uint nSerialNo, QotGetInsiderTradeList.Response rsp) { }
        public void OnReply_GetInstitutionDistribution(FTAPI_Conn client, uint nSerialNo, QotGetInstitutionDistribution.Response rsp) { }
        public void OnReply_GetInstitutionHoldingChange(FTAPI_Conn client, uint nSerialNo, QotGetInstitutionHoldingChange.Response rsp) { }
        public void OnReply_GetInstitutionHoldingList(FTAPI_Conn client, uint nSerialNo, QotGetInstitutionHoldingList.Response rsp) { }
        public void OnReply_GetInstitutionList(FTAPI_Conn client, uint nSerialNo, QotGetInstitutionList.Response rsp) { }
        public void OnReply_GetInstitutionProfile(FTAPI_Conn client, uint nSerialNo, QotGetInstitutionProfile.Response rsp) { }
        public void OnReply_GetIpoList(FTAPI_Conn client, uint nSerialNo, QotGetIpoList.Response rsp) { }
        public void OnReply_GetMacroIndicatorHistory(FTAPI_Conn client, uint nSerialNo, QotGetMacroIndicatorHistory.Response rsp) { }
        public void OnReply_GetMacroIndicatorList(FTAPI_Conn client, uint nSerialNo, QotGetMacroIndicatorList.Response rsp) { }
        public void OnReply_GetMarketState(FTAPI_Conn client, uint nSerialNo, QotGetMarketState.Response rsp) { }
        public void OnReply_GetOptionChain(FTAPI_Conn client, uint nSerialNo, QotGetOptionChain.Response rsp) { }
        public void OnReply_GetOptionEarnings(FTAPI_Conn client, uint nSerialNo, QotGetOptionEarningsScreener.Response rsp) { }
        public void OnReply_GetOptionEvent(FTAPI_Conn client, uint nSerialNo, QotGetOptionEvent.Response rsp) { }
        public void OnReply_GetOptionEventAlert(FTAPI_Conn client, uint nSerialNo, QotGetOptionEventAlert.Response rsp) { }
        public void OnReply_GetOptionExerciseProbability(FTAPI_Conn client, uint nSerialNo, QotGetOptionExerciseProbability.Response rsp) { }
        public void OnReply_GetOptionExpirationDate(FTAPI_Conn client, uint nSerialNo, QotGetOptionExpirationDate.Response rsp) { }
        public void OnReply_GetOptionMarketStatistic(FTAPI_Conn client, uint nSerialNo, QotGetOptionMarketStatistic.Response rsp) { }
        public void OnReply_GetOptionQuote(FTAPI_Conn client, uint nSerialNo, QotGetOptionQuote.Response rsp) { }
        public void OnReply_GetOptionRank(FTAPI_Conn client, uint nSerialNo, QotGetOptionRank.Response rsp) { }
        public void OnReply_GetOptionScreen(FTAPI_Conn client, uint nSerialNo, QotOptionScreen.Response rsp) { }
        public void OnReply_GetOptionSellerScreener(FTAPI_Conn client, uint nSerialNo, QotGetOptionSellerScreener.Response rsp) { }
        public void OnReply_GetOptionStrategy(FTAPI_Conn client, uint nSerialNo, QotGetOptionStrategy.Response rsp) { }
        public void OnReply_GetOptionStrategyAnalysis(FTAPI_Conn client, uint nSerialNo, QotGetOptionStrategyAnalysis.Response rsp) { }
        public void OnReply_GetOptionStrategySpread(FTAPI_Conn client, uint nSerialNo, QotGetOptionStrategySpread.Response rsp) { }
        public void OnReply_GetOptionUnderlyingHisStatistic(FTAPI_Conn client, uint nSerialNo, QotGetOptionUnderlyingHisStatistic.Response rsp) { }
        public void OnReply_GetOptionUnderlyingHisVolatility(FTAPI_Conn client, uint nSerialNo, QotGetOptionUnderlyingHisVolatility.Response rsp) { }
        public void OnReply_GetOptionUnderlyingOverview(FTAPI_Conn client, uint nSerialNo, QotGetOptionUnderlyingOverview.Response rsp) { }
        public void OnReply_GetOptionUnderlyingRank(FTAPI_Conn client, uint nSerialNo, QotGetOptionUnderlyingRank.Response rsp) { }
        public void OnReply_GetOptionVolatility(FTAPI_Conn client, uint nSerialNo, QotGetOptionVolatility.Response rsp) { }
        public void OnReply_GetOptionZeroDteContract(FTAPI_Conn client, uint nSerialNo, QotGetOptionZeroDteContract.Response rsp) { }
        public void OnReply_GetOptionZeroDteScreener(FTAPI_Conn client, uint nSerialNo, QotGetOptionZeroDteScreener.Response rsp) { }
        public void OnReply_GetOrderBook(FTAPI_Conn client, uint nSerialNo, QotGetOrderBook.Response rsp) { }
        public void OnReply_GetOwnerPlate(FTAPI_Conn client, uint nSerialNo, QotGetOwnerPlate.Response rsp) { }
        public void OnReply_GetPeriodChangeRank(FTAPI_Conn client, uint nSerialNo, QotGetPeriodChangeRank.Response rsp) { }
        public void OnReply_GetPlateSecurity(FTAPI_Conn client, uint nSerialNo, QotGetPlateSecurity.Response rsp) { }
        public void OnReply_GetPlateSet(FTAPI_Conn client, uint nSerialNo, QotGetPlateSet.Response rsp) { }
        public void OnReply_GetPriceReminder(FTAPI_Conn client, uint nSerialNo, QotGetPriceReminder.Response rsp) { }
        public void OnReply_GetRatingChange(FTAPI_Conn client, uint nSerialNo, QotGetRatingChange.Response rsp) { }
        public void OnReply_GetReference(FTAPI_Conn client, uint nSerialNo, QotGetReference.Response rsp) { }
        public void OnReply_GetResearchAnalystConsensus(FTAPI_Conn client, uint nSerialNo, QotGetResearchAnalystConsensus.Response rsp) { }
        public void OnReply_GetResearchMorningstarReport(FTAPI_Conn client, uint nSerialNo, QotGetResearchMorningstarReport.Response rsp) { }
        public void OnReply_GetResearchRatingSummary(FTAPI_Conn client, uint nSerialNo, QotGetResearchRatingSummary.Response rsp) { }
        public void OnReply_GetRiseFallDistribution(FTAPI_Conn client, uint nSerialNo, QotGetRiseFallDistribution.Response rsp) { }
        public void OnReply_GetRT(FTAPI_Conn client, uint nSerialNo, QotGetRT.Response rsp) { }
        public void OnReply_GetSearchNews(FTAPI_Conn client, uint nSerialNo, QotGetSearchNews.Response rsp) { }
        public void OnReply_GetSearchQuote(FTAPI_Conn client, uint nSerialNo, QotGetSearchQuote.Response rsp) { }
        public void OnReply_GetShareholdersHolderDetail(FTAPI_Conn client, uint nSerialNo, QotGetShareholdersHolderDetail.Response rsp) { }
        public void OnReply_GetShareholdersHoldingChanges(FTAPI_Conn client, uint nSerialNo, QotGetShareholdersHoldingChanges.Response rsp) { }
        public void OnReply_GetShareholdersInstitutional(FTAPI_Conn client, uint nSerialNo, QotGetShareholdersInstitutional.Response rsp) { }
        public void OnReply_GetShareholdersOverview(FTAPI_Conn client, uint nSerialNo, QotGetShareholdersOverview.Response rsp) { }
        public void OnReply_GetShortInterest(FTAPI_Conn client, uint nSerialNo, QotGetShortInterest.Response rsp) { }
        public void OnReply_GetShortSellingRank(FTAPI_Conn client, uint nSerialNo, QotGetShortSellingRank.Response rsp) { }
        public void OnReply_GetStaticInfo(FTAPI_Conn client, uint nSerialNo, QotGetStaticInfo.Response rsp) { }
        public void OnReply_GetStockScreen(FTAPI_Conn client, uint nSerialNo, QotStockScreen.Response rsp) { }
        public void OnReply_GetSubInfo(FTAPI_Conn client, uint nSerialNo, QotGetSubInfo.Response rsp) { }
        public void OnReply_GetTechnicalUnusual(FTAPI_Conn client, uint nSerialNo, SkillWrapAPI.TechnicalUnusualRsp rsp) { }
        public void OnReply_GetTicker(FTAPI_Conn client, uint nSerialNo, QotGetTicker.Response rsp) { }
        public void OnReply_GetTopMoversRank(FTAPI_Conn client, uint nSerialNo, QotGetTopMoversRank.Response rsp) { }
        public void OnReply_GetTopTenBuySellBrokers(FTAPI_Conn client, uint nSerialNo, QotGetTopTenBuySellBrokers.Response rsp) { }
        public void OnReply_GetUSAfterHoursRank(FTAPI_Conn client, uint nSerialNo, QotGetUSAfterHoursRank.Response rsp) { }
        public void OnReply_GetUserSecurity(FTAPI_Conn client, uint nSerialNo, QotGetUserSecurity.Response rsp) { }
        public void OnReply_GetUserSecurityGroup(FTAPI_Conn client, uint nSerialNo, QotGetUserSecurityGroup.Response rsp) { }
        public void OnReply_GetUSOvernightRank(FTAPI_Conn client, uint nSerialNo, QotGetUSOvernightRank.Response rsp) { }
        public void OnReply_GetUSPreMarketRank(FTAPI_Conn client, uint nSerialNo, QotGetUSPreMarketRank.Response rsp) { }
        public void OnReply_GetValuationDetail(FTAPI_Conn client, uint nSerialNo, QotGetValuationDetail.Response rsp) { }
        public void OnReply_GetValuationPlateStockList(FTAPI_Conn client, uint nSerialNo, QotGetValuationPlateStockList.Response rsp) { }
        public void OnReply_GetWarrant(FTAPI_Conn client, uint nSerialNo, QotGetWarrant.Response rsp) { }
        public void OnReply_GetWarrantScreen(FTAPI_Conn client, uint nSerialNo, QotWarrantScreen.Response rsp) { }
        public void OnReply_ModifyUserSecurity(FTAPI_Conn client, uint nSerialNo, QotModifyUserSecurity.Response rsp) { }
        public void OnReply_Notify(FTAPI_Conn client, uint nSerialNo, Notify.Response rsp) { }
        public void OnReply_PushIndicatorCalc(FTAPI_Conn client, uint nSerialNo, QotPushIndicatorCalc.Response rsp) { }
        public void OnReply_RegQotPush(FTAPI_Conn client, uint nSerialNo, QotRegQotPush.Response rsp) { }
        public void OnReply_RequestHistoryEventContractKL(FTAPI_Conn client, uint nSerialNo, QotRequestHistoryEventContractKL.Response rsp) { }
        public void OnReply_RequestHistoryKL(FTAPI_Conn client, uint nSerialNo, QotRequestHistoryKL.Response rsp)
        {
            try
            {
                _adapter.CompleteRequestHistoryKL(nSerialNo, rsp);
            }
            catch (Exception ex) { Log.Warning(ex, "[富途] OnReply_RequestHistoryKL 解析失败"); }
        }
        public void OnReply_RequestHistoryKLQuota(FTAPI_Conn client, uint nSerialNo, QotRequestHistoryKLQuota.Response rsp) { }
        public void OnReply_RequestIndicatorCalc(FTAPI_Conn client, uint nSerialNo, QotRequestIndicatorCalc.Response rsp) { }
        public void OnReply_RequestRehab(FTAPI_Conn client, uint nSerialNo, QotRequestRehab.Response rsp) { }
        public void OnReply_RequestTradeDate(FTAPI_Conn client, uint nSerialNo, QotRequestTradeDate.Response rsp) { }
        public void OnReply_SetOptionEventAlert(FTAPI_Conn client, uint nSerialNo, QotSetOptionEventAlert.Response rsp) { }
        public void OnReply_SetPriceReminder(FTAPI_Conn client, uint nSerialNo, QotSetPriceReminder.Response rsp) { }
        public void OnReply_StockFilter(FTAPI_Conn client, uint nSerialNo, QotStockFilter.Response rsp) { }
        public void OnReply_Sub(FTAPI_Conn client, uint nSerialNo, QotSub.Response rsp)
        {
            try
            {
                if (rsp == null) return;

                // RetType=0 成功；非0 = OpenD 返回错误（如配额超限），标记重订
                if (rsp.HasRetType && (int)rsp.RetType != 0)
                {
                    Log.Warning("[富途] 订阅应答失败 retType={Ret} msg={Msg}，标记重订",
                        rsp.RetType, rsp.HasRetMsg ? rsp.RetMsg : "");
                    _adapter.ResetSubscription();
                    _adapter.OnConnectionChanged?.Invoke(false);
                    return;
                }

                Log.Information("[富途] 订阅应答 serial={Serial} 成功", nSerialNo);
            }
            catch (Exception ex) { Log.Warning(ex, "[富途] OnReply_Sub 解析失败"); }
        }
        public void OnReply_SubEventContract(FTAPI_Conn client, uint nSerialNo, QotSubEventContract.Response rsp) { }
        public void OnReply_UpdateBroker(FTAPI_Conn client, uint nSerialNo, QotUpdateBroker.Response rsp) { }
        public void OnReply_UpdateEventContractKline(FTAPI_Conn client, uint nSerialNo, QotUpdateEventContractKline.Response rsp) { }
        public void OnReply_UpdateEventContractOrderBook(FTAPI_Conn client, uint nSerialNo, QotUpdateEventContractOrderBook.Response rsp) { }
        public void OnReply_UpdateEventContractTicker(FTAPI_Conn client, uint nSerialNo, QotUpdateEventContractTicker.Response rsp) { }
        public void OnReply_UpdateKL(FTAPI_Conn client, uint nSerialNo, QotUpdateKL.Response rsp) { }
        public void OnReply_UpdateOptionEvent(FTAPI_Conn client, uint nSerialNo, QotUpdateOptionEvent.Response rsp) { }
        public void OnReply_UpdateOrderBook(FTAPI_Conn client, uint nSerialNo, QotUpdateOrderBook.Response rsp) { }
        public void OnReply_UpdatePriceReminder(FTAPI_Conn client, uint nSerialNo, QotUpdatePriceReminder.Response rsp) { }
        public void OnReply_UpdateTicker(FTAPI_Conn client, uint nSerialNo, QotUpdateTicker.Response rsp) { }
    }
}
