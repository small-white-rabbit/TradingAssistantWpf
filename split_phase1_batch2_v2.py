"""
Phase 1 拆分脚本 - 第 2 批 v2：基于方法签名定位，不依赖固定行号。
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

# 方法签名模式：4 空格缩进 + 修饰符 + 返回类型 + 名字 + (
# 也匹配带注释/特性前导行
METHOD_RE = re.compile(r'^    (public|private|internal|protected)\b')

def find_methods(lines):
    """返回 [(start_idx, method_name), ...]，start_idx 是方法签名行（不含注释）"""
    methods = []
    for i, ln in enumerate(lines):
        m = METHOD_RE.match(ln)
        if m:
            # 排除字段声明（含 = new / = ... ; 或 get/set）
            stripped = ln.strip()
            if '= new' in stripped or 'get;' in stripped or 'set;' in stripped:
                continue
            if stripped.endswith(';') and '(' not in stripped:
                continue
            # 提取方法名
            name_match = re.search(r'(\w+)\s*\(', ln)
            if name_match:
                methods.append((i, name_match.group(1)))
    return methods

def split_class(filepath, namespace, classname, groups):
    """
    groups: [(filename, [method_name1, method_name2, ...]), ...]
    每组方法连同前导注释/特性一起移到新 partial 文件。
    骨架（字段 + 未分组方法）保留在主文件。
    """
    lines = read_lines(filepath)
    usings = extract_usings(lines)
    methods = find_methods(lines)

    # 找 class 声明行
    class_line_idx = None
    for i, ln in enumerate(lines):
        if 'public partial class' in ln and classname in ln:
            class_line_idx = i
            break

    # 骨架 = using + namespace + class 开括号 + 到第一个方法前的所有字段
    # 找第一个方法签名行
    first_method_idx = methods[0][0] if methods else len(lines)
    # 骨架区域：class_line_idx 到 first_method_idx 之间（含字段声明）
    # 但字段也可能在方法之间，所以骨架 = class 声明行到文件尾的所有"非分组方法"

    # 构建方法行号映射
    method_ranges = []  # [(start, end_exclusive, name)]
    for idx, (start, name) in enumerate(methods):
        end = methods[idx + 1][0] if idx + 1 < len(methods) else None
        # end 需要回溯：下一个方法前可能有注释，但我们不想包含下一个方法的前导注释
        # 简化：end = 下一个方法签名行（含其前导注释会被归到下一个方法）
        method_ranges.append((start, end, name))

    # 为每个方法扩展 start 到包含前导注释/特性
    def expand_start(start):
        j = start - 1
        while j >= 0:
            s = lines[j].strip()
            if s == '' or s.startswith('//') or s.startswith('['):
                j -= 1
            else:
                break
        return j + 1

    expanded_ranges = [(expand_start(s), e, n) for s, e, n in method_ranges]

    # 按分组归类
    grouped_names = set()
    for _, names in groups:
        grouped_names.update(names)

    # 骨架行：class 声明 + 字段 + 未分组方法
    skeleton_lines = lines[:first_method_idx]  # using + namespace + class 开括号 + 字段
    for s, e, n in expanded_ranges:
        if n not in grouped_names:
            # 保留在骨架
            end = e if e else len(lines)
            # 去掉尾部类闭合括号
            chunk = lines[s:end]
            skeleton_lines.extend(chunk)

    skeleton_lines.append("}\n")

    # 每组写入新 partial 文件
    for filename, names in groups:
        body = []
        for s, e, n in expanded_ranges:
            if n in names:
                end = e if e else len(lines)
                body.extend(lines[s:end])
        if body:
            header = usings + [
                f"\nnamespace {namespace};\n\n",
                f"public partial class {classname}\n",
                "{\n",
            ]
            write_file(filename, header + body + ["}\n"])
            print(f"  写入 {filename} ({len(body)} 行)")

    # 重写主文件（骨架）
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
        ]),
        ("Engines/SellPointDetectorService.Indicators.cs", [
            "CalculateATR", "CalculateRSI", "CalculateWR", "CalculateMFI",
            "CheckOverboughtResonance", "GetMarketContext", "GetPositionFactor",
            "FindPeaksRobust", "CalculateSlopeByTime", "CalculateVWAPSlope",
            "CalcVWAPSlopeRaw", "PrepareAnalyzeCtx", "CheckVolumeAmplified",
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
            "EstimateSnapshotIntervalMs", "ComputeReward", "SegmentWaves",
            "EvaluateDay", "UpdateStats", "GetRecentStats", "ClassifyQuality",
            "GetQualityStatsByStock",
        ]),
        ("Services/SignalEventService.Stats.cs", [
            "ReplayWithParams", "GetFactorRewardStats", "GetOptimizationSuggestions",
            "UpdateAttribution", "EntryLastNet", "DecayAttributionFreezes",
            "UnfreezeParam", "GetRecentMutedMissCounts", "Cleanup",
            "AnalyzeMissedSellPoints", "WaveFeatures",
        ]),
    ])

print("\n第 2 批 v2 完成，请 build + test 验证")
