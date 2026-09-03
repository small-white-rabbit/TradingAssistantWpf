using System;
using System.Collections.Generic;
using System.Text.Json;
using StockReview.Core.Data;

namespace StockReviewWpf.Services;

/// <summary>
/// 自适应预热：记录用户各视图导航频次（计数 + 近因衰减），为 <see cref="ViewModels.Main.MainViewModel.PreWarmViewCache"/>
/// 提供按使用习惯排序的预热集合——常用功能提高载入优先级，低频功能适当跳过，总体载入速度受双重约束兜底。
/// </summary>
/// <remarks>
/// 数据落在 SQLite <c>appConfig</c> 表的 <c>"viewUsage"</c> 键（JSON），与 SettingsService 拥有 "settings" 键、
/// SignalEventService 拥有多个键的模式一致。会话 = 一次 app 运行：启动加载历史滑动窗口，运行期内存累计（零 IO），
/// 退出 <see cref="FlushSession"/> 落盘。评分采用最近10次会话的滑动窗口 + 0.7 衰减因子，最近会话权重最高。
/// </remarks>
public class ViewUsageService
{
    private readonly IDatabaseService _db;

    // === 可调参数（实测后校准）===
    /// <summary>近因衰减因子：每往旧一次会话，权重乘以此值。</summary>
    public const double Decay = 0.7;
    /// <summary>滑动窗口会话数：仅保留最近 N 次会话的导航计数。</summary>
    public const int WindowSize = 10;
    /// <summary>自适应激活阈值：累计会话数达到此值后自适应策略接管（否则走冷启动默认）。</summary>
    public const int ActivationSessions = 3;
    /// <summary>预热页数上限：覆盖全部导航页（8 页）——预热后任何首次导航都是缓存命中（3-30ms），
    /// 未覆盖的页面首次进入仍是 UI 线程同步构造（实测 300-800ms 卡顿，2026-09-03 日志）。</summary>
    public const int MaxPrewarmPages = 8;
    /// <summary>预热墙钟上限（毫秒，含 ApplicationIdle 让出等待），仅作异常兜底。
    /// 注意不能用小值：空闲让出的等待时间也计入墙钟，交易时段推送/定时器频繁占用调度器时
    /// ApplicationIdle 长时间不来，小墙钟预算会让预热在没做任何工作时就超时终止（旧值 2500 的教训）。</summary>
    public const int MaxPrewarmMs = 30000;
    /// <summary>预热实际 UI 工作量上限（毫秒）：仅累计「创建视图 + 强制布局」的真实耗时，
    /// 不含空闲等待。这是真正的预算约束——8 页全量预热约 2-3s UI 工作量，空闲分摊不影响交互。</summary>
    public const int MaxPrewarmWorkMs = 8000;

    private const string ConfigKey = "viewUsage";

    // 滑动窗口：index 0 = 最旧，last = 最近一次会话
    private List<Dictionary<string, int>> _window = new();
    private int _sessionCount;

    // 本次会话内存计数（运行期累加，退出落盘）
    private readonly Dictionary<string, int> _currentSession = new();

    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    public ViewUsageService(IDatabaseService db)
    {
        _db = db;
        Load();
    }

    /// <summary>
    /// 启动加载历史滑动窗口。缺失/损坏自愈为空（沿用 PetSettingsStore.Load 自愈模式），
    /// 不抛异常以保证启动流程不被使用统计阻断。
    /// </summary>
    private void Load()
    {
        try
        {
            var row = _db.GetById("appConfig", ConfigKey);
            if (row != null
                && row.TryGetValue("value", out var v) && v is string json
                && !string.IsNullOrWhiteSpace(json))
            {
                var data = JsonSerializer.Deserialize<ViewUsageData>(json);
                if (data != null)
                {
                    _window = data.Window ?? new List<Dictionary<string, int>>();
                    _sessionCount = data.SessionCount;
                }
            }
        }
        catch
        {
            // 损坏自愈为空窗口，不阻断启动
        }
    }

    /// <summary>
    /// 导航埋点：仅更新内存计数（零 IO，可高频调用）。
    /// 由各 NavigateToXxx 在 SetCurrentView 之前调用。
    /// </summary>
    public void RecordNavigation(string viewKey)
    {
        if (string.IsNullOrEmpty(viewKey)) return;
        _currentSession[viewKey] = _currentSession.TryGetValue(viewKey, out var c) ? c + 1 : 1;
    }

    /// <summary>
    /// 退出落盘：把本次会话计数 append 进窗口（超 WindowSize 则 drop 最旧），sessionCount++，
    /// INSERT OR REPLACE 写回 appConfig。本地 DB 写入，不依赖网络。
    /// </summary>
    public void FlushSession()
    {
        try
        {
            _window.Add(new Dictionary<string, int>(_currentSession));
            while (_window.Count > WindowSize) _window.RemoveAt(0);
            _sessionCount++;

            var data = new ViewUsageData { Window = _window, SessionCount = _sessionCount };
            var json = JsonSerializer.Serialize(data, _jsonOpts);
            _db.Put("appConfig", new Dictionary<string, object?>
            {
                ["key"] = ConfigKey,
                ["value"] = json,
            });
        }
        catch
        {
            // 落盘失败不影响退出流程
        }
    }

    /// <summary>自适应是否激活（累计会话数 ≥ 阈值）。false 时 PreWarmViewCache 走冷启动默认。</summary>
    public bool IsAdaptiveActive => _sessionCount >= ActivationSessions;

    /// <summary>
    /// 计算某视图的近因衰减分数：
    /// <c>Score = Σ_{i} window[i].count(view) × Decay^(距最近会话的步数)</c>，
    /// 最近会话权重 1.0，10 次前 ≈ 0.040 几近淘汰。窗口不足时缺失会话按 0 计。
    /// </summary>
    public double GetScore(string viewKey)
    {
        double score = 0;
        var n = _window.Count;
        for (var i = 0; i < n; i++)
        {
            // i=0 最旧 → 距最近会话步数 = n-1-i；权重 = Decay^(n-1-i)
            var weight = Math.Pow(Decay, n - 1 - i);
            if (_window[i].TryGetValue(viewKey, out var cnt))
                score += cnt * weight;
        }
        return score;
    }

    private class ViewUsageData
    {
        public List<Dictionary<string, int>> Window { get; set; } = new();
        public int SessionCount { get; set; }
    }
}
