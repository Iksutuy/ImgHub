using SkiaSharp;

namespace Imgagent.Core.Imaging;

/// <summary>
/// 离线占位图 —— 网络不通/被地区限制/不想花钱时也能把流程跑通。
/// 对应 Python 的 <c>placeholder.placeholder_png()</c>：**确定性**
/// （同 prompt+step 永远同一张图），便于复现与对拍。
/// </summary>
public static class Placeholder
{
    private static (byte R, byte G, byte B) Hsv(double h, double s, double v)
    {
        h = ((h % 1.0) + 1.0) % 1.0;
        int i = (int)(h * 6);
        double f = h * 6 - i;
        double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        double r, g, b;
        switch (i % 6)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    /// <summary>CRC32（与 Python zlib.crc32 一致），保证跨语言的确定性。</summary>
    public static uint Crc32(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        uint crc = 0xFFFFFFFF;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>按提示词生成一张渐变占位图（PNG 字节）。step 用色带表示"第几轮"。</summary>
    public static byte[] Png(string prompt, int step = 0, int size = 768)
    {
        uint seed = Crc32(prompt ?? "");
        double hue = (seed % 360) / 360.0;
        double hue2 = (hue + 0.18) % 1.0;
        int band = Math.Max(6, size / 28);
        int bandY = (step % 20) * band;

        using var bmp = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        for (int y = 0; y < size; y++)
        {
            double v01 = (double)y / size;
            for (int x = 0; x < size; x++)
            {
                double u = (double)x / size;
                double t = u * 0.65 + v01 * 0.35;
                byte r, g, b;
                if (bandY <= y && y < bandY + band)
                {
                    (r, g, b) = Hsv(hue2, 0.85, 0.95);
                }
                else
                {
                    (r, g, b) = Hsv(hue + t * 0.25, 0.55 - 0.3 * v01, 0.35 + 0.55 * (1 - v01));
                    if (((x + y) / 24 + (int)(seed % 7)) % 7 == 0)
                    {
                        r = (byte)Math.Min(255, r + 22);
                        g = (byte)Math.Min(255, g + 22);
                        b = (byte)Math.Min(255, b + 22);
                    }
                    if (u < 0.03 || u > 0.97 || v01 < 0.03 || v01 > 0.97)
                    {
                        r /= 3; g /= 3; b /= 3;
                    }
                }
                bmp.SetPixel(x, y, new SKColor(r, g, b));
            }
        }
        using var image = SKImage.FromBitmap(bmp);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
