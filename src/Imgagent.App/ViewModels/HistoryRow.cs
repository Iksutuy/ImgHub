using Avalonia.Media.Imaging;
using Imgagent.Core.Models;

namespace Imgagent.App.ViewModels;

/// <summary>历史列表一行：包住 Core 的 Item 并附 UI 需要的缩略图。</summary>
public sealed class HistoryRow
{
    public Item Item { get; }
    public string Prompt => Item.Prompt;
    public string File => Item.File;
    public double Cost => Item.Cost;
    public string Kind => Item.Kind;

    private Bitmap? _thumb;
    private bool _tried;

    public Bitmap? Thumb
    {
        get
        {
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
}
