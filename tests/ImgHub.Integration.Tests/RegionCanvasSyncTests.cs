using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Skia;
using SkiaSharp;

namespace ImgHub.Integration.Tests;

/// <summary>
/// 标注层与图片层「缩放同步」的**端到端视觉回归**（用户报的"滚轮缩放跑位"）。
///
/// 历史教训（三次修错的路径，别再走）：
///   1. 以为根因是 `RenderTransformOrigin` 默认 50%（只影响缩放，不影响平移）；
///   2. 以为根因是矩阵里 pan 的位置；
///   3. 以为要在矩阵里补「图片内容矩形」偏移。
///
/// **真正的根因**（本测试文件用真实渲染确证）：
///   `Stretch="Uniform"` 的 `Image` 在 Avalonia 里 **Bounds 会自动收缩为「图片内容矩形」**
///   （400×400 图放 600×300 容器 → `Image.Bounds = (150,0,300,300)`），
///   而 `RegionCanvas` 默认铺满容器 → 两者**坐标系不同**。
///   平移时两者位移量恰好相同（所以"拖动看着没事"）；缩放时内容范围不同被同比例放大 → 分离。
///
/// **修法**：让两者 Center 对齐 + 画布宽高 = `Image.Bounds` 的宽高
///   （见 MainView.AlignCanvasToImage）→ 坐标系严格一致 → 同一个矩阵对两层通用。
///
/// 本文件用 **RenderTargetBitmap + 读像素** 验证：给两层套同一矩阵后，
/// 图片实际渲染位置与画布算出的位置一致。
/// </summary>
public class RegionCanvasSyncTests
{
    private const int CW = 600, CH = 300;

    static RegionCanvasSyncTests() => SkiaPlatform.Initialize();

    private static Bitmap WhiteSquare(int size)
    {
        using var b = new SKBitmap(size, size);
        using (var c = new SKCanvas(b)) c.Clear(SKColors.White);
        using var img = SKImage.FromBitmap(b);
        using var d = img.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream(d.ToArray());
        return new Bitmap(ms);
    }

    /// <summary>渲染并返回 y=150 行上白色像素的 x 范围。</summary>
    private static (int First, int Last) WhiteSpan(Control root)
    {
        var rtb = new RenderTargetBitmap(new PixelSize(CW, CH));
        rtb.Render(root);

        var buf = new byte[CW * CH * 4];
        var h = System.Runtime.InteropServices.GCHandle.Alloc(
            buf, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            rtb.CopyPixels(new PixelRect(0, 0, CW, CH),
                           h.AddrOfPinnedObject(), buf.Length, CW * 4);
        }
        finally { h.Free(); }

        int first = -1, last = -1;
        int row = 150 * CW * 4;
        for (int x = 0; x < CW; x++)
        {
            int o = row + x * 4;                 // BGRA
            if (buf[o] > 128 && buf[o + 1] > 128 && buf[o + 2] > 128)
            {
                if (first < 0) first = x;
                last = x;
            }
        }
        return (first, last);
    }

    /// <summary>
    /// 模拟真实布局：设完尺寸后必须让 Avalonia 重新测量/排列
    /// （真实运行时属性变化会自动触发，测试里需手动调用）。
    /// </summary>
    private static void Relayout(Control root, int w = CW, int h = CH)
    {
        root.InvalidateMeasure();
        root.Measure(new Size(w, h));
        root.Arrange(new Rect(0, 0, w, h));
    }

    /// <summary>按生产逻辑把画布对齐到图片 Bounds。</summary>
    private static void Align(Control canvas, Control image, Control root)
    {
        canvas.Width = image.Bounds.Width;
        canvas.Height = image.Bounds.Height;
        Relayout(root);
    }

    [Fact]
    public void CanvasWithStretch_WithoutExplicitSize_StillHasNonZeroBounds()
    {
        // ⚠️ **回归防护**（v0.5.33 用户报"编辑不了图片、滚轮失效"）：
        //    曾把 XAML 改成 `Center` 对齐 —— 那时未设尺寸的控件 DesiredSize 为 0
        //    → 画布 0×0 → 没有任何命中区域 → 工具画不上、滚轮也不响应。
        //    XAML 必须用 Stretch：即使对齐逻辑一次都没跑（Bounds 为 0），画布也铺满容器可交互。
        var canvas = new ImgHub.App.Controls.RegionCanvas
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };
        var root = new Grid { Width = CW, Height = CH };
        root.Children.Add(canvas);
        Relayout(root);

