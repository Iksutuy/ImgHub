using Avalonia;
using ImgHub.App.Controls;

namespace ImgHub.Integration.Tests;

/// <summary>
/// 标注编辑的视图变换回归（用户报的 ① 缩放锚点 / ③ 两层错位 / ④ 退出复位）。
///
/// 这些是**纯数学/状态**逻辑，不需要窗口即可验证 —— 正是它们出错才导致
/// 「滚轮缩放跑偏」「标注层与图片层位移」这类只能在 UI 上肉眼发现的问题。
/// </summary>
public class RegionCanvasViewTests
{
    // ================================================================ ① 以鼠标点为锚点缩放

    [Fact]
    public void ZoomAtAnchor_KeepsImagePointUnderCursorFixed()
    {
        // ① 核心契约：锚点处（鼠标下）的**图像内容**在缩放前后不动。
        // 屏幕坐标 ↔ 图像坐标：s = center + (i - center)·z + pan
        const double w = 400, h = 300, zoom = 1.0;
        var anchor = new Point(320, 90);          // 右上区域（偏离中心）

        var (panX, panY) = RegionCanvas.ComputeZoomPan(
            panX: 0, panY: 0, oldZoom: zoom, newZoom: 2.0,
            width: w, height: h, anchor: anchor);

        // 反解出锚点下的图像坐标（缩放前 / 缩放后必须相同）
        double ImageX(double s, double z, double pan) => (s - w / 2 - pan) / z + w / 2;
        double ImageY(double s, double z, double pan) => (s - h / 2 - pan) / z + h / 2;

        var beforeX = ImageX(anchor.X, 1.0, 0);
        var beforeY = ImageY(anchor.Y, 1.0, 0);
        var afterX = ImageX(anchor.X, 2.0, panX);
        var afterY = ImageY(anchor.Y, 2.0, panY);

        Assert.Equal(beforeX, afterX, 6);
        Assert.Equal(beforeY, afterY, 6);
    }

    [Fact]
    public void ZoomAtAnchor_ShrinkAlsoKeepsAnchorFixed()
    {
        // 双向都要成立（缩小时同样不能跑偏）
        const double w = 640, h = 480;
        var anchor = new Point(80, 400);

        var (panX, panY) = RegionCanvas.ComputeZoomPan(
            panX: 30, panY: -20, oldZoom: 2.0, newZoom: 1.0,
            width: w, height: h, anchor: anchor);

        double ImageX(double s, double z, double pan) => (s - w / 2 - pan) / z + w / 2;
        double ImageY(double s, double z, double pan) => (s - h / 2 - pan) / z + h / 2;

        Assert.Equal(ImageX(anchor.X, 2.0, 30), ImageX(anchor.X, 1.0, panX), 6);
        Assert.Equal(ImageY(anchor.Y, 2.0, -20), ImageY(anchor.Y, 1.0, panY), 6);
    }

    [Fact]
    public void ZoomWithoutAnchor_IsCentered_AndDoesNotShiftPan()
    {
        // 无锚点（按钮缩放）必须与旧行为一致：pan 不变，只变 zoom
        var (panX, panY) = RegionCanvas.ComputeZoomPan(
            panX: 12, panY: -7, oldZoom: 1.0, newZoom: 1.25,
            width: 400, height: 300, anchor: null);

        Assert.Equal(12, panX, 6);
        Assert.Equal(-7, panY, 6);
    }

    [Fact]
    public void ZoomAtCenter_OnlyScalesExistingPan()
    {
        // 锚点恰在中心 → d = 0，公式退化为 pan' = pan·r（r = z'/z）。
        // ⚠️ 新语义下 pan 是**屏幕像素**：放大 2 倍时旧 pan 也要同比例放大，
        //    否则原先平移过的图像会在缩放瞬间"跳"回一部分（这正是旧实现的观感问题之一）。
        var (panX, panY) = RegionCanvas.ComputeZoomPan(
            panX: 5, panY: 5, oldZoom: 1.0, newZoom: 2.0,
            width: 400, height: 300, anchor: new Point(200, 150));

        Assert.Equal(10, panX, 6);
        Assert.Equal(10, panY, 6);
    }

