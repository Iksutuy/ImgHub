using Avalonia;
using Avalonia.Media.Imaging;
using ImgHub.App.Controls;
using ImgHub.Core.Models;

namespace ImgHub.Integration.Tests;

/// <summary>
/// 标注形状与「蒙版说明」的回归（用户需求 ②③）。
///
/// ② 矩形/椭圆改为**实心填充**（此前只描边）：蒙版语义是"这片区域要改"，
///    实心才能看清覆盖范围；描边会让人以为只有那圈线生效，
///    作为 mask 送模型时也等于"没指定区域"。
/// ③ 「蒙版说明」按钮：讲清用法 / 传递流程 / 提示词怎么写
///    （内容按 provider 动态生成，避免与实际行为不符）。
/// </summary>
public class RegionShapeAndHelpTests
{
    static RegionShapeAndHelpTests() => SkiaPlatformInit();

    private static void SkiaPlatformInit()
    {
        try { Avalonia.Skia.SkiaPlatform.Initialize(); } catch { }
    }

    // ================================================================ ② 实心填充

    [Fact]
    public void Rectangle_HasSameExtentWhetherOutlineOrFilled_ButFillsInterior()
    {
        // 用导出蒙版验证"填充"：矩形中心点必须落在蒙版里。
        // 导出蒙版是"要改的区域"（alpha=0 = 要改，见 ExportMask 说明），
        // 所以这里断言中心像素属于标记区域。
        var png = ImgHub.Core.Imaging.Placeholder.Png("base", size: 200);
        var home = Path.Combine(Path.GetTempPath(), "imghub-shape-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var basePath = Path.Combine(home, "base.png");
            File.WriteAllBytes(basePath, png);

            var snap = new RegionCanvas.RegionSnapshot();
            snap.Strokes.Add(new RegionCanvas.StrokeData
            {
                R = 255, G = 0, B = 0, Size = 0.02,
                IsOutline = true, IsEllipse = false,
                X0 = 0.25, Y0 = 0.25, X1 = 0.75, Y1 = 0.75,
            });

            var canvas = new RegionCanvas
            {
                BaseImagePath = basePath,
                BrushColor = "#FF0000",
                BrushSize = 20,
            };
            canvas.Measure(new Size(200, 200));
            canvas.Arrange(new Rect(0, 0, 200, 200));
            canvas.ImportShapes(snap);

            var mask = canvas.ExportMask();
            Assert.NotNull(mask);

            // 蒙版中心（矩形正中）应为"要改" → 与四角不同
            Assert.True(MaskDiffersAtCenter(mask!, 200, 200),
                "矩形内部必须被填充（实心），中心像素应与角落不同");
        }
        finally { try { Directory.Delete(home, recursive: true); } catch { } }
    }

    /// <summary>比较蒙版中心与角落像素，判断矩形是否有填充区域。</summary>
    private static bool MaskDiffersAtCenter(byte[] mask, int w, int h)
    {
        using var ms = new MemoryStream(mask);
        using var bmp = new Bitmap(ms);
        // 用 RenderTargetBitmap 不方便直接读 Bitmap 像素 —— 改用 Skia 解码
        using var sk = SkiaSharp.SKBitmap.Decode(mask);
        if (sk is null) return false;

        var center = sk.GetPixel(w / 2, h / 2);
        var corner = sk.GetPixel(2, 2);
        return center != corner;
    }

    // ================================================================ ③ 蒙版说明

    [Fact]
    public void MaskHelp_CoversAllFourQuestions_ForMaskProvider()
    {
        // 说明必须回答用户的三个问题：怎么用 / 传给谁（流程）/ 提示词怎么写。
        var text = string.Join("\n", ImgHub.App.ViewModels.MainViewModel
            .BuildMaskHelpLines(ApiProvider.Apimart)
            .Select(l => l.Text));

        Assert.Contains("怎么画", text);            // 用法
        Assert.Contains("传递流程", text);          // 传给服务端
        Assert.Contains("提示词怎么写", text);      // 提示词指导
        Assert.Contains("APIMart", text);           // 当前 provider（动态）
        Assert.Contains("mask_url", text);          // 该 provider 的真实通道
        Assert.Contains("Alpha", text);             // 蒙版格式要求
        Assert.Contains("例：", text);              // 正例
        Assert.Contains("反例", text);              // 反例（避免用户重写整段画面）
    }

    [Fact]
    public void MaskHelp_ForNonMaskProvider_SaysSoHonestly()
    {
        // ⚠️ 关键：不能对所有 provider 都说"会发蒙版" —— OpenRouter 文档没有 mask 字段。
        // 文案与实际行为必须一致（这正是 RegionEditTip 踩过的坑）。
        var text = string.Join("\n", ImgHub.App.ViewModels.MainViewModel
            .BuildMaskHelpLines(ApiProvider.OpenRouter)
            .Select(l => l.Text));

        Assert.Contains("没有 mask 字段", text);
        Assert.Contains("标注合成图", text);
        Assert.DoesNotContain("mask_url", text);
        Assert.DoesNotContain("/images/edits", text);
    }

    [Fact]
    public void MaskHelp_OpenAiMentionsItsOwnChannel_NotApimart()
    {
        // OpenAI 走 /images/edits 的 multipart mask，与 APIMart 的 mask_url 不同 ——
        // 说明必须按 provider 给对（否则用户按错的通道去排查）。
        var text = string.Join("\n", ImgHub.App.ViewModels.MainViewModel
            .BuildMaskHelpLines(ApiProvider.OpenAi)
            .Select(l => l.Text));

        Assert.Contains("/images/edits", text);
        Assert.DoesNotContain("mask_url", text);
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
