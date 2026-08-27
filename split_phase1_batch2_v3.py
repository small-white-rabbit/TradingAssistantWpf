"""
Phase 1 拆分脚本 - 第 2 批 v3：基于完整方法清单修正。
"""
import re
from pathlib import Path

CORE = Path(r"D:\stock-review-system\TradingAssistantWpf\StockReview.Core")

def read_lines(rel):
    return (CORE / rel).read_text(encoding="utf-8-sig").splitlines(keepends=True)

def write_file(rel, lines):
    p = CORE / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text("".join(lines), encoding="utf-8")

def extract_usings(lines):
    usings = []
    for ln in lines:
        if ln.strip().startswith("using ") or ln.strip() == "":
            usings.append(ln)
        elif ln.strip().startswith("namespace "):
            break
    while usings and usings[-1].strip() == "":
        usings.pop()
    return usings

METHOD_RE = re.compile(r'^    (public|private|internal|protected)\b')

def find_methods(lines):
    methods = []
    for i, ln in enumerate(lines):
        m = METHOD_RE.match(ln)
        if m:
            stripped = ln.strip()
            if '= new' in stripped or 'get;' in stripped or 'set;' in stripped:
                continue
            if stripped.endswith(';') and '(' not in stripped:
                continue
            name_match = re.search(r'(\w+)\s*[\(<]', ln)
            if name_match:
                methods.append((i, name_match.group(1)))
    return methods

def expand_start(lines, start):
    j = start - 1
    while j >= 0:
        s = lines[j].strip()
        if s == '' or s.startswith('//') or s.startswith('['):
            j -= 1
        else:
            break
    return j + 1

def split_class(filepath, namespace, classname, groups):
    lines = read_lines(filepath)
    usings = extract_usings(lines)
    methods = find_methods(lines)

    class_line_idx = None
    for i, ln in enumerate(lines):
        if 'public partial class' in ln and classname in ln:
            class_line_idx = i
            break

    first_method_idx = methods[0][0] if methods else len(lines)

    expanded_ranges = []
    for idx, (start, name) in enumerate(methods):
        end = methods[idx + 1][0] if idx + 1 < len(methods) else len(lines)
        expanded_ranges.append((expand_start(lines, start), end, name))

    grouped_names = set()
    for _, names in groups:
        grouped_names.update(names)

    skeleton_lines = lines[:first_method_idx]
    for s, e, n in expanded_ranges:
        if n not in grouped_names:
            skeleton_lines.extend(lines[s:e])
    skeleton_lines.append("}\n")

    for filename, names in groups:
        body = []
        for s, e, n in expanded_ranges:
            if n in names:
                body.extend(lines[s:e])
        if body:
            header = usings + [
                f"\nnamespace {namespace};\n\n",
                f"public partial class {classname}\n",
                "{\n",
            ]
            write_file(filename, header + body + ["}\n"])
            print(f"  写入 {filename} ({len(body)} 行)")

    write_file(filepath, skeleton_lines)
    print(f"  骨架保留 {filepath} ({len(skeleton_lines)} 行)")

# ============ PlanSchedulerService ============
print("=== PlanSchedulerService ===")
split_class(
    "Services/PlanSchedulerService.cs",
    "StockReview.Core.Services",
    "PlanSchedulerService",
    [
        ("Services/PlanSchedulerService.Checking.cs", [
            "CheckPlanSignals", "CheckTodayPlan", "CheckTargetPriceAsync", "CheckStopLossAsync",
            "DetectMultiWindowRapid", "DetectLimitMove", "GetLimitPct", "CheckEntryDropForceStop",
            "CheckOvernightSellSignalsAsync", "DetectAndRouteSellSignals", "DetectAndRouteBuySignals",
            "EmitSignalAlert", "EmitBuySignalAlert", "EmitScoreAlert", "HandleAfterMarketAction",
        ]),
        ("Services/PlanSchedulerService.Snapshots.cs", [
            "ShouldEmitSignal", "CheckRateLimit", "CleanRateLimit",
            "WaveGateAllows", "WaveGatePass", "IsLevelHitNotifiedToday", "MarkLevelHitNotified",
            "RecordSnapshotsAsync", "FetchTrendsVwapAsync", "GetSnapshots", "SaveSnapshot",
            "FlushSnapshotsAsync", "FetchBatchDataWithCache", "FetchDailyKlinesWithCache",
            "FetchCapitalFlowWithCache", "CleanupExpiredCaches", "CleanupCache",
        ]),
        ("Services/PlanSchedulerService.Reminders.cs", [
            "CheckCustomRemindersAsync", "CheckCustomRemindersAsync_Legacy", "CheckPreCloseMA5Async",
            "LoadLatestTradingDayPicks", "ShowIdleInsightAsync", "ShowMarketDigestAsync",
            "LoadLatestTradingDayPicksAsync", "CollectTodaySignalSummary", "ShowWeekendSummary",
            "GetWeekStart", "GetWeekEnd", "BackfillTodayEventsAsync", "EvaluateTodaySignalsAsync",
        ]),
        ("Services/PlanSchedulerService.Evolution.cs", [
            "AutoOptimizeParamsAsync", "OptimizeFactorWeights", "OptimizeSignalWeights",
            "RunEvolutionSearchAsync", "DeriveSearchSteps", "ToDoubleMap", "ResurrectMutedFromMissed",
            "ShowSelfEvolutionReport", "LoadAutoOptimizedParams", "LoadOptimizedParams",
            "SaveAutoOptimizedParams", "SyncOptimizedParams", "EnsureSnapshotTable", "SaveConfig",
        ]),
        ("Services/PlanSchedulerService.Futu.cs", [
            "EnsureFutuSubscriptionAsync", "OnFutuConnectionChanged", "BindFutuPush", "OnFutuPush",
            "RunPushDrivenDetectAsync", "DetectForStockAsync", "CleanupFutuSubscriptionAfterClose",
            "IsPlanMonitorable", "ShouldShowPreMarketReminder", "ShouldShowNonTradingDayReminder",
            "PlanTypeText", "IsFinite", "data_currentPrice",
            "SaveAfterMarketNotified", "LoadAfterMarketNotified", "ClearAfterMarketSnooze",
            "SaveAfterMarketLastReminder",
        ]),
    ])