    [Fact]
    public void ZoomAtAnchor_ThenRescaleBack_RestoresOriginalPan()
    {
        // 放大再缩回原倍数（同一锚点）应回到原点 —— 说明变换群自洽、没有累积漂移
        const double w = 400, h = 300;
        var anchor = new Point(310, 70);

        var (px1, py1) = RegionCanvas.ComputeZoomPan(0, 0, 1.0, 2.0, w, h, anchor);
        var (px2, py2) = RegionCanvas.ComputeZoomPan(px1, py1, 2.0, 1.0, w, h, anchor);

        Assert.Equal(0, px2, 6);
        Assert.Equal(0, py2, 6);
    }

    [Fact]
    public void ZoomAtAnchor_ZeroSize_DoesNotProduceNaN()
    {
        // 布局尚未完成时 Bounds 为 0 → 不能算出 NaN 污染后续变换
        var (panX, panY) = RegionCanvas.ComputeZoomPan(
            panX: 3, panY: 4, oldZoom: 1.0, newZoom: 2.0,
            width: 0, height: 0, anchor: new Point(10, 10));

        Assert.Equal(3, panX, 6);
        Assert.Equal(4, panY, 6);
    }

    // ================================================================ ③ 两层变换一致性

    [Fact]
    public void BuildMatrix_And_ScreenToImage_AreMutuallyInverse()
    {
        // ⚠️ 本轮发现的**更根本 bug**（超出用户报的 4 项）：旧 BuildMatrix 把 pan 放在缩放
        //    之内（等价于屏幕平移 -pan·z），而 ToNormalized / 拖拽逻辑都按**屏幕单位 pan** 编写
        //    → 三者不自洽：点击处与落笔处偏移，缩放后两层错位。
        //    实测（修前）：w=400,h=300,z=2,pan=(50,30) 时点 (300,150) 反解后映回是 (150,60)。
        //
        // 契约：对任意 (zoom, pan)，image→screen→image 必须回到原值。
        // 这里断言的是**生产代码真实的 ViewMatrix**（不是复刻公式），避免假绿。
        const double w = 400, h = 300;
        foreach (var (zoom, panX, panY) in new[]
        {
            (1.0, 0.0, 0.0),
            (2.0, 50.0, 30.0),
            (0.5, -80.0, 40.0),
            (3.7, 123.0, -45.0),
        })
        {
            var canvas = new RegionCanvas();
            canvas.Measure(new Size(w, h));
            canvas.Arrange(new Rect(0, 0, w, h));
            canvas.SetViewForTest(zoom, panX, panY);
            var m = canvas.ViewMatrix;

            foreach (var screen in new[] { new Point(0, 0), new Point(300, 150), new Point(w, h) })
            {
                var (ix, iy) = RegionCanvas.ScreenToImage(screen, zoom, panX, panY, w, h);
                var back = new Point(ix, iy).Transform(m);
                Assert.Equal(screen.X, back.X, 6);
                Assert.Equal(screen.Y, back.Y, 6);
            }
        }
    }

    [Fact]
    public void ViewMatrix_PanIsInScreenPixels_NotScaledByZoom()
    {
        // pan 的语义必须是**屏幕像素**：平移 +60 时图像在屏幕上就整体右移 60px
        //（而不是 60·zoom）。这是上面"互逆"契约的直接推论，单独钉一条防止语义漂移。
        const double w = 400, h = 300, zoom = 2.0;
        var canvas = new RegionCanvas();
        canvas.Measure(new Size(w, h));
        canvas.Arrange(new Rect(0, 0, w, h));
        canvas.SetViewForTest(zoom, panX: 60, panY: 0);

        var center = new Point(w / 2, h / 2).Transform(canvas.ViewMatrix);
        Assert.Equal(w / 2 + 60, center.X, 6);   // 恰好 +60，不是 +120
        Assert.Equal(h / 2, center.Y, 6);
    }

