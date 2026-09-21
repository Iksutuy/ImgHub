using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Imgagent.App.Controls;
using Imgagent.App.ViewModels;

namespace Imgagent.App.Views;

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
            }
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>上一次预览的图片路径（用于切换时保存/恢复标注）。</summary>
    private string? _lastPreviewPath;

    /// <summary>
    /// 「标注跟随图片」：底图切换时，先把当前图的标注存入缓存，
    /// 再恢复目标图已缓存的标注。由 DataContextChanged 订阅 PreviewPath 变化触发。
    /// </summary>
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
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
    }

    /// <summary>
    /// 响应式断点：宽度 ≥900px 用三栏，否则纵向堆叠。
    /// Avalonia 无内置媒体查询，靠 SizeChanged 手动切换。
    /// </summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (Vm is null) return;
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
        if (sender is ListBox lb && lb.SelectedItem is string s && Vm is not null)
            Vm.Prompt = s;
    }

    private void OnHistorySelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedItem is HistoryRow row && Vm is not null)
            _ = Vm.ShowPreviewCommand.ExecuteAsync(row.Item);
    }

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
                var shrunk = Core.Imaging.ImageCodec.ShrinkForReference(data, 1024);
                await Vm.AddReferenceImageAsync(shrunk, media, file.Name);
            }
        }
        catch (Exception ex)
        {
            Vm?.LogPublic($"参考图导入失败：{ex.Message}", MainViewModel.MessageLevel.Err);
        }
    }

    private void OnToggleRegionClick(object? sender, RoutedEventArgs e)
    {
        Vm?.ToggleRegionModeCommand.Execute(null);
        // 进入标注模式后让画布获焦（否则首次按下只聚焦、不落笔）
        if (Vm?.RegionMode == true)
        {
            var canvas = ActiveCanvas;
            canvas.Focus();
        }
    }

    private void OnClearRegionsClick(object? sender, RoutedEventArgs e)
    {
        RegionLayer.ClearRegions();
        RegionLayerNarrow.ClearRegions();
        Vm?.ClearRegionsCommand.Execute(null);
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

            // 同时导出「修改区域蒙版」（白=要改，黑=保留），供支持 mask 的模型使用
            try
            {
                var mask = canvas.ExportMask();
                if (mask is not null) Vm.SetAnnotatedMask(mask);
            }
            catch { /* mask 可选，失败不影响主流程 */ }

            await Vm.EditWithRegionsCommand.ExecuteAsync(null);
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