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
    // 自定义提醒检查 - 对应 planScheduler.js checkCustomReminders
    // ============================================================================

    /// <summary>
    /// 自定义提醒检查 - 跨窗口触发锁 + 二次校验
    /// </summary>
    private async Task CheckCustomRemindersAsync(DateTime now)
    {
        // 已停用：自定义提醒触发由 CustomReminderSchedulerService 专职负责（含当日去重/连弹/snooze/错过补发）。
        // 本旧路径按"分钟匹配 + _signalStates 去重"触发，不检查 Done 状态与 LastTriggeredAt，
        // 与专职调度器形成双路径重复触发：点完成后 ~30s 的下一次 30 秒轮询仍处同一分钟内会再次入队弹出。
        // 全局开关检查也随之移至 CustomReminderSchedulerService。
        await Task.CompletedTask;
    }

    // 停用路径的原始实现保留备查：

    // 停用路径的原始实现保留备查：
    private async Task CheckCustomRemindersAsync_Legacy(DateTime now)
    {
        if (!_settingsStore.Settings.CustomRemindersEnabled) return;

        // 限频：每 30 秒检查一次
        if ((now - _lastCustomReminderCheck).TotalSeconds < 30) return;
        _lastCustomReminderCheck = now;

        var reminders = _customRemindersStore.GetReminders();
        if (reminders.Count == 0) return;

        var nowTimeStr = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        var todayStr = _marketTime.FormatDate(now);

        foreach (var reminder in reminders.Where(r => r.Enabled))
        {
            if (reminder.Time != nowTimeStr) continue;

            // 当日去重
            var dedupKey = $"custom_reminder_{reminder.Id}_{todayStr}";
            if (_signalStates.ContainsKey(dedupKey)) continue;

            _signalStates[dedupKey] = new SignalStateEntry { State = "triggered", At = NowMs };

            _petStore.AddReminder(new ReminderRequest
            {
                Id = $"custom_{reminder.Id}_{todayStr}",
                Type = "custom_reminder",
                Level = ReminderLevel.Hint,
                Title = reminder.Title ?? $"{reminder.StockName} 自定义提醒",
                Content = reminder.Content ?? $"{reminder.StockName}（{reminder.StockCode}）自定义提醒时间到。",
                StockCode = reminder.StockCode,
                StockName = reminder.StockName,
                Importance = 3,
                // 原版：气泡按钮来自用户在弹窗勾选的 actions（默认 ✅完成/⏰稍后提醒）
                // 每个动作注入原始提醒 ID（对齐 Electron 触发时 rawActions.map → reminderId）
                Actions = (reminder.Actions != null && reminder.Actions.Count > 0
                        ? reminder.Actions
                        : CustomRemindersService.DefaultActions)
                    .Select(a => new ReminderAction
                    {
                        Type = a.Type,
                        Label = a.Label,
                        PlanIds = a.PlanIds,
                        ReminderId = reminder.Id
                    })
                    .ToList()
            });
        }

        await Task.CompletedTask;
    }

    // ============================================================================
    // 盘前 MA5 检查 - 对应 planScheduler.js checkPreCloseMA5
    // ============================================================================

    /// <summary>
    /// 尾盘 MA5 检查（14:30-15:00 每 5 分钟，对齐 Electron checkPreCloseMA5）
    /// 当前价低于 MA5（未站上五日均线）的监控股合并播报，提示可能触发卖出条件
    /// </summary>

    // ============================================================================
    // 盘前 MA5 检查 - 对应 planScheduler.js checkPreCloseMA5
    // ============================================================================

    /// <summary>
    /// 尾盘 MA5 检查（14:30-15:00 每 5 分钟，对齐 Electron checkPreCloseMA5）
    /// 当前价低于 MA5（未站上五日均线）的监控股合并播报，提示可能触发卖出条件
    /// </summary>
    private async Task CheckPreCloseMA5Async()
    {
        // 设置开关（宠物设置-尾盘 MA5 检查，默认开启）
        if (!_settingsStore.Settings.PreCloseMA5Check) return;

        var now = Now;
        var hours = _marketTime.GetHours(now);

        // 仅在 14:30-15:00 执行
        if (hours < 14.5m || hours >= 15) return;

        var todayStr = _marketTime.FormatDate(now);
        if (_preCloseMA5State.Date != todayStr)
        {
            _preCloseMA5State = new PreCloseMA5State { Date = todayStr };
        }

        // 距上次播报不足 5 分钟不查（留 5 秒余量，避免 tick 间隔导致跳过）
        var nowMs = NowMs;
        if (_preCloseMA5State.LastReminderAt > 0 &&
            nowMs - _preCloseMA5State.LastReminderAt < 5 * 60 * 1000 - 5000)
            return;

        // 监控范围：今日计划 + 持仓过夜计划 + 前一交易日擒牛（对齐原版）
        var codeNameMap = new Dictionary<string, string>();
        foreach (var plan in _tradePlanStore.TodayPlans
            .Concat(_tradePlanStore.MonitoringPlans).Where(IsPlanMonitorable))
        {
            codeNameMap[plan.StockCode] = plan.StockName;
        }
        foreach (var (pickCode, pickName) in LoadLatestTradingDayPicks())
        {
            codeNameMap.TryAdd(pickCode, pickName);
        }

        if (codeNameMap.Count == 0)
        {
            Log.Information("[MA5检查] 当日无监控计划（昨日/今日/擒牛均为空），跳过");
            return;
        }

        var stockCodes = codeNameMap.Keys.ToList();
        var dataMap = await FetchBatchDataWithCache(stockCodes);

        var alerts = new List<string>();
        var quoteOk = 0;
        var ma5Ok = 0;
        foreach (var code in stockCodes)
        {
            if (!dataMap.TryGetValue(code, out var quote) || quote == null || quote.CurrentPrice <= 0)
                continue;
            quoteOk++;

            var dailyKlines = await FetchDailyKlinesWithCache(code);
            if (dailyKlines.Count < 5) continue;

            var lastBarIsToday = dailyKlines.Count > 5 &&
                _marketTime.FormatDate(dailyKlines.Last().Date) == todayStr;
            var ma5 = (lastBarIsToday
                ? dailyKlines.SkipLast(1).TakeLast(5)
                : dailyKlines.TakeLast(5)).Average(k => k.Close);
            if (ma5 <= 0) continue;
            ma5Ok++;

            // 当前价低于 5 日均线 → 未站上，收集待播报
            if (quote.CurrentPrice < ma5)
            {
                var name = quote.Name;
                if (string.IsNullOrEmpty(name) && !codeNameMap.TryGetValue(code, out name)) name = code;
                var deviation = (ma5 - quote.CurrentPrice) / ma5 * 100;
                alerts.Add($"• {name}({code}): 当前价 {quote.CurrentPrice} < MA5均价 {ma5:F2}，偏离 {deviation:F2}%");
            }
        }

        // 汇总日志：覆盖静默路径（行情部分失败 / MA5 部分失败 / 全部站上），零触发时可据此定位
        if (alerts.Count == 0)
        {
            Log.Information("[MA5检查] 监控{Total} 行情{QuoteOk}/{Total} MA5有效{Ma5Ok}/{Total} 低于MA5 0只（全部站上或数据不足）",
                stockCodes.Count, quoteOk, ma5Ok);
            return;
        }

        // 合并为一条提醒，展示 20 秒
        var content = $"注意注意，快收盘了以下股票还没站上五日均线，可能触发卖出条件哦：\n\n{string.Join("\n", alerts)}\n\n💡 5日均线(MA5)是短期趋势参考，收盘价未站上MA5意味着短期偏弱。";
        _petStore.AddReminder(new ReminderRequest
        {
            Type = "signal",
            Level = ReminderLevel.Alert,
            Title = $"⚠️ {alerts.Count}只股票未站上5日均线",
            Content = content,
            Importance = 5,
            DurationMs = 20000
        });

        _preCloseMA5State.LastReminderAt = NowMs;
        Log.Information("[MA5检查] {Count}只股票低于MA5，已合并播报（5分钟后复查）", alerts.Count);
    }

    /// <summary>前一交易日的每日擒牛（对齐原版 loadLatestTradingDayPicks，读本地 dailyPicks 表）</summary>

    // ============================================================================
    // 空闲心得提醒 - 对应 planScheduler.js showIdleInsight
    // ============================================================================

    /// <summary>
    /// 空闲时显示随机心得提醒
    /// </summary>
    private async Task ShowIdleInsightAsync()
    {
        var now = Now;

        // 每 30 分钟最多一次
        if ((now - _lastIdleInsightTime).TotalMinutes < 30) return;
        _lastIdleInsightTime = now;

        var insights = new[]
        {
            "交易不在多，在于精。宁可不交易，也不要随意交易。",
            "止损是交易的第一课，学会止损才能在市场中生存。",
            "不要试图抓住每一个机会，只做属于自己的交易。",
            "趋势是你的朋友，不要逆势而为。",
            "仓位管理比选股更重要，控制风险永远是第一位。",
            "盘后复盘是进步最快的方式，坚持每日总结。",
            "不要因为一次亏损就否定自己的策略，也不要因为一次盈利就盲目自信。",
            "市场永远是对的，不要和市场争辩。",
            "耐心等待机会，机会是等出来的，不是追出来的。",
            "交易是一场马拉松，不是百米冲刺，保持节奏很重要。"
        };

        var idx = new Random().Next(insights.Length);
        _petStore.AddReminder(new ReminderRequest
        {
            Type = "idle_insight",
            Level = ReminderLevel.Info,
            Title = "交易心得",
            Content = insights[idx],
            Importance = 1,
            DurationMs = 8000
        });

        await Task.CompletedTask;
    }

    // ============================================================================
    // 市场摘要播报 - 对应 planScheduler.js showMarketDigest
    // ============================================================================

    /// <summary>
    /// 市场摘要播报（周末/节假日每日一次）
    /// </summary>

    // ============================================================================
    // 市场摘要播报 - 对应 planScheduler.js showMarketDigest
    // ============================================================================

    /// <summary>
    /// 市场摘要播报（周末/节假日每日一次）
    /// </summary>
    private async Task ShowMarketDigestAsync()
    {
        var todayStr = _marketTime.FormatDate(Now);
        var digestKey = $"pet_market_digest_{todayStr}";
        if (_signalStates.ContainsKey(digestKey)) return;

        var prevTradingDay = _marketTime.FormatDate(_marketTime.GetPreviousTradingDay(Now));
        var prevPlans = _tradePlanStore.Plans.Where(p => p.PlanDate == prevTradingDay).ToList();
        var executed = prevPlans.Count(p => p.ExecutionStatus == "executed");

        var dailyPicks = await LoadLatestTradingDayPicksAsync();

        var sections = new List<string>
        {
            "市场休市摘要",
            $"上一交易日：{prevTradingDay}"
        };

        if (prevPlans.Count > 0)
        {
            sections.Add($"计划执行：{executed}/{prevPlans.Count} 已完成");
        }

        if (dailyPicks.Count > 0)
        {
            sections.Add($"擒牛 {dailyPicks.Count} 只：");
            var pickNames = string.Join("\n", dailyPicks.Take(5).Select(p => $"  {p.StockName}({p.StockCode})"));
            sections.Add(pickNames);
        }

        var nextTradingDay = _marketTime.FormatDate(_marketTime.GetNextTradingDay(Now));
        var nextPlans = _tradePlanStore.Plans.Where(p =>
            p.PlanDate == nextTradingDay &&
            p.ExecutionStatus != "executed" &&
            p.ExecutionStatus != "cancelled").ToList();

        if (nextPlans.Count > 0)
        {
            sections.Add($"下一交易日（{nextTradingDay}）");
            sections.Add($"待执行计划 {nextPlans.Count} 条");
        }
        else
        {
            sections.Add($"下一交易日（{nextTradingDay}）暂无计划");
        }

        sections.Add("休息日适合复盘和总结，有空可以回顾一下心得。");

        _petStore.AddReminder(new ReminderRequest
        {
            Id = $"market_digest_{todayStr}",
            Type = "market_digest",
            Level = ReminderLevel.Hint,
            Title = "休市摘要",
            Content = string.Join("\n", sections),
            Importance = 2
        });

        _signalStates[digestKey] = new SignalStateEntry { State = "triggered", At = NowMs };
    }

    /// <summary>
    /// 加载最近交易日的擒牛股
    /// </summary>

    // ============================================================================
    // 周末总结 - 对应 planScheduler.js showWeekendSummary
    // ============================================================================

    /// <summary>
    /// 显示周末总结
    /// </summary>
    private void ShowWeekendSummary()
    {
        var weekStart = GetWeekStart();
        var weekEnd = GetWeekEnd();
        var weekPlans = _tradePlanStore.Plans
            .Where(p => string.Compare(p.PlanDate, weekStart, StringComparison.Ordinal) >= 0 &&
                        string.Compare(p.PlanDate, weekEnd, StringComparison.Ordinal) <= 0)
            .ToList();

        var executed = weekPlans.Count(p => p.ExecutionStatus == "executed");
        var partial = weekPlans.Count(p => p.ExecutionStatus == "partial");
        var notExecuted = weekPlans.Count(p => p.ExecutionStatus == "not_executed");
        var cancelled = weekPlans.Count(p => p.ExecutionStatus == "cancelled");

        var content = $"本周交易总结\n\n本周共制定 {weekPlans.Count} 条计划\n";
        content += $"已执行：{executed} 条\n";
        if (partial > 0) content += $"部分执行：{partial} 条\n";
        content += $"未执行：{notExecuted} 条\n";
        if (cancelled > 0) content += $"已取消：{cancelled} 条\n";
        content += executed > 0 ? "\n继续保持，下周加油！" : "\n下周可以多制定一些计划哦！";

        var todayStr = _marketTime.FormatDate(Now);
        _petStore.AddReminder(new ReminderRequest
        {
            Id = $"weekend_summary_{todayStr}",
            Type = "weekend_summary",
            Level = ReminderLevel.Hint,
            Title = "本周交易总结",
            Content = content,
            Importance = 3
        });
    }


    private string GetWeekStart()
    {
        var now = Now;
        var day = (int)now.DayOfWeek;
        if (day == 0) day = 7; // 周日 = 7
        var monday = now.AddDays(-(day - 1));
        return _marketTime.FormatDate(monday);
    }


    private string GetWeekEnd()
    {
        var now = Now;
        var day = (int)now.DayOfWeek;
        if (day == 0) day = 7;
        var sunday = now.AddDays(7 - day);
        return _marketTime.FormatDate(sunday);
    }

    // ============================================================================
    // 冷启动回放补全 - 对应 planScheduler.js backfillTodayEvents
    // ============================================================================

    /// <summary>
    /// 冷启动回放补全 - 补全当天遗漏的事件
    /// </summary>

    // ============================================================================
    // 冷启动回放补全 - 对应 planScheduler.js backfillTodayEvents
    // ============================================================================

    /// <summary>
    /// 冷启动回放补全 - 补全当天遗漏的事件
    /// </summary>
    private async Task BackfillTodayEventsAsync()
    {
        var todayStr = _marketTime.FormatDate(Now);
        if (_lastBackfillDate == todayStr) return;
        _lastBackfillDate = todayStr;

        try
        {
            // 从数据库加载今日已有的快照到内存缓存
            using var conn = _db.CreateConnection();
            const string sql = @"
                SELECT stockCode AS StockCode, price AS Price, volume AS Volume,
                       amount AS Amount, timestamp AS TimestampStr, vwap AS Vwap, volumeReliable AS VolumeReliable,
                       cumulativeVolume AS CumulativeVolumeRaw
                FROM price_snapshots
                WHERE date(timestamp) = @Today
                ORDER BY timestamp";

            var rows = conn.Query<dynamic>(sql, new { Today = todayStr }).ToList();

            // 旧版快照 volume 列存的是当日累计量；按相邻行差分换算成区间量（对齐新版语义）
            var lastCumulativeByCode = new Dictionary<string, long>();

            foreach (var row in rows)
            {
                var code = (string)row.StockCode;
                var rawVolume = (long)row.Volume;
                var cumulative = row.CumulativeVolumeRaw != null && row.CumulativeVolumeRaw != DBNull.Value
                    ? (long)row.CumulativeVolumeRaw
                    : rawVolume; // 旧行降级：volume 即累计量
                var interval = lastCumulativeByCode.TryGetValue(code, out var prevCum) && cumulative >= prevCum
                    ? cumulative - prevCum
                    : 0;
                lastCumulativeByCode[code] = cumulative;

                var snapshot = new PriceSnapshot
                {
                    StockCode = code,
                    Price = (decimal)row.Price,
                    Volume = interval,
                    CumulativeVolume = cumulative,
                    Amount = (decimal)row.Amount,
                    Timestamp = DateTime.Parse((string)row.TimestampStr, CultureInfo.InvariantCulture),
                    Vwap = (decimal)row.Vwap,
                    VolumeReliable = (bool)row.VolumeReliable
                };

                var cache = _snapshotCache.GetOrAdd(code, _ => new List<PriceSnapshot>());
                lock (cache)
                {
                    cache.Add(snapshot);
                }
            }

            if (rows.Count > 0)
            {
                Log.Information("[计划调度] 冷启动回放补全：加载 {Count} 条今日快照", rows.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[计划调度] 冷启动回放补全失败");
        }

        await Task.CompletedTask;
    }

    // ============================================================================
    // 今日信号评估 - 对应 planScheduler.js evaluateTodaySignals
    // ============================================================================

    /// <summary>
    /// 今日信号评估 - 盘后调用，评估今日所有信号的质量
    /// </summary>

    // ============================================================================
    // 今日信号评估 - 对应 planScheduler.js evaluateTodaySignals
    // ============================================================================

    /// <summary>
    /// 今日信号评估 - 盘后调用，评估今日所有信号的质量
    /// </summary>
    private async Task EvaluateTodaySignalsAsync()
    {
        var todayStr = _marketTime.FormatDate(Now);
        if (_lastEvaluateDate == todayStr) return;
        _lastEvaluateDate = todayStr;

        try
        {
            // 收集所有有快照的股票
            // 注意：枚举 _snapshotCache 的 List 必须持有该 List 的锁——
            // 交易时段 10 秒 tick 会在锁内 cache.Add，无锁 Where 枚举会抛"集合已修改"
            var allSnapshots = new Dictionary<string, List<PriceSnapshot>>();
            foreach (var (code, snaps) in _snapshotCache)
            {
                List<PriceSnapshot> todaySnaps;
                lock (snaps)
                {
                    todaySnaps = snaps.Where(s => _marketTime.FormatDate(s.Timestamp) == todayStr).ToList();
                }
                if (todaySnaps.Count > 0)
                {
                    allSnapshots[code] = todaySnaps;
                }
            }

            // 委托给信号事件存储评估
            _signalEventStore.EvaluateTodaySignals(allSnapshots);

            Log.Information("[计划调度] 今日信号评估完成：{Count} 只股票", allSnapshots.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[计划调度] 今日信号评估失败");
        }

        await Task.CompletedTask;
    }

    // ============================================================================
    // 信号自进化 - 对应 planScheduler.js autoOptimizeParams / runEvolutionSearch 等
    // ============================================================================

    /// <summary>
    /// 自动优化参数 - 对应 planScheduler.js autoOptimizeParams
    /// 盘后自动执行因子权重 + 信号乘子优化
    /// </summary>
}
