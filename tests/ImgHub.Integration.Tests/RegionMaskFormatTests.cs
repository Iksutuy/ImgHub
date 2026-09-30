using Avalonia;
using Avalonia.Controls;
using Avalonia.Skia;
using SkiaSharp;

namespace ImgHub.Integration.Tests;

/// <summary>
/// 蒙版导出格式的回归（用户需求④：「是否该传**单色蒙版**？端点是这样要求的吗？」）。
///
/// ## 核实结论（查项目内的一手契约文档 + 实现）
/// · <c>docs/provider-openai-image-api.md</c> 的官方 "Mask requirements" 只要求两条：
///     ① *"The image to edit and mask must be of the same format and size"*；
///     ② *"The mask image must also contain an alpha channel"*。
///   **并没有**要求"必须单色"。
/// · <c>docs/provider-qwen-dashscope-api.md</c> 明确：千问**不支持** mask。
/// · APIMart 走 <c>mask_url</c>（同样要求带 Alpha 的 PNG、尺寸与图一致）。
///
/// ## 为什么仍然导成单色（RGB=0）
/// 语义只落在 **Alpha** 上（alpha=0 = 要改，255 = 保留）。
/// 把 RGB 归零是为了**消除歧义** —— 避免不同实现按 RGB 亮度去解读一份"黑白图"。
/// 所以：单色不是端点硬要求，而是我们主动选择的**最不易被误解**的表示。
///
/// 本文件钉住：导出确实满足端点那两条硬要求（尺寸一致 + 含 Alpha），且 RGB 已归零。
/// </summary>
public class RegionMaskFormatTests
{
    static RegionMaskFormatTests() => SkiaPlatform.Initialize();

    /// <summary>建一个有标记的画布，底图尺寸 <paramref name="w"/>×<paramref name="h"/>。</summary>
    private static (ImgHub.App.Controls.RegionCanvas Canvas, string Home) Build(int w, int h)
    {
        var home = Path.Combine(Path.GetTempPath(), "imghub-mask-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var basePath = Path.Combine(home, "base.png");

        using (var bm = new SKBitmap(w, h))
        {
            using (var c = new SKCanvas(bm)) c.Clear(new SKColor(30, 30, 30));
            using var img = SKImage.FromBitmap(bm);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(basePath, data.ToArray());
        }

        var canvas = new ImgHub.App.Controls.RegionCanvas
        { BaseImagePath = basePath, BrushColor = "#FF0000", BrushSize = 20 };
        var root = new Grid { Width = 300, Height = 300 };
        root.Children.Add(canvas);
        root.Measure(new Size(300, 300));
        root.Arrange(new Rect(0, 0, 300, 300));
        canvas.ImageContentRect = new Rect(0, 0, 300, 300);

        var snap = canvas.ExportShapes();
        snap.Strokes.Add(new ImgHub.App.Controls.RegionCanvas.StrokeData
        {
            R = 255, G = 0, B = 0, Size = 0.02,
            IsOutline = true, IsEllipse = false,
            X0 = 0.25, Y0 = 0.25, X1 = 0.75, Y1 = 0.75,
        });
        canvas.ImportShapes(snap);
        return (canvas, home);
    }

    [Fact]
    public void ExportMask_MatchesImageSize_AndHasAlphaChannel()
    {
        // 端点的两条硬要求：尺寸与编辑图一致 + 必须含 Alpha
        var (canvas, home) = Build(200, 200);
        try
        {
            var mask = canvas.ExportMask();
            Assert.NotNull(mask);

            using var sk = SKBitmap.Decode(mask);
            Assert.NotNull(sk);
            Assert.Equal(200, sk!.Width);      // 要求①：尺寸一致
            Assert.Equal(200, sk.Height);

            // 要求②：**PNG 文件里有 Alpha 通道**。
            // ⚠️ 判据必须查 PNG 的 IHDR color type（项目里的真源 Catalog.PngHasAlpha，
            //    与发送前的校验用同一段逻辑）—— 不能用 SKBitmap.Decode 的 ColorType：
            //    Skia 解码后按平台返回 Bgra8888/Rgba8888，那反映的是内存布局、不是文件格式。
            Assert.True(ImgHub.Core.Catalog.PngHasAlpha(mask),
                "蒙版 PNG 必须含 Alpha 通道（端点硬要求）");
        }
        finally { try { Directory.Delete(home, recursive: true); } catch { } }
    }

    [Fact]
    public void ExportMask_IsMonochrome_RgbIsZeroed()
    {
        // 我们的主动选择：RGB 归零，语义只落在 Alpha（避免被按亮度误解）
        var (canvas, home) = Build(120, 120);
        try
        {
            var mask = canvas.ExportMask();
            Assert.NotNull(mask);
            using var sk = SKBitmap.Decode(mask);
            Assert.NotNull(sk);

            for (int y = 0; y < sk!.Height; y += 7)
            {
                for (int x = 0; x < sk.Width; x += 7)
                {
                    var p = sk.GetPixel(x, y);
                    Assert.Equal(0, p.Red);
                    Assert.Equal(0, p.Green);
                    Assert.Equal(0, p.Blue);
                }
            }
        }
        finally { try { Directory.Delete(home, recursive: true); } catch { } }
    }

    [Fact]
    public void ExportMask_AlphaZeroMarksEditableArea_AlphaFullMarksKeep()
    {
        // 语义：**alpha=0 = 要改**（标记处），255 = 保留（未标记处）
        var (canvas, home) = Build(200, 200);
        try
        {
            var mask = canvas.ExportMask();
            Assert.NotNull(mask);
            using var sk = SKBitmap.Decode(mask);
            Assert.NotNull(sk);

            var marked = sk!.GetPixel(100, 100);   // 矩形正中（0.25..0.75）
            var kept = sk.GetPixel(5, 5);          // 角落（未标记）

            Assert.True(marked.Alpha < 30,
                $"标记区应表示「要改」（alpha≈0），实际 alpha={marked.Alpha}");
            Assert.True(kept.Alpha > 220,
                $"未标记区应表示「保留」（alpha≈255），实际 alpha={kept.Alpha}");
        }
        finally { try { Directory.Delete(home, recursive: true); } catch { } }
    }

    [Fact]
    public void ExportMask_NoShapes_ReturnsNull()
    {
        // 没圈画 → 返回 null（上层据此不传 mask，改用合成图）
        var home = Path.Combine(Path.GetTempPath(), "imghub-mask0-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var basePath = Path.Combine(home, "b.png");
            using (var bm = new SKBitmap(64, 64))
            {
                using (var c = new SKCanvas(bm)) c.Clear(SKColors.Black);
                using var img = SKImage.FromBitmap(bm);
                using var d = img.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(basePath, d.ToArray());
            }
            var canvas = new ImgHub.App.Controls.RegionCanvas { BaseImagePath = basePath };
            canvas.Measure(new Size(64, 64));
            canvas.Arrange(new Rect(0, 0, 64, 64));

            Assert.Null(canvas.ExportMask());
            Assert.Null(canvas.ExportComposite());
        }
        finally { try { Directory.Delete(home, recursive: true); } catch { } }
    }
}
