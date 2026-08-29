using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using StockReview.Core.Data;

namespace StockReview.Core.Services;

public partial class SignalEventService
{

    // ============ 两段式回放 ============

    /// <summary>
    /// 两段式回放（自进化搜索引擎核心）
    /// </summary>
    public ReplayResult ReplayWithParams(Dictionary<string, double>? newMultipliers, Dictionary<string, double>? newFactorWeights, int days = EvolutionWindowDays)
    {
        newMultipliers ??= new();
        newFactorWeights ??= new();
        var dateKeys = _events.Keys.OrderBy(k => k).TakeLast(days).ToList();
        var perEvent = new List<ReplayEventInfo>();
        var blame = new Dictionary<string, double>();
        var credit = new Dictionary<string, double>();

        foreach (var dk in dateKeys)
        {
            if (!_events.TryGetValue(dk, out var events)) continue;
            foreach (var evt in events)
            {
                if (!evt.Evaluated || evt.Evaluation == null) continue;
                if (evt.SignalType == "hold_filtered") continue;
                if (!SellSignalTypes.Contains(evt.SignalType)) continue;

                var quality = ClassifyQuality(evt.Evaluation);
                if (quality == "mid") continue;

                var md = evt.Metadata ?? new Dictionary<string, object>();
                var ev = new ReplayEventInfo
                {
                    Event = evt, Quality = quality, Stage1Pass = false, Stage2Pass = false,
                    Strength = 0.5, DateKey = dk
                };

                // 强度初始化
                if (md.TryGetValue("signalStrength", out var ss) && ss is double ssD)
                    ev.Strength = ssD;
                else if (md.TryGetValue("totalScore", out var ts) && ts is double tsD)
                    ev.Strength = Math.Min(1, tsD / 100);

                Dictionary<string, double>? contributions = null;

                // 共振事件 vs 单信号事件
                if (md.TryGetValue("composition", out var comp) && comp is List<object> compList && compList.Count > 0 &&
                    md.TryGetValue("scoreMods", out var sm) && sm is double scoreMods)
                {
                    // 共振事件重放
                    double newBase = 0;
                    contributions = new Dictionary<string, double>();
                    double contribSum = 0;
                    foreach (var item in compList)
                    {
                        if (item is not Dictionary<string, object> c) continue;
                        var cType = c.TryGetValue("type", out var t) ? t?.ToString() ?? "" : "";
                        double mult = newMultipliers.TryGetValue(cType, out var mv) ? mv : (c.TryGetValue("multiplier", out var m) && m is double mD ? mD : 1.0);
                        double cBase = c.TryGetValue("base", out var b) && b is double bD ? bD : 10;
                        bool halved = c.TryGetValue("halved", out var h) && h is true;
                        double contrib = cBase * mult * (halved ? 0.5 : 1);
                        newBase += contrib;
                        if (!contributions.ContainsKey(cType)) contributions[cType] = 0;
                        contributions[cType] += Math.Abs(contrib);
                        contribSum += Math.Abs(contrib);
                    }
                    double scoreBonus = md.TryGetValue("scoreBonus", out var sb) && sb is double sbD ? sbD : 0;
                    double newSignal = Math.Min(100, JsMath.JsRound(newBase * scoreMods + scoreBonus));

                    // 多因子评分重放
                    double? mfNew = null;
                    if (md.TryGetValue("factorScores", out var fs) && fs is Dictionary<string, object> fsDict)
                    {
                        var fd = md.TryGetValue("factorDirections", out var fd2) && fd2 is Dictionary<string, object> fdDict ? fdDict : new Dictionary<string, object>();
                        double num = 0, den = 0;
                        int bearCount = 0;
                        var factorContrib = new Dictionary<string, double>();
                        double factorContribSum = 0;
                        foreach (var (k, s) in fsDict)
                        {
                            double sv = s is double sd ? sd : 0;
                            var dir = fd.TryGetValue(k, out var dv) ? dv?.ToString() ?? "bear" : "bear";
                            double w = newFactorWeights.TryGetValue(k, out var wv) ? wv : 0;
                            if (dir == "bear")
                            {
                                num += sv * w; den += w; bearCount++;
                                var fk = $"factor:{k}";
                                if (!factorContrib.ContainsKey(fk)) factorContrib[fk] = 0;
                                factorContrib[fk] += Math.Abs(sv * w);
                                factorContribSum += Math.Abs(sv * w);
                            }
                            else if (dir == "bull") num -= sv * w * 0.5;
                        }
                        if (den > 0)
                        {
                            double resBonus = bearCount >= 5 ? 35 : bearCount >= 4 ? 25 : bearCount >= 3 ? 15 : 0;
                            mfNew = Math.Min(100, num / den + resBonus);
                        }
                        if (factorContribSum > 0 && mfNew.HasValue)
                        {
                            foreach (var (fk, fv) in factorContrib)
                            {
                                if (!contributions.ContainsKey(fk)) contributions[fk] = 0;
                                contributions[fk] += 0.4 * (fv / factorContribSum);
                            }
                        }
                    }

                    bool holdFilter = md.TryGetValue("holdFilter", out var hf) && hf is true;
                    double fused = mfNew.HasValue ? Math.Max(newSignal, newSignal * 0.6 + mfNew.Value * 0.4) : newSignal;
                    ev.Stage1Pass = fused >= 20 && (!holdFilter || fused >= 35);
                    ev.Strength = Math.Max(0, Math.Min(1, fused / 100));

                    if (contribSum > 0)
                    {
                        var keys = contributions.Keys.ToList();
                        foreach (var k in keys) contributions[k] /= contribSum;
                    }
                }
                else
                {
                    // 单信号事件
                    double mult = newMultipliers.TryGetValue(evt.SignalType, out var mv) ? mv : 1.0;
                    ev.Stage1Pass = mult > SignalMuteThreshold;
                    if (md.TryGetValue("baseWeight", out var bw) && bw is double bwD && bwD > 0)
                        ev.Strength = Math.Max(0, Math.Min(1, (bwD * mult) / 100));
                    contributions = new Dictionary<string, double> { [evt.SignalType] = 1 };
                }

                ev.Contributions = contributions;
                perEvent.Add(ev);
            }
        }

        // 第二段：波内限发
        var groups = new Dictionary<string, List<ReplayEventInfo>>();
        foreach (var ev in perEvent)
        {
            var waveIdx = ev.Event.Evaluation?.WaveIdx;
            string gKey = waveIdx.HasValue
                ? $"{ev.DateKey}|{ev.Event.StockCode}|{waveIdx.Value}"
                : $"__solo_{ev.Event.Id}";
            if (!groups.ContainsKey(gKey)) groups[gKey] = new List<ReplayEventInfo>();
            groups[gKey].Add(ev);
        }
        foreach (var list in groups.Values)
        {
            list.Sort((a, b) => a.Event.Timestamp.CompareTo(b.Event.Timestamp));
            int emitted = 0;
            double lastStrength = double.NegativeInfinity;
            foreach (var ev in list)
            {
                if (!ev.Stage1Pass) continue;
                if (emitted >= MaxAlertsPerWave) continue;
                if (emitted >= 1 && ev.Strength <= lastStrength) continue;
                ev.Stage2Pass = true;
                emitted++;
                lastStrength = ev.Strength;
            }
        }

        // 指标汇总
        int lowTotal = 0, lowFiltered = 0, highTotal = 0, highKept = 0;
        int stage1LowFiltered = 0, stage1HighKept = 0;
        var waveMap = new Dictionary<string, WaveReplayInfo>();

        foreach (var ev in perEvent)
        {
            bool alive = ev.Stage1Pass && ev.Stage2Pass;
            if (ev.Quality == "low")
            {
                lowTotal++;
                if (!alive)
                {
                    lowFiltered++;
                    if (ev.Stage1Pass) stage1LowFiltered++;
                }
                else if (ev.Contributions != null)
                {
                    foreach (var (k, share) in ev.Contributions)
                    {
                        if (!blame.ContainsKey(k)) blame[k] = 0;
                        blame[k] += share;
                    }
                }
            }
            else
            {
                highTotal++;
                if (alive)
                {
                    highKept++;
                    if (ev.Stage1Pass && ev.Stage2Pass) stage1HighKept++;
                }
                else if (ev.Contributions != null)
                {
                    foreach (var (k, share) in ev.Contributions)
                    {
                        if (!credit.ContainsKey(k)) credit[k] = 0;
                        credit[k] += share;
                    }
                }
                var waveIdx = ev.Event.Evaluation?.WaveIdx;
                if (waveIdx.HasValue)
                {
                    var wk = $"{ev.DateKey}|{ev.Event.StockCode}|{waveIdx.Value}";
                    if (!waveMap.ContainsKey(wk))
                    {
                        waveMap[wk] = new WaveReplayInfo
                        {
                            WaveKey = wk, DateKey = ev.DateKey, StockCode = ev.Event.StockCode,
                            StockName = ev.Event.StockName, WaveIdx = waveIdx.Value,
                            DepthPct = ev.Event.Evaluation?.WaveDepthPct,
                            HighTotal = 0, HighKept = 0, Top1Rank = -1, Top1Alive = false
                        };
                    }
                    var w = waveMap[wk];
                    w.HighTotal++;
                    if (alive) w.HighKept++;
                    double rank = ev.Event.Evaluation?.RankScore ?? 0;
                    if (rank > w.Top1Rank) { w.Top1Rank = rank; w.Top1Alive = alive; }
                }
            }
        }

        var waves = waveMap.Values.OrderByDescending(w => w.HighTotal).ToList();
        var waveViolations = waves
            .Where(w => (w.HighTotal >= 1 && !w.Top1Alive) || (w.HighTotal >= 2 && w.HighKept == 0))
            .Select(w => w.WaveKey).ToList();

        return new ReplayResult
        {
            Replayable = perEvent.Count,
            LowTotal = lowTotal, LowFiltered = lowFiltered,
            LowFilterRate = lowTotal > 0 ? (double?)lowFiltered / lowTotal : null,
            HighTotal = highTotal, HighKept = highKept,
            HighKeepRate = highTotal > 0 ? (double?)highKept / highTotal : null,
            Stage1LowFiltered = stage1LowFiltered, Stage1HighKept = stage1HighKept,
            Waves = waves, WaveViolations = waveViolations,
            Blame = blame, Credit = credit
        };
    }