    [Fact]
    public void ViewMatrix_MatchesDocumentedFormula()
    {
        // ③ 的前提：RegionCanvas 渲染用的矩阵就是 ViewMatrix（XAML 侧图片层套同一矩阵）。
        // 这里钉住公式本身，防止有人"顺手改" BuildMatrix 而不同步图片层。
        var canvas = new RegionCanvas();
        canvas.Measure(new Size(400, 300));
        canvas.Arrange(new Rect(0, 0, 400, 300));

        // 未缩放未平移 → 单位矩阵
        Assert.True(canvas.ViewMatrix.IsIdentity, "初始视图应为单位矩阵");

        canvas.ZoomBy(2.0);
        var m = canvas.ViewMatrix;

        // 中心点必须保持不变（矩阵的固定点 = 控件中心）
        var center = new Point(200, 150).Transform(m);
        Assert.Equal(200, center.X, 6);
        Assert.Equal(150, center.Y, 6);

        // 左上角 (0,0) → 绕中心放大 2 倍 = (-200, -150)
        var topLeft = new Point(0, 0).Transform(m);
        Assert.Equal(-200, topLeft.X, 6);
        Assert.Equal(-150, topLeft.Y, 6);
    }

    [Fact]
    public void RenderTransformOrigin_DefaultIsCenter_SoImageLayerMustOverrideIt()
    {
        // ③ 的根因留档：Avalonia 的 RenderTransformOrigin 默认是 **50%,50%**（Center），
        // 不是 (0,0)。Avalonia 会组合成 T(origin)·M·T(-origin)，
        // 而 RegionCanvas.Render 直接 PushTransform(M) —— 于是同 zoom 下必然差一层平移。
        // 修法是让 Image 显式使用 TopLeft；这里钉住"默认值确实是 Center"这一事实，
        // 防止将来有人在 XAML 里省掉这行却以为它是 (0,0)。
        var img = new Avalonia.Controls.Image();
        var origin = img.RenderTransformOrigin;

        Assert.Equal(0.5, origin.Point.X, 6);
        Assert.Equal(0.5, origin.Point.Y, 6);
        Assert.NotEqual(RelativePoint.TopLeft.Point.X, origin.Point.X);
    }

    [Fact]
    public void CenterOriginComposition_WouldShiftImage_WhileTopLeftDoesNot()
    {
        // 用数值说明"错位"到底有多大：w=400,h=300,zoom=2
        //   · origin=Center → 中心点被映射到 (0,0)（整体左上偏移 200/150）
        //   · origin=TopLeft → 与 RegionCanvas 完全一致（中心仍为中心）
        const double w = 400, h = 300, zoom = 2.0;
        var m = Matrix.CreateTranslation(-w / 2, -h / 2)
              * Matrix.CreateScale(zoom, zoom)
              * Matrix.CreateTranslation(w / 2, h / 2);

        var c = new Point(w / 2, h / 2);
        Matrix WithOrigin(Point origin)
            => Matrix.CreateTranslation(-origin.X, -origin.Y) * m
             * Matrix.CreateTranslation(origin.X, origin.Y);

        var centerOrigin = c.Transform(WithOrigin(new Point(w / 2, h / 2)));
        var topLeftOrigin = c.Transform(WithOrigin(new Point(0, 0)));

        Assert.Equal(0, centerOrigin.X, 6);        // ← 错位：中心跑到左上角
        Assert.Equal(0, centerOrigin.Y, 6);
        Assert.Equal(w / 2, topLeftOrigin.X, 6);   // ← 正确：与标注层一致
        Assert.Equal(h / 2, topLeftOrigin.Y, 6);
    }

    // ================================================================ ④ 退出编辑复位

