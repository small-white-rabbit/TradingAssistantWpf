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
    // 信号自进化 autoOptimizeParams / runEvolutionSearch 等
    // ============================================================================

    /// <summary>
    /// 自动优化参数 autoOptimizeParams
    /// 盘后自动执行因子权重 + 信号乘子优化
    /// </summary>
    public async Task AutoOptimizeParamsAsync()
    {
        var todayStr = _marketTime.FormatDate(Now);
        if (_lastAutoOptimizeDate == todayStr) return;

        // 持久化去重（跨重启）：应用重启后内存变量清零会导致盘后每次启动都
        // 重新执行自进化并重复推送"自进化报告"提醒，故以 appConfig 记录当日已执行
        try
        {
            using var conn = _db.CreateConnection();
            var savedDate = conn.ExecuteScalar<string>(
                "SELECT value FROM appConfig WHERE key = 'pet_last_auto_optimize_date'");
            if (savedDate == todayStr)
            {
                _lastAutoOptimizeDate = todayStr;
                return;
            }
        }
        catch { /* 读取失败不阻断本次执行 */ }

        // 仅在盘后执行
        var hours = _marketTime.GetHours(Now);
        if (hours < 15 || hours >= 20) return;

        _lastAutoOptimizeDate = todayStr;
        try
        {
            using var conn = _db.CreateConnection();
            SaveConfig(conn, "pet_last_auto_optimize_date", todayStr);
        }
        catch { /* ignore */ }

        try
        {
            // 1. 先评估今日信号（含漏报复盘分析，产出存入事件存储）
            await EvaluateTodaySignalsAsync();

            // 2. 闭环搜索引擎（主路径）：样本充足（≥8 个可回放已评估卖点）时
            //    由搜索承担信号乘子/因子权重调整
            var searchResult = await RunEvolutionSearchAsync();

            // 3. 搜索无改进空间（已达标或参数全冻结）或样本不足 → 传统微调路径渐进调整
            List<FactorChange> factorChanges;
            List<SignalChange> signalChanges;
            if (searchResult.Ran && searchResult.Improved)
            {
                factorChanges = new List<FactorChange>();
                signalChanges = new List<SignalChange>();
            }
            else
            {
                factorChanges = OptimizeFactorWeights();
                signalChanges = OptimizeSignalWeights();
            }

            // 4. 漏报复活（闭环反馈）：近5日 ≥2 个漏报波顶本可由某被静音类型覆盖 →
            //    证明该类型静音过度，乘子回升至 0.50 并解除归因冻结
            var missedResult = _signalEventStore.GetMissedAnalysis(todayStr);
            var resurrected = ResurrectMutedFromMissed(missedResult);

            // 5. 回放验证：以引擎当前实际参数（搜索已回填/复活已生效）重算
            ReplayResult? replayResult = null;
            try
            {
                replayResult = _signalEventStore.ReplayWithParams(
                    ToDoubleMap(_sellPointDetector.GetSignalMultipliers()),
                    ToDoubleMap(_multiFactorEngine.GetWeights()));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[自进化] 回放验证失败");
            }

            // 6. 卖点信号阈值调整（如放量滞涨）
            var thresholdChanges = AdjustSellThresholds();

            // 7. 推送自进化报告气泡通知（含搜索收敛过程/波次明细/漏报复盘+复活/主参数表）
            ShowSelfEvolutionReport(factorChanges, signalChanges, thresholdChanges,
                replayResult, searchResult, missedResult, resurrected);

            // 8. 持久化参数
            SaveAutoOptimizedParams();

            Log.Information("[计划调度] 自进化完成：因子变更 {FactorCount}，信号变更 {SignalCount}，阈值变更 {ThresholdCount}，复活 {ResurrectCount}",
                factorChanges.Count, signalChanges.Count, thresholdChanges.Count, resurrected.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[计划调度] 自进化失败");
        }
    }

    /// <summary>
    /// 卖点信号阈值调整（对齐原版 autoOptimizeParams 的阈值分支）
    /// 仅放量滞涨（volume_stagnant）参与：低胜率提高阈值（×1.2，上限1.5%），
    /// 高胜率降低阈值（×0.8，下限0.2%）；变化&gt;0.01 才应用。
    /// 快速拉升/下跌阈值由 RapidWindows 硬编码，不参与自动调整。
    /// </summary>
    private List<ThresholdChange> AdjustSellThresholds()
    {
        var thresholdChanges = new List<ThresholdChange>();
        try
        {
            var suggestions = _signalEventStore.GetOptimizationSuggestions();
            foreach (var s in suggestions)
            {
                if (s.SignalType != "volume_stagnant") continue;

                var oldThreshold = (decimal)_sellPointDetector.GetConfig().StagnantThreshold;
                var newThreshold = oldThreshold;
                if (s.Action == "increase_threshold")
                {
                    newThreshold = Math.Min(oldThreshold * 1.2m, 1.5m); // 上限1.5%
                }
                else if (s.Action == "decrease_threshold")
                {
                    newThreshold = Math.Max(oldThreshold * 0.8m, 0.2m); // 下限0.2%
                }
                if (Math.Abs(newThreshold - oldThreshold) > 0.01m)
                {
                    newThreshold = Math.Round(newThreshold, 3);
                    _sellPointDetector.UpdateConfig(new { stagnantThreshold = (double)newThreshold });
                    thresholdChanges.Add(new ThresholdChange
                    {
                        SignalType = s.SignalType,
                        SignalLabel = s.SignalLabel ?? s.SignalType,
                        Action = s.Action,
                        OldThreshold = oldThreshold,
                        NewThreshold = (decimal)_sellPointDetector.GetConfig().StagnantThreshold,
                        WinRate = s.WinRate,
                        Total = s.Total
                    });
                }
            }

            // 输出调整日志
            foreach (var adj in thresholdChanges)
            {
                var action = adj.Action == "increase_threshold" ? "提高阈值" : "降低阈值";
                Log.Information(
                    "[自进化] {Label} 胜率 {WinRate:F1}% ({Total}次样本) → {Action} {Old} → {New}",
                    adj.SignalLabel, adj.WinRate * 100, adj.Total, action, adj.OldThreshold, adj.NewThreshold);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[自进化] 阈值调整失败");
        }
        return thresholdChanges;
    }

    /// <summary>因子中文名映射（与多因子引擎 camelCase key 对应）</summary>
    private static readonly Dictionary<string, string> FactorNames = new()
    {
        ["pricePosition"] = "价格位置",
        ["surgeAngle"] = "拉升角度",
        ["volume"] = "量能",
        ["maPressure"] = "均线压力",
        ["klinePattern"] = "K线形态",
        ["intradayPattern"] = "分时形态",
        ["momentum"] = "动量",
        ["timeFactor"] = "时间因子",
        ["capitalFlow"] = "资金流向"
    };

    /// <summary>
    /// 显示自进化报告 _showSelfEvolutionReport
    /// 收盘后评估信号 → 自进化优化 → 推送报告给用户
    /// 展示：信号评估概况 + 搜索收敛过程 + 波次明细 + 因子/信号权重调整 +
    /// 阈值调整 + 回放验证 + 漏报复盘（+静音复活）+ 参数归因 + 按股票今日总结
    /// </summary>
    private void ShowSelfEvolutionReport(
        List<FactorChange> factorChanges,
        List<SignalChange> signalChanges,
        List<ThresholdChange> thresholdChanges,
        ReplayResult? replayResult,
        EvolutionSearchResult searchResult,
        MissedAnalysisSummary? missedResult,
        List<ResurrectedSignal> resurrected)
    {
        try
        {
            // ★ 近期窗口统计（与自进化参数调整同源）：报告展示的是回测依据本身
            var stats = _signalEventStore.GetRecentStats();

            // ===== 1. 信号统计概览 =====
            var allStats = stats
                .Where(kv => kv.Value.Total >= 3)
                .Select(kv => new
                {
                    Label = kv.Value.SignalLabel ?? kv.Key,
                    Total = kv.Value.Total,
                    Success = kv.Value.Success,
                    WinRate = kv.Value.Total > 0 ? (decimal)kv.Value.Success / kv.Value.Total : 0m
                })
                .OrderByDescending(s => s.WinRate)
                .ToList();

            var totalSignals = allStats.Sum(s => s.Total);
            var totalSuccess = allStats.Sum(s => s.Success);
            var overallWinRate = totalSignals > 0 ? (decimal)totalSuccess / totalSignals : 0m;

            var sections = new List<string> { "🧬 自进化报告\n" };

            // 整体概况（基于近期窗口，即本次参数调整的回测依据）
            sections.Add("📈 信号评估概况（近5个交易日回测）");
            sections.Add($"  本期 {totalSignals} 个信号，整体胜率 {overallWinRate * 100:F1}%");
            if (allStats.Count > 0)
            {
                var topStr = string.Join("、", allStats.Take(3)
                    .Select(s => $"{s.Label} {s.WinRate * 100:F0}%({s.Total}次)"));
                sections.Add($"  🏆 高胜率：{topStr}");
                var bottomStr = string.Join("、", allStats.TakeLast(3).Reverse()
                    .Select(s => $"{s.Label} {s.WinRate * 100:F0}%({s.Total}次)"));
                sections.Add($"  ⚠️ 低胜率：{bottomStr}");
            }
            sections.Add("");

            // ===== 1.5 闭环搜索：收敛过程 + 波次明细 + 主参数表 =====
            if (searchResult.Ran && searchResult.Initial != null)
            {
                string FmtRate(double? v) => v == null ? "—" : $"{v * 100:F0}%";

                if (searchResult.AppliedSteps.Count > 0)
                {
                    sections.Add($"🔍 闭环参数搜索（{searchResult.AppliedSteps.Count} 轮迭代）");
                    sections.Add($"  起点 → 终点：低质量过滤 {FmtRate(searchResult.Initial.LowFilterRate)} → {FmtRate(replayResult?.LowFilterRate)}，高质量保留 {FmtRate(searchResult.Initial.HighKeepRate)} → {FmtRate(replayResult?.HighKeepRate)}");
                    // 收敛轨迹（最近3轮）
                    var trail = searchResult.AppliedSteps.TakeLast(3)
                        .Select(r => $"{(r.Direction == "down" ? "↓" : "↑")}{r.Key}→×{r.NewValue:F2}");
                    var trailList = trail.ToList();
                    if (trailList.Count > 0) sections.Add($"  轨迹：{string.Join("，", trailList)}");
                }
                else if (!searchResult.Improved)
                {
                    sections.Add("🔍 闭环参数搜索：当前参数已达最优（无需调整）");
                }

                // 波次明细（高质量事件最多的前3波；含日期——波次按日独立编号）
                var waves = (replayResult?.Waves ?? new List<WaveReplayInfo>())
                    .Where(w => w.HighTotal > 0).Take(3).ToList();
                if (waves.Count > 0)
                {
                    sections.Add("🌊 波次保留明细");
                    foreach (var w in waves)
                    {
                        var depth = w.DepthPct != null ? $"深度{w.DepthPct:F1}%" : "";
                        var dayTag = !string.IsNullOrEmpty(w.DateKey) ? $"{w.DateKey[5..]}·" : "";
                        sections.Add($"  {w.StockName}·{dayTag}第{w.WaveIdx + 1}波（{depth}）：高质量 {w.HighKept}/{w.HighTotal}，Top1 {(w.Top1Alive ? "✅存活" : "⚠️被杀")}");
                    }
                }
                var viol = replayResult?.WaveViolations?.Count ?? 0;
                sections.Add($"  硬约束：{(viol == 0 ? "✅ 各波Top1全部存活" : $"⚠️ {viol} 个波次违规（越界调整已回滚）")}");
                sections.Add("");
            }

            // ===== 2. 因子权重调整 =====
            if (factorChanges.Count > 0)
            {
                sections.Add($"⚖️ 因子权重调整（{factorChanges.Count} 项）");
                foreach (var fc in factorChanges.Take(5))
                {
                    var name = FactorNames.GetValueOrDefault(fc.Factor, fc.Factor);
                    var arrow = fc.Direction == "up" ? "↑" : "↓";
                    var pct = $"{(fc.NewWeight / fc.OldWeight - 1) * 100:F1}";
                    sections.Add($"  {arrow} {name}：{fc.OldWeight * 100:F1}% → {fc.NewWeight * 100:F1}% ({pct}%)");
                }
                sections.Add("");
            }

            // ===== 3. 信号权重乘子调整 =====
            if (signalChanges.Count > 0)
            {
                var upCount = signalChanges.Count(s => s.Direction == "up");
                var downCount = signalChanges.Count(s => s.Direction == "down");
                sections.Add($"🎯 信号权重调整（{upCount} 增 / {downCount} 降）");
                var upSignals = signalChanges.Where(s => s.Direction == "up").Take(4).ToList();
                if (upSignals.Count > 0)
                {
                    sections.Add("  ↑ 增权信号：");
                    foreach (var s in upSignals)
                        sections.Add($"  {s.SignalLabel} {s.WinRate * 100:F0}%→×{s.NewMultiplier:F2}");
                }
                var downSignals = signalChanges.Where(s => s.Direction == "down").Take(4).ToList();
                if (downSignals.Count > 0)
                {
                    sections.Add("  ↓ 降权信号：");
                    foreach (var s in downSignals)
                        sections.Add($"  {s.SignalLabel} {s.WinRate * 100:F0}%→×{s.NewMultiplier:F2}");
                }
                sections.Add("");
            }

            // ===== 4. 阈值调整 =====
            if (thresholdChanges.Count > 0)
            {
                sections.Add($"🔧 触发阈值调整（{thresholdChanges.Count} 项）");
                foreach (var tc in thresholdChanges.Take(4))
                {
                    var action = tc.Action == "increase_threshold" ? "提高" : "降低";
                    sections.Add($"  {tc.SignalLabel}：{action} {tc.OldThreshold} → {tc.NewThreshold}（胜率 {tc.WinRate * 100:F0}%）");
                }
                sections.Add("");
            }

            // ===== 5. 回放验证（新参数下"本日重来一次"的模拟效果）=====
            if (replayResult != null && replayResult.Replayable > 0)
            {
                sections.Add($"🔄 回放验证（近5个交易日 {replayResult.Replayable} 个已评估卖点）");
                if (replayResult.LowTotal > 0)
                {
                    var rate = replayResult.LowFilterRate != null ? $"（{replayResult.LowFilterRate * 100:F0}%）" : "";
                    sections.Add($"  🗑️ 低质量信号可剔除 {replayResult.LowFiltered}/{replayResult.LowTotal}{rate}");
                }
                else
                {
                    sections.Add("  🗑️ 低质量信号：近期无低质量卖点");
                }
                if (replayResult.HighTotal > 0)
                {
                    var rate = replayResult.HighKeepRate != null ? $"（{replayResult.HighKeepRate * 100:F0}%）" : "";
                    sections.Add($"  💎 高质量信号保留 {replayResult.HighKept}/{replayResult.HighTotal}{rate}");
                }
                sections.Add("");
            }
            else if (replayResult != null)
            {
                sections.Add("🔄 回放验证：近5日暂无可回放的已评估卖点（需积累带评分构成的事件）");
                sections.Add("");
            }

            // ===== 5.5 漏报复盘：该出现卖点而未出现（闭环回放看不到的另一侧）=====
            if (missedResult != null && missedResult.SignificantWaves > 0)
            {
                var mr = missedResult;
                if (mr.MissedCount > 0)
                {
                    sections.Add("🕳️ 漏报复盘（该出现卖点而未出现）");
                    sections.Add($"  显著回落波 {mr.SignificantWaves} 个（涨≥1%且深≥1.5%），其中 {mr.MissedCount} 个未获有效卖点覆盖：");
                    foreach (var m in (mr.Missed ?? new List<MissedWaveInfo>()).Take(4))
                    {
                        var coverTag = m.Coverage == "zero"
                            ? "零信号"
                            : $"仅静音({string.Join("、", (m.MutedTypes ?? new List<string>()).Select(t => m.MutedLabels?.GetValueOrDefault(t) ?? t))})";
                        sections.Add($"  • {m.StockName} 第{m.WaveIdx + 1}波 回落{m.DepthPct:F1}%（{m.PeakTimeStr}波顶）【{coverTag}】");
                    }
                    if ((mr.Missed?.Count ?? 0) > 4)
                    {
                        sections.Add($"  ...等共 {mr.MissedCount} 个");
                    }
                    // 漏报波鉴别特征（vs 已捕获波）→ 反馈检测缺口
                    var fc = mr.FeatureCompare;
                    if (fc != null)
                    {
                        string Fmt(double? v) => v == null ? "—" : $"{v:F1}";
                        sections.Add($"  漏报波特征：乖离+{Fmt(fc.MissedVwapDev)}% 量能{Fmt(fc.MissedVolExp)}x 拉速{Fmt(fc.MissedSpeed)}%/5min");
                        sections.Add($"  已捕获波：乖离+{Fmt(fc.CoveredVwapDev)}% 量能{Fmt(fc.CoveredVolExp)}x 拉速{Fmt(fc.CoveredSpeed)}%/5min");
                        if (fc.MissedVwapDev != null && fc.CoveredVwapDev != null && fc.MissedVwapDev < fc.CoveredVwapDev - 0.3)
                        {
                            sections.Add("  → 反馈：波顶贴近均价线（浅乖离）的回落最易漏报，均价线类信号阈值偏高");
                        }
                        if (fc.MissedVolExp != null && fc.CoveredVolExp != null && fc.MissedVolExp < 0.9 && fc.CoveredVolExp >= 0.9)
                        {
                            sections.Add($"  → 反馈：漏报波量能不活跃（{fc.MissedVolExp:F1}x），量能类信号覆盖不到，形态/位置类需补位");
                        }
                        if (fc.MissedSpeed != null && fc.CoveredSpeed != null && fc.MissedSpeed < fc.CoveredSpeed * 0.5)
                        {
                            sections.Add($"  → 反馈：漏报波拉升平缓（{fc.MissedSpeed:F1}%/5min），快速拉升类信号抓不到，缓涨滞涨形态需关注");
                        }
                    }
                    if (!string.IsNullOrEmpty(mr.MutedHint))
                    {
                        sections.Add($"  ⚠️ {mr.MutedHint}");
                    }
                    if (mr.RecentMissed > 0)
                    {
                        sections.Add($"  近5日累计漏报 {mr.RecentMissed}/{mr.RecentSignificant} 波");
                    }
                    // ★ 静音复活（当日已执行的闭环反馈动作）
                    if (resurrected.Count > 0)
                    {
                        sections.Add("♻️ 静音复活（防止同类卖点再漏）");
                        foreach (var r in resurrected.Take(4))
                        {
                            sections.Add($"  {r.Label}：×{r.From:F2} → ×{r.To:F2}（近5日 {r.Hits} 个漏报波顶本可由其覆盖，已恢复提醒）");
                        }
                    }
                }
                else
                {
                    sections.Add($"🕳️ 漏报复盘：{mr.SignificantWaves} 个显著回落波全部有卖点覆盖，无漏报 ✅");
                }
                sections.Add("");
            }

            // ===== 5.6 参数归因账本：主参数与等效冻结关系 =====
            try
            {
                var ledgerEntries = (_signalEventStore.GetAttributionLedger()?.Entries ?? new Dictionary<string, AttributionEntry>())
                    .Values.Where(e => e.History != null && e.History.Count > 0).ToList();
                if (ledgerEntries.Count > 0)
                {
                    // 按累计净贡献排序（过滤收益 - 2×误杀代价）
                    ledgerEntries.Sort((a, b) => (b.TotalLowFiltered - 2 * b.TotalHighKilled)
                        .CompareTo(a.TotalLowFiltered - 2 * a.TotalHighKilled));
                    sections.Add("📌 参数归因（跨日累积，等效参数择主保留）");
                    foreach (var e in ledgerEntries.Take(4))
                    {
                        var role = e.Role == "main" ? "主参数" : (e.Frozen ? "已冻结" : "正常");
                        var kindTag = e.Kind == "factor" ? "因子" : "信号";
                        var reason = string.IsNullOrEmpty(e.FreezeReason) ? "" : $"（{e.FreezeReason}）";
                        sections.Add($"  {kindTag}·{e.Label}【{role}】过滤{e.TotalLowFiltered}个/误杀{e.TotalHighKilled}个{reason}");
                    }
                    sections.Add("");
                }
            }
            catch { /* 归因展示失败不影响报告 */ }

            // ===== 6. 总结：按股票的今日信号 + 回测质量 + 优化导向 =====
            var searchRoundCount = searchResult.AppliedSteps.Count;
            var stockStats = _signalEventStore.GetQualityStatsByStock()
                .Values.Where(s => s.TodayTotal > 0 || s.Total > 0)
                .OrderByDescending(s => s.TodayTotal + s.Total)
                .ToList();
            sections.Add("──");
            if (stockStats.Count > 0)
            {
                sections.Add("📌 今日信号回测总结（质量分类基于近5日波次回测）");
                foreach (var s in stockStats.Take(6))
                {
                    var midTag = s.Mid > 0 ? $"、中性 {s.Mid} 个" : "";
                    sections.Add($"  {s.StockName}：今日总信号 {s.TodayTotal} 个，回测高质量 {s.High} 个、低质量 {s.Low} 个{midTag}");
                }
                // 优化效果与导向（用户可直读的闭环结论）
                var lowTotal = replayResult?.LowTotal ?? stockStats.Sum(s => s.Low);
                var lowFiltered = replayResult?.LowFiltered ?? 0;
                var lowRate = lowTotal > 0 ? (int)Math.Round(lowFiltered * 100.0 / lowTotal) : (int?)null;
                var highRate = replayResult?.HighKeepRate != null ? (int)Math.Round(replayResult.HighKeepRate.Value * 100) : (int?)null;
                var effectStr = lowRate != null
                    ? $"参数调整后低质量信号将不再出现（回放验证：{lowFiltered}/{lowTotal} 被过滤，过滤率 {lowRate}%）"
                    : "参数调整后低质量信号将不再出现";
                sections.Add($"  {effectStr}{(highRate != null ? $"，高质量信号保留 {highRate}%" : "")}");
                sections.Add("  高质量信号通常出现在波段顶部附近，回测即可判断——参数与权重已按「保留高质量、过滤低质量」调整");
                var totalChanges = factorChanges.Count + signalChanges.Count + thresholdChanges.Count + searchRoundCount;
                if (totalChanges > 0)
                {
                    sections.Add($"本轮共优化 {totalChanges} 项参数（搜索{searchRoundCount}步），明日监控将应用新参数");
                }
                else
                {
                    sections.Add("本轮参数已达标无需再调，明日监控沿用当前参数");
                }
            }
            else if (factorChanges.Count == 0 && signalChanges.Count == 0 && thresholdChanges.Count == 0 && searchRoundCount == 0)
            {
                if (totalSignals < 5)
                {
                    sections.Add($"💤 样本积累中（{totalSignals}/5），暂无足够数据触发优化");
                    sections.Add("  系统正在学习你的交易信号特征，请保持耐心");
                }
                else
                {
                    sections.Add("✅ 本轮参数稳定，无需调整");
                    sections.Add("  当前信号系统表现良好，各参数维持在最优区间");
                }
            }
            else
            {
                // 总结调整数量（含搜索轮次）
                var totalChanges = factorChanges.Count + signalChanges.Count + thresholdChanges.Count + searchRoundCount;
                sections.Add($"本轮共优化 {totalChanges} 项参数（搜索{searchRoundCount}步），明日监控将应用新参数");
            }

            _petStore.AddReminder(new ReminderRequest
            {
                Type = "self_evolution",
                Level = ReminderLevel.Hint,
                Title = "🧬 自进化报告",
                Content = string.Join("\n", sections),
                Importance = 2,
                DurationMs = 10000,
                Persistent = true
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[自进化] 报告推送失败");
        }
    }

    // ============================================================================
    // 参数持久化 loadAutoOptimizedParams / loadOptimizedParams / _syncOptimizedParams
    // ============================================================================

    /// <summary>
    /// 加载自动优化持久化参数（启动时调用）
    /// </summary>

    // ============================================================================
    // 参数持久化 loadAutoOptimizedParams / loadOptimizedParams / _syncOptimizedParams
    // ============================================================================

    /// <summary>
    /// 加载自动优化持久化参数（启动时调用）
    /// </summary>
    private void LoadAutoOptimizedParams()
    {
        try
        {
            // 从数据库加载（对应 JS 的 localStorage）
            using var conn = _db.CreateConnection();

            // 快速拉升阈值已排除在自进化之外（阈值由 Config.RapidWindows 硬编码，
            // signalEvents 统计跳过 rapid_ 前缀，不参与自动调整）。
            // 旧版本曾把阈值调至 0.65%/0.98%（默认 1%/2%）并持久化，导致小波动触发提醒
            // 并开启长冷却，压制后续真正的大幅拉升信号。版本号不匹配时删除持久化值
            // （localStorage.removeItem 语义：旧实现只写版本号不清值，下次启动版本匹配
            // 又把毒值加载回来），确保代码内默认阈值永久生效。
            const int rapidThresholdVersion = 3;
            var savedRapidVersion = conn.QueryFirstOrDefault<string>(
                "SELECT value FROM appConfig WHERE key = 'pet_auto_optimized_rapid_version'");
            if (savedRapidVersion != rapidThresholdVersion.ToString())
            {
                conn.Execute("DELETE FROM appConfig WHERE key = 'pet_auto_optimized_rapid_windows'");
                SaveConfig(conn, "pet_auto_optimized_rapid_version", rapidThresholdVersion.ToString());
                Log.Information("[自进化] 快速拉升阈值版本升级至 v{Version}，已清除旧持久化值（阈值硬编码不参与自动调整）",
                    rapidThresholdVersion);
            }

            // 卖点配置
            var sellRaw = conn.QueryFirstOrDefault<string>(
                "SELECT value FROM appConfig WHERE key = 'pet_auto_optimized_sell'");
            if (!string.IsNullOrEmpty(sellRaw))
            {
                var savedSell = JsonConvert.DeserializeObject<dynamic>(sellRaw);
                if (savedSell?.stagnantThreshold != null)
                {
                    // 更新卖点检测器配置
                    _sellPointDetector.UpdateConfig(new
                    {
                        stagnantThreshold = (decimal)savedSell.stagnantThreshold
                    });
                    Log.Information("[自进化] 已加载历史优化的放量滞涨阈值");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[自进化] 加载历史参数失败");
        }
    }

    /// <summary>
    /// 加载优化参数
    /// </summary>
    private void LoadOptimizedParams()
    {
        try
        {
            using var conn = _db.CreateConnection();

            // 信号乘子
            var multRaw = conn.QueryFirstOrDefault<string>(
                "SELECT value FROM appConfig WHERE key = 'pet_optimized_signal_multipliers'");
            if (!string.IsNullOrEmpty(multRaw))
            {
                var multipliers = JsonConvert.DeserializeObject<Dictionary<string, decimal>>(multRaw);
                if (multipliers != null)
                {
                    _sellPointDetector.UpdateSignalMultipliers(multipliers);
                    Log.Information("[自进化] 已加载信号乘子参数");
                }
            }

            // 因子权重
            var weightRaw = conn.QueryFirstOrDefault<string>(
                "SELECT value FROM appConfig WHERE key = 'pet_optimized_factor_weights'");
            if (!string.IsNullOrEmpty(weightRaw))
            {
                var weights = JsonConvert.DeserializeObject<Dictionary<string, decimal>>(weightRaw);
                if (weights != null)
                {
                    _multiFactorEngine.UpdateWeights(weights);
                    Log.Information("[自进化] 已加载因子权重参数");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[自进化] 加载优化参数失败");
        }
    }

    /// <summary>
    /// 保存自动优化参数到数据库
    /// </summary>
    private void SaveAutoOptimizedParams()
    {
        try
        {
            using var conn = _db.CreateConnection();

            // 快速拉升阈值不持久化（硬编码不参与自动调整，
            // 保存只会让历史污染值无限延续）

            // 保存信号乘子
            var multJson = JsonConvert.SerializeObject(_sellPointDetector.GetSignalMultipliers());
            SaveConfig(conn, "pet_optimized_signal_multipliers", multJson);

            // 保存因子权重
            var weightJson = JsonConvert.SerializeObject(_multiFactorEngine.GetWeights());
            SaveConfig(conn, "pet_optimized_factor_weights", weightJson);

            // 保存卖点阈值配置（放量滞涨 stagnantThreshold，LoadAutoOptimizedParams 启动时回载）
            var sellJson = JsonConvert.SerializeObject(new
            {
                stagnantThreshold = _sellPointDetector.GetConfig().StagnantThreshold
            });
            SaveConfig(conn, "pet_auto_optimized_sell", sellJson);

            Log.Information("[自进化] 参数已持久化");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[自进化] 保存参数失败");
        }
    }

    /// <summary>
    /// 同步优化参数 _syncOptimizedParams
    /// 优化参数优先级高于用户设置
    /// </summary>
    private void SyncOptimizedParams()
    {
        // 快速拉升窗口配置已在 Config 中，直接使用
        // 信号乘子和因子权重已通过 LoadOptimizedParams 加载到检测器中
    }

    /// <summary>
    /// 确保 price_snapshots 表存在
    /// </summary>
    private void EnsureSnapshotTable()
    {
        try
        {
            using var conn = _db.CreateConnection();
            const string sql = @"
                CREATE TABLE IF NOT EXISTS price_snapshots (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    stockCode TEXT NOT NULL,
                    price REAL NOT NULL,
                    volume INTEGER,
                    amount REAL,
                    timestamp TEXT NOT NULL,
                    vwap REAL,
                    volumeReliable INTEGER DEFAULT 1,
                    cumulativeVolume INTEGER
                );
                CREATE INDEX IF NOT EXISTS idx_snapshots_code_time ON price_snapshots(stockCode, timestamp);";
            conn.Execute(sql);

            // 旧库升级：补 cumulativeVolume 列（已存在时报 duplicate column，忽略）
            try
            {
                conn.Execute("ALTER TABLE price_snapshots ADD COLUMN cumulativeVolume INTEGER");
                Log.Information("[计划调度] price_snapshots 表已升级：新增 cumulativeVolume 列");
            }
            catch { /* 列已存在 */ }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[计划调度] 创建 price_snapshots 表失败");
        }
    }

    /// <summary>
    /// 保存配置项到 appConfig 表
    /// </summary>
    private static void SaveConfig(SqliteConnection conn, string key, string value)
    {
        const string sql = @"
            INSERT OR REPLACE INTO appConfig (key, value) VALUES (@Key, @Value)";
        conn.Execute(sql, new { Key = key, Value = value });
    }

}
