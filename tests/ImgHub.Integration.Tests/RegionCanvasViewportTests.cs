using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Skia;
using SkiaSharp;

namespace ImgHub.Integration.Tests;

/// <summary>
/// 标注层的**坐标语义**与「缩放不跑位」回归（v0.5.34 用户澄清后定稿）。
///
/// ## 语义（两条独立边界，别再混）
/// | 概念 | 范围 | 实现 |
/// |---|---|---|
/// | **可查看范围** | 整个预览**灰区** | 画布 `Bounds` 铺满容器 + `ClipToBounds = false`（父 Border 负责裁到灰区） |
/// | **可绘制范围** | **图片**尺寸 | 归一化基准 = `ImageContentRect`（图片在灰区中的矩形） |
///
/// ## 用户报的两个现象与根因
/// 1. 「放大/移动后超出起始位置就变形、移位」
///    → 矩阵少了图片原点偏移（图片在灰区里居中，不是从 (0,0) 起）。
/// 2. 「蒙版超出起始上界后，上面的部分消失了」
///    → 画布是 `ClipToBounds = true` 且尺寸 = 图片尺寸 → 超出图片的标注被裁掉。
/// </summary>
public class RegionCanvasViewportTests
{
    static RegionCanvasViewportTests() => SkiaPlatform.Initialize();

    private const int CW = 600, CH = 400;