    [Fact]
    public void ResetView_ReturnsToIdentityMatrix()
    {
        // ④ 的"复位"底层依赖：ResetView 后视图矩阵必须是单位矩阵
        var canvas = new RegionCanvas();
        canvas.Measure(new Size(400, 300));
        canvas.Arrange(new Rect(0, 0, 400, 300));

        canvas.ZoomBy(3.0, new Point(50, 60));
        Assert.False(canvas.ViewMatrix.IsIdentity);

        canvas.ResetView();

        Assert.True(canvas.ViewMatrix.IsIdentity);
        Assert.Equal(1.0, canvas.Zoom, 6);
    }

    [Fact]
    public void ResetView_RaisesViewChanged_SoImageLayerSyncsBack()
    {
        // ④ 关键：ResetView 若不广播，图片层会停留在旧变换 → 复位后反而错位
        var canvas = new RegionCanvas();
        var raised = 0;
        canvas.ViewChanged += (_, _) => raised++;

        canvas.ZoomBy(2.0);
        var afterZoom = raised;
        Assert.True(afterZoom > 0, "缩放必须广播（否则图片层不跟随）");

        canvas.ResetView();
        Assert.True(raised > afterZoom, "复位也必须广播（否则图片层停在旧变换）");
    }

    [Fact]
    public void ClearAnnotationState_ResetsViewModelAnnotationFields()
    {
        // ④ 第二半：取消编辑时 VM 侧引用的标注/蒙版路径必须清空，
        // 否则下一轮编辑会复用**上一轮的旧蒙版**（同图重复编辑时归属校验会通过）。
        var home = Path.Combine(Path.GetTempPath(), "imghub-anno-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var vm = new ImgHub.App.ViewModels.MainViewModel(new ImgHub.App.Services.AppServices
            {
                Session = new ImgHub.Core.Storage.Session(home),
                Http = new ImgHub.Core.Http.HttpJsonClient(),
                ImageApi = new ImgHub.Core.Services.ImageApi(
                    ImgHub.Core.Models.ApiProvider.OpenRouter,
                    new ImgHub.Core.Http.HttpJsonClient()),
                Polish = new ImgHub.Core.Services.PolishService(new ImgHub.Core.Http.HttpJsonClient()),
                Storage = new NoopStorage(),
                Platform = new NoopPlatform(),
                ModelStats = new ImgHub.Core.Services.ModelStatsService(home),
                Pending = new ImgHub.Core.Storage.PendingTaskStore(home),
            });

            // 造出"已导出过标注与蒙版"的状态
            vm.SetAnnotatedImage(new byte[] { 1, 2, 3 }, regionCount: 3);
            vm.SetAnnotatedMask(new byte[] { 4, 5, 6 }, baseImagePath: "x.png");
            vm.RegionCount = 3;

            Assert.NotEqual("", vm.AnnotatedPath);
            Assert.NotEqual("", vm.AnnotatedMaskPath);

            vm.ClearAnnotationState();

            Assert.Equal("", vm.AnnotatedPath);
            Assert.Equal("", vm.AnnotatedMaskPath);
            Assert.Equal(0, vm.RegionCount);
        }
        finally { try { Directory.Delete(home, recursive: true); } catch { } }
    }

    private sealed class NoopStorage : ImgHub.App.Services.IPlatformStorage
    {
        public Task<ImgHub.App.Services.PlatformOpResult> SaveToGalleryAsync(
            byte[] data, string fileName, string mediaType)
            => Task.FromResult(ImgHub.App.Services.PlatformOpResult.Ok(fileName));
        public Task<ImgHub.App.Services.PlatformOpResult> OpenInExternalViewerAsync(string path)
            => Task.FromResult(ImgHub.App.Services.PlatformOpResult.Ok(path));
        public Task<ImgHub.App.Services.PlatformOpResult> RevealInFileManagerAsync(string path)
            => Task.FromResult(ImgHub.App.Services.PlatformOpResult.Ok(path));
        public Task<string?> GetGalleryDirAsync() => Task.FromResult<string?>(null);
    }

    private sealed class NoopPlatform : ImgHub.App.Services.IPlatformInfo
    {
        public string PlatformName => "Test";
        public string Version => "1.0";
    }
}
