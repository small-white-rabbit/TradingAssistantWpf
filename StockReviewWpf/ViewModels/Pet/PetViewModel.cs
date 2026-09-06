using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using StockReviewWpf.Models;

namespace StockReviewWpf.ViewModels.Pet;

public partial class PetViewModel : ObservableObject
{
    [ObservableProperty]
    private string _currentSpritePath = "";

    [ObservableProperty]
    private bool _isBubbleVisible;

    [ObservableProperty]
    private string _bubbleText = "";

    [ObservableProperty]
    private bool _isPanelOpen;

    [ObservableProperty]
    private string _currentPanel = "";

    [ObservableProperty]
    private double _petScale = 1.0;

    [ObservableProperty]
    private double _petSize = 140.0;

    [ObservableProperty]
    private string _petId = "firefly--lingxiaotian";

    [ObservableProperty]
    private int _spriteVersion = 3;

    [ObservableProperty]
    private double _petOpacity = 1.0;

    [ObservableProperty]
    private double _animationSpeed = 1.0;

    [ObservableProperty]
    private string _petName = "小助手";

    public PetViewModel()
    {
        UpdateSprite();
    }

    private void UpdateSprite()
    {
    }

    [RelayCommand]
    private void ShowPlanList()
    {
        IsPanelOpen = true;
        CurrentPanel = "PlanList";
        Log.Information("[宠物] 打开计划列表面板");
    }

    [RelayCommand]
    private void ShowReminder()
    {
        IsPanelOpen = true;
        CurrentPanel = "Reminder";
    }

    [RelayCommand]
    private void ShowGallery()
    {
        IsPanelOpen = true;
        CurrentPanel = "Gallery";
    }

    [RelayCommand]
    private void ShowSettings()
    {
        IsPanelOpen = true;
        CurrentPanel = "Settings";
    }

    [RelayCommand]
    private void ShowMainWindow()
    {
        // pet-only 模式主窗可能尚未创建：EnsureMainWindow 懒创建（2026-09-06 P2）
        var main = App.EnsureMainWindow();
        main.Show();
        main.Activate();
    }

    [RelayCommand]
    private void ClosePet()
    {
        foreach (var window in App.Current.Windows)
        {
            if (window is Views.Pet.PetWindow petWindow)
            {
                petWindow.Close();
                break;
            }
        }
    }

    public void ShowBubble(string text, int durationMs = 3000)
    {
        BubbleText = text;
        IsBubbleVisible = true;

        System.Threading.Tasks.Task.Delay(durationMs).ContinueWith(_ =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                IsBubbleVisible = false;
            });
        });
    }
}