namespace StockReviewWpf.Views;

/// <summary>
/// 视图驱逐时释放重型资源（ScottPlot 位图、WebView2 进程等）的钩子。
/// 由 MainViewModel.TryDisposeView 在 LRU 驱逐时调用——
/// 与 Unloaded（每次导航离开都触发）区分开，避免缓存视图被误清。
/// </summary>
public interface IHeavyResourceView
{
    /// <summary>释放图表/原生资源并解除事件订阅。调用后视图不可再复用。</summary>
    void ReleaseHeavyResources();
}
