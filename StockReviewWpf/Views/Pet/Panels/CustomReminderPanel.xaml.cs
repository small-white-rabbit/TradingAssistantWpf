using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Serilog;
using StockReviewWpf.Models;
using StockReviewWpf.ViewModels.Pet;
using CoreReminderSvc = StockReview.Core.Services.CustomRemindersService;
using CoreReminderScheduler = StockReview.Core.Services.CustomReminderSchedulerService;

namespace StockReviewWpf.Views.Pet.Panels;

public partial class CustomReminderPanel : UserControl
{
    private CustomReminderPanelViewModel _viewModel = null!;
    private CustomReminder? _editingReminder;
    private CoreReminderScheduler? _scheduler;

    public CustomReminderPanel()
    {
        InitializeComponent();
        var reminderService = App.Host?.Services.GetService(typeof(CoreReminderSvc)) as CoreReminderSvc;
        _scheduler = App.Host?.Services.GetService(typeof(CoreReminderScheduler)) as CoreReminderScheduler;
        Log.Information("[CustomReminderPanel] 构造: reminderService={(reminderService != null)} scheduler={(scheduler != null)} count={Count}",
            reminderService != null, _scheduler != null, reminderService?.Reminders.Count ?? 0);
        _viewModel = new CustomReminderPanelViewModel(reminderService);
        DataContext = _viewModel;
        Log.Information("[CustomReminderPanel] 构造完成: HasData={HasData}", _viewModel.HasData);

        for (int h = 0; h < 24; h++) HourBox.Items.Add(h.ToString("D2"));
        for (int m = 0; m < 60; m++) MinuteBox.Items.Add(m.ToString("D2"));
    }

    public void RefreshData()
    {
        _viewModel.LoadFromService();
    }

    private void RefreshScheduler()
    {
        _scheduler?.RefreshSchedule();
    }

    // ====== 列表操作 ======

    private void AddReminder_Click(object sender, RoutedEventArgs e)
    {
        _editingReminder = null;
        OpenDialog();
    }

