using System.Diagnostics;

namespace StockReviewWpf.Services;

/// <summary>
/// 内存自检探针（2026-09-06 内存治理）：定位"主进程 900M"的去向。
/// 定时（30 分钟）+ 关键时机（启动/主窗隐藏/恢复）记录：
///   1. 本进程私有内存/工作集 —— 任务管理器主 exe 行的主要构成
///   2. GC 托管堆（堆大小/提交量/碎片）—— 区分托管 vs native
///   3. 全部 msedgewebview2 子进程工作集合计 —— WebView2 渲染进程组
///      （注意：按进程名汇总，若机器上有其他应用的 WebView2 会一并计入，
///       与任务管理器对照时以个数为参考）
/// 日志级别 Information，搜 "[内存探针]" 可追溯内存随时间的曲线。
/// </summary>
public static class MemoryProbe
{
    private const int IntervalMs = 30 * 60 * 1000;

    private static readonly System.Threading.Timer _timer =
        new(_ => LogSnapshot("定时"), null, IntervalMs, IntervalMs);

    /// <summary>记录一次内存快照（后台线程执行，读进程信息不碰 UI）</summary>
    public static void LogSnapshot(string reason)
    {
        _ = System.Threading.Tasks.Task.Run(() => Collect(reason));
    }

    private static void Collect(string reason)
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            var gc = GC.GetGCMemoryInfo();

            // WebView2 子进程合计（按进程名，含其他应用实例，见类注释）
            long webviewWs = 0;
            int webviewCount = 0;
            foreach (var p in Process.GetProcessesByName("msedgewebview2"))
            {
                try { webviewWs += p.WorkingSet64; webviewCount++; }
                catch { /* 进程已退出 */ }
                finally { p.Dispose(); }
            }

            Serilog.Log.Information(
                "[内存探针] {Reason}：私有内存 {Private:N0}MB / 工作集 {Ws:N0}MB | GC堆 {Heap:N0}MB（碎片 {Frag:N0}MB， pinned {Pinned}） | WebView2 子进程 {Count} 个合计 {WvWs:N0}MB",
                reason,
                proc.PrivateMemorySize64 / 1048576.0,
                proc.WorkingSet64 / 1048576.0,
                gc.HeapSizeBytes / 1048576.0,
                gc.FragmentedBytes / 1048576.0,
                gc.PinnedObjectsCount,
                webviewCount,
                webviewWs / 1048576.0);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[内存探针] 快照失败");
        }
    }
}
