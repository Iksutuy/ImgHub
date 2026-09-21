using Avalonia.Media.Imaging;
using Imgagent.Core.Models;

namespace Imgagent.App.ViewModels;

/// <summary>缩略图条/参考图列表的一项。</summary>
public sealed class PreviewThumb
{
    public Item Item { get; }
    public string File => Item.File;
    public string ShortPrompt { get; }
    public string CostText { get; }

    public string HomePath { get; set; } = "";

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

    public PreviewThumb(Item item)
    {
        Item = item;
        var p = (item.Prompt ?? "").Replace("\n", " ").Trim();
        ShortPrompt = p.Length > 14 ? p[..14] + "…" : (p.Length > 0 ? p : item.Kind);
        CostText = item.Cost > 0 ? $"${item.Cost:0.0000}" : "";
    }
}
