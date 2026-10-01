using ImgHub.App.Services;
using ImgHub.App.ViewModels;
using ImgHub.Core;
using ImgHub.Core.Http;
using ImgHub.Core.Imaging;
using ImgHub.Core.Models;
using ImgHub.Core.Services;
using ImgHub.Core.Storage;

namespace ImgHub.Integration.Tests;

/// <summary>
/// 「参考图 / 历史编辑」端到端契约测试（v5.27.0）。
///
/// 为什么必须在这一层测（而不是只测 Core 的 payload 构造）：
///   用户报告：「我上传的参考图也好、编辑历史图片也好，出来的图片总是不相关的」。
///   根因在 **ViewModel 的编排**里，不在 payload 构造里：
///     · 点「生成」时 refs 恒为 null（参考图被整个丢弃，压根没进请求）；
///     · 编辑目标恒取历史最新一张（在历史里点选旧图无效）。
///   这两条只有走到 ViewModel 才会暴露，且**不会抛异常**——必须断言"实际发出的请求内容"。
///   所以这里注入一个记录型假 API，直接检查它收到的 GenRequest。
/// </summary>
/// <remarks>
/// ⚠️ 构造函数会设进程级环境变量 <c>IMGHUB_HOME</c>，故纳入串行集合
///    （见 <see cref="EnvironmentVariableTests"/>），避免与其它同类测试互相覆盖。
/// </remarks>
[Collection(EnvironmentVariableTests.Name)]
public class GenerationRequestContractTests : IDisposable
{
    private readonly string _home;
    private readonly RecordingApi _api = new();
    private readonly AppServices _svc;
    private readonly MainViewModel _vm;

    public GenerationRequestContractTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "imghub-req-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("IMGHUB_HOME", _home);

        var session = new Session(_home);
        session.Config.Offline = false;     // 要走假 API（不联网、不花钱）
        session.Config.Provider = "apimart";
        session.Config.Model = Catalog.DefaultModelApimart;
        session.Config.Quality = "low";
        session.Sanitize();
        // 写入假 key → CanRun 为 true（否则生成会被前置检查挡掉，测不到请求内容）
        // 两个 provider 都写：测试里会切换 provider 验证各自行为
        session.SaveKey("sk-fake-for-test", ApiProvider.Apimart);
        session.SaveKey("sk-or-v1-fake-for-test", ApiProvider.OpenRouter);

