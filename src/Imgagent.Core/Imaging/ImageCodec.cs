using SkiaSharp;

namespace Imgagent.Core.Imaging;

/// <summary>
/// 图像编解码 + 缩放。用 SkiaSharp 替代 Python 里手写的 <c>pngcodec.py</c>。
/// 覆盖：尺寸探测、media_type 嗅探、扩展名映射、参考图缩小（限制最大边）。
/// </summary>
public static class ImageCodec
{
    private static readonly Dictionary<string, string> MimeByExt = new()
    {
        ["png"] = "image/png", ["jpg"] = "image/jpeg", ["jpeg"] = "image/jpeg",
        ["webp"] = "image/webp", ["gif"] = "image/gif", ["bmp"] = "image/bmp",
    };

    private static readonly Dictionary<string, string> ExtByMime = new()
    {
        ["image/png"] = "png", ["image/jpeg"] = "jpg", ["image/webp"] = "webp",
        ["image/gif"] = "gif", ["image/bmp"] = "bmp",
    };

    /// <summary>嗅探图片 media_type（按魔数）。无法识别返回 null。</summary>
    public static string? SniffMediaType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return "image/png";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return "image/jpeg";
        if (data.Length >= 12 &&
            data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
            data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
            return "image/webp";
        if (data.Length >= 6 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
            return "image/gif";
        if (data.Length >= 2 && data[0] == 0x42 && data[1] == 0x4D)
            return "image/bmp";
        return null;
    }

    /// <summary>media_type -> 扩展名（上传时 filename 用）。</summary>
    public static string ExtFor(string media) =>
        ExtByMime.TryGetValue(media, out var e) ? e : "png";

    /// <summary>文件扩展名 -> media_type。</summary>
    public static string MimeForExt(string ext)
    {
        ext = ext.TrimStart('.').ToLowerInvariant();
        return MimeByExt.TryGetValue(ext, out var m) ? m : "image/*";
    }

    /// <summary>探测图片尺寸（不解全图）。失败返回 null。</summary>
    public static (int Width, int Height)? Size(byte[] data)
    {
        using var codec = SKCodec.Create(new MemoryStream(data));
        if (codec is null) return null;
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0) return null;
        return (info.Width, info.Height);
    }

    /// <summary>解码成 SKBitmap（失败返回 null）。</summary>
    public static SKBitmap? Decode(byte[] data)
    {
        try { return SKBitmap.Decode(data); }
        catch { return null; }
    }

    /// <summary>
    /// 参考图缩小：限制最大边到 <paramref name="maxSide"/>，输出 PNG 字节。
    /// 对应 Python 的 <c>pngcodec.shrink_for_reference()</c>。
    /// 非图片或解码失败时原样返回。
    /// </summary>
    public static byte[] ShrinkForReference(byte[] data, int maxSide = 1024)
    {
        using var src = Decode(data);
        if (src is null) return data;

        int longSide = Math.Max(src.Width, src.Height);
        if (longSide <= maxSide) return data;

        double r = (double)maxSide / longSide;
        int nw = Math.Max(1, (int)(src.Width * r));
        int nh = Math.Max(1, (int)(src.Height * r));

        using var dst = src.Resize(new SKImageInfo(nw, nh),
                                   new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (dst is null) return data;
        using var image = SKImage.FromBitmap(dst);
        using var png = image.Encode(SKEncodedImageFormat.Png, 90);
        return png.ToArray();
    }

    /// <summary>按 media 编码（用于输出格式转换）。</summary>
    public static byte[] Encode(SKBitmap bmp, string media)
    {
        var fmt = media switch
        {
            "image/jpeg" => SKEncodedImageFormat.Jpeg,
            "image/webp" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Png,
        };
        using var image = SKImage.FromBitmap(bmp);
        using var enc = image.Encode(fmt, 92);
        return enc.ToArray();
    }
}
