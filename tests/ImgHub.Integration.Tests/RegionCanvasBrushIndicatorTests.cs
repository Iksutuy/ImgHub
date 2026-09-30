using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Skia;
using SkiaSharp;

namespace ImgHub.Integration.Tests;

/// <summary>
/// 笔刷**指示圈**的回归（用户需求⑦⑧：橡皮指示圈要等于真实擦除范围；马克笔也要有指示圈）。
///
/// 用真实渲染读像素验证 —— 因为"指示圈画没画出来/画在哪儿"是几何问题，
/// 只有量像素能确证（读代码只能确认"调用了 DrawEllipse"）。
/// </summary>
public class RegionCanvasBrushIndicatorTests
{
    static RegionCanvasBrushIndicatorTests() => SkiaPlatform.Initialize();

    private const int W = 300, H = 300;

    private static string MakeBase()
    {
        var home = Path.Combine(Path.GetTempPath(), "imghub-ind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var path = Path.Combine(home, "b.png");
        using var bm = new SKBitmap(W, H);
        using (var c = new SKCanvas(bm)) c.Clear(new SKColor(20, 20, 20));
        using var img = SKImage.FromBitmap(bm);
        using var d = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, d.ToArray());
        return path;
    }

    /// <summary>建画布并把鼠标位置设到图片中心（模拟"指针停在图上"）。</summary>
    private static ImgHub.App.Controls.RegionCanvas Build(
        string basePath, ImgHub.App.Controls.RegionCanvas.RegionTool tool, double brushSize)
    {
        var canvas = new ImgHub.App.Controls.RegionCanvas
        {
            BaseImagePath = basePath,
            BrushColor = "#FF0000",
            BrushSize = brushSize,
            Tool = tool,
        };
        var root = new Grid { Width = W, Height = H };
        root.Children.Add(canvas);
        root.Measure(new Size(W, H));
        root.Arrange(new Rect(0, 0, W, H));
        canvas.ImageContentRect = new Rect(0, 0, W, H);
        // 关键：指示圈只在"指针在图片内"时绘制 → 用测试入口设置指针位置
        canvas.SetPointerForTest(new Point(W / 2.0, H / 2.0));
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

    /// <summary>
    /// 沿一条水平线扫描"比暗底亮很多"的像素（指示圈是浅色虚线，底图是深色）。
    /// 用**亮度增量**而不是固定阈值 —— 这样橡皮（白）与马克笔（淡黄）都能被识别。
    /// </summary>
    private static (int Min, int Max, int Count) BrightSpan(byte[] png, int y, int delta = 60)
    {
        using var bmp = SKBitmap.Decode(png);
        int min = int.MaxValue, max = -1, n = 0;
        for (int x = 0; x < bmp.Width; x++)
        {
            var c = bmp.GetPixel(x, y);
            // 底图 ≈ (20,20,20)，指示圈明显更亮
            int lum = (c.Red + c.Green + c.Blue) / 3;
            if (lum > 20 + delta)
            {
                if (x < min) min = x;
                if (x > max) max = x;
                n++;
            }
        }
        return (min == int.MaxValue ? -1 : min, max, n);
    }

    [Fact]
    public void Marker_DrawsIndicatorCircle_AtPointer()
    {
        // 需求⑧：马克笔应有指示圈（此前完全没有）
        var basePath = MakeBase();
        try
        {
            var canvas = Build(basePath, ImgHub.App.Controls.RegionCanvas.RegionTool.Marker, 40);
            var png = Render(canvas);
            var (min, max, n) = BrightSpan(png, H / 2);

            Assert.True(n > 0, "马克笔在图上应画出指示圈（亮像素），实际没有");
            Assert.True(min < W / 2 && max > W / 2, "指示圈应跨越指针所在位置");
        }
        finally { TryClean(basePath); }
    }

    [Fact]
    public void Eraser_DrawsIndicatorCircle_AtPointer()
    {
        var basePath = MakeBase();
        try
        {
            var canvas = Build(basePath, ImgHub.App.Controls.RegionCanvas.RegionTool.Eraser, 40);
            var png = Render(canvas);
            var (_, _, n) = BrightSpan(png, H / 2);
            Assert.True(n > 0, "橡皮应画出指示圈");
        }
        finally { TryClean(basePath); }
    }

    [Fact]
    public void Rectangle_DoesNotDrawIndicatorCircle()
    {
        // 指示圈只对"笔刷类"工具有意义；方框/圆圈是拖拽出区域 → 不该有
        var basePath = MakeBase();
        try
        {
            var canvas = Build(basePath, ImgHub.App.Controls.RegionCanvas.RegionTool.Rectangle, 40);
            var png = Render(canvas);
            var (_, _, n) = BrightSpan(png, H / 2);
            Assert.Equal(0, n);
        }
        finally { TryClean(basePath); }
    }

    [Fact]
    public void MarkerIndicator_IsAStableCrosshair_NotSizedByBrush()
    {
        // ⚠️ v0.5.39 行为变更（用户反馈 #3）：
        //   "马克笔的提示标记能否换一个简洁一点的，粗细调很小的时候显示异常"
        //   旧实现：马克笔画一个半径 = 笔宽/2 的**虚线圆** → 笔宽最小时半径只有几像素，
        //           虚线在小圆上糊成一团（就是用户说的"显示异常"）。
        //   新实现：马克笔改为**十字准星**（尺寸固定为屏幕可见大小，不随笔宽变化）。
        //   ⇒ 契约变了：指示标记 **不再**随 BrushSize 变大，而是恒定的准星。
        var basePath = MakeBase();
        try
        {
            var small = Build(basePath, ImgHub.App.Controls.RegionCanvas.RegionTool.Marker, 6);
            var smallCount = BrightPixelCount(Render(small));

            var big = Build(basePath, ImgHub.App.Controls.RegionCanvas.RegionTool.Marker, 80);
            var bigCount = BrightPixelCount(Render(big));

            Assert.True(smallCount > 0, "小粗细下也必须看得见准星（这正是本次修复的目的）");

            // 准星面积与笔宽无关 → 两者应接近（允许少量抗锯齿差异）
            var ratio = bigCount / (double)Math.Max(1, smallCount);
            Assert.True(ratio > 0.5 && ratio < 2.0,
                $"马克笔准星不应随笔宽变化（小={smallCount}px 大={bigCount}px，比值={ratio:F2}）");
        }
        finally { TryClean(basePath); }
    }

    [Fact]
    public void MarkerIndicator_IsVisibleEvenAtMinimumBrush()
    {
        // 用户报"粗细调很小的时候显示异常"→ 这条钉住"最小笔宽时仍然清晰可见"。
        var basePath = MakeBase();
        try
        {
            foreach (var size in new[] { 1.0, 2.0, 3.0 })
            {
                var c = Build(basePath, ImgHub.App.Controls.RegionCanvas.RegionTool.Marker, size);
                var n = BrightPixelCount(Render(c));
                Assert.True(n >= 8, $"笔宽 {size} 时准星像素数应 ≥8（实测 {n}）");
            }
        }
        finally { TryClean(basePath); }
    }

    /// <summary>统计整张图里"明显比暗底亮"的像素数（指示圈总面积）。</summary>
    private static int BrightPixelCount(byte[] png, int delta = 60)
    {
        using var bmp = SKBitmap.Decode(png);
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if ((c.Red + c.Green + c.Blue) / 3 > 20 + delta) n++;
            }
        return n;
    }

