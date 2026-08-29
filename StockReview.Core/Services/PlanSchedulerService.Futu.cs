using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Serilog;
using StockReview.Core.Data;
using StockReview.Core.MarketData;

namespace StockReview.Core.Services;

public partial class PlanSchedulerService
{

    // ============================================================================
    // 富途推送 - 对应 planScheduler.js _bindFutuPush / _onFutuPush / _runPushDrivenDetect / _ensureFutuSubscription
    // ============================================================================

    /// <summary>
    /// 确保富途订阅 - 对应 planScheduler.js _ensureFutuSubscription
    /// 调用 FutuAdapter 连接 OpenD + 订阅计划内股票
    /// </summary>
    private async Task EnsureFutuSubscriptionAsync()
    {
        if (_futuAdapter == null) return;  // 未注入富途适配器，降级到 HTTP 轮询

        // 期望订阅集合（今日 + 持仓过夜，含备份导入的旧日期计划）
        var stockCodes = _tradePlanStore.TodayPlans
            .Concat(_tradePlanStore.MonitoringPlans)
            .Where(IsPlanMonitorable)
            .Select(p => p.StockCode)
            .Distinct()
            .ToList();

        // 已连接且全部覆盖：无需操作（盘中新添计划在此发现缺口 → 下个 tick 增量补订）
        if (_futuSubscribed && _futuAdapter.IsConnected)
        {
            var subscribed = _futuAdapter.GetSubscribedCodes();
            if (stockCodes.All(subscribed.Contains)) return;
        }

        var now = Now;
        var retryDelay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, _futuSubscribeRetryCount)));
        if (now - _lastFutuSubscribeAttempt < retryDelay) return;

        _lastFutuSubscribeAttempt = now;
        _futuSubscribeRetryCount++;

        try
        {
            // 1. 连接 OpenD
            if (!_futuAdapter.IsConnected)
            {
                var ok = _futuAdapter.Connect();
                if (!ok)
                {
                    Log.Warning("[计划调度] 富途 OpenD 连接失败(重试 {Count})", _futuSubscribeRetryCount);
                    return;
                }
            }

            // 2. 绑定推送/连接事件（只绑一次；重订时不重复挂处理器）
            if (!_futuHandlerBound)
            {
                _futuAdapter.OnQuotePush += OnFutuPush;
                _futuAdapter.OnConnectionChanged += OnFutuConnectionChanged;
                _futuHandlerBound = true;
                Log.Information("[计划调度] 富途推送回调已绑定");
            }

            // 3. 增量订阅（adapter 内部按已订阅集合去重，只发缺失代码）
            if (stockCodes.Count > 0)
            {
                if (_futuAdapter.Subscribe(stockCodes))
                {
                    _futuSubscribed = true;
                    _futuSubscribeRetryCount = 0;
                    Log.Information("[计划调度] 富途实时推送已订阅 {Count} 只股票", stockCodes.Count);
                }
                else
                {
                    // 发送失败（连接未就绪）：保留 _futuSubscribed=false，下个 tick 按
                    // 退避节奏重试（对齐 Electron _scheduleFutuRetry 自愈）
                    Log.Warning("[计划调度] 富途订阅发送失败(重试 {Count})", _futuSubscribeRetryCount);
                }
            }
            else
            {
                _futuSubscribed = true; // 无监控股票视为订阅完成，避免每 tick 重复尝试
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[计划调度] 富途订阅失败(重试 {Count})", _futuSubscribeRetryCount);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 富途连接/订阅状态变更回调：断开或订阅失败时复位标记，
    /// 由交易时段每个 tick 的 EnsureFutuSubscriptionAsync 自动重连重订（对齐 Electron closed/error 自愈）
    /// </summary>
    private void OnFutuConnectionChanged(bool connected)
    {
        if (connected) return;
        if (_futuSubscribed)
        {
            _futuSubscribed = false;
            Log.Warning("[计划调度] 富途连接/订阅中断，等待交易tick自动重连重订");
        }
    }

    /// <summary>
    /// 绑定富途推送 - 已在 EnsureFutuSubscriptionAsync 中内联实现
    /// </summary>
    private void BindFutuPush()
    {
        // 推送回调绑定逻辑已移入 EnsureFutuSubscriptionAsync
    }

    /// <summary>
    /// 富途推送回调 - 对应 planScheduler.js _onFutuPush
    /// 直接受秒级推送价格触发信号检测，不走 HTTP 重新拉取
    /// </summary>
    public void OnFutuPush(string stockCode, decimal price, long volume, decimal amount)
    {
        var now = Now;
        var cacheTtl = TimeSpan.FromMilliseconds(Math.Max(3000, _settingsStore.Settings.RefreshIntervalMs));

        // 更新/创建缓存中的行情（推送数据直接写入，不回头走 HTTP）
        StockQuote quote;
        if (_batchQuoteCache.TryGetValue(stockCode, out var cached))
        {
            quote = cached.Data;
            quote.CurrentPrice = price;
            quote.Volume = volume;
            quote.Amount = amount;
            quote.DateTime = now;
        }
        else
        {
            quote = new StockQuote
            {
                Code = stockCode,
                CurrentPrice = price,
                Volume = volume,
                Amount = amount,
                DateTime = now
            };
        }
        _batchQuoteCache[stockCode] = (quote, now.Add(cacheTtl));

        // 原地更新该股最新快照的价格（不追加新快照，对齐 Electron _onFutuPush：
        // 保持快照节奏由 10 秒 tick 主导，避免推送把多快照脉冲窗口压缩成几秒钟）
        var snapCache = _snapshotCache.GetOrAdd(stockCode, _ => new List<PriceSnapshot>());
        lock (snapCache)
        {
            if (snapCache.Count > 0)
            {
                var last = snapCache[^1];
                last.Price = price;
                // 秒级轨迹：维护本采样区间内的高低点。若只存区间末价，秒级冲高会被
                // 随后的回落覆盖，双顶提前预警看到的"反弹高点"失真（对齐 Electron）
                if (last.High == 0 || price > last.High) last.High = price;
                if (last.Low == 0 || price < last.Low) last.Low = price;
                // 富途推送的 amount/volume 即当日累计额/量 → 真实 VWAP；失败保留原均价
                if (volume > 0 && amount > 0)
                {
                    last.Vwap = amount / volume;
                }
            }
        }

        // 直接触发推送驱动检测（对齐 Electron：空闲立即检测；检测中标记 trailing 补跑）
        if (_pushDetectRunning.ContainsKey(stockCode))
        {
            _pushDetectQueued[stockCode] = 1;
        }
        else
        {
            _ = RunPushDrivenDetectAsync(stockCode, quote);
        }
    }

    /// <summary>
    /// 推送驱动检测 - 对应 planScheduler.js _runPushDrivenDetect
    /// 按股票防重入（检测为纯计算毫秒级，无节流）；trailing 补跑保证
    /// 检测执行期间到达的新价格不丢（检测完成后立即用最新价再跑一轮）
    /// </summary>
    private async Task RunPushDrivenDetectAsync(string stockCode, StockQuote pushQuote)
    {
        if (!_pushDetectRunning.TryAdd(stockCode, 1)) return; // 已在检测中：仅排队

        try
        {
            await DetectForStockAsync(stockCode, pushQuote);

            // trailing 补跑：检测期间有新推送到达 → 用缓存中的最新价立即再跑
            while (_pushDetectQueued.TryRemove(stockCode, out _))
            {
                var latest = pushQuote;
                if (_batchQuoteCache.TryGetValue(stockCode, out var cached) && cached.Data.CurrentPrice > 0)
                {
                    latest = cached.Data; // 检测期间到达的推送持续覆盖缓存
                }
                await DetectForStockAsync(stockCode, latest);
            }
        }
        finally
        {
            _pushDetectRunning.TryRemove(stockCode, out _);
        }
    }

    /// <summary>单股票增量检测（推送价直供，绕过 HTTP 轮询/缓存）</summary>

    /// <summary>单股票增量检测（推送价直供，绕过 HTTP 轮询/缓存）</summary>
    private async Task DetectForStockAsync(string stockCode, StockQuote quote)
    {
        // 查找该股票的计划（今日 + 持仓过夜，含备份导入的旧日期计划）
        var plans = _tradePlanStore.TodayPlans
            .Concat(_tradePlanStore.MonitoringPlans)
            .Where(p => p.StockCode == stockCode && IsPlanMonitorable(p))
            .ToList();

        if (plans.Count == 0) return;
        if (quote == null || quote.CurrentPrice <= 0) return;

        foreach (var plan in plans)
        {
            // 检测期间计划可能已被执行/取消
            if (!IsPlanMonitorable(plan)) continue;
            await CheckPlanSignals(plan, quote);
        }
    }

    /// <summary>
    /// 清理富途订阅 - 对应 planScheduler.js _cleanupFutuSubscriptionAfterClose
    /// </summary>
    private void CleanupFutuSubscriptionAfterClose()
    {
        if (!_futuSubscribed) return;

        // 解绑推送/连接回调
        if (_futuAdapter != null)
        {
            _futuAdapter.OnQuotePush -= OnFutuPush;
            _futuAdapter.OnConnectionChanged -= OnFutuConnectionChanged;
        }

        _futuSubscribed = false;
        _futuHandlerBound = false;
        _pushDetectRunning.Clear();
        _pushDetectQueued.Clear();
        Log.Information("[计划调度] 富途订阅已清理");
    }

    // ============================================================================
    // 辅助方法
    // ============================================================================

    /// <summary>
    /// 计划是否可监控 - 未执行/未取消且在监控日期范围内
    /// </summary>

    // ============================================================================
    // 辅助方法
    // ============================================================================

    /// <summary>
    /// 计划是否可监控 - 未执行/未取消且在监控日期范围内
    /// </summary>
    private bool IsPlanMonitorable(TradePlan plan)
    {
        if (plan.ExecutionStatus == "executed") return false;
        if (plan.ExecutionStatus == "cancelled") return false;
        if (plan.Status == "cancelled") return false;
        return true;
    }

    /// <summary>
    /// 是否显示盘前提醒（当日只提醒一次）
    /// </summary>
    private bool ShouldShowPreMarketReminder()
    {
        var today = _marketTime.FormatDate(Now);
        if (_lastPreMarketReminderDate == today) return false;
        try
        {
            using var conn = _db.CreateConnection();
            var saved = conn.ExecuteScalar<string>(
                "SELECT value FROM appConfig WHERE key = 'pet_last_pre_market_reminder_date'");
            if (saved == today)
            {
                _lastPreMarketReminderDate = today;
                return false;
            }
        }
        catch { /* 读取失败不阻断本次执行 */ }
        return true;
    }

    /// <summary>
    /// 是否显示非交易日提醒
    /// </summary>
    private bool ShouldShowNonTradingDayReminder()
    {
        var today = _marketTime.FormatDate(Now);
        if (_lastNonTradingDayReminderDate == today) return false;
        try
        {
            using var conn = _db.CreateConnection();
            var saved = conn.ExecuteScalar<string>(
                "SELECT value FROM appConfig WHERE key = 'pet_last_non_trading_day_reminder_date'");
            if (saved == today)
            {
                _lastNonTradingDayReminderDate = today;
                return false;
            }
        }
        catch { /* 读取失败不阻断本次执行 */ }
        return true;
    }

    /// <summary>
    /// 计划类型文本
    /// </summary>
    private static string PlanTypeText(string planType)
    {
        return planType switch
        {
            "buy" => "买入",
            "sell" => "卖出",
            "watch" => "数据收集",
            _ => planType
        };
    }

    /// <summary>
    /// 数值是否有效
    /// </summary>
    private static bool IsFinite(decimal value) => !double.IsInfinity((double)value) && !double.IsNaN((double)value);

    /// <summary>
    /// 从信号信息中提取当前价（辅助方法）
    /// </summary>
    private static decimal data_currentPrice(SellSignalInfo signal) => signal.CurrentPrice;

    // ============================================================================
    // 盘后状态持久化
    // ============================================================================
    // 盘后状态持久化
    // ============================================================================

    private void SaveAfterMarketNotified(AfterMarketNotifiedState state)
    {
        _afterMarketNotified = state;
        try
        {
            using var conn = _db.CreateConnection();
            SaveConfig(conn, "pet_after_market_notified", JsonConvert.SerializeObject(state));
        }
        catch { /* ignore */ }
    }


    private AfterMarketNotifiedState LoadAfterMarketNotified()
    {
        try
        {
            using var conn = _db.CreateConnection();
            var raw = conn.QueryFirstOrDefault<string>(
                "SELECT value FROM appConfig WHERE key = 'pet_after_market_notified'");
            if (!string.IsNullOrEmpty(raw))
            {
                return JsonConvert.DeserializeObject<AfterMarketNotifiedState>(raw) ?? new AfterMarketNotifiedState();
            }
        }
        catch { /* ignore */ }
        return _afterMarketNotified;
    }


    private void ClearAfterMarketSnooze()
    {
        _afterMarketSnoozeUntil = 0;
        try
        {
            using var conn = _db.CreateConnection();
            conn.Execute("DELETE FROM appConfig WHERE key = 'pet_after_market_snooze_until'");
        }
        catch { /* ignore */ }
    }


    private void SaveAfterMarketLastReminder(long timestamp)
    {
        _afterMarketLastReminder = timestamp;
        try
        {
            using var conn = _db.CreateConnection();
            SaveConfig(conn, "pet_after_market_last_reminder", timestamp.ToString());
        }
        catch { /* ignore */ }
    }
}
