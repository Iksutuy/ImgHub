using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using ImgHub.App.ViewModels;

namespace ImgHub.App.Views.Sections;

/// <summary>
/// 历史 / 累计面板（Plan A / v0.5.28）。
///
/// 设计要点（review 后确定，见 docs/plan-a-section-refactor.md §2.1）：
///   · **事件回调**而非命令绑定 —— 原处理器要读 View 层的 SelectedItems 并清选择，
///     命令化会静默丢掉这些副作用。
///   · 两套布局的差异用**依赖属性**吸收（ListHeight / ShowUnknownCost）。
///   · 暴露 <see cref="HistoryListBox"/>，让主 View 的批量选中逻辑仍能拿到 ListBox
///     （MainView.axaml.cs 的 SelectedHistoryRows / ClearListSelection 需要它）。
/// </summary>
public partial class HistoryPanel : UserControl
{
    /// <summary>列表高度。默认 double.NaN = 自适应（宽屏用 `*` 撑满）。</summary>
    public static readonly StyledProperty<double> ListHeightProperty =
        AvaloniaProperty.Register<HistoryPanel, double>(nameof(ListHeight), double.NaN);
    public double ListHeight
    {
        get => GetValue(ListHeightProperty);
        set => SetValue(ListHeightProperty, value);
    }

    /// <summary>是否显示「（含未归类旧数据 …）」。宽屏显示、窄屏不显示。</summary>
    public static readonly StyledProperty<bool> ShowUnknownCostProperty =
        AvaloniaProperty.Register<HistoryPanel, bool>(nameof(ShowUnknownCost), true);
    public bool ShowUnknownCost
    {
        get => GetValue(ShowUnknownCostProperty);
        set => SetValue(ShowUnknownCostProperty, value);
    }

    /// <summary>
    /// 内部 ListBox（供主 View 读取 SelectedItems / 清选择）。
    /// ⚠️ 不能合并成一个 —— 宽/窄各有一个实例，主 View 会同时检查两者。
    /// ⚠️ 名字不能与 XAML 的 x:Name 相同：Avalonia 的 NameGenerator 会为
    ///    `x:Name="HistoryListBox"` 自动生成同名字段，手写属性会与之冲突（CS0102）。
    /// </summary>
    public ListBox InnerList { get; }

    // ---- 事件回调：主 View 在此挂原有处理器（保留全部副作用）----
    // 注意带 sender 的两个：原处理器靠 (sender as Control)?.DataContext 取 HistoryRow，
    // 所以必须把真实触发源（MenuItem）透传出去，否则取不到行 → 右键菜单失效。
    public event EventHandler? DeleteSelectedRequested;
    public event EventHandler? RemoveSelectedRequested;
    public event EventHandler? ToggleMultiRequested;
    public event EventHandler? ImportRequested;
    public event EventHandler<object?>? RowRemoveFromListRequested;
    public event EventHandler<object?>? RowDeleteRequested;
    public event EventHandler<SelectionChangedEventArgs>? ListSelectionChanged;

    public HistoryPanel()
    {
        InitializeComponent();
        // x:Name="HistoryListBox" 由 NameGenerator 生成了同名字段，直接用它
        InnerList = HistoryListBox;
        // ⚠️ Height 的绑定写在 XAML 里（ListHeight="{CompiledBinding ...}"），
        //    不在这里 new Binding(...) —— 后者是**反射绑定**，
        //    AOT 下会触发 IL2026/IL3050 警告（CONSTRAINTS H1），且裁剪后可能失效。
    }

    private void OnDeleteSelectedClick(object? sender, RoutedEventArgs e)
        => DeleteSelectedRequested?.Invoke(this, EventArgs.Empty);

    private void OnRemoveSelectedClick(object? sender, RoutedEventArgs e)
        => RemoveSelectedRequested?.Invoke(this, EventArgs.Empty);

    private void OnToggleMultiClick(object? sender, RoutedEventArgs e)
        => ToggleMultiRequested?.Invoke(this, EventArgs.Empty);

    private void OnImportClick(object? sender, RoutedEventArgs e)
        => ImportRequested?.Invoke(this, EventArgs.Empty);

    private void OnRowRemoveFromListClick(object? sender, RoutedEventArgs e)
        => RowRemoveFromListRequested?.Invoke(this, sender);

    private void OnRowDeleteClick(object? sender, RoutedEventArgs e)
        => RowDeleteRequested?.Invoke(this, sender);

    private void OnListSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => ListSelectionChanged?.Invoke(this, e);
}

