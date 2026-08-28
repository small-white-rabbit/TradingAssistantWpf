using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using StockReviewWpf.Services;
using StockReviewWpf.ViewModels.Main;

namespace StockReviewWpf.Views.Main;

/// <summary>
/// 交易录入表单（新增/编辑），对齐 Electron TradeForm.vue 布局。
/// DataContext 继承父级 YearMonthViewModel。
/// </summary>
public partial class TradeFormView : UserControl
{
    private YearMonthViewModel? _vm;

    // 进场类型 RadioButton 集合（按父级分组）
    private readonly Dictionary<string, List<RadioButton>> _entryTypeButtons = new();

    // 抑制 StockInputBox 程序化赋值时触发的 TextChanged 反写 VM（避免把"名称(代码)"整串当 code、清空 name）
    private bool _suppressTextChanged;

    public TradeFormView()
    {
        InitializeComponent();
        Loaded += (_, _) => AttachVm();
        Unloaded += (_, _) => DetachVm();
    }

    private void AttachVm()
    {
        if (_vm != null) return;
        _vm = DataContext as YearMonthViewModel;
        if (_vm == null) return;
        _vm.PropertyChanged += Vm_PropertyChanged;
    }

    private void DetachVm()
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= Vm_PropertyChanged;
            _vm = null;
        }
    }

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(YearMonthViewModel.ShowForm) && _vm!.ShowForm)
        {
            // 表单打开时：重建进场类型/问题标签控件，同步股票显示
            BuildEntryTypeButtons();
            BuildProblemTagCheckboxes();
            RestorePositionStatus();
            RestoreTodayPerformance();
            RestoreMeetExpectation();
            RestoreFollowUp();
            UpdateStockDisplay();
            UpdatePanelVisibility();
        }
        // 修复：OCR 识别/行情回填后 VM 的 code/name 已更新，但股票输入框由 code-behind
        // 手动设 Text（非数据绑定），不刷新则 code/name 永不显示。监听 code/name 变化，
        // 当 code 为6位（OCR/行情回填结果，非用户名称搜索场景）时刷新输入框显示。
        else if ((e.PropertyName == nameof(YearMonthViewModel.FormStockCode)
                 || e.PropertyName == nameof(YearMonthViewModel.FormStockName))
                && _vm != null
                && Regex.IsMatch(_vm.FormStockCode ?? "", @"^\d{6}$"))
        {
            UpdateStockDisplay();
        }
    }

    // ======== 股票输入（代码+名称合并） ========

    private void StockInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_vm == null || _suppressTextChanged) return;
        _vm.FormStockCode = StockInputBox.Text.Trim();
        _vm.FormStockName = "";
        StockHintBlock.Visibility = Visibility.Collapsed;
    }

    private void StockInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        if (_vm == null) return;

        var input = StockInputBox.Text.Trim();
        // 提取6位数字代码
        var codeMatch = Regex.Match(input, @"\d{6}");
        if (codeMatch.Success)
        {
            _vm.FormStockCode = codeMatch.Value;
            _vm.FormStockName = "";
        }
        else
        {
            _vm.FormStockCode = input;
            _vm.FormStockName = input; // 按名称搜索
        }
        _ = _vm.OnFormEnter();
        // 异步回填后刷新显示
        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateStockDisplay();
            RestorePositionStatus();
            UpdatePanelVisibility();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void UpdateStockDisplay()
    {
        if (_vm == null) return;
        // 修复：程序设置输入框 Text 时，抑制 TextChanged 反向回写 VM
        // （否则会把"名称(代码)"整串当作 code、并把 name 清空，污染 OCR 识别结果）
        _suppressTextChanged = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(_vm.FormStockCode) && !string.IsNullOrWhiteSpace(_vm.FormStockName))
            {
                StockInputBox.Text = $"{_vm.FormStockName} ({_vm.FormStockCode})";
                StockHintBlock.Text = $"已匹配：{_vm.FormStockName} ({_vm.FormStockCode})";
                StockHintBlock.Visibility = Visibility.Visible;
            }
            else if (!string.IsNullOrWhiteSpace(_vm.FormStockCode))
            {
                StockInputBox.Text = _vm.FormStockCode;
                StockHintBlock.Visibility = Visibility.Collapsed;
            }
            else
            {
                StockInputBox.Text = "";
                StockHintBlock.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _suppressTextChanged = false;
        }
    }

    // ======== 进场类型 RadioButton 分组 ========
    // 使用 EntryTypeTree 通用构建器，与 AddPlanDialog / Electron 版数据形态一致：
    // 根节点名作分组标题，children 非空用 children 作选项，为空则根自身作唯一选项。

    private void BuildEntryTypeButtons()
    {
        if (_vm?.EntryTypeItems == null) return;
        EntryTypePanel.Children.Clear();
        _entryTypeButtons.Clear();

        var roots = StockReviewWpf.Services.EntryTypeTree.Build(_vm.EntryTypeItems.Where(t => t.IsActive));

        foreach (var root in roots)
        {
            var groupName = root.Name;
            var children = root.Children.ToList();

            var groupStack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            var groupLabel = new TextBlock
            {
                Text = groupName + "：",
                FontWeight = System.Windows.FontWeights.Bold,
                FontSize = 12,
                Foreground = FindResource("TextRegularBrush") as System.Windows.Media.Brush,
                Margin = new Thickness(0, 0, 0, 3)
            };
            groupStack.Children.Add(groupLabel);

            var radioPanel = new WrapPanel();
            var buttonList = new List<RadioButton>();

            foreach (var item in children)
            {
                var rb = new RadioButton
                {
                    Content = item.Name,
                    Tag = item.Name,
                    Margin = new Thickness(0, 0, 12, 3),
                    FontSize = 12
                };
                rb.Checked += EntryType_Checked;
                radioPanel.Children.Add(rb);
                buttonList.Add(rb);
            }

            groupStack.Children.Add(radioPanel);
            EntryTypePanel.Children.Add(groupStack);

            if (groupName != null)
                _entryTypeButtons[groupName] = buttonList;
        }

        // 回填当前选中值
        if (!string.IsNullOrWhiteSpace(_vm?.FormEntryType))
        {
            foreach (var rb in _entryTypeButtons.Values.SelectMany(l => l))
            {
                if (rb.Tag?.ToString() == _vm.FormEntryType)
                {
                    rb.IsChecked = true;
                    break;
                }
            }
        }
    }

    private void EntryType_Checked(object sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not RadioButton rb) return;
        _vm.FormEntryType = rb.Tag?.ToString() ?? "";
    }

    // ======== 问题标签（下拉多选：ToggleButton + Popup） ========

    private void BuildProblemTagCheckboxes()
    {
        if (_vm?.ProblemTagItems == null) return;
        ProblemTagPanel.Children.Clear();

        var selectedTags = (_vm.FormProblemTags ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet();

        foreach (var tag in _vm.ProblemTagItems.Where(t => t.IsActive))
        {
            var cb = new CheckBox
            {
                Content = tag.Name,
                Tag = tag.Name,
                Margin = new Thickness(0, 0, 0, 6),
                FontSize = 13,
                IsChecked = selectedTags.Contains(tag.Name)
            };
            cb.Checked += ProblemTag_Changed;
            cb.Unchecked += ProblemTag_Changed;
            ProblemTagPanel.Children.Add(cb);
        }
        UpdateProblemTagToggleText();
    }

    private void ProblemTag_Changed(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var tags = ProblemTagPanel.Children.OfType<CheckBox>()
            .Where(c => c.IsChecked == true)
            .Select(c => c.Tag?.ToString() ?? "")
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        _vm.FormProblemTags = string.Join(",", tags);
        UpdateProblemTagToggleText();
    }

    /// <summary>按钮文本同步选中标签（无选中时显示占位提示，选中文本用主色区分）</summary>
    private void UpdateProblemTagToggleText()
    {
        var text = _vm?.FormProblemTags;
        if (string.IsNullOrWhiteSpace(text))
        {
            ProblemTagToggleText.Text = "选择问题标签";
            ProblemTagToggleText.Foreground = FindResource("TextSecondaryBrush") as System.Windows.Media.Brush;
        }
        else
        {
            ProblemTagToggleText.Text = text;
            ProblemTagToggleText.Foreground = FindResource("TextPrimaryBrush") as System.Windows.Media.Brush;
        }
    }

    private void ProblemTagToggle_Click(object sender, RoutedEventArgs e)
    {
        ProblemTagPopup.IsOpen = ProblemTagToggle.IsChecked == true;
    }

    private void ProblemTagPopup_Opened(object sender, EventArgs e)
    {
        // 弹层最小宽度对齐按钮宽度，选项少时不收缩
        ProblemTagPanel.MinWidth = Math.Max(0, ProblemTagToggle.ActualWidth - 16);
    }

    private void ProblemTagPopup_Closed(object sender, EventArgs e)
    {
        ProblemTagToggle.IsChecked = false;
    }

    // ======== 持仓状态（ComboBox 单选，默认已清仓） ========

    private void PosStatusCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || PosStatusCombo.SelectedItem is not ComboBoxItem item) return;
        var status = item.Content?.ToString();
        if (string.IsNullOrEmpty(status) || status == _vm.FormPositionStatus) return;
        _vm.FormPositionStatus = status;
        UpdatePanelVisibility();
    }

    private void RestorePositionStatus()
    {
        if (_vm == null) return;
        PosStatusCombo.SelectedIndex = _vm.FormPositionStatus switch
        {
            "首次建仓" => 2,
            "持仓中" => 1,
            _ => 0 // 已清仓（默认）
        };
        UpdatePanelVisibility();
    }

    private void UpdatePanelVisibility()
    {
        if (_vm == null) return;
        var status = _vm.FormPositionStatus;

        // 清仓信息（含首次日期+清仓日期+总收益同一行）：已清仓时显示
        ExitPanel.Visibility = status == "已清仓" ? Visibility.Visible : Visibility.Collapsed;

        // 首次日期独立行：非首次建仓且未显示清仓信息（持仓中）时显示，避免与清仓信息行重复
        FirstDatePanel.Visibility = status != "首次建仓" && ExitPanel.Visibility != Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;

        // 待办事项：持仓中且非首次日时显示
        var showTodo = status == "持仓中"
            && !string.IsNullOrWhiteSpace(_vm.FormFirstDate)
            && _vm.FormTradeDate != _vm.FormFirstDate;
        TodoPanel.Visibility = showTodo ? Visibility.Visible : Visibility.Collapsed;
    }

    // ======== 今日表现 ========

    private void TodayPerformance_Changed(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        if (PerfExcellentBtn.IsChecked == true) _vm.FormTodayPerformance = "超预期";
        else if (PerfExpectedBtn.IsChecked == true) _vm.FormTodayPerformance = "符合预期";
        else if (PerfBelowBtn.IsChecked == true) _vm.FormTodayPerformance = "低于预期";
        else if (PerfStopLossBtn.IsChecked == true) _vm.FormTodayPerformance = "止损";
        else if (PerfTakeProfitBtn.IsChecked == true) _vm.FormTodayPerformance = "止盈";
    }

    private void RestoreTodayPerformance()
    {
        if (_vm == null) return;
        PerfExcellentBtn.IsChecked = _vm.FormTodayPerformance == "超预期";
        PerfExpectedBtn.IsChecked = _vm.FormTodayPerformance == "符合预期";
        PerfBelowBtn.IsChecked = _vm.FormTodayPerformance == "低于预期";
        PerfStopLossBtn.IsChecked = _vm.FormTodayPerformance == "止损";
        PerfTakeProfitBtn.IsChecked = _vm.FormTodayPerformance == "止盈";
    }

    // ======== 符合预期 ========

    private void MeetExpectation_Changed(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        if (MeetYesBtn.IsChecked == true) _vm.FormMeetExpectation = "是";
        else if (MeetNoBtn.IsChecked == true) _vm.FormMeetExpectation = "否";
        else if (MeetPartialBtn.IsChecked == true) _vm.FormMeetExpectation = "部分符合";
    }

    private void RestoreMeetExpectation()
    {
        if (_vm == null) return;
        MeetYesBtn.IsChecked = _vm.FormMeetExpectation == "是";
        MeetNoBtn.IsChecked = _vm.FormMeetExpectation == "否";
        MeetPartialBtn.IsChecked = _vm.FormMeetExpectation == "部分符合";
    }

    // ======== 后续追踪 ========

    private void FollowUp_Changed(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var items = new List<string>();
        if (FollowExpectedBtn.IsChecked == true) items.Add("符合预期");
        if (FollowDeviatedBtn.IsChecked == true) items.Add("背离预期");
        if (FollowOverBtn.IsChecked == true) items.Add("超预期");
        if (FollowUnderBtn.IsChecked == true) items.Add("低于预期");
        _vm.FormFollowUp = string.Join(",", items);
    }

    private void RestoreFollowUp()
    {
        if (_vm == null) return;
        var tags = (_vm.FormFollowUp ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet();
        FollowExpectedBtn.IsChecked = tags.Contains("符合预期");
        FollowDeviatedBtn.IsChecked = tags.Contains("背离预期");
        FollowOverBtn.IsChecked = tags.Contains("超预期");
        FollowUnderBtn.IsChecked = tags.Contains("低于预期");
    }

    // ======== 价格联动（收盘价/最高价 + 前收价 → 自动计算涨跌幅、最大涨幅） ========

    private void FormPrice_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_vm == null) return;
        // 前收价从 ViewModel 内部获取（FormPrevClose 仍保留在 VM 中但不显示）
        if (!double.TryParse(_vm.FormPrevClose, out var prev) || prev <= 0) return;

        // 涨跌幅 = (收盘价 - 前收) / 前收 * 100：用户改收盘价或行情回填收盘价时实时重算
        // （修复「涨跌幅空白」的联动补充——即使 data.ChangePct 缺失也能由收盘价反推）
        if (double.TryParse(FormClosePriceBox.Text, out var close) && close > 0)
        {
            _vm.FormChangePct = Math.Round((close - prev) / prev * 100, 2).ToString("F2");
        }

        // 最大涨幅 = (最高价 - 前收) / 前收 * 100
        if (double.TryParse(FormHighPriceBox.Text, out var high) && high > 0)
        {
            _vm.FormMaxChangePct = Math.Round((high - prev) / prev * 100, 2).ToString("F2");
        }
    }

    // ======== 日期变更 ========

    private void FormTradeDate_Changed(object sender, EventArgs e)
    {
        if (_vm != null)
            _ = _vm.AutoFetchStockData();
    }

    // ======== 保存并继续 ========

    private void SaveAndContinue_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.SaveTradeCommand.Execute(null);
        // 保存后重新打开新增表单（保留日期）
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_vm == null) return;
            var savedDate = _vm.FormTradeDate;
            _vm.AddTradeCommand.Execute(savedDate);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }
}