    [Fact]
    public void Eraser_IndicatorRadius_EqualsEraseArea()
    {
        // ⚠️ 需求⑦的核心：指示圈半径必须**等于**真实擦除半径。
        //   做法：在圈**内**画一个标记 → 应被擦掉；在圈**外**画一个 → 应保留。
        var basePath = MakeBase();
        try
        {
            var canvas = Build(basePath, ImgHub.App.Controls.RegionCanvas.RegionTool.Eraser, 40);

            // 先放两个"笔迹"：一个在指针附近（圈内），一个在角落（圈外）
            var snap = canvas.ExportShapes();
            snap.Strokes.Add(new ImgHub.App.Controls.RegionCanvas.StrokeData
            {
                R = 255, G = 0, B = 0, Size = 0.02, IsOutline = false,
                Points = { 0.52, 0.52 },      // 靠近指针（0.5,0.5）→ 圈内
            });
            snap.Strokes.Add(new ImgHub.App.Controls.RegionCanvas.StrokeData
            {
                R = 255, G = 0, B = 0, Size = 0.02, IsOutline = false,
                Points = { 0.05, 0.05 },      // 角落 → 圈外
            });
            canvas.ImportShapes(snap);
            Assert.Equal(2, canvas.RegionCount);

            // 指针在中心，点一下擦除
            canvas.EraseAtPointerForTest();

            Assert.Equal(1, canvas.RegionCount);   // 只擦掉圈内那个
            var left = canvas.ExportShapes();
            Assert.Single(left.Strokes);
            // 留下的应是角落那个（x≈0.05）
            Assert.True(left.Strokes[0].Points[0] < 0.2,
                "应保留圈外的标记（说明擦除范围没有过大）");
        }
        finally { TryClean(basePath); }
    }

    private static void TryClean(string basePath)
    {
        try { Directory.Delete(Path.GetDirectoryName(basePath)!, recursive: true); } catch { }
    }
}