# ============ SellPointDetectorService ============
print("=== SellPointDetectorService ===")
split_class(
    "Engines/SellPointDetectorService.cs",
    "StockReview.Core.Engines",
    "SellPointDetectorService",
    [
        ("Engines/SellPointDetectorService.Analyze.cs", [
            "Analyze", "CreateBreakSignal",
            "DetectSurgePullback", "DetectVolumeStagnant", "DetectSpikeVolumeTop",
            "DetectMASuppress", "DetectTopDivergence", "DetectVolumeDivergence",
            "DetectKeyLevelBreakdown", "DetectDoubleTop", "DetectDoubleTopEarly",
            "DetectFishingLine", "DetectTripleTop", "DetectPlatformBreakdown",
            "DetectHighDeviationPullback", "DetectVWAPBreakdown", "DetectVWAPRejection",
            "DetectVWAPSlopeDown", "DetectLateSessionExit", "DetectWeakReboundFailure",
            "DetectDeepDropRebound", "DetectATRStopLoss", "DetectAsync",
            "CalculateDynamicSupport",
        ]),
        ("Engines/SellPointDetectorService.Indicators.cs", [
            "CalculateATR", "CalculateMA", "CalculateDailyMA",
            "CalculateRSI", "CalculateWR", "CalculateMFI",
            "CheckOverboughtResonance", "GetMarketContext", "GetPositionFactor",
            "FindPeaksRobust", "CalculateSlopeByTime", "CalculateVWAPSlope",
            "CalcVWAPSlopeRaw", "PrepareAnalyzeCtx", "CheckVolumeAmplified",
            "FindPlatformBefore",
        ]),
        ("Engines/SellPointDetectorService.Scoring.cs", [
            "GetBaseWeight", "IsNoEvolveType", "GetSignalWeight", "DeduplicateSignals",
            "CalculateTimeDensity", "EvaluateSignals", "CheckMomentumConfirm",
            "CheckTouchedLineBefore", "GetMinNeckDepth", "CalculateLegVolumes",
            "CalculateTripleLegVolumes", "GetHourMin",
        ]),
    ])

# ============ SignalEventService ============
print("=== SignalEventService ===")
split_class(
    "Services/SignalEventService.cs",
    "StockReview.Core.Services",
    "SignalEventService",
    [
        ("Services/SignalEventService.Evaluation.cs", [
            "EstimateSnapshotIntervalMs", "ComputeReward", "ComputeOptimalExitPoints",
            "SegmentWaves", "EvaluateDay", "UpdateStats", "GetRecentStats",
            "ClassifyQuality", "GetQualityStatsByStock", "EvaluateEvent",
        ]),
        ("Services/SignalEventService.Stats.cs", [
            "ReplayWithParams", "GetFactorRewardStats", "GetOptimizationSuggestions",
            "UpdateAttribution", "EntryLastNet", "DecayAttributionFreezes",
            "UnfreezeParam", "GetRecentMutedMissCounts", "Cleanup",
            "AnalyzeMissedSellPoints", "WaveFeatures", "CompareWaveFeatures",
        ]),
    ])

print("\n第 2 批 v3 完成，请 build + test 验证")