    // ============ 因子级奖励统计 ============


    public void UpdateAttribution(List<AttributionRoundEntry> roundEntries)
    {
        if (roundEntries == null || roundEntries.Count == 0) return;
        var entries = _attribution.Entries ??= new();
        var touched = new List<string>();

        foreach (var re in roundEntries)
        {
            if (string.IsNullOrEmpty(re.ParamKey)) continue;
            var key = re.ParamKey;
            if (!entries.ContainsKey(key))
            {
                entries[key] = new AttributionEntry
                {
                    Kind = re.Kind ?? "signal", Label = re.Label ?? key,
                    Role = "normal", Frozen = false, FreezeReason = "",
                    DirectionStreak = 0, TotalLowFiltered = 0, TotalHighKilled = 0,
                    FailedSteps = 0, History = new List<AttributionHistoryRecord>()
                };
            }
            var entry = entries[key];
            entry.Kind = re.Kind ?? entry.Kind;
            entry.Label = re.Label ?? entry.Label;

            int dir = Math.Sign(re.Delta);
            if (dir != 0)
            {
                entry.DirectionStreak = Math.Sign(entry.DirectionStreak) == dir ? entry.DirectionStreak + dir : dir;
            }

            if (re.Failed)
            {
                entry.FailedSteps = (entry.FailedSteps ?? 0) + 1;
                if (entry.FailedSteps >= 2 && !entry.Frozen)
                {
                    entry.Frozen = true;
                    entry.FreezeReason = $"连续{entry.FailedSteps}次该方向调整被回滚";
                    entry.FrozenAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
            }
            else
            {
                entry.FailedSteps = 0;
                entry.TotalLowFiltered += Math.Max(0, re.LowFiltered);
                entry.TotalHighKilled += Math.Max(0, re.HighKilled);
                double net = re.LowFiltered - 2 * re.HighKilled;
                entry.History ??= new();
                entry.History.Add(new AttributionHistoryRecord
                {
                    Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Delta = re.Delta, Net = net,
                    LowFiltered = re.LowFiltered, HighKilled = re.HighKilled
                });
                if (entry.History.Count > 10) entry.History = entry.History.TakeLast(10).ToList();
            }
            touched.Add(key);
        }

        // 等效参数归并
        if (touched.Count > 1)
        {
            var active = touched.Select(k => (k, net: EntryLastNet(entries[k])))
                .Where(x => x.net > 0)
                .OrderByDescending(x => x.net)
                .ToList();
            if (active.Count > 1)
            {
                double mainNet = active[0].net;
                entries[active[0].k].Role = "main";
                foreach (var item in active.Skip(1))
                {
                    if (item.net >= mainNet * 0.8)
                    {
                        entries[item.k].Role = "secondary";
                        entries[item.k].Frozen = true;
                        entries[item.k].FreezeReason = $"等效次选（主参数 {entries[active[0].k].Label} 已覆盖该方向）";
                        entries[item.k].FrozenAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    }
                }
            }
        }
        SaveAttribution();
    }


    private static double EntryLastNet(AttributionEntry entry)
    {
        if (entry.History == null || entry.History.Count == 0) return 0;
        return entry.History[^1].Net;
    }

    /// <summary>
    /// 归因冻结日衰减
    /// </summary>
    public void DecayAttributionFreezes()
    {
        try
        {
            var today = TodayKey();
            if (_attribution.DayKey == today) return;
            if (_attribution.Entries == null) return;

            bool changed = false;
            long weekAgo = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 7 * 24 * 60 * 60 * 1000L;

            foreach (var entry in _attribution.Entries.Values)
            {
                if ((entry.FailedSteps ?? 0) > 0) { entry.FailedSteps = 0; changed = true; }
                if (entry.Frozen)
                {
                    bool isFailFreeze = entry.FreezeReason?.StartsWith("连续") == true;
                    bool expired = (entry.FrozenAt ?? 0) < weekAgo;
                    if (isFailFreeze || expired)
                    {
                        entry.Frozen = false;
                        entry.FreezeReason = "";
                        entry.FrozenAt = 0;
                        if (entry.Role == "secondary") entry.Role = "normal";
                        changed = true;
                    }
                }
            }
            _attribution.DayKey = today;
            if (changed) SaveAttribution();
        }
        catch (Exception e)
        {
            Log.Warning(e, "[SignalEvent] 衰减归因冻结失败");
        }
    }

    /// <summary>
    /// 解冻单个参数
    /// </summary>
    public void UnfreezeParam(string paramKey, string note = "")
    {
        if (_attribution.Entries == null || !_attribution.Entries.TryGetValue(paramKey, out var entry)) return;
        entry.Frozen = false;
        entry.FreezeReason = "";
        entry.FrozenAt = 0;
        entry.FailedSteps = 0;
        if (entry.Role == "secondary") entry.Role = "normal";
        if (!string.IsNullOrEmpty(note))
        {
            entry.History ??= new();
            entry.History.Add(new AttributionHistoryRecord
            {
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Delta = 0, Net = 0, LowFiltered = 0, HighKilled = 0, Note = note
            });
            if (entry.History.Count > 10) entry.History = entry.History.TakeLast(10).ToList();
        }
        SaveAttribution();
    }

    /// <summary>
    /// 近 N 日被静音类型在漏报波顶的累计命中统计
    /// </summary>

    // ============ 清理过期数据 ============

    public void Cleanup()
    {
        var tz = CnTimeZone.Get;
        var cutoffDate = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow.AddDays(-MaxHistoryDays), tz);
        var cutoffStr = $"{cutoffDate.Year:0000}-{cutoffDate.Month:00}-{cutoffDate.Day:00}";

        bool changed = false;
        var toRemove = _events.Keys.Where(k => string.Compare(k, cutoffStr, StringComparison.Ordinal) < 0).ToList();
        foreach (var k in toRemove) { _events.Remove(k); changed = true; }
        if (changed) SaveEvents();
    }

