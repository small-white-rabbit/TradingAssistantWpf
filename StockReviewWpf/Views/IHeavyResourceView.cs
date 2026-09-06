using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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

/// <summary>
/// 主窗隐藏到托盘 / 恢复显示时的截图驻留生命周期（2026-09-06 P1 内存治理）。
/// 窗口 Hide 不触发视图 Unloaded，截图字符串与解码位图会整段驻留隐藏期；
/// 隐藏时由 MainViewModel.ReleaseWebViewsOnHide 统一释放，恢复显示时仅对
/// 当前激活视图重载已 realize 卡片的截图（其余缓存视图导航回来时 Image.Loaded 自动重载）。
/// </summary>
public interface ITrayScreenshotLifecycle
{
    /// <summary>清空全部截图字符串驻留（绑定同步置空 Image.Source，位图失去引用）。</summary>
    void ReleaseTransientScreenshots();

    /// <summary>对当前可视树中已 realize 的卡片重新触发截图懒加载。</summary>
    void ReloadVisibleScreenshots();
}

/// <summary>可视树遍历小工具：隐藏/恢复截图治理用（Collapsed 节点无可视子级，天然跳过）。</summary>
internal static class VisualTreeUtil
{
    /// <summary>枚举 root 可视子树内全部 Image（含弹层内节点；Collapsed 子树不生成可视节点，不会被枚举）。</summary>
    public static IEnumerable<Image> EnumerateImages(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Image img) yield return img;
            foreach (var descendant in EnumerateImages(child))
                yield return descendant;
        }
    }
}