    private void EditReminder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is CustomReminder reminder)
        {
            _editingReminder = reminder;
            OpenDialog();
        }
    }

    private void DeleteReminder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is CustomReminder reminder)
        {
            _viewModel.Reminders.Remove(reminder);
            _viewModel.UpdateCounts();
            _viewModel.SaveToService();
            RefreshScheduler();
        }
    }

    private void ToggleEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is CustomReminder reminder)
        {
            reminder.Enabled = cb.IsChecked == true;
            _viewModel.UpdateCounts();
            _viewModel.SaveToService();
            RefreshScheduler();
        }
    }

    // ====== 弹窗操作 ======

    private void OpenDialog()
    {
        var isEdit = _editingReminder != null;
        DialogTitle.Text = isEdit ? "编辑提醒" : "添加提醒";

        if (isEdit)
        {
            var r = _editingReminder!;
            TitleBox.Text = r.Title;
            SetTime(r.Time);
            DatePicker.SelectedDate = string.IsNullOrEmpty(r.Date) ? (DateTime?)null : DateTime.TryParse(r.Date, out var d) ? d : null;
            RepeatBurstBox.Text = r.RepeatBurstCount.ToString();
            StockCodeBox.Text = r.StockCode ?? "";
            StockNameBox.Text = r.StockName ?? "";
            ContentBox.Text = r.Content;

            TypeOnce.IsChecked = r.Type == "once";
            TypeDaily.IsChecked = r.Type == "daily";
            TypeWeekly.IsChecked = r.Type == "weekly";

            ChkMon.IsChecked = r.Weekdays.Contains(1);
            ChkTue.IsChecked = r.Weekdays.Contains(2);
            ChkWed.IsChecked = r.Weekdays.Contains(3);
            ChkThu.IsChecked = r.Weekdays.Contains(4);
            ChkFri.IsChecked = r.Weekdays.Contains(5);
            ChkSat.IsChecked = r.Weekdays.Contains(6);
            ChkSun.IsChecked = r.Weekdays.Contains(0);

            // 动作按钮回显（原版：(reminder.actions || DEFAULT_ACTIONS).map(a => a.type)，默认全选）
            var actionTypes = (r.Actions ?? CoreReminderSvc.DefaultActions).Select(a => a.Type).ToList();
            ChkDoneAction.IsChecked = actionTypes.Contains("custom_done");
            ChkSnoozeAction.IsChecked = actionTypes.Contains("custom_snooze");
        }
        else
        {
            TitleBox.Text = "";
            SetTime("09:00");
            DatePicker.SelectedDate = DateTime.Today;
            RepeatBurstBox.Text = "1";
            StockCodeBox.Text = "";
            StockNameBox.Text = "";
            ContentBox.Text = "";
            TypeOnce.IsChecked = true;
            ChkMon.IsChecked = ChkTue.IsChecked = ChkWed.IsChecked = ChkThu.IsChecked = true;
            ChkFri.IsChecked = ChkSat.IsChecked = ChkSun.IsChecked = false;
            ChkDoneAction.IsChecked = ChkSnoozeAction.IsChecked = true;
        }

        UpdateTypeUI();
        DialogOverlay.Visibility = Visibility.Visible;
    }

    private void CloseDialog_Click(object sender, RoutedEventArgs e)
    {
        DialogOverlay.Visibility = Visibility.Collapsed;
        _editingReminder = null;
    }

    private void SaveReminder_Click(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        if (string.IsNullOrEmpty(title))
        {
            MessageBox.Show("请输入提醒标题", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var reminder = _editingReminder ?? new CustomReminder();
        reminder.Title = title;
        reminder.Time = GetTime();
        reminder.Date = DatePicker.SelectedDate?.ToString("yyyy-MM-dd");
        reminder.RepeatBurstCount = int.TryParse(RepeatBurstBox.Text, out var n) && n >= 1 ? Math.Min(n, 5) : 1;
        reminder.StockCode = StockCodeBox.Text.Trim();
        reminder.StockName = StockNameBox.Text.Trim();
        reminder.Content = ContentBox.Text.Trim();

        if (TypeOnce.IsChecked == true) reminder.Type = "once";
        else if (TypeDaily.IsChecked == true) reminder.Type = "daily";
        else reminder.Type = "weekly";

        reminder.Weekdays = new System.Collections.ObjectModel.ObservableCollection<int>();
        if (ChkMon.IsChecked == true) reminder.Weekdays.Add(1);
        if (ChkTue.IsChecked == true) reminder.Weekdays.Add(2);
        if (ChkWed.IsChecked == true) reminder.Weekdays.Add(3);
        if (ChkThu.IsChecked == true) reminder.Weekdays.Add(4);
        if (ChkFri.IsChecked == true) reminder.Weekdays.Add(5);
        if (ChkSat.IsChecked == true) reminder.Weekdays.Add(6);
        if (ChkSun.IsChecked == true) reminder.Weekdays.Add(0);

        // 动作按钮（原版：actions = DEFAULT_ACTIONS.filter(a => selectedActionTypes.includes(a.type))）
        var actions = new List<StockReview.Core.Services.ReminderAction>();
        if (ChkDoneAction.IsChecked == true)
            actions.Add(new StockReview.Core.Services.ReminderAction { Type = "custom_done", Label = "完成" });
        if (ChkSnoozeAction.IsChecked == true)
            actions.Add(new StockReview.Core.Services.ReminderAction { Type = "custom_snooze", Label = "稍后提醒" });
        reminder.Actions = actions.Count > 0 ? actions : null;

        if (_editingReminder == null)
            _viewModel.Reminders.Add(reminder);

        _viewModel.UpdateCounts();
        _viewModel.SaveToService();
        RefreshScheduler();
        DialogOverlay.Visibility = Visibility.Collapsed;
        _editingReminder = null;
    }

    private void Type_Changed(object sender, RoutedEventArgs e)
    {
        if (TypeOnce == null || TypeWeekly == null) return;
        UpdateTypeUI();
    }

    private void UpdateTypeUI()
    {
        var isOnce = TypeOnce.IsChecked == true;
        var isWeekly = TypeWeekly.IsChecked == true;
        DatePanel.Visibility = isOnce ? Visibility.Visible : Visibility.Collapsed;
        WeekdayPanel.Visibility = isWeekly ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetTime(string time)
    {
        if (time.Length >= 5 && time[2] == ':')
        {
            HourBox.SelectedItem = time[..2];
            MinuteBox.SelectedItem = time[3..];
        }
        else
        {
            HourBox.SelectedIndex = 9;
            MinuteBox.SelectedIndex = 0;
        }
    }

    private string GetTime()
    {
        var h = (HourBox.SelectedItem as string) ?? "09";
        var m = (MinuteBox.SelectedItem as string) ?? "00";
        return $"{h}:{m}";
    }
}