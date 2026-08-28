using System.Windows;

namespace StockReviewWpf.Controls;

/// <summary>
/// 按钮选中态标记（附加属性）：
/// 供 TypeOptionButton 样式的模板触发器识别选中态。
/// 视图层用 DataTrigger 设置 ui:ButtonHelper.IsSelected="True"，
/// 而不是直接改 Background/Foreground——避免与模板内置
/// hover/pressed 触发器（模板触发器优先级更高）冲突导致
/// 「选中按钮悬停/按下时文字看不清」。
/// </summary>
public static class ButtonHelper
{
    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.RegisterAttached("IsSelected", typeof(bool), typeof(ButtonHelper),
            new PropertyMetadata(false));

    public static bool GetIsSelected(DependencyObject obj) => (bool)obj.GetValue(IsSelectedProperty);

    public static void SetIsSelected(DependencyObject obj, bool value) => obj.SetValue(IsSelectedProperty, value);
}
