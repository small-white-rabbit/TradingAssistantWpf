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

    /// <summary>
    /// 处理收盘提醒操作 handleAfterMarketAction
    /// </summary>
    public void HandleAfterMarketAction(string actionType, List<string> planIds)
    {
        var todayStr = _marketTime.FormatDate(Now);

        switch (actionType)
        {
            case "after_market_continue":
            {
                // 继续执行：planDate 改为下一交易日
                var nextDateStr = _marketTime.FormatDate(_marketTime.GetNextTradingDay(Now));
                foreach (var id in planIds)
                {
                    _tradePlanStore.UpdatePlan(id, new
                    {
                        planDate = nextDateStr,
                        status = "pending",
                        executionStatus = "not_executed"
                    });
                }
                SaveAfterMarketNotified(new AfterMarketNotifiedState { Date = todayStr, Done = true });
                ClearAfterMarketSnooze();
                SaveAfterMarketLastReminder(NowMs);
                _petStore.HideBubble();
                _petStore.SetMood(MoodType.Happy);
                _petStore.ScheduleMoodRestore(3000);
                break;
            }

            case "after_market_complete":
            {
                foreach (var id in planIds)
                {
                    _tradePlanStore.RecordExecution(id, new
                    {
                        executionStatus = "executed",
                        note = "收盘自动完成"
                    });
                }
                SaveAfterMarketNotified(new AfterMarketNotifiedState { Date = todayStr, Done = true });
                ClearAfterMarketSnooze();
                SaveAfterMarketLastReminder(NowMs);
                _petStore.HideBubble();
                _petStore.SetMood(MoodType.Happy);
                _petStore.ScheduleMoodRestore(3000);
                break;
            }

            case "after_market_dismiss":
            {
                // 稍后提醒：清除 done + 设置 snooze
                SaveAfterMarketNotified(new AfterMarketNotifiedState { Date = todayStr, Done = false });
                _afterMarketSnoozeUntil = NowMs + Config.AfterMarketSnoozeMinutes * 60 * 1000L;
                SaveAfterMarketLastReminder(NowMs);
                _petStore.HideBubble();
                break;
            }

            case "after_market_record":
            {
                // 打开交易记录面板：短 snooze
                SaveAfterMarketNotified(new AfterMarketNotifiedState { Date = todayStr, Done = false });
                _afterMarketSnoozeUntil = NowMs + 2 * 60 * 1000L;
                SaveAfterMarketLastReminder(NowMs);
                _petStore.HideBubble();
                break;
            }
        }
    }

    // ============================================================================
    // 计划信号检查 checkPlanSignals / checkTodayPlan
    // ============================================================================

    /// <summary>
    /// 检查计划信号 checkPlanSignals
    /// 全量信号检查：快速涨跌 → 封板 → 进场价跌 → 目标价 → 止损价 → 卖点 → 买点
    /// </summary>

    // ============================================================================
    // 计划信号检查 checkPlanSignals / checkTodayPlan
    // ============================================================================

    /// <summary>
    /// 检查计划信号 checkPlanSignals
    /// 全量信号检查：快速涨跌 → 封板 → 进场价跌 → 目标价 → 止损价 → 卖点 → 买点
    /// </summary>
    public async Task CheckPlanSignals(TradePlan plan, StockQuote data)
    {
        // 行情请求期间计划可能已被执行
        if (!IsPlanMonitorable(plan)) return;
        if (data == null || !IsFinite(data.CurrentPrice) || data.CurrentPrice <= 0) return;

        // 午休时段不触发
        var (phase, _) = _marketTime.GetIntradayPhase(Now);
        if (phase == IntradayPhase.Lunch) return;

        var currentPrice = data.CurrentPrice;

        // 读取设置
        var sellPointEnabled = _settingsStore.Settings.SellPointDetection;

        // 同步卖点阈值
        if (sellPointEnabled)
        {
            var s = _settingsStore.Settings;
            _sellPointDetector.UpdateConfig(new
            {
                surgePullbackThreshold = s.SurgePullbackThreshold,
                volumeAmplifyMultiple = s.VolumeAmplifyMultiple,
                stagnantThreshold = s.StagnantThreshold,
                supportBreakdownTolerance = s.SupportBreakdownTolerance,
                priceNearThreshold = s.PriceNearThreshold
            });
        }

        // 优化参数同步
        SyncOptimizedParams();

        // 1. 快速涨跌检测（秒级轨迹时间窗口，推送即时触发；含恶化升级穿透冷却）
        var snaps = GetSnapshots(plan.StockCode);
        var rapidMatch = DetectRapidByTimeTrail(plan.StockCode);
        if (rapidMatch != null)
        {
            if (CanEmitRapidSignal(plan.Id, rapidMatch.Direction, rapidMatch))
            {
                if (CheckRateLimit(plan.StockCode, "price_alert", 2, 60 * 1000))
                {
                    CommitRapidSignalState(plan.Id, rapidMatch.Direction, rapidMatch);
                    var moveWord = rapidMatch.Direction == "up"
                        ? $"{rapidMatch.WindowLabel}拉升"
                        : (string.IsNullOrEmpty(rapidMatch.DownLabel) ? $"{rapidMatch.WindowLabel}下跌" : rapidMatch.DownLabel);
                    var changeTxt = (rapidMatch.ChangePct >= 0 ? "+" : "") +
                                    rapidMatch.ChangePct.ToString("F2", CultureInfo.InvariantCulture) + "%";
                    var minutes = rapidMatch.WindowMinutes.ToString("F1", CultureInfo.InvariantCulture);

                    // 数据收集计划：仅留痕不弹气泡
                    if (plan.PlanType != "watch")
                    {
                        var reminder = new ReminderRequest
                        {
                            Type = "price_alert",
                            Level = ReminderLevel.Alert,
                            Title = $"{plan.StockName} {moveWord}",
                            Content = $"{plan.StockName}（{plan.StockCode}）{minutes}分钟内{moveWord} {changeTxt}，现 {currentPrice} 元，建议查看分时决定是否{(rapidMatch.Direction == "up" ? "止盈/减仓" : "补仓/止损")}。",
                            StockCode = plan.StockCode,
                            StockName = plan.StockName,
                            Importance = 5,
                            DurationMs = 12000
                        };
                        _petStore.AddReminder(reminder);
                        _petStore.ScheduleUpgrade(reminder, 20000, "warning");
                    }

                    // 记录信号事件
                    _signalEventStore.RecordEvent(new SignalEventRecord
                    {
                        StockCode = plan.StockCode,
                        StockName = plan.StockName,
                        SignalType = $"rapid_{rapidMatch.Direction}_{rapidMatch.WindowLabel}",
                        SignalLabel = moveWord,
                        Price = currentPrice,
                        Timestamp = NowMs,
                        SnapshotIndex = snaps.Count - 1,
                        Metadata = new Dictionary<string, object>
                        {
                            ["changePct"] = rapidMatch.ChangePct,
                            ["windowBars"] = rapidMatch.WindowBars,
                            ["windowLabel"] = rapidMatch.WindowLabel,
                            ["alerted"] = plan.PlanType != "watch",
                            ["collectOnly"] = plan.PlanType == "watch"
                        }
                    });
                }
            }
        }

        // 2. 涨跌停封板检测
        var limitMove = DetectLimitMove(plan.StockCode, plan.StockName, currentPrice, data);
        if (limitMove is { Sealed: true })
        {
            var key = $"{plan.Id}:limit_sealed";
            // TryAdd 原子占位：推送线程与定时器线程可能并发到达，ContainsKey+写两步
            // 之间存在窗口，双方同时通过检查 → 同一事件弹两次提醒
            if (_signalStore.SignalStates.TryAdd(key, new SignalStateEntry
            {
                State = limitMove.Direction,
                At = NowMs
            }))
            {

                // 数据收集模式：仅标记状态不弹气泡
                if (plan.PlanType == "watch") return;

                var directionText = limitMove.Direction == "up" ? "涨停" : "跌停";
                var advice = limitMove.Direction == "up"
                    ? (plan.PlanType == "sell" ? "挂单排队中，可能无法成交" : "已封板，追涨风险大")
                    : (plan.PlanType == "buy" ? "抄底需谨慎" : "卖出可能无法成交，注意风险");

                if (!CheckRateLimit(plan.StockCode, "limit_move")) return;

                _petStore.AddReminder(new ReminderRequest
                {
                    Type = "limit_move",
                    Level = limitMove.Direction == "down" ? ReminderLevel.Critical : ReminderLevel.Alert,
                    Title = $"{plan.StockName} {directionText}封板",
                    Content = $"{plan.StockName}（{plan.StockCode}）当前价 {currentPrice}，{directionText}封板。\n{advice}。",
                    StockCode = plan.StockCode,
                    StockName = plan.StockName,
                    Importance = 6,
                    DurationMs = 15000
                });
            }
            return; // 封板时不再检测其他信号
        }

        // 3. 进场价跌 5% 强制止损
        CheckEntryDropForceStop(plan, currentPrice);

        // 4. 目标价检测
        if ((plan.TargetPrice ?? 0) > 0)
        {
            await CheckTargetPriceAsync(plan, currentPrice, data);
        }

        // 5. 止损价检测
        if ((plan.StopLoss ?? 0) > 0)
        {
            await CheckStopLossAsync(plan, currentPrice, data);
        }

        // 6. 分时卖点识别
        await DetectAndRouteSellSignals(plan, data, sellPointEnabled);

        // 7. 分时买点识别
        await DetectAndRouteBuySignals(plan, data);
    }

    /// <summary>
    /// 检查今日计划 checkTodayPlan
    /// 与 checkPlanSignals 共用 N1 去重，负责盘中监控逻辑
    /// </summary>
    public async Task CheckTodayPlan(TradePlan plan, StockQuote data)
    {
        if (!IsPlanMonitorable(plan)) return;
        if (data == null || !IsFinite(data.CurrentPrice) || data.CurrentPrice <= 0) return;

        var (phase, _) = _marketTime.GetIntradayPhase(Now);

        // 午休时段跳过
        if (phase == IntradayPhase.Lunch) return;

        var currentPrice = data.CurrentPrice;

        // 读取设置
        var sellPointEnabled = _settingsStore.Settings.SellPointDetection;

        // 同步卖点阈值
        if (sellPointEnabled)
        {
            var s = _settingsStore.Settings;
            _sellPointDetector.UpdateConfig(new
            {
                surgePullbackThreshold = s.SurgePullbackThreshold,
                volumeAmplifyMultiple = s.VolumeAmplifyMultiple,
                stagnantThreshold = s.StagnantThreshold,
                supportBreakdownTolerance = s.SupportBreakdownTolerance,
                priceNearThreshold = s.PriceNearThreshold
            });
        }

        SyncOptimizedParams();

        // 1. 快速涨跌检测（秒级轨迹时间窗口，推送即时触发；含恶化升级穿透冷却）
        var rapidMatch = DetectRapidByTimeTrail(plan.StockCode);
        if (rapidMatch != null)
        {
            if (CanEmitRapidSignal(plan.Id, rapidMatch.Direction, rapidMatch))
            {
                if (CheckRateLimit(plan.StockCode, "price_alert", 2, 60 * 1000))
                {
                    CommitRapidSignalState(plan.Id, rapidMatch.Direction, rapidMatch);
                    var moveWord = rapidMatch.Direction == "up"
                        ? $"{rapidMatch.WindowLabel}拉升"
                        : (string.IsNullOrEmpty(rapidMatch.DownLabel) ? $"{rapidMatch.WindowLabel}下跌" : rapidMatch.DownLabel);
                    var changeTxt = (rapidMatch.ChangePct >= 0 ? "+" : "") +
                                    rapidMatch.ChangePct.ToString("F2", CultureInfo.InvariantCulture) + "%";
                    var minutes = rapidMatch.WindowMinutes.ToString("F1", CultureInfo.InvariantCulture);

                    if (plan.PlanType != "watch")
                    {
                        var reminder = new ReminderRequest
                        {
                            Type = "price_alert",
                            Level = ReminderLevel.Alert,
                            Title = $"{plan.StockName} {moveWord}",
                            Content = $"{plan.StockName}（{plan.StockCode}）{minutes}分钟内{moveWord} {changeTxt}，现 {currentPrice} 元。",
                            StockCode = plan.StockCode,
                            StockName = plan.StockName,
                            Importance = 5,
                            DurationMs = 12000
                        };
                        _petStore.AddReminder(reminder);
                        _petStore.ScheduleUpgrade(reminder, 20000, "warning");
                    }
                }
            }
        }

        // 2. 涨跌停封板检测
        var limitMove = DetectLimitMove(plan.StockCode, plan.StockName, currentPrice, data);
        if (limitMove is { Sealed: true })
        {
            var key = $"{plan.Id}:limit_sealed";
            // TryAdd 原子占位（同 CheckPlanSignals：防双线程重复弹提醒）
            if (_signalStore.SignalStates.TryAdd(key, new SignalStateEntry { State = limitMove.Direction, At = NowMs }))
            {
                if (plan.PlanType == "watch") return;

                var directionText = limitMove.Direction == "up" ? "涨停" : "跌停";
                var advice = limitMove.Direction == "up"
                    ? (plan.PlanType == "sell" ? "挂单排队中，可能无法成交" : "已封板，追涨风险大")
                    : (plan.PlanType == "buy" ? "抄底需谨慎" : "卖出可能无法成交，注意风险");

                if (!CheckRateLimit(plan.StockCode, "limit_move")) return;

                _petStore.AddReminder(new ReminderRequest
                {
                    Type = "limit_move",
                    Level = limitMove.Direction == "down" ? ReminderLevel.Critical : ReminderLevel.Alert,
                    Title = $"{plan.StockName} {directionText}封板",
                    Content = $"{plan.StockName}（{plan.StockCode}）当前价 {currentPrice}，{directionText}封板。\n{advice}。",
                    StockCode = plan.StockCode,
                    StockName = plan.StockName,
                    Importance = 6,
                    DurationMs = 15000
                });
            }
            return;
        }

        // 3. 进场价跌 5% 强制止损
        CheckEntryDropForceStop(plan, currentPrice);

        // 4. 目标价检测
        if ((plan.TargetPrice ?? 0) > 0)
        {
            await CheckTargetPriceAsync(plan, currentPrice, data);
        }

        // 5. 止损价检测
        if ((plan.StopLoss ?? 0) > 0)
        {
            await CheckStopLossAsync(plan, currentPrice, data);
        }

        // 6. 分时卖点识别
        await DetectAndRouteSellSignals(plan, data, sellPointEnabled);

        // 7. 分时买点识别
        await DetectAndRouteBuySignals(plan, data);
    }

    // ============================================================================
    // 目标价检测 collectTargetSignal / checkTargetPrice
    // ============================================================================

    /// <summary>
    /// 目标价检测 - 三状态机：approaching → reached → breakthrough / pullback
    /// </summary>

    // ============================================================================
    // 目标价检测 collectTargetSignal / checkTargetPrice
    // ============================================================================

    /// <summary>
    /// 目标价检测 - 三状态机：approaching → reached → breakthrough / pullback
    /// </summary>
    private async Task CheckTargetPriceAsync(TradePlan plan, decimal currentPrice, StockQuote data)
    {
        var target = plan.TargetPrice ?? 0;
        if (target <= 0) return;

        var key = $"{plan.Id}:target";
        var diff = (currentPrice - target) / target * 100;
        var prevState = _signalStore.SignalStates.TryGetValue(key, out var entry) ? entry.State : "";
        var wasAboveTarget = prevState == "reached" || prevState == "breakthrough";

        // 对齐原版 collectTargetSignal 状态判定（基于"当前价 vs 目标价"，防震荡重复触发）：
        // - reached：现价 ≥ 目标价 且 |diff| ≤ 阈值（刚到目标价）
        // - breakthrough：现价 ≥ 目标价 且超出阈值（大幅突破 / 从 reached 升级）
        // - pullback：之前在目标价上方，现回落到下方（最佳卖点窗口）
        // - approaching：现价 < 目标价 且距目标 ≤ 阈值（下方容差内接近）
        //   （旧实现 reached 在目标价下方阈值内即触发、approaching 无下界判定，语义偏差）
        string newState;
        string? reason = null;

        if (currentPrice >= target)
        {
            if (!wasAboveTarget)
            {
                newState = Math.Abs(diff) <= Config.PriceNearThreshold ? "reached" : "breakthrough";
                reason = newState;
            }
            else if (prevState == "reached" && Math.Abs(diff) > Config.PriceNearThreshold)
            {
                // 已到过目标价后继续大幅上行 → 升级为突破
                newState = "breakthrough";
                reason = "breakthrough";
            }
            else
            {
                // 停留在目标价上方小幅波动：不重复触发
                newState = "";
            }
        }
        else if (wasAboveTarget)
        {
            newState = "pullback";
            reason = "pullback";
        }
        else if (Math.Abs(diff) <= Config.PriceNearThreshold)
        {
            newState = "approaching";
            reason = "approaching";
        }
        else
        {
            newState = "normal";
        }

        if (string.IsNullOrEmpty(newState) || newState == "normal") return;

        // 同状态冷却（15分钟）+ 状态持久化（pullback/wasAboveTarget 判定依赖）
        if (!CanEmitSignal(key, newState, 15 * 60 * 1000)) return;

        // ---- 只读去重判定：不写任何状态，避免被下方门槛拦截后当日永久丢失 ----
        // 级别去重
        if (IsLevelHitNotifiedToday(plan.Id, newState)) return;

        // 动作型提醒当日一次去重
        var actionKey = $"{plan.Id}:target_{newState}";
        if (_signalStore.ActionEmittedToday.ContainsKey(actionKey)) return;

        // ---- 门槛检查：全部通过后才允许落状态 ----
        // 波内限发检查
        if (!WaveGateAllows(plan.StockCode, currentPrice, newState)) return;

        if (plan.PlanType == "watch")
        {
            // 数据收集模式：仅记录不弹气泡
            return;
        }

        if (!CheckRateLimit(plan.StockCode, "target_price")) return;

        // ---- 所有门槛通过 → 提交去重状态与信号状态 ----
        MarkLevelHitNotified(plan.Id, newState);
        _signalStore.ActionEmittedToday[actionKey] = true;
        CommitSignalState(key, newState);

        var (title, content, level) = newState switch
        {
            "breakthrough" => ($"{plan.StockName} 目标价突破",
                $"{plan.StockName}（{plan.StockCode}）已突破目标价 {target}，当前 {currentPrice} 元，涨幅 {diff:F2}%。", ReminderLevel.Alert),
            "reached" => ($"{plan.StockName} 目标价到位",
                $"{plan.StockName}（{plan.StockCode}）已到达目标价 {target}，当前 {currentPrice} 元。", ReminderLevel.Alert),
            "pullback" => ($"{plan.StockName} 目标价回落",
                $"{plan.StockName}（{plan.StockCode}）目标价 {target} 到过后回落，当前 {currentPrice} 元。", ReminderLevel.Hint),
            "approaching" => ($"{plan.StockName} 接近目标价",
                $"{plan.StockName}（{plan.StockCode}）接近目标价 {target}，当前 {currentPrice} 元，差距 {diff:F2}%。", ReminderLevel.Hint),
            _ => ("", "", ReminderLevel.Info)
        };

        if (string.IsNullOrEmpty(title)) return;

        _petStore.AddReminder(new ReminderRequest
        {
            Type = "target_price",
            Level = level,
            Title = title,
            Content = content,
            StockCode = plan.StockCode,
            StockName = plan.StockName,
            Importance = level == ReminderLevel.Alert ? 5 : 3,
            DurationMs = 10000
        });

        // 波内限发通过
        WaveGatePass(plan.StockCode, currentPrice, newState);

        // 记录信号事件
        _signalEventStore.RecordEvent(new SignalEventRecord
        {
            StockCode = plan.StockCode,
            StockName = plan.StockName,
            SignalType = $"target_{newState}",
            SignalLabel = reason ?? newState,
            Price = currentPrice,
            Timestamp = NowMs,
            Metadata = new Dictionary<string, object>
            {
                ["targetPrice"] = target,
                ["diff"] = diff,
                ["state"] = newState,
                ["alerted"] = plan.PlanType != "watch"
            }
        });

        await Task.CompletedTask;
    }

    // ============================================================================
    // 止损价检测 collectStopLossSignal / checkStopLoss
    // ============================================================================

    /// <summary>
    /// 止损价检测 - 三状态机：approaching → touched / broken
    /// </summary>

    // ============================================================================
    // 止损价检测 collectStopLossSignal / checkStopLoss
    // ============================================================================

    /// <summary>
    /// 止损价检测 - 三状态机：approaching → touched / broken
    /// </summary>
    private async Task CheckStopLossAsync(TradePlan plan, decimal currentPrice, StockQuote data)
    {
        var stopLoss = plan.StopLoss ?? 0;
        if (stopLoss <= 0) return;

        var key = $"{plan.Id}:stop";
        var diff = (currentPrice - stopLoss) / stopLoss * 100;

        // 对齐原版 collectStopLossSignal 状态判定：
        // - broken：现价低于止损价超过 0.1%（已跌破）
        // - touched：|diff| ≤ 0.1%（真正触及止损价，固定小容差）
        // - approaching：现价高于止损价且距止损 ≤ 用户设置阈值（PriceNearThreshold）
        //   （旧实现 approaching=阈值×2 导致超出设定仍触发"接近"、touched=±阈值
        //     导致未真正触及就报"触及止损价"，均与设置语义不符）
        const decimal HitTolerancePct = 0.1m;
        string newState;
        string? reason;

        if (diff < -HitTolerancePct)
        {
            newState = "broken";
            reason = "broken";
        }
        else if (Math.Abs(diff) <= HitTolerancePct)
        {
            newState = "touched";
            reason = "touched";
        }
        else if (diff <= Config.PriceNearThreshold)
        {
            newState = "approaching";
            reason = "approaching";
        }
        else
        {
            newState = "normal";
            reason = null;
        }

        if (newState == "normal") return;
        if (!CanEmitSignal(key, newState, 10 * 60 * 1000)) return;

        // ---- 只读去重判定：不写任何状态，避免被下方门槛拦截后当日永久丢失 ----
        if (IsLevelHitNotifiedToday(plan.Id, newState)) return;

        var actionKey = $"{plan.Id}:stop_{newState}";
        if (_signalStore.ActionEmittedToday.ContainsKey(actionKey)) return;

        // ---- 门槛检查：全部通过后才允许落状态 ----
        if (!WaveGateAllows(plan.StockCode, currentPrice, newState)) return;

        if (plan.PlanType == "watch") return;

        // 止损使用 10 分钟窗口 3 次限频
        if (!CheckRateLimit(plan.StockCode, "stop_loss", 3, 10 * 60 * 1000)) return;

        // ---- 所有门槛通过 → 提交去重状态与信号状态 ----
        MarkLevelHitNotified(plan.Id, newState);
        _signalStore.ActionEmittedToday[actionKey] = true;
        CommitSignalState(key, newState);

        var (title, content, level) = newState switch
        {
            "broken" => ($"{plan.StockName} 止损价跌破",
                $"{plan.StockName}（{plan.StockCode}）已跌破止损价 {stopLoss}，当前 {currentPrice} 元，跌幅 {-diff:F2}%。请立即评估是否止损。", ReminderLevel.Critical),
            "touched" => ($"{plan.StockName} 止损价触及",
                $"{plan.StockName}（{plan.StockCode}）已触及止损价 {stopLoss}，当前 {currentPrice} 元。请注意风险。", ReminderLevel.Alert),
            "approaching" => ($"{plan.StockName} 接近止损价",
                $"{plan.StockName}（{plan.StockCode}）接近止损价 {stopLoss}，当前 {currentPrice} 元，差距 {diff:F2}%。", ReminderLevel.Hint),
            _ => ("", "", ReminderLevel.Info)
        };

        if (string.IsNullOrEmpty(title)) return;

        var reminder = new ReminderRequest
        {
            Type = "stop_loss",
            Level = level,
            Title = title,
            Content = content,
            StockCode = plan.StockCode,
            StockName = plan.StockName,
            Importance = level == ReminderLevel.Critical ? 7 : (level == ReminderLevel.Alert ? 6 : 3),
            DurationMs = level == ReminderLevel.Critical ? 20000 : 12000
        };
        _petStore.AddReminder(reminder);

        if (level == ReminderLevel.Critical)
        {
            _petStore.ScheduleUpgrade(reminder, 30000, "warning");
        }

        WaveGatePass(plan.StockCode, currentPrice, newState);

        _signalEventStore.RecordEvent(new SignalEventRecord
        {
            StockCode = plan.StockCode,
            StockName = plan.StockName,
            SignalType = $"stop_{newState}",
            SignalLabel = reason ?? newState,
            Price = currentPrice,
            Timestamp = NowMs,
            Metadata = new Dictionary<string, object>
            {
                ["stopLoss"] = stopLoss,
                ["diff"] = diff,
                ["state"] = newState,
                ["alerted"] = plan.PlanType != "watch"
            }
        });

        await Task.CompletedTask;
    }

    // ============================================================================
    // 快速涨跌检测 detectMultiWindowRapid
    // ============================================================================

    /// <summary>
    /// 多时间窗口快速拉升/下跌检测（对齐原版 detectMultiWindowRapid）
    /// 3个时间窗口（3min/10min/15min）匹配不同拉升模式，任一窗口满足阈值即触发
    /// 方向判定：优先首尾涨跌幅，不够时用窗口波动率兜底（解决慢牛拉升不触发）
    /// </summary>
    public RapidMatch? DetectMultiWindowRapid(List<PriceSnapshot> snapshots)
    {
        // 守卫下限取配置中最小窗口的 Bars，避免硬编码与窗口配置脱节
        var minBars = Config.RapidWindows.Count > 0 ? Config.RapidWindows.Min(w => w.Bars) : 9;
        if (snapshots == null || snapshots.Count < minBars) return null;

        RapidMatch? bestMatch = null;

        foreach (var window in Config.RapidWindows)
        {
            if (snapshots.Count < window.Bars) continue;

            var recent = snapshots.TakeLast(window.Bars).ToList();
            var prices = recent.Select(s => s.Price).Where(p => p > 0).ToList();
            if (prices.Count < 2) continue;

            var firstPrice = prices[0];
            var lastPrice = prices[^1];
            var wLow = prices.Min();
            var wHigh = prices.Max();
            var changePct = (lastPrice - firstPrice) / firstPrice * 100;
            var volatilityPct = (wHigh - wLow) / Math.Min(wLow, firstPrice) * 100;

            // 方向判定：优先用首尾涨跌幅，不够时用波动率兜底
            var dir = changePct >= window.Pct ? "up"
                    : changePct <= -window.Pct ? "down"
                    : "normal";
            if (dir == "normal" && volatilityPct >= window.Pct)
            {
                dir = lastPrice < firstPrice ? "down" : lastPrice > firstPrice ? "up" : "normal";
            }

            if (dir == "normal") continue;

            // 选择满足条件的最长窗口（更可靠，避免短窗口噪音）；
            // 但如果短窗口幅度远超阈值（>2倍），优先选择短窗口（更及时）
            var ratio = Math.Abs(changePct) / window.Pct;
            if (bestMatch == null || window.Bars > bestMatch.WindowBars || ratio > 2)
            {
                bestMatch = new RapidMatch
                {
                    Direction = dir,
                    ChangePct = changePct,
                    WindowBars = window.Bars,
                    WindowLabel = window.Label,
                    DownLabel = window.DownLabel,
                    CooldownMs = window.CooldownMs,
                    WindowMinutes = Math.Max(0.1, (recent[^1].Timestamp - recent[0].Timestamp).TotalMinutes)
                };
            }
        }

        return bestMatch;
    }

    // ============================================================================
    // 涨跌停封板检测 detectLimitMove
    // ============================================================================

    /// <summary>
    /// 涨跌停封板检测
    /// A 股规则：主板 ±10%，创业板/科创板 ±20%，ST ±5%
    /// 封板 = 当前价 == 涨停价/跌停价 且 卖一/买一 量极大
    /// </summary>

    // ============================================================================
    // 涨跌停封板检测 detectLimitMove
    // ============================================================================

    /// <summary>
    /// 涨跌停封板检测
    /// A 股规则：主板 ±10%，创业板/科创板 ±20%，ST ±5%
    /// 封板 = 当前价 == 涨停价/跌停价 且 卖一/买一 量极大
    /// </summary>
    public LimitMoveResult? DetectLimitMove(string stockCode, string stockName, decimal currentPrice, StockQuote data)
    {
        if (currentPrice <= 0 || data.PreClose <= 0) return null;

        // 判断涨跌幅限制
        var limitPct = GetLimitPct(stockCode);
        var limitUpPrice = JsMath.JsRound(data.PreClose * (1 + limitPct / 100), 2);
        var limitDownPrice = JsMath.JsRound(data.PreClose * (1 - limitPct / 100), 2);

        // 涨停封板：当前价 == 涨停价
        if (Math.Abs(currentPrice - limitUpPrice) < 0.01m)
        {
            return new LimitMoveResult
            {
                Sealed = true,
                Direction = "up",
                LimitPrice = limitUpPrice
            };
        }

        // 跌停封板：当前价 == 跌停价
        if (Math.Abs(currentPrice - limitDownPrice) < 0.01m)
        {
            return new LimitMoveResult
            {
                Sealed = true,
                Direction = "down",
                LimitPrice = limitDownPrice
            };
        }

        return new LimitMoveResult { Sealed = false };
    }

    /// <summary>
    /// 获取涨跌幅限制（%）
    /// </summary>
    private static decimal GetLimitPct(string stockCode)
    {
        // 创业板 30xxxx → 20%
        if (stockCode.StartsWith("30")) return 20m;
        // 科创板 68xxxx → 20%
        if (stockCode.StartsWith("68")) return 20m;
        // ST 股票 → 5%（简化判断：名称含 ST，实际应由调用方传入）
        // 北交所 8xxxxx/4xxxxx → 30%
        if (stockCode.StartsWith("8") || stockCode.StartsWith("4")) return 30m;
        // 主板默认 → 10%
        return 10m;
    }

    // ============================================================================
    // 进场价跌 5% 强制止损 _checkEntryDropForceStop
    // ============================================================================

    /// <summary>
    /// 进场价跌 5% 强制止损提示
    /// 即使未触及用户设置的止损价，只要相对进场价已跌 5% 就强制提醒
    /// </summary>

    // ============================================================================
    // 进场价跌 5% 强制止损 _checkEntryDropForceStop
    // ============================================================================

    /// <summary>
    /// 进场价跌 5% 强制止损提示
    /// 即使未触及用户设置的止损价，只要相对进场价已跌 5% 就强制提醒
    /// </summary>
    private void CheckEntryDropForceStop(TradePlan plan, decimal currentPrice)
    {
        // 数据收集计划无真实持仓意图，跳过
        if (plan.PlanType == "watch") return;
        if ((plan.EntryPrice ?? 0) <= 0) return;

        var entryDropPct = ((double)(currentPrice - (plan.EntryPrice ?? 0m)) / (double)(plan.EntryPrice ?? 0m) * 100);
        if (entryDropPct > -(double)Config.EntryDropThreshold) return;

        var isCritical = entryDropPct <= -10;
        if (!CanEmitSignal($"{plan.Id}:entry_drop", "triggered", Config.EntryDropCooldownMs)) return;

        // 同股同类信息限频：10 分钟内最多 3 次
        if (!CheckRateLimit(plan.StockCode, "stop_loss", 3, 10 * 60 * 1000)) return;
        CommitSignalState($"{plan.Id}:entry_drop", "triggered");

        var dropAbs = Math.Abs(entryDropPct).ToString("F2", CultureInfo.InvariantCulture);

        _petStore.AddReminder(new ReminderRequest
        {
            Type = "stop_loss",
            Level = isCritical ? ReminderLevel.Critical : ReminderLevel.Alert,
            Title = $"{plan.StockName} 强制止损提醒",
            Content = $"{plan.StockName}（{plan.StockCode}）从进场价 {plan.EntryPrice} 跌 {dropAbs}% 至 {currentPrice}。即使未到计划中的止损价 {plan.StopLoss}，已大幅亏损，请立即评估是否止损。",
            StockCode = plan.StockCode,
            StockName = plan.StockName,
            Importance = isCritical ? 7 : 6,
            DurationMs = 20000
        });
    }

    // ============================================================================
    // 隔夜低开止损检测 checkOvernightSellSignals
    // ============================================================================

    /// <summary>
    /// 隔夜低开止损检测 + 单日亏损熔断
    /// 时间窗口：9:25-9:30 集合竞价时段
    /// </summary>

    // ============================================================================
    // 隔夜低开止损检测 checkOvernightSellSignals
    // ============================================================================

    /// <summary>
    /// 隔夜低开止损检测 + 单日亏损熔断
    /// 时间窗口：9:25-9:30 集合竞价时段
    /// </summary>
    public async Task CheckOvernightSellSignalsAsync(TradePlan plan, StockQuote data)
    {
        if (!IsPlanMonitorable(plan)) return;
        if (plan.PlanType == "watch") return;

        var now = Now;
        var hours = _marketTime.GetHours(now);

        // 时间窗口：9:25-9:30（9:30 后由 handleTradingTime 调用，但内部窗口已收窄）
        if (hours < 9 + 25m / 60m || hours >= 9.5m) return;

        if (data == null || data.CurrentPrice <= 0 || data.PreClose <= 0) return;

        var currentPrice = data.CurrentPrice;
        var preClose = data.PreClose;
        var openGapPct = (currentPrice - preClose) / preClose * 100;

        // 隔夜低开止损：低开超过 3%
        if (openGapPct < -3m)
        {
            var key = $"{plan.Id}:overnight_gap";
            if (!CanEmitSignal(key, "triggered", 30 * 60 * 1000)) return;

            if (!CheckRateLimit(plan.StockCode, "overnight_gap", 2, 30 * 60 * 1000)) return;
            CommitSignalState(key, "triggered");

            _petStore.AddReminder(new ReminderRequest
            {
                Type = "overnight_gap",
                Level = openGapPct < -5 ? ReminderLevel.Critical : ReminderLevel.Alert,
                Title = $"{plan.StockName} 隔夜低开提醒",
                Content = $"{plan.StockName}（{plan.StockCode}）低开 {openGapPct:F2}%，当前 {currentPrice} 元，昨收 {preClose} 元。请评估是否止损或补仓。",
                StockCode = plan.StockCode,
                StockName = plan.StockName,
                Importance = openGapPct < -5 ? 7 : 6,
                DurationMs = 20000
            });
        }

        // 单日亏损熔断：相对进场价亏损超过 8%
        if ((plan.EntryPrice ?? 0) > 0)
        {
            var dailyLossPct = ((double)(currentPrice - (plan.EntryPrice ?? 0m)) / (double)(plan.EntryPrice ?? 0m) * 100);
            if (dailyLossPct <= -8.0)
            {
                var key = $"{plan.Id}:daily_loss_breaker";
                if (!CanEmitSignal(key, "triggered", 30 * 60 * 1000)) return;

                if (!CheckRateLimit(plan.StockCode, "daily_loss_breaker", 2, 30 * 60 * 1000)) return;
                CommitSignalState(key, "triggered");

                _petStore.AddReminder(new ReminderRequest
                {
                    Type = "daily_loss_breaker",
                    Level = ReminderLevel.Critical,
                    Title = $"{plan.StockName} 单日亏损熔断",
                    Content = $"{plan.StockName}（{plan.StockCode}）相对进场价已亏损 {Math.Abs(dailyLossPct):F2}%，触发单日亏损熔断。建议立即止损或减仓。",
                    StockCode = plan.StockCode,
                    StockName = plan.StockName,
                    Importance = 7,
                    DurationMs = 30000
                });
            }
        }

        await Task.CompletedTask;
    }

    // ============================================================================
    // 卖点/买点检测路由 _detectAndRouteSellSignals / _detectAndRouteBuySignals
    // ============================================================================

    private static readonly HashSet<string> KeyLevelTypes = new()
    { "break_ma5", "break_ma10", "break_ma30", "break_support" };

    /// <summary>
    /// ATR 类区间信号类型（条件持续为真的状态型信号：价格在阈值线一侧一直成立）
    /// </summary>
    private static readonly HashSet<string> AtrZoneTypes = new()
    { "atr_stop_loss", "atr_trailing_stop", "atr_take_profit" };

    /// <summary>
    /// ATR 类区间信号状态转换门控。
    /// 根因：止损/追踪止损/止盈的判定条件是持续状态（价格在线下/线上一直为真），
    /// 旧逻辑"条件为真即触发 + 15 分钟冷却"导致——
    /// ① 所有股票的快照由同一 10 秒 tick 统一写入，在"攒够第 10 根快照"的同一时刻
    ///    集体获得检测资格，已在线下的股票同 tick 批量爆发；
    /// ② 冷却到期后价格仍在线下 → 条件仍真 → 每 15 分钟周期性再爆发。
    /// 改为状态转换触发：
    /// - 新进入区间（价格实际穿越阈值线）→ 提醒一次；
    /// - 持续处于区间内 → 静默（不进入共振，防止状态信号反复参与批量评分提醒），
    ///   但较上次提醒价再恶化 ≥1% 时穿透再提醒；
    /// - 回升离开区间 → 状态重置，之后再次跌破会重新提醒；
    /// - 启动/预热时已在线下（秒级轨迹中从未出现过线的另一侧价格）→ 存量状态静默初始化。
    /// </summary>
    public List<SellSignalInfo> FilterAtrZoneTransitionSignals(string planId, string stockCode, List<SellSignalInfo> signals)
    {
        signals ??= new List<SellSignalInfo>();
        if (signals.Count == 0)
        {
            // 信号全消失 = 全部离开区间 → 重置状态（之后再次进入会重新提醒）
            foreach (var type in AtrZoneTypes)
            {
                var resetKey = $"{planId}:atr_zone_{type}";
                if (_signalStore.SignalStates.TryGetValue(resetKey, out var st) && st.State == "in")
                {
                    _signalStore.SignalStates[resetKey] = new SignalStateEntry { State = "out", At = NowMs };
                }
            }
            return signals;
        }

        var present = new HashSet<string>(signals.Where(s => AtrZoneTypes.Contains(s.Type)).Select(s => s.Type));

        // 回升离开区间 → 状态重置
        foreach (var type in AtrZoneTypes)
        {
            if (present.Contains(type)) continue;
            var resetKey = $"{planId}:atr_zone_{type}";
            if (_signalStore.SignalStates.TryGetValue(resetKey, out var st) && st.State == "in")
            {
                _signalStore.SignalStates[resetKey] = new SignalStateEntry { State = "out", At = NowMs };
            }
        }

        var result = new List<SellSignalInfo>();
        foreach (var sig in signals)
        {
            if (!AtrZoneTypes.Contains(sig.Type))
            {
                result.Add(sig);
                continue;
            }

            var key = $"{planId}:atr_zone_{sig.Type}";
            var hasPrev = _signalStore.SignalStates.TryGetValue(key, out var prev);
            var prevIn = hasPrev && prev!.State == "in";

            if (!prevIn)
            {
                // 无任何历史状态（首次观测）：用秒级轨迹区分"新穿越"与"存量"
                if (!hasPrev && !TrailShowsCrossedFrom(stockCode, sig.LevelPrice, sig.Type == "atr_take_profit"))
                {
                    // 轨迹中从未出现线的另一侧价格 = 启动/预热时已处于区间内（存量）→ 静默初始化
                    _signalStore.SignalStates[key] = new SignalStateEntry { State = "in", At = NowMs, Price = sig.CurrentPrice };
                    continue;
                }

                // 新进入区间（历史 out 后再进，或轨迹证实刚穿越）→ 提醒
                _signalStore.SignalStates[key] = new SignalStateEntry { State = "in", At = NowMs, Price = sig.CurrentPrice };
                result.Add(sig);
                continue;
            }

            // 持续在区间内：较上次提醒价再恶化 ≥1% → 穿透再提醒
            var lastPrice = prev!.Price ?? 0;
            if (lastPrice > 0)
            {
                var changePct = (sig.CurrentPrice - lastPrice) / lastPrice * 100;
                // 止损/追踪止损看进一步下跌；止盈看进一步上涨
                var worsened = sig.Type == "atr_take_profit" ? changePct >= 1m : changePct <= -1m;
                if (worsened)
                {
                    _signalStore.SignalStates[key] = new SignalStateEntry { State = "in", At = NowMs, Price = sig.CurrentPrice };
                    result.Add(sig);
                }
            }
            // 未恶化 → 静默丢弃
        }

        return result;
    }

    /// <summary>
    /// 秒级轨迹中是否出现过阈值线的另一侧价格（判定存量/新穿越）：
    /// zoneIsAbove=true（止盈，区间在线上方）→ 找轨迹中低于线的点（从下方穿越上来）；
    /// zoneIsAbove=false（止损，区间在线下方）→ 找轨迹中高于线的点（从上方跌破下来）。
    /// </summary>
    private bool TrailShowsCrossedFrom(string stockCode, decimal levelPrice, bool zoneIsAbove)
    {
        if (string.IsNullOrEmpty(stockCode) || levelPrice <= 0) return false;
        if (!_marketCache.LiveTrail.TryGetValue(stockCode, out var trail)) return false;

        lock (trail)
        {
            foreach (var p in trail)
            {
                if (zoneIsAbove ? p.Price < levelPrice : p.Price > levelPrice) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 分时卖点检测 + 提醒路由
    /// 门控：全局 sellPointDetection 开关 + 计划级 monitorSellPoint
    /// 路由：2+ 信号共振 → emitScoreAlert；单信号 → emitSignalAlert；
    ///       形态相似度信号豁免（即使参与共振也额外单独提醒）；
    ///       过滤后仅剩 vwap_slope_down → 静音记录、不提醒（不作为独立提醒依据）
    /// </summary>
    private async Task DetectAndRouteSellSignals(TradePlan plan, StockQuote data, bool sellPointEnabled)
    {
        if (!sellPointEnabled || plan.MonitorSellPoint == 0) return;

        var snapshots = GetSnapshots(plan.StockCode);
        if (snapshots.Count < 5) return;

        // keyLevelDetection 关闭时过滤均线/支撑位跌破信号
        var keyLevelEnabled = _settingsStore.Settings.KeyLevelDetection;

        var dailyKlines = await FetchDailyKlinesWithCache(plan.StockCode);
        var capitalFlow = await FetchCapitalFlowWithCache(plan.StockCode);

        var signals = _sellPointDetector.Analyze(plan, data, snapshots, dailyKlines, capitalFlow);

        if (!keyLevelEnabled)
        {
            signals = signals.Where(s => !KeyLevelTypes.Contains(s.Type)).ToList();
        }

        // ATR 类区间信号改为状态转换触发（防快照预热同步批量爆发，详见 FilterAtrZoneTransitionSignals）
        signals = FilterAtrZoneTransitionSignals(plan.Id, plan.StockCode, signals);

        // 均价线拐头向下不作为独立提醒（兜底守卫）：检测器侧守卫（SellPointDetectorService.Analyze
        // 末尾）只覆盖检测结果本身只剩拐头信号的情况；keyLevelDetection 过滤与本层 ATR 状态转换
        // 过滤发生在检测器守卫之后，可能把共振信号清到只剩拐头信号，单信号路由会绕过守卫弹出
        // 独立提醒。此处过滤后仅剩拐头信号 → 按静音处理，仅记录事件、不弹提醒。
        if (signals.Count > 0 && signals.All(s => s.Type == "vwap_slope_down"))
        {
            foreach (var sig in signals)
            {
                await RecordMutedSignalEvent(plan, sig, "noStandaloneAlert");
            }
            return;
        }

        // 自进化低成功率规矩（对齐均线拐头"不作为独立提醒依据"）：
        // 乘子≤静音阈值的卖点特征只能作为共振因子参与多信号评分，
        // 不可成为提醒的唯一依据——全部信号均为低成功率特征时静默记录事件、不弹提醒。
        if (signals.Count >= 2)
        {
            var multipliers = _sellPointDetector.GetSignalMultipliers();
            var allMuted = signals.All(s =>
                multipliers.TryGetValue(s.Type, out var m) && m <= MonitorConfig.SignalMuteThreshold);

            if (allMuted)
            {
                // 全部为低成功率特征：无有效锚点信号，仅记录 muted 事件供进化回放/漏报复盘
                foreach (var sig in signals)
                {
                    await RecordMutedSignalEvent(plan, sig);
                }
                return;
            }

            // 多信号共振 → 评分提醒（低成功率特征以乘子缩放后的权重参与，作为多因子之一）
            await EmitScoreAlert(plan, signals);

            // 形态相似度信号豁免：即使参与共振也额外单独提醒
            foreach (var sig in signals)
            {
                if (PatternSimilarityTypes.Contains(sig.Type) && sig.Similarity != null)
                {
                    await EmitSignalAlert(plan, sig);
                }
            }
        }
        else if (signals.Count == 1)
        {
            await EmitSignalAlert(plan, signals[0]);
        }
    }

    /// <summary>
    /// 记录被静音的卖点信号事件（mutedByEvolution 供漏报复盘区分静音/漏报）。
    /// 静音原因：lowMultiplier=自进化低成功率（乘子≤静音阈值）；noStandaloneAlert=规则禁止独立提醒（如均价线拐头）。
    /// 与提醒共用 15 分钟 N1 去重键，避免持续状态刷屏事件。
    /// </summary>
    private async Task RecordMutedSignalEvent(TradePlan plan, SellSignalInfo signal, string reason = "lowMultiplier")
    {
        var key = $"{plan.Id}:sell_{signal.Type}";
        if (!CanEmitSignal(key, "muted", 15 * 60 * 1000)) return;
        CommitSignalState(key, "muted");

        var collectOnly = plan.PlanType == "watch";
        _signalEventStore.RecordEvent(new SignalEventRecord
        {
            StockCode = plan.StockCode,
            StockName = plan.StockName,
            SignalType = signal.Type,
            SignalLabel = $"{signal.Label}(已静音)",
            Price = signal.CurrentPrice,
            Timestamp = NowMs,
            Metadata = new Dictionary<string, object>
            {
                ["score"] = signal.Score,
                ["alerted"] = false,
                ["collectOnly"] = collectOnly,
                ["mutedByEvolution"] = true,
                ["mutedReason"] = reason
            }
        });
        Log.Debug("[计划调度] 卖点信号 {Type} 静音记录(原因: {Reason})，不作为独立提醒",
            signal.Type, reason);

        await Task.CompletedTask;
    }

    /// <summary>
    /// 分时买点检测 + 提醒路由
    /// 门控：计划级 monitorBuyPoint=1
    /// </summary>
    private async Task DetectAndRouteBuySignals(TradePlan plan, StockQuote data)
    {
        if (plan.MonitorBuyPoint != 1) return;

        var snapshots = GetSnapshots(plan.StockCode);
        if (snapshots.Count < 5) return;

        var dailyKlines = await FetchDailyKlinesWithCache(plan.StockCode);
        var buySignals = _buyPointDetector.Analyze(plan, data, snapshots, dailyKlines);

        foreach (var signal in buySignals)
        {
            await EmitBuySignalAlert(plan, signal);
        }
    }

    // ============================================================================
    // 信号提醒发射 emitSignalAlert / emitBuySignalAlert / emitScoreAlert / emitCollectedSignal
    // ============================================================================

    /// <summary>
    /// 卖点信号提醒 emitSignalAlert
    /// 含静音门控、数据收集模式、形态相似度豁免
    /// </summary>

    // ============================================================================
    // 信号提醒发射 emitSignalAlert / emitBuySignalAlert / emitScoreAlert / emitCollectedSignal
    // ============================================================================

    /// <summary>
    /// 卖点信号提醒 emitSignalAlert
    /// 含静音门控、数据收集模式、形态相似度豁免
    /// </summary>
    private async Task EmitSignalAlert(TradePlan plan, SellSignalInfo signal)
    {
        // 静音门控：低成功率特征（乘子≤静音阈值）不作为独立提醒依据，
        // 改为记录 mutedByEvolution 事件（供进化回放/漏报复盘/复活机制使用）
        var multipliers = _sellPointDetector.GetSignalMultipliers();
        if (multipliers.TryGetValue(signal.Type, out var multiplier))
        {
            if (multiplier <= MonitorConfig.SignalMuteThreshold)
            {
                Log.Debug("[计划调度] 信号 {Type} 已静音(乘子={Multiplier:F3})，记录muted事件",
                    signal.Type, multiplier);
                await RecordMutedSignalEvent(plan, signal);
                return;
            }
        }

        // N1 去重
        var key = $"{plan.Id}:sell_{signal.Type}";
        if (!CanEmitSignal(key, "triggered", 15 * 60 * 1000)) return;

        // 波内限发
        if (!WaveGateAllows(plan.StockCode, data_currentPrice(signal), "sell")) return;

        // 同股同类限频
        if (!CheckRateLimit(plan.StockCode, "sell_signal", 2, 60 * 1000)) return;
        CommitSignalState(key, "triggered");

        // 数据收集模式：仅记录不弹气泡
        var collectOnly = plan.PlanType == "watch";

        if (!collectOnly)
        {
            _petStore.AddReminder(new ReminderRequest
            {
                Type = "sell_signal",
                Level = signal.Score >= 70 ? ReminderLevel.Alert : ReminderLevel.Hint,
                Title = $"{plan.StockName} {signal.Label}",
                Content = $"{plan.StockName}（{plan.StockCode}）触发卖点信号：{signal.Label}（评分 {signal.Score:F0}）。当前价 {signal.CurrentPrice:F2}。",
                StockCode = plan.StockCode,
                StockName = plan.StockName,
                Importance = signal.Score >= 70 ? 6 : 4,
                DurationMs = 12000
            });
        }

        WaveGatePass(plan.StockCode, data_currentPrice(signal), "sell");

        // 记录信号事件（数据收集计划留痕带·收集后缀，与弹窗通道同 type 聚合统计）
        _signalEventStore.RecordEvent(new SignalEventRecord
        {
            StockCode = plan.StockCode,
            StockName = plan.StockName,
            SignalType = signal.Type,
            SignalLabel = collectOnly ? $"{signal.Label}·收集" : signal.Label,
            Price = signal.CurrentPrice,
            Timestamp = NowMs,
            Metadata = new Dictionary<string, object>
            {
                ["score"] = signal.Score,
                ["similarity"] = signal.Similarity ?? 0,
                ["alerted"] = !collectOnly,
                ["collectOnly"] = collectOnly
            }
        });

        await Task.CompletedTask;
    }

    /// <summary>
    /// 买点信号提醒 emitBuySignalAlert
    /// </summary>
    private async Task EmitBuySignalAlert(TradePlan plan, BuySignalInfo signal)
    {
        var key = $"{plan.Id}:buy_{signal.Type}";
        if (!CanEmitSignal(key, "triggered", 15 * 60 * 1000)) return;

        if (!CheckRateLimit(plan.StockCode, "buy_signal", 2, 60 * 1000)) return;
        CommitSignalState(key, "triggered");

        var collectOnly = plan.PlanType == "watch";

        if (!collectOnly)
        {
            _petStore.AddReminder(new ReminderRequest
            {
                Type = "buy_signal",
                Level = signal.Score >= 70 ? ReminderLevel.Alert : ReminderLevel.Hint,
                Title = $"{plan.StockName} {signal.Label}",
                Content = $"{plan.StockName}（{plan.StockCode}）触发买点信号：{signal.Label}（评分 {signal.Score:F0}）。",
                StockCode = plan.StockCode,
                StockName = plan.StockName,
                Importance = signal.Score >= 70 ? 6 : 4,
                DurationMs = 12000
            });
        }

        _signalEventStore.RecordEvent(new SignalEventRecord
        {
            StockCode = plan.StockCode,
            StockName = plan.StockName,
            SignalType = $"buy_{signal.Type}",
            SignalLabel = collectOnly ? $"{signal.Label}·收集" : signal.Label,
            Price = signal.CurrentPrice,
            Timestamp = NowMs,
            Metadata = new Dictionary<string, object>
            {
                ["score"] = signal.Score,
                ["alerted"] = !collectOnly,
                ["collectOnly"] = collectOnly
            }
        });

        await Task.CompletedTask;
    }

    /// <summary>
    /// 多信号共振评分提醒 emitScoreAlert
    /// VIX 四档优先级：强制清仓(>=80) / 立即卖出(>=60) / 减仓观察(>=40) / 预警关注(<40)
    /// </summary>
    private async Task EmitScoreAlert(TradePlan plan, List<SellSignalInfo> signals)
    {
        // 计算综合评分
        var totalScore = signals.Sum(s => s.Score);

        // VIX 四档优先级配置（对齐原版 priorityConfig：emoji/等级/重要度/操作建议/气泡时长）
        var priorityName = totalScore switch
        {
            >= 80 => "强制清仓",
            >= 60 => "立即卖出",
            >= 40 => "减仓观察",
            _ => "预警关注"
        };
        var (emoji, level, importance, advice, durationMs) = priorityName switch
        {
            "强制清仓" => ("🔥", ReminderLevel.Critical, 8, "多因子强烈共振且量能动量双确认，建议立即清仓避险。", 35000),
            "立即卖出" => ("🔴", ReminderLevel.Critical, 7, "多信号强烈共振，建议立即执行卖出操作。", 30000),
            "减仓观察" => ("🟡", ReminderLevel.Alert, 6, "多信号共振，建议分批减仓观察走势。", 20000),
            _ => ("🟠", ReminderLevel.Alert, 5, "出现多个卖点信号，请密切关注后续走势。", 15000)
        };

        // N1 去重
        var key = $"{plan.Id}:score_alert";
        if (!CanEmitSignal(key, priorityName, 10 * 60 * 1000)) return;

        // 波内限发
        var avgPrice = signals.Average(s => s.CurrentPrice);
        if (!WaveGateAllows(plan.StockCode, avgPrice, "score")) return;

        if (!CheckRateLimit(plan.StockCode, "score_alert", 2, 10 * 60 * 1000)) return;
        CommitSignalState(key, priorityName);

        var collectOnly = plan.PlanType == "watch";

        if (!collectOnly)
        {
            // 多因子上下文（适配器已从引擎结果透传到信号上）
            var mfScore = signals[0].MultiFactorScore;
            var mfDetail = signals[0].MultiFactorDetail;
            var holdFilter = signals[0].HoldFilter;

            // 五段式正文（对齐原版：共振信号/综合评分/优先级/多因子明细/操作建议）
            var signalNames = string.Join("、", signals.Select(s => s.Label));
            var content =
                $"{plan.StockName}（{plan.StockCode}）触发 {signals.Count} 个卖点共振：\n" +
                $"📊 共振信号：{signalNames}\n" +
                $"⭐ 综合评分：{totalScore:F2} 分{(mfScore > 0 ? $"（因子{mfScore:F2}）" : "")}\n" +
                $"📌 优先级：{priorityName}{(string.IsNullOrEmpty(holdFilter) ? "" : $" · {holdFilter}")}" +
                $"{(string.IsNullOrEmpty(mfDetail) ? "" : $"\n🔬 多因子：{mfDetail}")}\n\n" +
                advice;

            _petStore.AddReminder(new ReminderRequest
            {
                Type = "score_alert",
                Level = level,
                Title = $"{emoji} {plan.StockName} {priorityName}（{totalScore:F2}分）",
                Content = content,
                StockCode = plan.StockCode,
                StockName = plan.StockName,
                Importance = importance,
                DurationMs = durationMs
            });
        }

        WaveGatePass(plan.StockCode, avgPrice, "score");

        // 记录信号事件（label 对齐原版评分通道：优先级名(总分)，观察池留痕带·收集后缀）
        foreach (var sig in signals)
        {
            _signalEventStore.RecordEvent(new SignalEventRecord
            {
                StockCode = plan.StockCode,
                StockName = plan.StockName,
                SignalType = sig.Type,
                SignalLabel = collectOnly
                    ? $"{priorityName}({totalScore:F2}分)·收集"
                    : $"{priorityName}({totalScore:F2}分)",
                Price = avgPrice,
                Timestamp = NowMs,
                Metadata = new Dictionary<string, object>
                {
                    ["score"] = sig.Score,
                    ["totalScore"] = totalScore,
                    ["priorityName"] = priorityName,
                    ["signalCount"] = signals.Count,
                    ["multiFactorScore"] = signals[0].MultiFactorScore,
                    ["multiFactorDetail"] = signals[0].MultiFactorDetail ?? "",
                    ["alerted"] = !collectOnly,
                    ["collectOnly"] = collectOnly
                }
            });
        }

        await Task.CompletedTask;
    }

    // ============================================================================
    // 限频去重 shouldEmitSignal / checkRateLimit / cleanRateLimit
    // ============================================================================

    /// <summary>
    /// 信号去重检查 shouldEmitSignal
    /// 同一 key 同一状态在冷却时间内不重复触发
    /// </summary>
}