        _svc = new AppServices
        {
            Session = session,
            Http = new HttpJsonClient(),
            ImageApi = _api,
            // 切换 provider 时也会走工厂 → 始终拿到同一个记录型假实现
            ImageApiFactory = (_, _, _) => _api,
            Polish = new PolishService(new HttpJsonClient()),
            Storage = new FakeStorage2(),
            Platform = new FakePlatform2(),
            ModelStats = new ModelStatsService(_home),
            Pending = new PendingTaskStore(_home),
        };
        _vm = new MainViewModel(_svc) { Offline = false };
    }

    public void Dispose()
    {
        // 环境变量是进程级状态：用完清掉，避免污染同集合的后续测试。
        Environment.SetEnvironmentVariable("IMGHUB_HOME", null);
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    // ---------------------------------------------------------------- 根因①
    [Fact]
    public async Task Generate_SendsUserReferenceImages()
    {
        // 回归：点「生成」时参考图此前被完全丢弃（refs 恒为 null）
        await _vm.AddReferenceImageAsync(MakePng(120, 90), "image/png", "product.png");
        Assert.Single(_vm.RefImages);

        _vm.Prompt = "换成雪原背景，保留主体";
        await _vm.GenerateCommand.ExecuteAsync(null);

        Assert.NotNull(_api.Last);
        Assert.NotNull(_api.Last!.Refs);
        var refs = _api.Last.Refs!;
        Assert.Single(refs);
        // 关键：参考图必须**原样**送入（不被压到 1024 —— 那会削弱主体/文字细节）
        Assert.Equal(120, ImageCodec.Size(refs[0].Data)!.Value.Width);
    }

    [Fact]
    public async Task Generate_WithoutRefs_SendsNullRefs()
    {
        // 反向断言：没有参考图时不应凭空造出 refs（避免污染纯文生图）
        _vm.Prompt = "一只猫";
        await _vm.GenerateCommand.ExecuteAsync(null);
        Assert.NotNull(_api.Last);
        Assert.Null(_api.Last!.Refs);
    }

    // ---------------------------------------------------------------- 根因②
    [Fact]
    public async Task Edit_UsesTheImageSelectedInHistory_NotLatest()
    {
        // 回归：编辑目标此前恒取 Items[0]（最新一张）→ 在历史里点选旧图完全无效
        _vm.Prompt = "第一张";
        await _vm.GenerateCommand.ExecuteAsync(null);
        var first = _vm.History[0].Item;

        _vm.Prompt = "第二张";
        await _vm.GenerateCommand.ExecuteAsync(null);
        var second = _vm.History[0].Item;
        Assert.NotEqual(first.File, second.File);

        // 假 API 每次返回同样的字节 —— 这里**人为改写**第二张的内容，
        // 好让"发的到底是哪张"能够被字节断言区分出来。
        var firstPath = Path.Combine(_home, first.File);
        var secondPath = Path.Combine(_home, second.File);
        var firstBytes = await File.ReadAllBytesAsync(firstPath);
        var secondBytes = await File.ReadAllBytesAsync(secondPath);
        var distinctSecond = MakePng(96, 96);          // 与第一张不同
        await File.WriteAllBytesAsync(secondPath, distinctSecond);
        Assert.NotEqual(firstBytes, distinctSecond);

        // 在历史里**点选第一张**（触发预览切换 → 成为编辑目标）
        await _vm.ShowPreviewCommand.ExecuteAsync(first);
        Assert.Equal(first.File, _vm.CurrentItem?.File);

        _vm.Prompt = "把背景改成夜晚";
        await _vm.EditCommand.ExecuteAsync(null);

        // 编辑请求的参考图第 1 位必须是**选中的那张**，而不是最新那张
        Assert.NotNull(_api.Last);
        var refs = _api.Last!.Refs;
        Assert.NotNull(refs);
        Assert.NotEmpty(refs!);
        Assert.Equal(firstBytes, refs[0].Data);        // 发的是第一张
        Assert.NotEqual(distinctSecond, refs[0].Data); // 不是最新那张
    }

    [Fact]
    public async Task Edit_NoSelection_FallsBackToLatest()
    {
        // 没有显式点选时，回退到"最新一张"（保持原有直觉行为）
        _vm.Prompt = "底图";
        await _vm.GenerateCommand.ExecuteAsync(null);
        var latest = _vm.History[0].Item;
        // 生成成功后预览自动切到新图 → 它即当前编辑目标
        Assert.Equal(latest.File, _vm.CurrentItem?.File);

        var latestBytes = await File.ReadAllBytesAsync(Path.Combine(_home, latest.File));

        _vm.Prompt = "改一下";
        await _vm.EditCommand.ExecuteAsync(null);

        // 送入的参考图第 1 位 = 那张最新图
        Assert.NotNull(_api.Last?.Refs);
        Assert.Equal(latestBytes, _api.Last!.Refs![0].Data);
        // 注意：编辑完成后 CurrentItem 会切到"编辑结果"（这是期望行为），
        // 所以这里不再断言 CurrentItem 仍是 latest。
    }

    // ---------------------------------------------------------------- 新参数贯通
    [Fact]
    public async Task Generate_PassesAllDocumentedParams()
    {
        // 文档参数必须一路贯通到请求（否则 UI 上调了也不生效）
        _vm.Background = "transparent";
        _vm.OutputFormat = "png";
        _vm.OutputCompression = 70;
        _vm.Moderation = "low";
        _vm.Seed = "123";
        _vm.Stream = true;
        _vm.Resolution = "2k";
        _vm.ProviderSort = "price";
        _vm.ProviderOnly = "a, b";
        _vm.Prompt = "透明背景商品图";

        await _vm.GenerateCommand.ExecuteAsync(null);

        var req = _api.Last;
        Assert.NotNull(req);
        Assert.Equal("transparent", req!.Background);
        Assert.Equal(70, req.OutputCompression);
        Assert.Equal("low", req.Moderation);
        Assert.Equal("123", req.Seed);
        Assert.True(req.Stream);
        Assert.Equal("2k", req.Resolution);
        // APIMart 无 provider 路由概念 → payload 构造时会忽略；但参数本身应已收集
        Assert.NotNull(req.Routing);
        Assert.Equal(new List<string> { "a", "b" }, req.Routing!.Only);
    }

    [Fact]
    public async Task RegionEdit_SendsOriginalAsFirstRef_AndMaskViaMaskUrl()
    {
        // 回归：区域编辑此前把「标注合成图」当唯一参考图、且蒙版从未发送（mask_url 全程缺席）
        _vm.Prompt = "底图";
        await _vm.GenerateCommand.ExecuteAsync(null);
        var baseItem = _vm.History[0].Item;
        var baseBytes = await File.ReadAllBytesAsync(Path.Combine(_home, baseItem.File));

        // 模拟画布导出：合成图 + **带 Alpha 的真实蒙版**（透明=要改）
        var composite = MakePng(64, 64);
        _vm.SetAnnotatedImage(composite, regionCount: 1);
        _vm.SetAnnotatedMask(MakeAlphaMask(64, 64), Path.Combine(_home, baseItem.File));

        _vm.Prompt = "把这块换成蓝天";
        await _vm.EditWithRegionsCommand.ExecuteAsync(null);

        var req = _api.Last;
        Assert.NotNull(req);
        // ① 参考图第 1 位 = 未标注的原图（主体）
        Assert.NotNull(req!.Refs);
        Assert.Equal(baseBytes, req.Refs![0].Data);
        // ② 蒙版走独立字段（APIMart 会转成 mask_url），而不是混进参考图
        Assert.NotNull(req.Mask);
        Assert.True(Catalog.PngHasAlpha(req.Mask!));
    }

    [Fact]
    public async Task RegionEdit_MaskFromAnotherImage_IsNotSent()
    {
        // 蒙版尺寸必须与参考图第 1 位一致。若用户切换了预览图，旧蒙版不能直接发
        //（APIMart 不预校验 → 会静默产生错位的重绘区域）
        _vm.Prompt = "图 A";
        await _vm.GenerateCommand.ExecuteAsync(null);
        var itemA = _vm.History[0].Item;
        _vm.SetAnnotatedImage(MakePng(64, 64), 1);
        _vm.SetAnnotatedMask(MakeAlphaMask(64, 64), Path.Combine(_home, itemA.File));

        // 切到另一张尺寸不同的图
        _vm.Prompt = "图 B";
        await _vm.GenerateCommand.ExecuteAsync(null);
        var itemB = _vm.History[0].Item;
        await _vm.ShowPreviewCommand.ExecuteAsync(itemB);
        Assert.Equal(itemB.File, _vm.CurrentItem?.File);

        _vm.Prompt = "改这里";
        await _vm.EditWithRegionsCommand.ExecuteAsync(null);

        // 尺寸不匹配 → 应降级（不发蒙版），但仍能编辑（退回合成图方式）
        Assert.NotNull(_api.Last);
        Assert.Null(_api.Last!.Mask);
        Assert.NotNull(_api.Last.Refs);
    }

    [Fact]
    public async Task RegionEdit_OpenRouter_DropsMaskButKeepsOriginal()
    {
        // OpenRouter 文档没有 mask 字段 → 不应发明字段，改用"原图+合成图"表达区域
        _vm.Provider = ApiProvider.OpenRouter;
        _vm.Model = Catalog.DefaultModelOpenRouter;
        _vm.Prompt = "底图";
        await _vm.GenerateCommand.ExecuteAsync(null);
        var item = _vm.History[0].Item;

        _vm.SetAnnotatedImage(MakePng(64, 64), 1);
        _vm.SetAnnotatedMask(MakeAlphaMask(64, 64), Path.Combine(_home, item.File));

        _vm.Prompt = "改高亮处";
        await _vm.EditWithRegionsCommand.ExecuteAsync(null);

        Assert.Null(_api.Last!.Mask);                    // 不发蒙版
        Assert.True(_api.Last.Refs!.Count >= 2);         // 原图 + 合成图
    }

    // ---------------------------------------------------------------- 按模型收窄（v5.28.0 待办 1/2）

    [Fact]
    public async Task SwitchingToGemini_ClampsBatchNToOne()
    {
        // 回归：此前 UI 固定 1~4，切到 gemini 图像系仍可拉 4 张 → 服务端拒绝
        _vm.Provider = ApiProvider.OpenRouter;
        _vm.Model = "openai/gpt-image-2.5-flare";
        Assert.Equal(10, _vm.BatchNMax);
        _vm.BatchN = 4;

        // 切到 gemini 图像系（实测 n 上限 1）
        _vm.Model = "google/gemini-3.1-flash-image";

        Assert.Equal(1, _vm.BatchNMax);      // 上限收窄
        Assert.Equal(1, _vm.BatchN);         // 当前值也被收窄
        Assert.True(_vm.BatchNLimited);
        Assert.Contains("仅支持 1 张", _vm.BatchNHint);

        // 再切回 → 上限恢复
        _vm.Model = "openai/gpt-image-2.5-flare";
        Assert.Equal(10, _vm.BatchNMax);
    }

    [Fact]
    public void SwitchingToGemini_RestrictsAspectAndResolutionOptions()
    {
        _vm.Provider = ApiProvider.OpenRouter;
        _vm.Model = "openai/gpt-image-2.5-flare";
        _vm.Aspect = "21:9";
        _vm.Resolution = "4k";

        // ① 切到只支持 1K 的模型 → 分辨率必须回退（否则服务端拒绝）
        _vm.Model = "google/gemini-3.1-flash-lite-image";
        // ⚠️ 内部统一小写（1k/2k/4k）；大写只在发往 OpenRouter 时由
        //    Catalog.OpenRouterResolution 映射。早期断言 "1K" 固化了"下拉选不中"的 bug。
        Assert.Equal("1k", _vm.Resolution);
        Assert.Single(_vm.ResolutionOptions);
        Assert.Contains(_vm.Resolution, _vm.ResolutionOptions);   // 下拉必须能选中它

        // ② 切到只支持 4 种比例的 gpt-image-1 → 21:9 不合法，必须回退到合法值
        _vm.Model = "openai/gpt-image-1";
        Assert.Contains(_vm.Aspect, _vm.AspectOptions);       // 回退后的值一定在选项内
        Assert.DoesNotContain("21:9", _vm.AspectOptions);     // 21:9 确实不被支持

        // ③ 切到 gemini-3.1 → 选项集合换成含扩展比例的那套
        _vm.Model = "google/gemini-3.1-flash-image";
        Assert.Contains("1:4", _vm.AspectOptions);
        Assert.Contains("8:1", _vm.AspectOptions);
        Assert.DoesNotContain("2:1", _vm.AspectOptions);
    }

    [Fact]
    public void SwitchingToApimart_RestoresFullOptionSets()
    {
        _vm.Provider = ApiProvider.OpenRouter;
        _vm.Model = "openai/gpt-image-1";
        Assert.Equal(4, _vm.AspectOptions.Length);

        _vm.Provider = ApiProvider.Apimart;
        // APIMart = 文档 15 种比例 + auto
        Assert.Contains("3:1", _vm.AspectOptions);
        Assert.Contains("9:21", _vm.AspectOptions);
        Assert.Equal(3, _vm.ResolutionOptions.Length);   // 1k/2k/4k
        Assert.Equal(4, _vm.BatchNMax);
    }

    // ---------------------------------------------------------------- 标注按钮可用性（待办 2）

    [Fact]
    public void AnnotatedEdit_DisabledUntilSomethingIsDrawn()
    {
        // 回归：此前只绑 CanRun → 没圈画时按钮也可点，只能在点击后弹警告
        Assert.True(_vm.CanRun);              // 测试里已配好 key
        Assert.Equal(0, _vm.RegionCount);
        Assert.False(_vm.CanEditWithRegions); // 没圈画 → 禁用
        Assert.False(_vm.HasRegions);
        Assert.Equal("尚未圈画", _vm.RegionCountText);

        _vm.RegionCount = 2;
        Assert.True(_vm.CanEditWithRegions);
        Assert.True(_vm.HasRegions);
        Assert.Equal("已标注 2 处", _vm.RegionCountText);

        // 清除标注 → 重新禁用
        _vm.RegionCount = 0;
        Assert.False(_vm.CanEditWithRegions);
    }

    [Fact]
    public async Task RegionEditTip_DiffersByProvider()
    {
        // 两条链路的"区域约束强度"不同，提示必须如实区分
        _vm.Provider = ApiProvider.Apimart;
        Assert.Contains("mask_url", _vm.RegionEditTip);
        Assert.Contains("严格限定改动区域", _vm.RegionEditTip);

        _vm.Provider = ApiProvider.OpenRouter;
        Assert.Contains("无 mask 字段", _vm.RegionEditTip);
        Assert.Contains("尽力遵守", _vm.RegionEditTip);
    }

    // ---------------------------------------------------------------- 辅助
    private static byte[] MakePng(int w, int h)
    {
        using var bmp = new SkiaSharp.SKBitmap(w, h, SkiaSharp.SKColorType.Rgb888x,
                                               SkiaSharp.SKAlphaType.Opaque);
        using (var c = new SkiaSharp.SKCanvas(bmp)) c.Clear(new SkiaSharp.SKColor(20, 140, 90));
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static byte[] MakeAlphaMask(int w, int h)
    {
        using var bmp = new SkiaSharp.SKBitmap(w, h, SkiaSharp.SKColorType.Rgba8888,
                                               SkiaSharp.SKAlphaType.Unpremul);
        using (var c = new SkiaSharp.SKCanvas(bmp))
        {
            c.Clear(SkiaSharp.SKColors.Transparent);
            using var p = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0, 0, 0, 255) };
            c.DrawRect(new SkiaSharp.SKRect(0, 0, w / 2f, h / 2f), p);
        }
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>记录型假 API：把收到的请求存下来（不发网络请求、不花钱）。</summary>
    private sealed class RecordingApi : IImageApi
    {
        public GenRequest? Last { get; private set; }
        public SubmitProgressHandler? OnSubmitProgress { get; set; }
        public PartialImageHandler? OnPartialImage { get; set; }

        public Task<GenResult> GenerateAsync(GenRequest req)
        {
            Last = req;
            // 返回一张可落盘的图。尺寸固定 64×64 → 让蒙版测试能用 64×64 蒙版对齐尺寸
            // （文档要求蒙版必须与参考图第 1 位逐像素一致）。
            return Task.FromResult(new GenResult(
                new List<(byte[], string)> { (MakePng(64, 64), "image/png") }, 0, 0));
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(string? apiKey = null,
                                                           bool imagesOnly = true,
                                                           CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(new List<string>());

        public Task<KeyInfo> CheckKeyAsync(string apiKey, CancellationToken ct = default)
            => Task.FromResult(new KeyInfo(true, 1, null, null));
    }

    private sealed class FakeStorage2 : IPlatformStorage
    {
        public Task<PlatformOpResult> SaveToGalleryAsync(byte[] data, string fileName, string mediaType)
            => Task.FromResult(PlatformOpResult.Cancelled());
        public Task<PlatformOpResult> OpenInExternalViewerAsync(string path)
            => Task.FromResult(PlatformOpResult.Ok(path));
        public Task<PlatformOpResult> RevealInFileManagerAsync(string path)
            => Task.FromResult(PlatformOpResult.Ok(path));
        public Task<string?> GetGalleryDirAsync() => Task.FromResult<string?>(null);
    }

    private sealed class FakePlatform2 : IPlatformInfo
    {
        public string PlatformName => "Test";
        public string Version => "1.0";
    }
}

/// <summary>
/// 改名（Imgagent → ImgHub）的目录解析回归测试。
///
/// ⚠️ 这些常量是"沿用旧数据目录"的依据 —— 不能被当作残留清掉，
///    否则老用户升级后历史图片与配置会"凭空消失"。
///
/// ⚠️ 本类会读写进程级环境变量（IMGHUB_HOME / IMGAGENT_HOME），
///    故与其它同类测试同属串行集合（见 EnvironmentVariableTests）。
/// </summary>
[Collection(EnvironmentVariableTests.Name)]
public class RenamePathTests
{
    [Fact]
    public void AppPaths_LegacyDirNames_AreRecorded()
    {
        Assert.Equal("imghub", AppPaths.DirNameWindows);
        Assert.Equal(".imghub", AppPaths.DirNameUnix);
        Assert.Equal("imgagent", AppPaths.LegacyDirNameWindows);
        Assert.Equal(".imgagent", AppPaths.LegacyDirNameUnix);
    }

    [Fact]
    public void ResolveHome_PrefersExplicitEnv()
    {
        // 显式 IMGHUB_HOME 优先
        var dir = Path.Combine(Path.GetTempPath(), "imghub-env-" + Guid.NewGuid().ToString("N"));
        // 保存原值并在结束后恢复：环境变量是**进程级**状态，
        // 直接置 null 会把同集合后续测试的前置条件一并清掉。
        var oldHome = Environment.GetEnvironmentVariable("IMGHUB_HOME");
        try
        {
            Environment.SetEnvironmentVariable("IMGHUB_HOME", dir);
            Assert.Equal(dir, AppPaths.ResolveHome());
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            Environment.SetEnvironmentVariable("IMGHUB_HOME", oldHome);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void ResolveHome_AcceptsLegacyEnv()
    {
        // 兼容：改名前的 IMGAGENT_HOME 仍可识别（老用户环境变量不必改）
        var dir = Path.Combine(Path.GetTempPath(), "imghub-envlegacy-" + Guid.NewGuid().ToString("N"));
        var oldHome = Environment.GetEnvironmentVariable("IMGHUB_HOME");
        var oldLegacy = Environment.GetEnvironmentVariable("IMGAGENT_HOME");
        try
        {
            Environment.SetEnvironmentVariable("IMGHUB_HOME", null);
            Environment.SetEnvironmentVariable("IMGAGENT_HOME", dir);
            Assert.Equal(dir, AppPaths.ResolveHome());
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            Environment.SetEnvironmentVariable("IMGHUB_HOME", oldHome);
            Environment.SetEnvironmentVariable("IMGAGENT_HOME", oldLegacy);
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
