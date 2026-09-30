using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ImgHub.App.Controls;
using ImgHub.App.ViewModels;
using ImgHub.Core;
using ImgHub.Core.Diagnostics;

namespace ImgHub.App.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        RegionLayer.RegionCountChanged += (_, n) =>
        {
            if (DataContext is MainViewModel vm) vm.RegionCount = n;
        };
        // 窄屏布局有独立的画布实例，事件也要接（两套布局共用同一个 ViewModel 状态）
        RegionLayerNarrow.RegionCountChanged += (_, n) =>
        {
            if (DataContext is MainViewModel vm) vm.RegionCount = n;
        };

        // 订阅 PreviewPath 变化 → 实现「标注跟随图片」
        DataContextChanged += (_, _) =>
        {
            if (Vm is { } vm)
            {
                vm.PropertyChanged -= OnVmPropertyChanged;
                vm.PropertyChanged += OnVmPropertyChanged;
                _lastPreviewPath = vm.PreviewPath;

                // v0.5.29 修问题 5（贴底不自动滚动）：
                // ⚠️ 原实现在**构造函数**里订阅 Messages.CollectionChanged，
                //    但那时 DataContext 还是 null（由父级 MainWindow 在 XAML 加载时注入，
                //    晚于子控件构造）→ `if (DataContext is MainViewModel vm)` 判空失败 →
                //    **订阅从未生效**，于是新日志不滚动。
                //    改到这里订阅（DataContext 已就绪），并先解绑避免重复订阅。
                vm.Messages.CollectionChanged -= OnMessagesChanged;
                vm.Messages.CollectionChanged += OnMessagesChanged;

                // v0.5.31：指南浮窗用 Markdown 渲染（内容随标签页切换）
                RenderGuide(vm);

                // 图片内容矩形（Uniform letterbox 基准）在启动时初始化一次；
                // 布局完成前 Bounds 可能为 0，故用 Background 优先级延后一拍。
                SyncPreviewTransform(Vm?.IsWideLayout != false);
            }
        };

        // v5.25.0：缩放/平移同步到底图 —— 否则只有标注在动，
        // 用户看到的现象是「缩放 / 复位完全不起效」。
        RegionLayer.ViewChanged += (_, _) => SyncPreviewTransform(wide: true);
        RegionLayerNarrow.ViewChanged += (_, _) => SyncPreviewTransform(wide: false);

        // ⭐ v0.5.33 根治「缩放偏移」：让画布**自动跟随**底图 Bounds。
        //   之前靠若干时机手工对齐（DataContext / PreviewPath / PreviewImage / resize /
        //   布局切换 / 进出编辑模式），漏掉任一个就偏 —— 这就是反复没修好的原因。
        //   改为订阅 Bounds 后，位图异步就绪、resize、布局切换全都自动生效。
        WireCanvasFollow();

        // Plan A：两个历史面板共用同一 Section 实现，事件在此接到原有处理器
        //（保留读 SelectedItems / 清选择等 View 层副作用）
        WireHistoryPanel(WideHistory);
        WireHistoryPanel(NarrowHistory);

        // v0.5.29：消息区自动滚动**只在这里接线**（ScrollChanged 不依赖 DataContext）；
        // 但 Messages.CollectionChanged 的订阅移到 DataContextChanged
        //（构造函数里 DataContext 还是 null，订阅会静默失败 —— 见该方法内注释）。
        SetupMessageAutoScroll();
    }

    /// <summary>是否跟随最新消息（用户停留在底部时为 true）。</summary>
    private bool _stickToBottom = true;

    /// <summary>
    /// 重建指南浮窗内容（v0.5.31）：
    /// 把 <see cref="PromptGuide.Guide"/> 的每一节/每一条用 <see cref="MarkdownText"/>
    /// 渲染成控件树，塞进 XAML 里的 <c>GuideBody</c> 容器。
    ///
    /// ⚠️ 为什么用 code-behind 而不是纯 XAML：
    ///   Markdown 需要**运行时解析成 Inlines**（粗体/行内代码/列表），XAML 里无法表达；
    ///   而写一个 IValueConverter 也要在转换器里做同样的事，还多一层绑定开销。
    ///   直接把控件树建出来最直白，也便于把 Good/Bad 示例一起渲染。
    /// </summary>
    private void RenderGuide(MainViewModel vm)
    {
        if (GuideBody is null) return;
        try
        {
            var guide = vm.CurrentGuide;
            GuideBody.Children.Clear();

            // 来源说明（副标题）
            if (!string.IsNullOrWhiteSpace(guide.Subtitle))
            {
                var sub = new TextBlock
                {
                    Text = guide.Subtitle,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11.5,
                };
                BindBrush(sub, TextBlock.ForegroundProperty, "AppTextDimBrush");
                GuideBody.Children.Add(sub);
            }

            foreach (var section in guide.Sections)
            {
                var stack = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 10) };

                // 小节标题（用 MarkdownText 渲染，支持标题里的 **粗体**）
                stack.Children.Add(MarkdownText.Render(
                    "**" + section.Heading + "**", fontSize: 13.5,
                    foregroundKey: "AppAccentBrush", lineHeight: 20));

                foreach (var item in section.Items)
                {
                    stack.Children.Add(MarkdownText.Render(
                        item.Text, fontSize: 13,
                        foregroundKey: "AppTextBrush", lineHeight: 21));

                    // 正例 / 反例（官方示例），各自一个浅底框
                    AddExample(stack, item.Good, "AppOkBrush");
                    AddExample(stack, item.Bad, "AppWarnBrush");
                }
                GuideBody.Children.Add(stack);
            }
        }
        catch (Exception ex)
        {
            // 渲染失败不能让浮窗空白（用户会以为按钮坏了）→ 退化显示纯文本
            AppLog.Warn("指南渲染失败，退化为纯文本", "MainView.RenderGuide", ex: ex);
            GuideBody.Children.Clear();
            GuideBody.Children.Add(new TextBlock
            {
                Text = vm.CurrentGuide.Title, TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    /// <summary>加一个示例框（Good/Bad）。内容为空就不加（不留空框）。</summary>
    private void AddExample(Panel host, string? text, string brushKey)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var border = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 5),
            Child = MarkdownText.Render(text, fontSize: 12,
                                        foregroundKey: brushKey, lineHeight: 19),
        };
        // 底色同样走 DynamicResource（与主题联动）—— 不在这里查画刷实例
        BindBrush(border, Border.BackgroundProperty, "AppSurfaceBrush");
        host.Children.Add(border);
    }

    /// <summary>
    /// 把控件属性绑到主题资源键。
    ///
    /// ⚠️ 为什么用 <c>DynamicResource</c> 而不是 <c>TryFindResource</c>（v0.5.31 真实故障）：
    ///   本方法在 <c>DataContextChanged</c> 阶段调用，那时控件**还没挂到窗口可视树**，
    ///   而画刷定义在 <c>App.axaml</c> 的 <c>ThemeDictionaries</c>（Light/Dark）里 →
    ///   <c>TryFindResource</c> 返回 false → <c>Foreground = null</c> →
    ///   **Avalonia 不绘制文字**，表现为「浮窗几乎不显示内容」。
    ///   DynamicResource 是延迟求值，挂树后自然解析，还会跟随主题切换。
    /// </summary>
    private static void BindBrush(Control c,
                                  AvaloniaProperty<IBrush?> prop, string resourceKey)
        => c.Bind(prop,
                  new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(resourceKey));

    /// <summary>
    /// 消息区自动滚动（v0.5 用户要求）：
    ///   · 当前**已在最低端** → 新消息插入后自动滚到底显示新消息；
    ///   · **不在最低端**（用户正在回看历史）→ 不滚动，避免打断阅读；
    ///   · 用户手动滚回底部 → 自动恢复跟随。
    /// </summary>
    private void SetupMessageAutoScroll()
    {
        // 监听滚动位置：判断"是否贴底"（阈值 24px ≈ 一行高度）
        // ⚠️ 这里**只**处理 ScrollChanged —— 它挂在自身的 x:Name 控件上，不依赖 DataContext。
        //    Messages.CollectionChanged 的订阅在 DataContextChanged 里做（见构造函数）。
        MessageScroll.ScrollChanged += (_, _) =>
        {
            try
            {
                var sv = MessageScroll;
                var distanceToBottom =
                    sv.Extent.Height - (sv.Offset.Y + sv.Viewport.Height);
                _stickToBottom = distanceToBottom <= 24;
            }
            catch { /* 忽略：不影响主流程 */ }
        };
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (!_stickToBottom) return;      // 用户不在底部 → 不打扰

        // 用 Post 等布局更新完成后再滚（否则 Extent 还是旧值）
        Dispatcher.UIThread.Post(() =>
        {
            try { MessageScroll.ScrollToEnd(); }
            catch (Exception ex) { Core.Diagnostics.AppLog.Debug("消息面板自动滚动失败", ex, where: "MainView.ScrollMessages"); }
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 把 RegionCanvas 的视图矩阵同步给底图 `Image`。
    ///
    /// ⚠️ 坐标系分工（v0.5.34，用户澄清的语义）：
    ///   · **画布 Bounds** = 整个预览灰区（可**查看**范围 → 不裁标注）；
    ///   · **画布 `ImageContentRect`** = 图片矩形（可**绘制**范围 + 坐标基准）；
    ///   · 图片层套 <see cref="RegionCanvas.ImageMatrix"/>（不含图片原点偏移，
    ///     因为 `Image` 已被布局放在图片矩形左上角）。
    /// </summary>
    private void SyncPreviewTransform(bool wide)
    {
        try
        {
            var canvas = wide ? RegionLayer : RegionLayerNarrow;
            var image = wide ? PreviewLayer : PreviewLayerNarrow;
            if (canvas is null || image is null) return;

            // RenderTransform 原点必须是左上角（默认 50%,50% 会多一层 T(o)·M·T(-o)）
            image.RenderTransformOrigin = RelativePoint.TopLeft;

            var m = canvas.ImageMatrix;
            image.RenderTransform = m.IsIdentity ? null : new MatrixTransform(m);
        }
        catch { /* 同步失败不影响主流程 */ }
    }

    /// <summary>
    /// 一次性接线：让画布的 <see cref="RegionCanvas.ImageContentRect"/> **自动跟随**
    /// 底图 `Image` 的 Bounds（含宽高 + 图片在灰区中的位置）。
    ///
    /// 为什么用订阅而不是"在若干时机手工调"（v0.5.33 的教训）：
    ///   时机有 DataContext 注入 / PreviewPath 变化 / PreviewImage 解码完成 /
    ///   窗口 resize / 宽窄布局切换 / 进出编辑模式 —— 漏掉任一个就会偏。
    ///   订阅 `Image.Bounds` 后，这些时机全部自动覆盖。
    /// </summary>
    private void WireCanvasFollow()
    {
        try
        {
            FollowImage(RegionLayer, PreviewLayer);
            FollowImage(RegionLayerNarrow, PreviewLayerNarrow);
        }
        catch { /* 接线失败不影响主流程 */ }

        static void FollowImage(ImgHub.App.Controls.RegionCanvas canvas, Image image)
        {
            // DataContext 与位图都是异步就绪的 → 用 Dispatcher 兜一拍，
            // 之后靠 Bounds 变化驱动（位图就绪 / resize / 布局切换都会触发）。
            void Apply() => canvas.ImageContentRect = image.Bounds;

            image.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.BoundsProperty ||
                    e.Property == Layoutable.WidthProperty ||
                    e.Property == Layoutable.HeightProperty)
                    Apply();
            };
            Apply();
        }
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>上一次预览的图片路径（用于切换时保存/恢复标注）。</summary>
    private string? _lastPreviewPath;

    /// <summary>
    /// 「标注跟随图片」+「悬停说明开关即时生效」：底图切换时保存/恢复标注。
    /// 由 DataContextChanged 订阅 ViewModel 的属性变化触发。
    /// </summary>
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // ⚠️ v5.28.0 修复：设置里的「悬停说明」开关此前需**重启才生效** ——
        //    Help.TipEnabled 只在启动时应用一次，而 Help.RefreshTree 从未被调用。
        //    这里在开关变化时刷新整棵树，让已挂载 Help.Tip 的控件立即跟随。
        if (e.PropertyName == nameof(MainViewModel.EnableToolTips))
        {
            try { Help.RefreshTree(this); }
            catch { /* 刷新失败不影响主流程 */ }
            return;
        }

        // v0.5.31：指南浮窗 —— 打开或切换标签页时重建内容（Markdown 渲染）
        if (e.PropertyName is nameof(MainViewModel.GuideOpen)
                           or nameof(MainViewModel.GuideTab)
                           or nameof(MainViewModel.CurrentGuide))
        {
            if (Vm is { GuideOpen: true } gvm) RenderGuide(gvm);
            return;
        }

        // ④ 退出编辑模式 → 完全复位（视图 + 标注 + 蒙版）。
        //    ⚠️ 必须在这里监听，而不是只在按钮 Click 里做：VM 在**编辑成功后**也会
        //    自行把 RegionMode 置 false（MainViewModel.EditWithRegionsAsync），
        //    那条路径不经过按钮 Click —— 只挂按钮会漏掉"编辑完成后残留缩放/标注"。
        if (e.PropertyName == nameof(MainViewModel.RegionMode))
        {
            if (Vm?.RegionMode == false) ResetEditorState();
            // 进入编辑模式也要对齐（此时才需要正确的画布尺寸）
            else if (Vm?.RegionMode == true)
                SyncPreviewTransform(Vm?.IsWideLayout != false);
            return;
        }

        // ⚠️ 位图异步加载完成后 `Image.Bounds` 才会变成内容尺寸 ——
        //    只监听 PreviewPath 会在"图还没解码完"时对齐到 0，等于没对齐。
        //    这里在 PreviewImage 就绪后再对齐一次（这是"缩放仍跑位"的关键缺口）。
        if (e.PropertyName == nameof(MainViewModel.PreviewImage))
        {
            SyncPreviewTransform(Vm?.IsWideLayout != false);
            return;
        }

        if (e.PropertyName != nameof(MainViewModel.PreviewPath)) return;
        if (Vm is null) return;

        var newPath = Vm.PreviewPath;
        if (newPath == _lastPreviewPath) return;

        // ① 保存旧图的标注
        if (_lastPreviewPath is not null)
            Vm.SaveRegionsFor(_lastPreviewPath, ActiveCanvas.ExportShapes());

        // ② 恢复新图的标注（没有则清空）
        var snap = Vm.GetRegionsFor(newPath);
        RegionLayer.ImportShapes(snap);
        RegionLayerNarrow.ImportShapes(snap);

        _lastPreviewPath = newPath;

        // ③ 新图的宽高比可能不同 → 画布会通过 FollowBoundsOf 自动跟随新尺寸
        //   （`Image.Bounds` 变化即触发），这里只需同步一次矩阵。
        SyncPreviewTransform(wide: true);
        SyncPreviewTransform(wide: false);
    }

    /// <summary>
    /// 响应式断点：宽度 ≥900px 用三栏，否则纵向堆叠。
    /// Avalonia 无内置媒体查询，靠 SizeChanged 手动切换。
    /// </summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (Vm is null) return;

        // ⚠️ 尺寸变化 → 图片内容矩形（Uniform letterbox）也随之变化。
        //    画布通过 FollowBoundsOf 自动跟随；这里补一次矩阵同步。
        //    注意：要在下面的"布局是否切换"提前 return **之前**做。
        SyncPreviewTransform(wide: true);
        SyncPreviewTransform(wide: false);

        var wasWide = Vm.IsWideLayout;
        var nowWide = e.NewSize.Width >= 900;
        if (wasWide == nowWide) return;

        // 布局切换前：把当前可见画布的标注搬到另一个实例，
        // 否则用户画好的标注会在切换时"消失"（两个实例各持一份数据）。
        try
        {
            if (wasWide) RegionLayerNarrow.ImportShapes(RegionLayer.ExportShapes());
            else         RegionLayer.ImportShapes(RegionLayerNarrow.ExportShapes());
        }
        catch { /* 同步失败不应阻断布局切换 */ }

        Vm.IsWideLayout = nowWide;
    }

    private void OnPromptHistorySelected(object? sender, SelectionChangedEventArgs e)
    {
        // 多选模式下不改变提示词（避免批量选择时反复覆盖输入框）
        if (Vm?.PromptMultiSelect == true) return;
        if (sender is ListBox lb && lb.SelectedItem is string s && Vm is not null)
            Vm.Prompt = s;
    }

    private void OnHistorySelected(object? sender, SelectionChangedEventArgs e)
    {
        // 多选模式下不切换预览（点击仅用于勾选）
        if (Vm?.HistoryMultiSelect == true) return;
        if (sender is ListBox lb && lb.SelectedItem is HistoryRow row && Vm is not null)
            _ = Vm.ShowPreviewCommand.ExecuteAsync(row.Item);
    }

    // ================================================================ 右键菜单（P1/P2）
    // 说明：原先用 Command + CommandParameter 绑定，但因
    //   ① ListBox.ItemsSource 是 HistoryRow，而命令参数声明为 Item → 类型不符 → CanExecute=false
    //   ② ContextMenu 是弹出层，$parent[ListBox] 查找不到 → Command 为 null
    // 导致菜单项**恒为禁用**（用户报告「右键无法点击」）。
    // 改为 Click 事件：MenuItem.DataContext 天然继承宿主行的数据，稳且可测。

    /// <summary>历史行右键 → 仅从列表移除（保留文件）。</summary>
    private void OnHistoryRemoveFromListClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not HistoryRow row) return;
        Vm?.DeleteListItemCommand.Execute(row.Item);
    }

    /// <summary>历史行右键 → 删除（含文件）。**先弹确认浮层**（P11）。</summary>
    private void OnHistoryDeleteClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not HistoryRow row) return;
        Vm?.RequestDeleteHistory(row);
    }

    /// <summary>提示词历史右键 → 删除该条（先确认）。</summary>
    private void OnPromptHistoryDeleteClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not string prompt) return;
        Vm?.RequestDeletePrompt(prompt);
    }

    // ================================================================ Plan A：历史面板接线
    /// <summary>
    /// 两个历史面板实例的内部 ListBox（宽/窄各一）。
    ///
    /// ⚠️ Plan A 前这里是 `new[] { HistoryList, HistoryListNarrow }`（XAML 的 x:Name）。
    ///    抽成 Section 后 x:Name 变成了 Section 实例（WideHistory / NarrowHistory），
    ///    ListBox 通过 <see cref="Sections.HistoryPanel.InnerList"/> 暴露出来。
    ///    语义完全不变：仍然同时检查两个 ListBox（只有一个是可见的）。
    /// </summary>
    private IEnumerable<ListBox> HistoryListBoxes
    {
        get
        {
            if (WideHistory?.InnerList is { } w) yield return w;
            if (NarrowHistory?.InnerList is { } n) yield return n;
        }
    }

    /// <summary>
    /// 把历史面板的事件回调接到原有处理器上。
    ///
    /// ⚠️ 为什么不用命令绑定（review 修正）：原处理器读 View 层状态并清选择
    ///    （SelectedHistoryRows / ClearListSelection），命令化会静默丢掉这些副作用。
    ///    所以 Section 只抛事件，这里转发到原有处理器 —— **零逻辑改动**。
    /// </summary>
    private void WireHistoryPanel(Sections.HistoryPanel panel)
    {
        panel.DeleteSelectedRequested += (_, _) => OnDeleteSelectedHistoryClick(panel, new RoutedEventArgs());
        panel.RemoveSelectedRequested += (_, _) => OnRemoveSelectedHistoryClick(panel, new RoutedEventArgs());
        panel.ToggleMultiRequested += (_, _) => OnToggleHistoryMultiClick(panel, new RoutedEventArgs());
        panel.ImportRequested += (_, _) => OnImportClick(panel, new RoutedEventArgs());
        // 右键菜单：原处理器靠 (sender as Control)?.DataContext 取行 → 透传真实触发源
        panel.RowRemoveFromListRequested += (_, s) => OnHistoryRemoveFromListClick(s, new RoutedEventArgs());
        panel.RowDeleteRequested += (_, s) => OnHistoryDeleteClick(s, new RoutedEventArgs());
        panel.ListSelectionChanged += (_, e) => OnHistorySelected(panel.InnerList, e);
    }

    // ================================================================ 多选批量（P12）
    private void OnToggleHistoryMultiClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Vm.HistoryMultiSelect = !Vm.HistoryMultiSelect;
        if (!Vm.HistoryMultiSelect) ClearListSelection(HistoryListBoxes.ToArray());
    }

    private void OnTogglePromptMultiClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Vm.PromptMultiSelect = !Vm.PromptMultiSelect;
        if (!Vm.PromptMultiSelect) ClearListSelection(PromptHistoryList);
    }

    private static void ClearListSelection(params ListBox?[] lists)
    {
        foreach (var lb in lists) lb?.SelectedItems?.Clear();
    }

    /// <summary>批量「移除选中」（仅列表，保留文件）。</summary>
    private void OnRemoveSelectedHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var rows = SelectedHistoryRows();
        Vm.BatchRemoveFromList(rows);
        ClearListSelection(HistoryListBoxes.ToArray());
    }

    /// <summary>批量「删除选中」（含文件，走确认浮层）。</summary>
    private void OnDeleteSelectedHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var rows = SelectedHistoryRows();
        Vm.BatchDeleteWithFiles(rows);
        // 不清选择：用户在确认浮层点「取消」时应保留选择
    }

    /// <summary>批量删除提示词（走确认浮层）。</summary>
    private void OnDeleteSelectedPromptsClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var prompts = PromptHistoryList?.SelectedItems?.OfType<string>().ToList()
                      ?? new List<string>();
        Vm.BatchDeletePromptHistory(prompts);
    }

    /// <summary>当前**可见**历史列表里被选中的行。
    /// 注意：宽/窄屏各有一个 ListBox，二者都绑定同一 ItemsSource，但只有一个是可见的。
    /// 不能只按 IsWideLayout 判断（布局切换时序可能不一致）——改为取"有选中项的那个"。</summary>
    private List<HistoryRow> SelectedHistoryRows()
    {
        var picks = new List<HistoryRow>();
        foreach (var lb in HistoryListBoxes)
        {
            var sel = lb.SelectedItems?.OfType<HistoryRow>().ToList();
            if (sel is { Count: > 0 }) picks.AddRange(sel);
        }
        return picks;
    }

    /// <summary>清空两个历史 ListBox 的选中项。</summary>
    private void ClearHistorySelection() => ClearListSelection(HistoryListBoxes.ToArray());

    private void OnThumbClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PreviewThumb thumb } && Vm is not null)
            _ = Vm.ShowPreviewCommand.ExecuteAsync(thumb.Item);
    }

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        try { await OnImportClickCoreAsync(); }
        catch (Exception ex)
        {
            Vm?.LogPublic($"导入失败：{ex.Message}", MainViewModel.MessageLevel.Err);
        }
    }

    private async Task OnImportClickCoreAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || Vm is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择图片", AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("图片")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif" },
                    MimeTypes = new[] { "image/*" },
                },
            },
        });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        var data = ms.ToArray();
        var media = Core.Imaging.ImageCodec.SniffMediaType(data) ?? "image/png";
        await Vm.ImportImageAsync(data, media, files[0].Name);
    }

    private async void OnAddRefImageClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is null || Vm is null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择参考图", AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("图片")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" },
                        MimeTypes = new[] { "image/*" },
                    },
                },
            });
            foreach (var file in files)
            {
                await using var stream = await file.OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                var data = ms.ToArray();
                var media = Core.Imaging.ImageCodec.SniffMediaType(data) ?? "image/png";
                // ⚠️ v5.27.0：这里**不再**压到 1024 —— 参考图保真是"参考是否生效"的关键。
                //    压缩统一由 Core 的 GenRequest.PrepareRefs 按文档上限（仅在超限时）处理，
                //    并与蒙版尺寸保持一致。
                await Vm.AddReferenceImageAsync(data, media, file.Name);
            }
        }
        catch (Exception ex)
        {
            Vm?.LogPublic($"参考图导入失败：{ex.Message}", MainViewModel.MessageLevel.Err);
        }
    }

    /// <summary>清空提示词与当前标注（用户需求 #13）。</summary>
    private void OnClearPromptClick(object? sender, RoutedEventArgs e)
        => Vm?.ClearPrompt();

    private void OnToggleRegionClick(object? sender, RoutedEventArgs e)
    {
        Vm?.ToggleRegionModeCommand.Execute(null);
        // 进入标注模式后让画布获焦（否则首次按下只聚焦、不落笔）。
        // 退出时的复位由 OnVmPropertyChanged 统一处理（覆盖"编辑成功后自动退出"那条路径）。
        if (Vm?.RegionMode == true) ActiveCanvas.Focus();
    }

    /// <summary>
    /// 退出编辑时把编辑相关状态**完全复位**（④）：
    ///   ① 两个画布的缩放/平移归位（并广播 → 图片层同步回到原位）；
    ///   ② 清掉画布上已画的标注；
    ///   ③ 清掉 VM 里已导出的标注合成图 / 蒙版路径。
    /// </summary>
    private void ResetEditorState()
    {
        try
        {
            RegionLayer.ResetView();
            RegionLayerNarrow.ResetView();
            RegionLayer.ClearRegions();
            RegionLayerNarrow.ClearRegions();
            Vm?.ClearAnnotationState();
            SyncPreviewTransform(wide: true);
            SyncPreviewTransform(wide: false);
        }
        catch { /* 复位失败不影响主流程 */ }
    }

    private void OnClearRegionsClick(object? sender, RoutedEventArgs e)
    {
        RegionLayer.ClearRegions();
        RegionLayerNarrow.ClearRegions();
        Vm?.ClearRegionsCommand.Execute(null);
    }

    // ================================================================ 编辑历史（需求②）

    /// <summary>
    /// 撤销标注编辑动作。
    ///
    /// ⚠️ 必须**同时**作用于两个布局实例：宽窄两套各有一个 <see cref="RegionCanvas"/>，
    ///   只撤一个会让另一套停留在旧状态（切换布局后标注"回退"）。
    /// </summary>
    private void OnUndoRegionClick(object? sender, RoutedEventArgs e)
    {
        var ok = RegionLayer.UndoEdit() | RegionLayerNarrow.UndoEdit();
        SyncRegionCount();
        if (ok) Vm?.LogPublic("已撤销上一步标注", MainViewModel.MessageLevel.Info);
    }

    /// <summary>重做标注编辑动作（同撤销，两个实例都做）。</summary>
    private void OnRedoRegionClick(object? sender, RoutedEventArgs e)
    {
        var ok = RegionLayer.RedoEdit() | RegionLayerNarrow.RedoEdit();
        SyncRegionCount();
        if (ok) Vm?.LogPublic("已重做标注", MainViewModel.MessageLevel.Info);
    }

    /// <summary>图片回到预览框中央（复位缩放与平移）。</summary>
    private void OnCenterRegionClick(object? sender, RoutedEventArgs e)
        => OnZoomResetClick(sender, e);   // 与「复位」同一语义

    /// <summary>把两个画布的数量差异同步给 VM（撤销/重做后调用）。</summary>
    private void SyncRegionCount()
    {
        if (Vm is null) return;
        Vm.RegionCount = ActiveCanvas.RegionCount;
    }

    /// <summary>当前可见布局对应的画布（窄屏优先用窄屏那个）。</summary>
    private RegionCanvas ActiveCanvas =>
        Vm?.IsWideLayout == false ? RegionLayerNarrow : RegionLayer;

    private async void OnAnnotatedEditClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Vm is null) return;
            var canvas = ActiveCanvas;
            var png = canvas.ExportComposite();
            if (png is null || png.Length == 0)
            {
                Vm.LogPublic("请先在预览图上圈画要修改的区域", MainViewModel.MessageLevel.Warn);
                return;
            }
            Vm.SetAnnotatedImage(png, canvas.RegionCount);

            // 同时导出「修改区域蒙版」（**透明=要改，不透明=保留**，带 Alpha 通道）
            // → 支持 mask/inpainting 的模型走 mask_url 真正做局部重绘。
            // 记录底图路径，供 ViewModel 校验"蒙版是否还属于这张图"。
            var maskOk = false;
            try
            {
                var mask = canvas.ExportMask();
                if (mask is not null)
                {
                    Vm.SetAnnotatedMask(mask, canvas.BaseImagePath);
                    maskOk = true;
                }
            }
            catch (Exception mex)
            {
                // 蒙版可选：失败时退回"合成图 + 提示词"方式，但必须让用户知道
                Vm.LogPublic($"蒙版导出失败（将退回标注合成图方式）：{mex.Message}",
                             MainViewModel.MessageLevel.Warn);
            }

            await Vm.EditWithRegionsCommand.ExecuteAsync(null);
            if (maskOk)
                Vm.LogPublic("本次走蒙版局部重绘（马克笔/方框覆盖处 = 要改的区域）",
                             MainViewModel.MessageLevel.Dim);
        }
        catch (Exception ex)
        {
            Vm?.LogPublic($"区域编辑失败：{ex.Message}", MainViewModel.MessageLevel.Err);
        }
    }

    // ---------------------------------------------------------------- 缩放（#7）
    private void OnZoomInClick(object? sender, RoutedEventArgs e)
        => ActiveCanvas.ZoomBy(1.25);

    private void OnZoomOutClick(object? sender, RoutedEventArgs e)
        => ActiveCanvas.ZoomBy(1 / 1.25);

    private void OnZoomResetClick(object? sender, RoutedEventArgs e)
    {
        RegionLayer.ResetView();
        RegionLayerNarrow.ResetView();
    }

    private void OnPaletteColorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex } && Vm is not null)
            Vm.ApplyPaletteColor(hex);
    }
}