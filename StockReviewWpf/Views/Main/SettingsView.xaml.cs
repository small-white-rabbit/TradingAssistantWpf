using System.Windows;
using System.Windows.Controls;
using StockReviewWpf.ViewModels.Main;

namespace StockReviewWpf.Views.Main;

public partial class SettingsView : UserControl
{
    private readonly SettingsViewModel _vm;

    public SettingsView()
    {
        InitializeComponent();
        _vm = new SettingsViewModel();
        DataContext = _vm;
        Loaded += (_, _) => SyncPasswordBoxes();
    }

    private void SyncPasswordBoxes()
    {
        if (OcrApiKeyBox != null && _vm.OcrApiKey != OcrApiKeyBox.Password)
            OcrApiKeyBox.Password = _vm.OcrApiKey;
        if (OcrSecretKeyBox != null && _vm.OcrSecretKey != OcrSecretKeyBox.Password)
            OcrSecretKeyBox.Password = _vm.OcrSecretKey;
        if (WebDavPasswordBox != null && _vm.WebDavPassword != WebDavPasswordBox.Password)
            WebDavPasswordBox.Password = _vm.WebDavPassword;
    }

    private void OcrApiKey_PasswordChanged(object sender, RoutedEventArgs e)
        => _vm.OcrApiKey = ((PasswordBox)sender).Password;

    private void OcrSecretKey_PasswordChanged(object sender, RoutedEventArgs e)
        => _vm.OcrSecretKey = ((PasswordBox)sender).Password;

    private void WebDavPassword_PasswordChanged(object sender, RoutedEventArgs e)
        => _vm.WebDavPassword = ((PasswordBox)sender).Password;
}