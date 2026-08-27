using System.Windows.Controls;
using System.Windows.Input;
using StockReviewWpf.ViewModels.Pet;

namespace StockReviewWpf.Views.Pet.Panels;

public partial class PetSettingsPanel : UserControl
{
    private PetSettingsPanelViewModel _viewModel = null!;

    public PetSettingsPanel()
    {
        InitializeComponent();
        _viewModel = new PetSettingsPanelViewModel();
        DataContext = _viewModel;
    }

    private void SourceCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement fe && fe.DataContext is SourceOption option)
        {
            _viewModel.SelectSourceCommand.Execute(option.Key);
        }
    }

    // 恢复默认：确认后重置（对齐原版 ElMessageBox.confirm）
    private void ResetButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(this),
            "确定要恢复默认设置吗？", "确认",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result == System.Windows.MessageBoxResult.Yes)
            _viewModel.ResetSettingsCommand.Execute(null);
    }

    // 取消：丢弃未保存修改并关闭面板（对齐原版 handleClose）
    private void CancelButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _viewModel.CancelCommand.Execute(null);
        CloseHostPanel();
    }

    // 保存：持久化并关闭面板（对齐原版 handleSave → handleClose）
    private void SaveButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _viewModel.SaveSettingsCommand.Execute(null);
        CloseHostPanel();
    }

    private void CloseHostPanel()
    {
        (System.Windows.Window.GetWindow(this) as PetPanelWindow)?.RequestClose();
    }
}