    // ============ 漏报复盘 ============
    /// <summary>
    /// 漏报复盘：检测"该出现卖点而未出现"的显著回落波
    /// </summary>
    public MissedAnalysisSummary AnalyzeMissedSellPoints(string dateKey, Dictionary<string, List<Snapshot>> snapshotsMap)
    {
        var dayEvents = GetEventsByDate(dateKey);
        var nameByCode = new Dictionary<string, string>();
        foreach (var e in dayEvents)
        {
            if (!string.IsNullOrEmpty(e.StockName) && !nameByCode.ContainsKey(e.StockCode))
                nameByCode[e.StockCode] = e.StockName;
        }

        var waveList = new List<MissedWaveInfo>();
        if (snapshotsMap != null)
        {
            foreach (var (code, snaps) in snapshotsMap)
            {
                if (snaps == null || snaps.Count < 5) continue;
                foreach (var w in SegmentWaves(snaps))
                {
                    if (w.RisePct < MissedWaveMinRisePct) continue;
                    if (w.DepthPct < MissedWaveMinDepthPct) continue;

                    int activeCount = 0;
                    var mutedTypes = new HashSet<string>();
                    var mutedLabels = new Dictionary<string, string>();
                    foreach (var e in dayEvents)
                    {
                        if (e.StockCode != code) continue;
                        if (!SellSignalTypes.Contains(e.SignalType)) continue;
                        if (Math.Abs(e.Timestamp - w.PeakTime) > WaveTopToleranceMs) continue;
                        if (e.Metadata != null && e.Metadata.TryGetValue("mutedByEvolution", out var muted) && muted is true)
                        {
                            mutedTypes.Add(e.SignalType);
                            var label = e.SignalLabel ?? "";
                            mutedLabels[e.SignalType] = label.Replace("(已静音)", "");
                        }
                        else activeCount++;
                    }

                    waveList.Add(new MissedWaveInfo
                    {
                        StockCode = code,
                        StockName = nameByCode.GetValueOrDefault(code, code),
                        WaveIdx = w.WaveIdx,
                        PeakTime = w.PeakTime,
                        RisePct = JsMath.JsRound(w.RisePct, 2),
                        DepthPct = JsMath.JsRound(w.DepthPct, 2),
                        Coverage = activeCount > 0 ? "active" : (mutedTypes.Count > 0 ? "muted" : "zero"),
                        MutedTypes = mutedTypes.ToList(),
                        MutedLabels = mutedLabels,
                        Features = WaveFeatures(snaps, w)
                    });
                }
            }
        }

        var missed = waveList.Where(w => w.Coverage != "active").ToList();
        var covered = waveList.Where(w => w.Coverage == "active").ToList();
        var featureCompare = CompareWaveFeatures(covered, missed);

        // 静音过度提示
        var mutedHitTypes = new Dictionary<string, int>();
        var mutedHitLabels = new Dictionary<string, string>();
        foreach (var m in missed)
        {
            if (m.Coverage != "muted") continue;
            if (m.MutedTypes == null) continue;
            foreach (var t in m.MutedTypes)
            {
                if (!mutedHitTypes.ContainsKey(t)) mutedHitTypes[t] = 0;
                mutedHitTypes[t]++;
                if (m.MutedLabels != null && m.MutedLabels.TryGetValue(t, out var label))
                    mutedHitLabels[t] = label;
            }
        }
        int mutedHitTotal = mutedHitTypes.Values.Sum();
        string? mutedHint = mutedHitTotal > 0
            ? $"静音类型「{string.Join("、", mutedHitLabels.Values)}」在 {mutedHitTotal} 个漏报波顶本可覆盖（检测到但被自进化压制），系统将依据近5日累计自动复活"
            : null;

        var summary = new MissedAnalysisSummary
        {
            DateKey = dateKey,
            SignificantWaves = waveList.Count,
            MissedCount = missed.Count,
            Missed = missed,
            FeatureCompare = featureCompare,
            MutedHint = mutedHint,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        _missedAnalysis[dateKey] = summary;
        SaveMissedAnalysis();

        var recent = _missedAnalysis.Keys.OrderBy(k => k).TakeLast(EvolutionWindowDays).ToList();
        summary.RecentSignificant = recent.Sum(k => _missedAnalysis[k]?.SignificantWaves ?? 0);
        summary.RecentMissed = recent.Sum(k => _missedAnalysis[k]?.MissedCount ?? 0);

        return summary;
    }


    private static WaveFeatures WaveFeatures(List<Snapshot> snaps, Wave wave)
    {
        var f = new WaveFeatures();
        try
        {
            var peakSnap = snaps[wave.PeakIdx];
            double avg = (double)peakSnap.AvgPrice.GetValueOrDefault();
            double peak = (double)peakSnap.Price;
            if (double.IsFinite(avg) && avg > 0 && double.IsFinite(peak) && peak > 0)
                f.VwapDevPct = JsMath.JsRound(((peak - avg) / avg * 100), 2);

            double durMin = Math.Max(1, (wave.PeakTime - wave.TroughTime) / 60000.0);
            f.SurgeSpeed5m = JsMath.JsRound(wave.RisePct / (durMin / 5), 2);

            int end = wave.PeakIdx;
            int s1 = Math.Max(0, end - 5);
            int s0 = Math.Max(0, s1 - 20);
            double recentSum = 0; int recentN = 0;
            double baseSum = 0; int baseN = 0;
            for (int i = s1; i <= end; i++)
            {
                double v = (double)snaps[i].Volume.GetValueOrDefault();
                bool reliable = snaps[i].VolumeReliable != false;
                if (reliable && double.IsFinite(v) && v > 0) { recentSum += v; recentN++; }
            }
            for (int i = s0; i < s1; i++)
            {
                double v = (double)snaps[i].Volume.GetValueOrDefault();
                bool reliable = snaps[i].VolumeReliable != false;
                if (reliable && double.IsFinite(v) && v > 0) { baseSum += v; baseN++; }
            }
            if (recentN > 0 && baseN > 0)
                f.VolumeExp = JsMath.JsRound((recentSum / recentN) / (baseSum / baseN), 2);
        }
        catch { /* ignore */ }
        return f;
    }


    private static FeatureCompareResult? CompareWaveFeatures(List<MissedWaveInfo> coveredList, List<MissedWaveInfo> missedList)
    {
        double? AvgOf(List<MissedWaveInfo> list, string key)
        {
            var vals = list.Select(w => w.Features).Where(f => f != null).Select(f =>
                key == "vwapDevPct" ? f!.VwapDevPct :
                key == "volumeExp" ? f!.VolumeExp :
                key == "surgeSpeed5m" ? f!.SurgeSpeed5m : (double?)null)
                .Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return vals.Count > 0 ? vals.Average() : null;
        }
        var cmp = new FeatureCompareResult
        {
            MissedVwapDev = AvgOf(missedList, "vwapDevPct"),
            CoveredVwapDev = AvgOf(coveredList, "vwapDevPct"),
            MissedVolExp = AvgOf(missedList, "volumeExp"),
            CoveredVolExp = AvgOf(coveredList, "volumeExp"),
            MissedSpeed = AvgOf(missedList, "surgeSpeed5m"),
            CoveredSpeed = AvgOf(coveredList, "surgeSpeed5m")
        };
        return (cmp.MissedVwapDev.HasValue || cmp.MissedVolExp.HasValue) ? cmp : null;
    }
}
