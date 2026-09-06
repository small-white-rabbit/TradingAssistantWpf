using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StockReviewWpf.Services;

/// <summary>
/// 内存自检探针（2026-09-06 内存治理）：定位主进程内存去向。
/// 定时（30 分钟）+ 关键时机（启动/主窗隐藏/恢复）记录：
///   1. 本进程私有内存/工作集/线程/句柄 —— 任务管理器主 exe 行的主要构成
///   2. GC 托管堆（堆大小/碎片/pinned）—— 区分托管 vs native
///   3. 归属本进程的 msedgewebview2 子进程工作集合计 —— WebView2 渲染进程组
///      （2026-09-06 v2 纠偏：按父进程链归属，只统计浏览器根进程祖先是本进程的
///       msedgewebview2；机器上其他应用（如 Windows 小组件 SearchHost.exe）的
///       WebView2 进程不再误计入本应用，历史读数曾因此虚高 300~700MB）
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

            var (ownCount, ownWs, otherCount, otherWs) = SumWebView2WorkingSets(proc.Id);

            Serilog.Log.Information(
                "[内存探针] {Reason}：私有内存 {Private:N0}MB / 工作集 {Ws:N0}MB | 线程 {Threads} 句柄 {Handles} | GC堆 {Heap:N0}MB（碎片 {Frag:N0}MB， pinned {Pinned}） | 本应用 WebView2 子进程 {Count} 个合计 {WvWs:N0}MB（机器上另有其他应用 {OtherCount} 个 {OtherWs:N0}MB）",
                reason,
                proc.PrivateMemorySize64 / 1048576.0,
                proc.WorkingSet64 / 1048576.0,
                proc.Threads.Count,
                SafeHandleCount(proc),
                gc.HeapSizeBytes / 1048576.0,
                gc.FragmentedBytes / 1048576.0,
                gc.PinnedObjectsCount,
                ownCount,
                ownWs / 1048576.0,
                otherCount,
                otherWs / 1048576.0);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[内存探针] 快照失败");
        }
    }

    private static int SafeHandleCount(Process proc)
    {
        try { return proc.HandleCount; }
        catch { return -1; }
    }

    /// <summary>
    /// 统计 msedgewebview2 工作集并按父进程链归属：
    /// 浏览器根进程（--type 缺省）的父进程是本进程 → 整组（browser/gpu/renderer/
    /// utility/crashpad）计为本应用；父进程是其他进程（如 SearchHost）→ 计为其他应用。
    /// </summary>
    private static (int ownCount, long ownWs, int otherCount, long otherWs) SumWebView2WorkingSets(int ownPid)
    {
        var procs = Process.GetProcessesByName("msedgewebview2");
        if (procs.Length == 0) return (0, 0, 0, 0);

        // pid → (父 pid, 工作集)
        var info = new Dictionary<int, (int parent, long ws)>(procs.Length);
        foreach (var p in procs)
        {
            try
            {
                var parent = GetParentPid(p);
                info[p.Id] = (parent, p.WorkingSet64);
            }
            catch
            {
                // 访问被拒/进程已退出：跳过该进程，不影响其余统计
            }
            finally { p.Dispose(); }
        }

        // 浏览器根进程：直接父进程是本进程
        var browserRoots = new HashSet<int>(
            info.Where(kv => kv.Value.parent == ownPid).Select(kv => kv.Key));

        int ownCount = 0, otherCount = 0;
        long ownWs = 0, otherWs = 0;
        foreach (var kv in info)
        {
            if (BelongsToUs(kv.Key, info, browserRoots, ownPid))
            {
                ownCount++;
                ownWs += kv.Value.ws;
            }
            else
            {
                otherCount++;
                otherWs += kv.Value.ws;
            }
        }
        return (ownCount, ownWs, otherCount, otherWs);
    }

    /// <summary>沿父进程链向上爬：命中本进程或本应用浏览器根 → 属于本应用；爬到链外 → 不属于。</summary>
    private static bool BelongsToUs(
        int pid, Dictionary<int, (int parent, long ws)> info, HashSet<int> browserRoots, int ownPid)
    {
        var cur = pid;
        for (var hop = 0; hop < 10; hop++)
        {
            if (cur == ownPid || browserRoots.Contains(cur)) return true;
            if (!info.TryGetValue(cur, out var parentInfo)) return false; // 父进程不是 msedgewebview2 → 出链
            cur = parentInfo.parent;
        }
        return false;
    }

    // ===== 工作集修剪（主窗隐藏到托盘时调用）=====
    // EmptyWorkingSet 强制 OS 把本进程工作集中的页面换出（standby/pagefile）。
    // 隐藏窗口的渲染资源/视图树之后不会被 touch，页面保持换出状态 → 物理占用立降
    //（任务管理器数字立刻下降；Chrome/任务管理器最小化时同款手法）。
    // 私有提交不变、托管对象全部存活，恢复显示时按需换回，无白屏/重建成本。

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>强制修剪本进程工作集，并记录修剪前后工作集（后台安全，失败仅 Debug 日志）。</summary>
    public static void TrimWorkingSet(string reason)
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            var before = proc.WorkingSet64 / 1048576.0;
            EmptyWorkingSet(GetCurrentProcess());
            proc.Refresh();
            var after = proc.WorkingSet64 / 1048576.0;
            Serilog.Log.Information("[内存探针] {Reason}：工作集修剪 {Before:N0}MB → {After:N0}MB",
                reason, before, after);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[内存探针] 工作集修剪失败");
        }
    }

    // ===== 父进程 PID（NtQueryInformationProcess，避免引 System.Management）=====

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation,
        int processInformationLength, out int returnLength);

    private static int GetParentPid(Process p)
    {
        var pbi = new PROCESS_BASIC_INFORMATION();
        var status = NtQueryInformationProcess(
            p.Handle, 0, ref pbi, Marshal.SizeOf(pbi), out _);
        if (status != 0) throw new InvalidOperationException($"NtQueryInformationProcess 返回 0x{status:X}");
        return pbi.InheritedFromUniqueProcessId.ToInt32();
    }
}
