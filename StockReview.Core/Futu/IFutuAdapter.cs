// FutuAdapter 接口（2026-09-02 优化报告 A2）。
// 目的：消费方（PlanSchedulerService / FutuIntradaySource / IntradayChartPanel）依赖接口，
// 行情源可替换、可 mock 测试。返回类型为 futu-api SDK 的 Response（消费方本就直接使用，无新增泄漏）。
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Futu.OpenApi.Pb;

namespace StockReview.Core.Futu;

/// <summary>富途 OpenD 适配器接口。实现：<see cref="FutuAdapter"/>。</summary>
public interface IFutuAdapter
{
    /// <summary>
    /// 实时行情推送事件。
    /// 参数: stockCode(纯6位数字代码，如 600519 —— 已归一化), lastPrice, volume, turnover
    /// </summary>
    event Action<string, decimal, long, decimal>? OnQuotePush;

    /// <summary>连接/订阅状态变更事件（false=断开或订阅失败，上层应重连重订）</summary>
    event Action<bool>? OnConnectionChanged;

    bool IsConnected { get; }

    /// <summary>连接本机 FutuOpenD（默认 127.0.0.1:11111），成功返回 true。</summary>
    bool Connect(string host = "127.0.0.1", ushort port = 11111);

    /// <summary>批量订阅股票实时行情，成功返回 true。</summary>
    bool Subscribe(List<string> stockCodes);

    List<string> GetSubscribedCodes();

    /// <summary>获取 K 线（klType: 1=1分钟, 2=日K 等，对齐富途 KLType）。</summary>
    Task<QotGetKL.Response?> GetKLAsync(string stockCode, int klType = 1, int count = 300, int timeoutMs = 5000);

    /// <summary>获取个股快照。</summary>
    Task<QotGetSecuritySnapshot.Response?> GetSecuritySnapshotAsync(string stockCode, int timeoutMs = 5000);

    /// <summary>
    /// 请求历史 K 线（单页）。区间内K线多于 maxAckKLNum 时响应附带 nextReqKey，
    /// 调用方可传回该键翻页直至取到最新数据（详见 FutuAdapter 实现注释）。
    /// </summary>
    Task<QotRequestHistoryKL.Response?> RequestHistoryKLAsync(string stockCode, int klType = 2, int count = 250, int timeoutMs = 10000, byte[]? nextReqKey = null);

    void Disconnect();
}
