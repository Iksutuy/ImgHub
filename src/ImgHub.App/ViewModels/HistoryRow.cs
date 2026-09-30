using Avalonia.Media.Imaging;
using ImgHub.Core.Models;

namespace ImgHub.App.ViewModels;

/// <summary>
/// 历史列表一行：包住 Core 的 Item 并附 UI 需要的缩略图。
///
/// ⚠️ **P0-2（位图生命周期）**：缩略图是**非托管**资源（Skia surface）。
/// 历史列表每次刷新会重建最多 200 个本对象 —— 不在替换时释放旧行，
/// 就会累积 200 个非托管位图，只能等 GC finalizer 回收（内存峰值 / 句柄耗尽）。
/// 因此本类实现 <see cref="IDisposable"/>，由 <c>MainViewModel.RefreshHistory</c> 批量释放。
/// </summary>
public sealed class HistoryRow : IDisposable
{
    public Item Item { get; }
    public string Prompt => Item.Prompt;
    public string File => Item.File;
    public double Cost => Item.Cost;
    public string Kind => Item.Kind;

    private Bitmap? _thumb;
    private bool _tried;
    private bool _disposed;

    public Bitmap? Thumb
    {
        get
        {
            if (_disposed) return null;      // 已释放 → 不再解码（避免拿回已释放位图）
            if (_tried) return _thumb;
            _tried = true;
            try
            {
                var path = Item.Path(HomePath);
                if (!System.IO.File.Exists(path)) return null;
                using var fs = System.IO.File.OpenRead(path);
                _thumb = Bitmap.DecodeToWidth(fs, 80);
            }
            catch { _thumb = null; }
            return _thumb;
        }
    }

    public string HomePath { get; }

    public HistoryRow(Item item, string homePath)
    {
        Item = item;
        HomePath = homePath;
    }

    public void Dispose()
    {
        if (_disposed) return;   // 幂等：列表刷新可能重复释放
        _disposed = true;
        _thumb?.Dispose();       // 从未解码时 _thumb 为 null → 安全
        _thumb = null;
    }
}
