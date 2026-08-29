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
    // 信号自进化 - 对应 planScheduler.js autoOptimizeParams / runEvolutionSearch 等
    // ============================================================================

    /// <summary>
    /// 自动优化参数 - 对应 planScheduler.js autoOptimizeParams
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
            // 1. 先评估今日信号
            await EvaluateTodaySignalsAsync();

            // 2. 因子权重优化
            var factorChanges = OptimizeFactorWeights();

            // 3. 信号乘子优化
            var signalChanges = OptimizeSignalWeights();

            // 4. 进化搜索
            var searchResult = await RunEvolutionSearchAsync();

            // 5. 漏报复活
            var resurrected = ResurrectMutedFromMissed();

            // 6. 显示自进化报告
            if (factorChanges.Count > 0 || signalChanges.Count > 0 || searchResult.Improved || resurrected.Count > 0)
            {
                ShowSelfEvolutionReport(factorChanges, signalChanges, searchResult, resurrected);
            }

            // 7. 持久化参数
            SaveAutoOptimizedParams();

            Log.Information("[计划调度] 自进化完成：因子变更 {FactorCount}，信号变更 {SignalCount}，复活 {ResurrectCount}",
                factorChanges.Count, signalChanges.Count, resurrected.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[计划调度] 自进化失败");
        }
    }

    /// <summary>
    /// 显示自进化报告 - 对应 planScheduler.js _showSelfEvolutionReport
    /// </summary>
    private void ShowSelfEvolutionReport(
        List<FactorChange> factorChanges,
        List<SignalChange> signalChanges,
        EvolutionSearchResult searchResult,
        List<string> resurrected)
    {
        var sections = new List<string> { "自进化报告" };

        if (factorChanges.Count > 0)
        {
            sections.Add($"因子权重调整({factorChanges.Count}项):");
            sections.AddRange(factorChanges.Select(c =>
                $"  {c.Factor}: {c.OldWeight}→{c.NewWeight}({c.Direction})"));
        }

        if (signalChanges.Count > 0)
        {
            sections.Add($"信号乘子调整({signalChanges.Count}项):");
            sections.AddRange(signalChanges.Select(c =>
                $"  {c.SignalLabel}: {c.OldMultiplier}→{c.NewMultiplier}({c.Direction}, {c.Reason})"));
        }

        if (searchResult.Improved)
        {
            sections.Add($"进化搜索: 评分 {searchResult.OldScore:F1}→{searchResult.NewScore:F1}(+{searchResult.NewScore - searchResult.OldScore:F1})");
        }

        if (resurrected.Count > 0)
        {
            sections.Add($"漏报复活({resurrected.Count}项): {string.Join(", ", resurrected)}");
        }

        _petStore.AddReminder(new ReminderRequest
        {
            Type = "self_evolution",
            Level = ReminderLevel.Info,
            Title = "自进化报告",
            Content = string.Join("\n", sections),
            Importance = 2,
            DurationMs = 10000
        });
    }

    // ============================================================================
    // 参数持久化 - 对应 planScheduler.js loadAutoOptimizedParams / loadOptimizedParams / _syncOptimizedParams
    // ============================================================================

    /// <summary>
    /// 加载自动优化持久化参数（启动时调用）
    /// </summary>

    // ============================================================================
    // 参数持久化 - 对应 planScheduler.js loadAutoOptimizedParams / loadOptimizedParams / _syncOptimizedParams
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

            // 快速拉升阈值已排除在自进化之外（对齐 Electron v2：阈值由 Config.RapidWindows 硬编码，
            // signalEvents 统计跳过 rapid_ 前缀，不参与自动调整）。
            // 旧版本曾把阈值调至 0.65%/0.98%（默认 1%/2%）并持久化，导致小波动触发提醒
            // 并开启长冷却，压制后续真正的大幅拉升信号。版本号不匹配时删除持久化值（对齐
            // Electron localStorage.removeItem 语义，旧实现只写版本号不清值，下次启动版本匹配
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

            // 快速拉升阈值不持久化（对齐 Electron v2：硬编码不参与自动调整，
            // 保存只会让历史污染值无限延续）
            // 保存信号乘子
            var multJson = JsonConvert.SerializeObject(_sellPointDetector.GetSignalMultipliers());
            SaveConfig(conn, "pet_optimized_signal_multipliers", multJson);

            // 保存因子权重
            var weightJson = JsonConvert.SerializeObject(_multiFactorEngine.GetWeights());
            SaveConfig(conn, "pet_optimized_factor_weights", weightJson);

            Log.Information("[自进化] 参数已持久化");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[自进化] 保存参数失败");
        }
    }

    /// <summary>
    /// 同步优化参数 - 对应 planScheduler.js _syncOptimizedParams
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