    [Fact]
    public void WireCanvasFollow_Subscription_ActuallyFiresWhenImageBoundsChanges()
    {
        // ⚠️ 这条钉住"订阅链路"本身：模拟 MainView.WireCanvasFollow 的接线方式，
        //    验证 Image.Bounds 变化**真的**会把新矩形推给画布。
        //    （此前测试都用手工 Apply，掩盖了"订阅没接上"这类问题。）
        var img = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var canvas = new ImgHub.App.Controls.RegionCanvas
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var root = new Grid { Width = CW, Height = CH };
        root.Children.Add(img);
        root.Children.Add(canvas);
        Relayout(root);

        // ── 与生产代码同构的接线（MainView.WireCanvasFollow）──
        void Apply() => canvas.ImageContentRect = img.Bounds;
        img.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty ||
                e.Property == Layoutable.WidthProperty ||
                e.Property == Layoutable.HeightProperty)
                Apply();
        };
        Apply();

        // 位图"异步就绪"：此后 Bounds 才变成内容尺寸
        img.Source = Bmp(400, 400);
        Relayout(root);

        Assert.Equal(img.Bounds.Width, canvas.ImageContentRect.Width, 1);
        Assert.Equal(img.Bounds.Height, canvas.ImageContentRect.Height, 1);
        Assert.True(img.Bounds.Width > 0, "前提：图片已有非零尺寸");

        // 再 resize 容器 → 订阅还要继续生效
        root.Width = 900;
        root.Height = 700;
        Relayout(root, 900, 700);

        Assert.Equal(img.Bounds.Width, canvas.ImageContentRect.Width, 1);
        Assert.Equal(img.Bounds.X, canvas.ImageContentRect.X, 1);
    }

    [Fact]
    public void ImageMatrix_ExcludesImageOrigin_ViewMatrixIncludesIt()
    {
        // 两者关系：ViewMatrix == ImageMatrix · T(图片原点)
        // 写反会导致"图片在灰区里居中时缩放偏移"（用户报的"变形、移位"）。
        var (canvas, img, _) = Build(400, 400);
        canvas.SetViewForTest(2.0, 30, -15);

        var r = canvas.ImageContentRect;
        Assert.True(r.X > 0 || r.Y > 0, "前提：图片在灰区里不是从 (0,0) 起（有原点偏移）");

        var local = new Point(r.Width / 2, r.Height / 2);
        var viaView = local.Transform(canvas.ViewMatrix);

        var viaImageRaw = local.Transform(canvas.ImageMatrix);
        var viaImagePlusOrigin = new Point(viaImageRaw.X + img.Bounds.X,
                                          viaImageRaw.Y + img.Bounds.Y);

        Assert.Equal(viaView.X, viaImagePlusOrigin.X, 4);
        Assert.Equal(viaView.Y, viaImagePlusOrigin.Y, 4);
    }

    // ================================================================ 可绘制范围 = 图片

    [Fact]
    public void ImportShapes_ClampsOutOfRangeCoordinates()
    {
        // v0.5.35：旧快照可能含越界坐标（v0.5.34 之前在图片外也能落笔）
        // → 导入时必须钳制，否则框会重新画到灰区里
        var (canvas, _, _) = Build(400, 400);

        var snap = new ImgHub.App.Controls.RegionCanvas.RegionSnapshot();
        snap.Strokes.Add(new ImgHub.App.Controls.RegionCanvas.StrokeData
        {
            R = 255, G = 0, B = 0, Size = 0.02,
            IsOutline = true, IsEllipse = false,
            X0 = -0.5, Y0 = -0.8,      // 越界（左上）
            X1 = 1.7, Y1 = 2.3,        // 越界（右下）
        });
        canvas.ImportShapes(snap);

        var back = canvas.ExportShapes();
        Assert.Single(back.Strokes);
        var s = back.Strokes[0];
        Assert.InRange(s.X0, 0, 1);
        Assert.InRange(s.Y0, 0, 1);
        Assert.InRange(s.X1, 0, 1);
        Assert.InRange(s.Y1, 0, 1);
    }

    [Fact]
    public void ImportShapes_ClampsStrokePoints()
    {
        var (canvas, _, _) = Build(400, 400);

        var snap = new ImgHub.App.Controls.RegionCanvas.RegionSnapshot();
        var stroke = new ImgHub.App.Controls.RegionCanvas.StrokeData
        { R = 255, G = 0, B = 0, Size = 0.02, IsOutline = false };
        stroke.Points.AddRange(new double[] { -1, 0.5, 0.5, -1, 0.5, 0.5, 3, 3 });
        snap.Strokes.Add(stroke);
        canvas.ImportShapes(snap);

        var back = canvas.ExportShapes();
        Assert.Single(back.Strokes);
        foreach (var v in back.Strokes[0].Points)
            Assert.InRange(v, 0, 1);
    }

    [Fact]
    public void ExportMask_OnlyCoversImagePixels_NeverBeyond()
    {
        // 即使形状坐标越界（模拟极端情况），导出的蒙版也**只在图片像素范围内**生效：
        // 导出用的 SKCanvas 尺寸 = 图片像素 → 超出部分被 Skia 自然丢弃。
        var home = Path.Combine(Path.GetTempPath(), "imghub-clamp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var basePng = Path.Combine(home, "base.png");
            File.WriteAllBytes(basePng, Png(200, 200));

            var snap = new ImgHub.App.Controls.RegionCanvas.RegionSnapshot();
            snap.Strokes.Add(new ImgHub.App.Controls.RegionCanvas.StrokeData
            {
                R = 255, G = 0, B = 0, Size = 0.02,
                IsOutline = true, IsEllipse = false,
                X0 = 0.25, Y0 = 0.25, X1 = 0.75, Y1 = 0.75,
            });

            var canvas = new ImgHub.App.Controls.RegionCanvas
            {
                BaseImagePath = basePng, BrushColor = "#FF0000", BrushSize = 20,
            };
            canvas.Measure(new Size(200, 200));
            canvas.Arrange(new Rect(0, 0, 200, 200));
            canvas.ImportShapes(snap);

            var mask = canvas.ExportMask();
            Assert.NotNull(mask);

            // 蒙版尺寸必须与图片一致（不能因为画布铺满灰区而变大）
            using var sk = SKBitmap.Decode(mask);
            Assert.NotNull(sk);
            Assert.Equal(200, sk!.Width);
            Assert.Equal(200, sk.Height);
        }
        finally { try { Directory.Delete(home, recursive: true); } catch { } }
    }

    private static byte[] Png(int w, int h, byte r = 255, byte g = 255, byte b = 255)
    {
        using var bm = new SKBitmap(w, h);
        using (var c = new SKCanvas(bm)) c.Clear(new SKColor(r, g, b));
        using var i = SKImage.FromBitmap(bm);
        using var d = i.Encode(SKEncodedImageFormat.Png, 100);
        return d.ToArray();
    }

    private static Bitmap Bmp(int w, int h) => new(new MemoryStream(Png(w, h)));

    private static void Relayout(Control root, int w = CW, int h = CH)
    {
        root.InvalidateMeasure();
        root.Measure(new Size(w, h));
        root.Arrange(new Rect(0, 0, w, h));
    }

    /// <summary>搭一个"灰区 + 居中图片 + 铺满的画布"的树（与真实 XAML 同构）。</summary>
    private static (ImgHub.App.Controls.RegionCanvas Canvas, Image Img, Grid Root) Build(int imgW, int imgH)
    {
        var img = new Image
        {
            Source = Bmp(imgW, imgH),
            Stretch = Stretch.Uniform,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };
        var canvas = new ImgHub.App.Controls.RegionCanvas
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };
        var root = new Grid { Width = CW, Height = CH, Background = Brushes.Gray };
        root.Children.Add(img);
        root.Children.Add(canvas);
        Relayout(root);

        // 模拟 MainView.WireCanvasFollow：订阅 Image.Bounds → 写 ImageContentRect
        void Apply() => canvas.ImageContentRect = img.Bounds;
        img.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty ||
                e.Property == Layoutable.WidthProperty ||
                e.Property == Layoutable.HeightProperty)
                Apply();
        };
        Apply();
        Relayout(root);

        return (canvas, img, root);
    }

    // ================================================================ 边界语义

    [Fact]
    public void Canvas_FillsWholeViewport_NotJustImage()
    {
        // **可查看范围 = 整个灰区**：画布必须铺满容器（否则超出的标注会被裁 → 用户报"消失"）
        var (canvas, img, _) = Build(400, 400);

        Assert.Equal(CW, canvas.Bounds.Width, 1);
        Assert.Equal(CH, canvas.Bounds.Height, 1);
        Assert.NotEqual(img.Bounds.Width, canvas.Bounds.Width);   // 与图片**不同**（这正是要点）
    }

    [Fact]
    public void Canvas_DoesNotClipToBounds_SoAnnotationsOutsideImageStayVisible()
    {
        // ⚠️ 核心回归：`ClipToBounds` 必须为 false。
        //    true 时缩放后超出图片的标注会被裁掉（用户报"上面部分消失了"）。
        var (canvas, _, _) = Build(400, 400);
        Assert.False(canvas.ClipToBounds,
            "画布不能裁剪：超出图片的标注必须仍然可见（可见范围由父 Border 界定）");
    }

    [Fact]
    public void ImageContentRect_EqualsImageBounds_AndTracksIt()
    {
        // **可绘制范围 = 图片尺寸**：基准矩形必须等于图片在灰区中的位置+尺寸
        var (canvas, img, root) = Build(400, 400);

        Assert.Equal(img.Bounds.Width, canvas.ImageContentRect.Width, 1);
        Assert.Equal(img.Bounds.Height, canvas.ImageContentRect.Height, 1);
        Assert.Equal(img.Bounds.X, canvas.ImageContentRect.X, 1);
        Assert.Equal(img.Bounds.Y, canvas.ImageContentRect.Y, 1);

        // 换成宽图 → 基准随之变化（不同宽高比的内容矩形不同）
        var before = canvas.ImageContentRect;
        img.Source = Bmp(600, 200);
        Relayout(root);
        Assert.NotEqual(before.Width, canvas.ImageContentRect.Width);
        Assert.Equal(img.Bounds.Width, canvas.ImageContentRect.Width, 1);
    }

    // ================================================================ 缩放不跑位

    [Fact]
    public void ImagePoint_MapsToSameScreenPosition_ForBothLayers_AtAnyZoom()
    {
        // ⭐⭐ 决定性断言：同一个"图片局部坐标"，经
        //   · 画布矩阵（含图片原点偏移）
        //   · 图片矩阵（不含偏移，因 Image 已被布局放在该原点）
        // 必须落在**同一屏幕位置**。
        // 之前缺图片原点偏移 → 图片在灰区居中时，缩放中心跑到灰区中心 → 偏移。
        var (canvas, img, _) = Build(400, 400);   // 400x400 放进 600x400 → 内容 400x400 居中 x=100

        foreach (var zoom in new[] { 1.0, 1.5, 2.5, 0.6 })
        {
            canvas.SetViewForTest(zoom, 0, 0);
            img.RenderTransformOrigin = RelativePoint.TopLeft;
            img.RenderTransform = new MatrixTransform(canvas.ImageMatrix);

            // 图片局部坐标下的一个点（图片的 1/4, 3/4 处）
            var local = new Point(canvas.ImageContentRect.Width * 0.25,
                                  canvas.ImageContentRect.Height * 0.75);

            // 画布路径：ViewMatrix（含 origin）
            var viaCanvas = local.Transform(canvas.ViewMatrix);

            // 图片路径：ImageMatrix（不含 origin）+ 图片自身的布局偏移
            var viaImage = local.Transform(canvas.ImageMatrix);
            var imageScreen = new Point(viaImage.X + img.Bounds.X, viaImage.Y + img.Bounds.Y);

            Assert.True(Math.Abs(viaCanvas.X - imageScreen.X) <= 1.5,
                $"zoom={zoom}：X 不同步（画布 {viaCanvas.X:F1} vs 图片 {imageScreen.X:F1}）");
            Assert.True(Math.Abs(viaCanvas.Y - imageScreen.Y) <= 1.5,
                $"zoom={zoom}：Y 不同步（画布 {viaCanvas.Y:F1} vs 图片 {imageScreen.Y:F1}）");
        }
    }

    [Fact]
    public void ZoomAtCursor_KeepsImagePointUnderCursorFixed_WithCenteredImage()
    {
        // 图片在灰区里**居中**（有 origin 偏移）时，滚轮锚点缩放仍必须保持鼠标下的图像点不动
        var (canvas, _, _) = Build(400, 400);
        var cursor = new Point(430, 120);   // 灰区内、图片上的某点

        var r = canvas.ImageContentRect;
        var before = ImgHub.App.Controls.RegionCanvas.ScreenToImage(
            cursor, 1.0, 0, 0, r.Width, r.Height, r.X, r.Y);

        canvas.ZoomBy(2.0, cursor);

        var pan = canvas.ViewPanForTest;
        var after = ImgHub.App.Controls.RegionCanvas.ScreenToImage(
            cursor, canvas.Zoom, pan.PanX, pan.PanY, r.Width, r.Height, r.X, r.Y);

        Assert.Equal(before.X, after.X, 4);
        Assert.Equal(before.Y, after.Y, 4);
    }

    [Fact]
    public void ViewMatrix_And_ScreenToImage_AreMutuallyInverse_WithOriginOffset()
    {
        // 带图片原点偏移时，矩阵与反解仍须严格互逆
        var (canvas, _, _) = Build(400, 400);
        var r = canvas.ImageContentRect;

        foreach (var (zoom, panX, panY) in new[] { (1.0, 0.0, 0.0), (2.0, 40.0, -25.0), (0.5, -70.0, 33.0) })
        {
            canvas.SetViewForTest(zoom, panX, panY);

            foreach (var screen in new[] { new Point(0, 0), new Point(300, 200), new Point(CW, CH) })
            {
                var (lx, ly) = ImgHub.App.Controls.RegionCanvas.ScreenToImage(
                    screen, zoom, panX, panY, r.Width, r.Height, r.X, r.Y);
                var back = new Point(lx, ly).Transform(canvas.ViewMatrix);

                Assert.Equal(screen.X, back.X, 4);
                Assert.Equal(screen.Y, back.Y, 4);
            }
        }
    }

    [Fact]
    public void ResetView_ReturnsToIdentity_AndRaisesChanged()
    {
        var (canvas, _, _) = Build(400, 400);
        var raised = 0;
        canvas.ViewChanged += (_, _) => raised++;

        canvas.ZoomBy(2.5);
        Assert.False(canvas.ViewMatrix.IsIdentity);
        var afterZoom = raised;

        canvas.ResetView();
        Assert.Equal(1.0, canvas.Zoom, 6);
        Assert.True(raised > afterZoom, "复位必须广播（否则图片层停在旧变换）");

        // ⚠️ 注意：`ViewMatrix` **含图片原点偏移**（图片在灰区里居中时不为单位矩阵）。
        //    复位后应满足「ViewMatrix == T(图片原点)」；图片层用的 `ImageMatrix`
        //    不含偏移，才是真正的单位矩阵（→ `SyncPreviewTransform` 会移除变换）。
        var r = canvas.ImageContentRect;
        var expected = Matrix.CreateTranslation(r.X, r.Y);
        Assert.Equal(expected.M11, canvas.ViewMatrix.M11, 6);
        Assert.Equal(expected.M31, canvas.ViewMatrix.M31, 6);
        Assert.Equal(expected.M32, canvas.ViewMatrix.M32, 6);

        Assert.True(canvas.ImageMatrix.IsIdentity,
            "复位后 ImageMatrix 应为单位矩阵（图片层据此移除 RenderTransform）");
        var pan = canvas.ViewPanForTest;
        Assert.Equal(0, pan.PanX, 6);
        Assert.Equal(0, pan.PanY, 6);
    }

    [Fact]
    public void ImageContentRectChange_RaisesViewChanged()
    {
        // 切图（宽高比变化）→ 基准矩形变化 → 必须广播，否则图片层停在旧矩阵
        var (canvas, _, _) = Build(400, 400);
        var raised = 0;
        canvas.ViewChanged += (_, _) => raised++;

        canvas.ImageContentRect = new Rect(50, 0, 500, 400);

        Assert.True(raised > 0, "基准矩形变化必须触发 ViewChanged");
    }
}