        Assert.True(canvas.Bounds.Width > 0, "Stretch 下画布必须有非零宽度（否则工具失效）");
        Assert.True(canvas.Bounds.Height > 0, "Stretch 下画布必须有非零高度");
        Assert.Equal(CW, canvas.Bounds.Width, 3);    // 兜底 = 铺满容器
        Assert.Equal(CH, canvas.Bounds.Height, 3);
    }

    [Fact]
    public void CanvasWithCenterAlignment_AndNoSize_WouldDegradeToZero()
    {
        // 反证：Center + 无尺寸 = 0 尺寸（这正是当初引入回归的写法）。
        // 这条测试的存在是为了**说明为什么不能用 Center**，若哪天有人改回 Center 会立刻红。
        var canvas = new ImgHub.App.Controls.RegionCanvas
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var root = new Grid { Width = CW, Height = CH };
        root.Children.Add(canvas);
        Relayout(root);

        Assert.Equal(0, canvas.Bounds.Width, 3);     // ← Center 下确实退化
        Assert.Equal(0, canvas.Bounds.Height, 3);
    }

    [Fact]
    public void AlignThenClearSize_FallsBackToFillingContainer()
    {
        // 生产逻辑的兜底：`Image.Bounds` 为 0 时必须**清掉显式尺寸**，
        // 让 Stretch 重新生效（否则会一直停在 0 或旧尺寸）。
        var canvas = new ImgHub.App.Controls.RegionCanvas
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };
        var root = new Grid { Width = CW, Height = CH };
        root.Children.Add(canvas);
        Relayout(root);

        // 模拟"对齐到某张图"
        canvas.Width = 300;
        canvas.Height = 300;
        Relayout(root);
        Assert.Equal(300, canvas.Bounds.Width, 3);

        // 模拟"图没了 / Bounds=0 → 清尺寸"
        canvas.Width = double.NaN;
        canvas.Height = double.NaN;
        Relayout(root);

        Assert.Equal(CW, canvas.Bounds.Width, 3);    // 退回铺满容器
        Assert.Equal(CH, canvas.Bounds.Height, 3);
    }

    [Fact]
    public void UniformImage_BoundsShrinksToContentRect_NotContainer()
    {
        // 本方案的**前提事实**：Image 在 Uniform 下的 Bounds 是内容矩形，不是容器。
        // 这条一旦不成立，AlignCanvasToImage 的整个依据就没了 —— 所以单独钉住。
        var img = new Image
        {
            Source = WhiteSquare(400),
            Stretch = Stretch.Uniform,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var root = new Grid { Width = CW, Height = CH };
        root.Children.Add(img);
        Relayout(root);

        // 400×400 放进 600×300 → scale = min(1.5, 0.75) = 0.75 → 300×300，居中
        Assert.Equal(300, img.Bounds.Width, 3);
        Assert.Equal(300, img.Bounds.Height, 3);
        Assert.Equal(150, img.Bounds.X, 3);        // ← 不是 0（没铺满容器）
        Assert.Equal(0, img.Bounds.Y, 3);
    }

    [Fact]
    public void CanvasAlignedToImageBounds_HasIdenticalCoordinateSystem()
    {
        // 对齐后：画布 Bounds 应与 Image.Bounds 完全相同（宽高 + 相对容器的位置）
        var img = new Image
        {
            Source = WhiteSquare(400),
            Stretch = Stretch.Uniform,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var canvas = new ImgHub.App.Controls.RegionCanvas
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var root = new Grid { Width = CW, Height = CH };
        root.Children.Add(img);
        root.Children.Add(canvas);
        Relayout(root);

        Align(canvas, img, root);

        Assert.Equal(img.Bounds.Width, canvas.Bounds.Width, 3);
        Assert.Equal(img.Bounds.Height, canvas.Bounds.Height, 3);
        Assert.Equal(img.Bounds.X, canvas.Bounds.X, 3);
        Assert.Equal(img.Bounds.Y, canvas.Bounds.Y, 3);
    }

    [Fact]
    public void Zoom_ImageRendersExactlyWhereCanvasThinksItIs()
    {
        // ⭐ 核心端到端断言：给两层套同一个 ViewMatrix（origin=TopLeft）后，
        //    图片**实际渲染**的位置必须等于画布算出的位置。
        var img = new Image
        {
            Source = WhiteSquare(400),
            Stretch = Stretch.Uniform,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var canvas = new ImgHub.App.Controls.RegionCanvas
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var root = new Grid { Width = CW, Height = CH, Background = Brushes.Black };
        root.Children.Add(img);
        root.Children.Add(canvas);
        Relayout(root);

        Align(canvas, img, root);
        canvas.ZoomBy(2.0);

        img.RenderTransformOrigin = RelativePoint.TopLeft;
        img.RenderTransform = new MatrixTransform(canvas.ViewMatrix);
        Relayout(root);

        var (first, last) = WhiteSpan(root);

        // 画布认为图片内容（左边缘 → 右边缘）映射到哪（注意加画布在容器中的偏移）
        var c = canvas.Bounds;
        var expectedFirst = new Point(0, c.Height / 2).Transform(canvas.ViewMatrix).X + c.X;
        var expectedLast = new Point(c.Width, c.Height / 2).Transform(canvas.ViewMatrix).X + c.X;

        // 渲染会裁剪到容器内，所以预期值也要夹到 [0, CW-1]
        expectedFirst = Math.Clamp(expectedFirst, 0, CW - 1);
        expectedLast = Math.Clamp(expectedLast, 0, CW - 1);

        Assert.True(Math.Abs(expectedFirst - first) <= 2,
            $"图片左边缘：画布认为 {expectedFirst:F0}，实际渲染 {first}");
        Assert.True(Math.Abs(expectedLast - last) <= 2,
            $"图片右边缘：画布认为 {expectedLast:F0}，实际渲染 {last}");
    }

    [Fact]
    public void Zoom_ThenPan_KeepsTwoLayersInSync()
    {
        // 缩放 + 平移组合下也要同步（用户报的正是"缩放后跑位"）
        var img = new Image
        {
            Source = WhiteSquare(400),
            Stretch = Stretch.Uniform,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var canvas = new ImgHub.App.Controls.RegionCanvas
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var root = new Grid { Width = CW, Height = CH, Background = Brushes.Black };
        root.Children.Add(img);
        root.Children.Add(canvas);
        Relayout(root);

        Align(canvas, img, root);
        canvas.SetViewForTest(zoom: 1.5, panX: 20, panY: 0);

        img.RenderTransformOrigin = RelativePoint.TopLeft;
        img.RenderTransform = new MatrixTransform(canvas.ViewMatrix);
        Relayout(root);

        var (first, last) = WhiteSpan(root);
        var c = canvas.Bounds;
        var expectedFirst = new Point(0, c.Height / 2).Transform(canvas.ViewMatrix).X + c.X;
        var expectedLast = new Point(c.Width, c.Height / 2).Transform(canvas.ViewMatrix).X + c.X;
        expectedFirst = Math.Clamp(expectedFirst, 0, CW - 1);
        expectedLast = Math.Clamp(expectedLast, 0, CW - 1);

        Assert.True(Math.Abs(expectedFirst - first) <= 2,
            $"缩放+平移下左边缘：预期 {expectedFirst:F0}，实际 {first}");
        Assert.True(Math.Abs(expectedLast - last) <= 2,
            $"缩放+平移下右边缘：预期 {expectedLast:F0}，实际 {last}");
    }

    [Fact]
    public void ZoomAtCursor_KeepsContentPointUnderCursorFixed()
    {
        const double w = 400, h = 300;
        var anchor = new Point(320, 90);

        var (panX, panY) = ImgHub.App.Controls.RegionCanvas.ComputeZoomPan(
            0, 0, 1.0, 2.0, w, h, anchor);

        double ImgX(double s, double z, double pan) => (s - w / 2 - pan) / z + w / 2;
        double ImgY(double s, double z, double pan) => (s - h / 2 - pan) / z + h / 2;

        Assert.Equal(ImgX(anchor.X, 1.0, 0), ImgX(anchor.X, 2.0, panX), 6);
        Assert.Equal(ImgY(anchor.Y, 1.0, 0), ImgY(anchor.Y, 2.0, panY), 6);
    }

    [Fact]
    public void ViewMatrix_And_ScreenToImage_AreMutuallyInverse()
    {
        const double w = 400, h = 300;
        foreach (var (zoom, panX, panY) in new[]
        {
            (1.0, 0.0, 0.0), (2.0, 50.0, 30.0), (0.5, -80.0, 40.0),
        })
        {
            var c = new ImgHub.App.Controls.RegionCanvas();
            c.Measure(new Size(w, h));
            c.Arrange(new Rect(0, 0, w, h));
            c.SetViewForTest(zoom, panX, panY);

            foreach (var screen in new[] { new Point(0, 0), new Point(300, 150), new Point(w, h) })
            {
                var (ix, iy) = ImgHub.App.Controls.RegionCanvas.ScreenToImage(
                    screen, zoom, panX, panY, w, h);
                var back = new Point(ix, iy).Transform(c.ViewMatrix);
                Assert.Equal(screen.X, back.X, 6);
                Assert.Equal(screen.Y, back.Y, 6);
            }
        }
    }

    [Fact]
    public void ResetView_RaisesViewChanged_SoImageLayerSyncsBack()
    {
        var c = new ImgHub.App.Controls.RegionCanvas();
        var raised = 0;
        c.ViewChanged += (_, _) => raised++;

        c.ZoomBy(2.0);
        var afterZoom = raised;
        Assert.True(afterZoom > 0);

        c.ResetView();
        Assert.True(raised > afterZoom, "复位也必须广播，否则图片层停在旧变换");
        Assert.True(c.ViewMatrix.IsIdentity);
    }
}
