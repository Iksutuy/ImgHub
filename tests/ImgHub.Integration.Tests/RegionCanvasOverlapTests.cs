using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Skia;
using SkiaSharp;

namespace ImgHub.Integration.Tests;

/// <summary>
/// 需求③「重叠区域不透明度不叠加」的回归。
///
/// 语义（用户要求）：蒙版 = **一片区域**。同一处画两次不应该比画一次更深，
/// 否则用户会以为"多涂几遍效果更强"，而实际送服务端的蒙版是二值的。
/// </summary>
public class RegionCanvasOverlapTests
{
    static RegionCanvasOverlapTests() => SkiaPlatform.Initialize();

    private const int W = 300, H = 300;

    private static byte[] SolidPng(int w, int h)
    {
        using var bm = new SKBitmap(w, h);
        using (var c = new SKCanvas(bm)) c.Clear(SKColors.Black);
        using var i = SKImage.FromBitmap(bm);
        using var d = i.Encode(SKEncodedImageFormat.Png, 100);
        return d.ToArray();
    }

    /// <summary>建一个铺满的画布（内容矩形 = 全画布，避免 letterbox 干扰）。</summary>
    private static ImgHub.App.Controls.RegionCanvas Build(string basePath)
    {
        var canvas = new ImgHub.App.Controls.RegionCanvas
        {
            BaseImagePath = basePath,
            BrushColor = "#FF0000",
            BrushSize = 30,
            LayerAlpha = 110,
        };
        var root = new Grid { Width = W, Height = H };
        root.Children.Add(canvas);
        root.Measure(new Size(W, H));
        root.Arrange(new Rect(0, 0, W, H));
        // 让坐标基准 = 整个画布（单测里没有真实 Image）
        canvas.ImageContentRect = new Rect(0, 0, W, H);
        return canvas;
    }

    private static byte[] Render(ImgHub.App.Controls.RegionCanvas canvas)
    {
        var root = (Grid)canvas.Parent!;
        var rtb = new RenderTargetBitmap(new PixelSize(W, H));
        rtb.Render(root);
        using var ms = new MemoryStream();
        rtb.Save(ms);
        return ms.ToArray();
    }

    private static SKColor PixelAt(byte[] png, int x, int y)
    {
        using var bmp = SKBitmap.Decode(png);
        return bmp.GetPixel(x, y);
    }

    /// <summary>把一个矩形加进画布（通过 ImportShapes，避免依赖指针事件）。</summary>
    private static void AddRect(ImgHub.App.Controls.RegionCanvas canvas,
                                double x0, double y0, double x1, double y1)
    {
        var snap = canvas.ExportShapes();
        snap.Strokes.Add(new ImgHub.App.Controls.RegionCanvas.StrokeData
        {
            R = 255, G = 0, B = 0, Size = 0.02,
            IsOutline = true, IsEllipse = false,
            X0 = x0, Y0 = y0, X1 = x1, Y1 = y1,
        });
        canvas.ImportShapes(snap);
    }

    [Fact]
    public void OverlappingSameColorShapes_DoNotDarkenAtIntersection()
    {
        // 画两个**重叠**的同色矩形：单层处与重叠处的红色浓度必须一致。
        var home = Path.Combine(Path.GetTempPath(), "imghub-ovl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var basePath = Path.Combine(home, "b.png");
            File.WriteAllBytes(basePath, SolidPng(W, H));

            var canvas = Build(basePath);
            // 两个矩形重叠在 (0.4..0.6, 0.4..0.6)
            AddRect(canvas, 0.20, 0.20, 0.60, 0.60);
            AddRect(canvas, 0.40, 0.40, 0.80, 0.80);

            var png = Render(canvas);

            var single = PixelAt(png, (int)(W * 0.30), (int)(H * 0.30));   // 只在第 1 个矩形内
            var overlap = PixelAt(png, (int)(W * 0.50), (int)(H * 0.50));  // 两者重叠

            // 前置：必须真的画出了红色（否则测试是"空跑"）
            Assert.True(single.Red > 80,
                $"单层处应有红色（实际 R={single.Red}）—— 若为 0 说明渲染没生效，测试无效");

            Assert.True(Math.Abs(single.Red - overlap.Red) <= 6,
                $"重叠处不应更深：单层 R={single.Red} vs 重叠 R={overlap.Red}");
        }
        finally { try { Directory.Delete(home, recursive: true); } catch { } }
    }

    [Fact]
    public void OverlappingShapes_AlphaIsNotAccumulated()
    {
        // 更直接的判据：重叠处的 alpha 不应高于单层处（不叠加）
        var home = Path.Combine(Path.GetTempPath(), "imghub-ovl2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var basePath = Path.Combine(home, "b.png");
            File.WriteAllBytes(basePath, SolidPng(W, H));

            var canvas = Build(basePath);
            AddRect(canvas, 0.20, 0.20, 0.60, 0.60);
            AddRect(canvas, 0.40, 0.40, 0.80, 0.80);

            var png = Render(canvas);
            var single = PixelAt(png, (int)(W * 0.30), (int)(H * 0.30));
            var overlap = PixelAt(png, (int)(W * 0.50), (int)(H * 0.50));

            Assert.True(single.Alpha > 40,
                $"单层处应有可见 alpha（实际 {single.Alpha}）—— 若为 0 说明渲染没生效，测试无效");
            Assert.True(overlap.Alpha <= single.Alpha + 6,
                $"重叠处 alpha 不应累加：单层 {single.Alpha} vs 重叠 {overlap.Alpha}");
        }
        finally { try { Directory.Delete(home, recursive: true); } catch { } }
    }
}
