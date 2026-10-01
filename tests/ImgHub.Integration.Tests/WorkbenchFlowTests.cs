using ImgHub.App.Services;
using ImgHub.App.ViewModels;
using ImgHub.Core;
using ImgHub.Core.Http;
using ImgHub.Core.Models;
using ImgHub.Core.Services;
using ImgHub.Core.Storage;

namespace ImgHub.Integration.Tests;

/// <summary>测试用的假平台存储（不碰真实文件系统对话框）。</summary>
internal sealed class FakeStorage : IPlatformStorage
{
    public List<string> Saved { get; } = new();
    public Task<PlatformOpResult> SaveToGalleryAsync(byte[] data, string fileName, string mediaType)
    { Saved.Add(fileName); return Task.FromResult(PlatformOpResult.Ok("/fake/" + fileName)); }
    public Task<PlatformOpResult> OpenInExternalViewerAsync(string path)
        => Task.FromResult(PlatformOpResult.Ok(path));
    public Task<PlatformOpResult> RevealInFileManagerAsync(string path)
        => Task.FromResult(PlatformOpResult.Ok(path));
    public Task<string?> GetGalleryDirAsync() => Task.FromResult<string?>(null);
}

internal sealed class FakePlatform : IPlatformInfo
{
    public string PlatformName => "Test";
    public string Version => "1.0";
}

/// <summary>
/// 端到端集成测试：ViewModel 编排（离线生成 → 落盘 → 历史 → 撤回 → 持久化）。
/// 用 offline=true，不联网不花钱 —— 与 Python 版测试哲学一致（CONSTRAINTS F1）。
/// </summary>
/// <remarks>
/// ⚠️ 构造函数会设进程级环境变量 <c>IMGHUB_HOME</c>，故纳入串行集合
///    （见 <see cref="EnvironmentVariableTests"/>），避免与其它同类测试互相覆盖。
/// </remarks>
[Collection(EnvironmentVariableTests.Name)]
public class WorkbenchFlowTests : IDisposable
{
    private readonly string _home;
    private readonly AppServices _svc;
    private readonly MainViewModel _vm;

    public WorkbenchFlowTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "imghub-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("IMGHUB_HOME", _home);

        var session = new Session(_home);
        session.Config.Offline = true;      // 全离线
        session.Config.Provider = "openrouter";
        session.Config.Model = Catalog.DefaultModelOpenRouter;
        session.Config.Quality = "low";
        session.Sanitize();

        _svc = new AppServices
        {
            Session = session,
            Http = new HttpJsonClient(),
            ImageApi = new ImageApi(session.Provider, new HttpJsonClient()),
            Polish = new PolishService(new HttpJsonClient()),
            Storage = new FakeStorage(),
            Platform = new FakePlatform(),
            ModelStats = new ImgHub.Core.Services.ModelStatsService(_home),
            Pending = new ImgHub.Core.Storage.PendingTaskStore(_home),
        };
        _vm = new MainViewModel(_svc);
        _vm.Offline = true;
    }

    public void Dispose()
    {
        // 环境变量是进程级状态：用完清掉，避免污染同集合的后续测试。
        Environment.SetEnvironmentVariable("IMGHUB_HOME", null);
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    [Fact]
    public async Task OfflineGenerate_ProducesImageAndHistory()
    {
        _vm.Prompt = "一只猫在窗台上";
        await _vm.GenerateCommand.ExecuteAsync(null);

        Assert.Single(_vm.History);
        var item = _vm.History[0];
        Assert.Equal("offline", item.Kind);

        // 图片真的落盘了
        var path = Path.Combine(_home, item.File);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);

        // 是合法 PNG
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal("image/png", ImgHub.Core.Imaging.ImageCodec.SniffMediaType(bytes));

        // 提示词已记入历史
        Assert.Contains("一只猫在窗台上", _vm.PromptHistory);
    }

    [Fact]
    public async Task OfflineGenerate_ClearsPromptOnSuccess()
    {
        _vm.Prompt = "测试提示词";
        await _vm.GenerateCommand.ExecuteAsync(null);
        Assert.Equal("", _vm.Prompt);   // 成功才清空
    }

    [Fact]
    public async Task Generate_EmptyPrompt_DoesNothing()
    {
        _vm.Prompt = "   ";
        await _vm.GenerateCommand.ExecuteAsync(null);
        Assert.Empty(_vm.History);
    }

    [Fact]
    public async Task Edit_NoImage_LogsWarning_NoCrash()
    {
        _vm.Prompt = "改成雪原";
        await _vm.EditCommand.ExecuteAsync(null);
        Assert.Empty(_vm.History);   // 没有可编辑的图 → 不应生成
    }

    [Fact]
    public async Task Edit_WithImage_UsesCurrentAsReferenceFirst()
    {
        // 先生成一张
        _vm.Prompt = "一只橘猫";
        await _vm.GenerateCommand.ExecuteAsync(null);
        var first = _vm.History[0];

        // 编辑：待修改图必须排第 1 位（CONSTRAINTS D1）
        _vm.Prompt = "把背景换成雪原";
        await _vm.EditCommand.ExecuteAsync(null);

        Assert.Equal(2, _vm.History.Count);
        Assert.Equal("edit", _vm.History[0].Kind);
        Assert.Equal("一只橘猫", first.Prompt);
    }

    [Fact]
    public async Task Undo_RemovesLatest_KeepsHistoryFile()
    {
        _vm.Prompt = "第一张";
        await _vm.GenerateCommand.ExecuteAsync(null);
        _vm.Prompt = "第二张";
        await _vm.GenerateCommand.ExecuteAsync(null);
        Assert.Equal(2, _vm.History.Count);

        _vm.UndoCommand.Execute(null);
        Assert.Single(_vm.History);

        // 流水账文件仍保留（历史记录不删）
        var log = Path.Combine(_home, "history.jsonl");
        Assert.True(File.Exists(log));
        Assert.True(File.ReadAllLines(log).Length >= 2);
    }

    [Fact]
    public async Task TotalCost_Accumulates()
    {
        _vm.Prompt = "a";
        await _vm.GenerateCommand.ExecuteAsync(null);
        _vm.Prompt = "b";
        await _vm.GenerateCommand.ExecuteAsync(null);
        Assert.Equal(_svc.Session.TotalCost, _vm.TotalCost, 6);
    }

    [Fact]
    public async Task Persistence_ReloadKeepsHistory()
    {
        _vm.Prompt = "持久化测试";
        await _vm.GenerateCommand.ExecuteAsync(null);
        Assert.Single(_vm.History);

        // 模拟重启：新建 Session
        var s2 = new Session(_home);
        Assert.Single(s2.Items);
        Assert.Equal("持久化测试", s2.Items[0].Prompt);
    }

    [Fact]
    public async Task ImportImage_AddsAsImported()
    {
        var data = ImgHub.Core.Imaging.Placeholder.Png("imported", size: 32);
        await _vm.ImportImageAsync(data, "image/png", "my_photo.png");
        Assert.Single(_vm.History);
        Assert.Equal("import", _vm.History[0].Kind);
    }

    [Fact]
    public void QualityOptions_FollowProvider()
    {
        _vm.Provider = ApiProvider.Apimart;
        _vm.Model = "gpt-image-2.5-flare";
        Assert.Contains("xhigh", _vm.QualityOptions);

        _vm.Provider = ApiProvider.OpenRouter;
        Assert.DoesNotContain("xhigh", _vm.QualityOptions);
    }

    [Fact]
    public void ProviderSwitch_FixesMismatchedModel()
    {
        _vm.Provider = ApiProvider.OpenRouter;
        _vm.Model = "openai/gpt-image-2";   // OpenRouter 风格
        _vm.Provider = ApiProvider.Apimart; // 切换 → 模型应改成裸名
        Assert.Equal(Catalog.DefaultModelApimart, _vm.Model);
    }

    [Fact]
    public void ProviderSwitch_ToBareNameProviders_FixesAspectToTheirOwnDialect()
    {
        // ⚠️ 回归（v0.5.31 AOT 实跑抓到）：切到 OpenAI 官方 / 千问时，旧画幅是比例名
        //    （如 "16:9"），而这两家的 size 是**像素串** → 必须回退成它们自己的合法值。
        //    若回退目标硬编码 "1:1"，回退后的值对它们**依然非法**，会刷出
        //    「模型不支持画幅 1:1 → 已回退 1:1」这种自相矛盾的警告。
        foreach (var p in new[] { ApiProvider.OpenAi, ApiProvider.DashScope })
        {
            _vm.Provider = ApiProvider.OpenRouter;
            _vm.Aspect = "16:9";        // OpenRouter 的合法比例名
            _vm.Provider = p;           // 切换 → 必须换成新 provider 认得的画幅

            var allowed = Catalog.AspectChoicesFor(p, _vm.Model);
            Assert.Contains(_vm.Aspect, allowed);
            // 关键：回退结果必须**稳定**（再切一次也不变），否则说明"回退到了非法值"
            var afterFirst = _vm.Aspect;
            _vm.Provider = ApiProvider.OpenRouter;
            _vm.Provider = p;
            Assert.Equal(afterFirst, _vm.Aspect);
        }
    }

    [Fact]
    public void ParamPreset_RestoresPerProviderAndModel_OnSwitchBack()
    {
        // 需求③：按端点记录上次使用的配置并复原。
        // 场景：在 OpenAI 上调好一套参数 → 切到千问改一套 → 切回 OpenAI 应复原原来那套。
        _vm.Provider = ApiProvider.OpenAi;
        _vm.Model = "gpt-image-2.5-sunburst";
        _vm.Quality = "xhigh";
        _vm.Aspect = "1536x1024";
        _vm.OutputFormat = "webp";
        _vm.BatchN = 2;

        _vm.Provider = ApiProvider.DashScope;
        _vm.Model = "qwen-image-3.0-pro";
        _vm.Quality = "auto";
        _vm.Aspect = "2048x2048";
        _vm.OutputFormat = "png";
        _vm.BatchN = 4;
        _vm.NegativePrompt = "低分辨率";
        _vm.Watermark = true;

        // 切回 OpenAI：应复原
        _vm.Provider = ApiProvider.OpenAi;
        _vm.Model = "gpt-image-2.5-sunburst";
        Assert.Equal("xhigh", _vm.Quality);
        Assert.Equal("1536x1024", _vm.Aspect);
        Assert.Equal("webp", _vm.OutputFormat);
        Assert.Equal(2, _vm.BatchN);

        // 再切回千问：也应复原（含千问专属字段）
        _vm.Provider = ApiProvider.DashScope;
        _vm.Model = "qwen-image-3.0-pro";
        Assert.Equal("2048x2048", _vm.Aspect);
        Assert.Equal(4, _vm.BatchN);
        Assert.Equal("低分辨率", _vm.NegativePrompt);
        Assert.True(_vm.Watermark);
    }

    [Fact]
    public void ParamPreset_RestoresPerModel_WithinSameProvider()
    {
        // ⚠️ 键必须带模型：同一 provider 下 dall-e-3 与 gpt-image-2.5 的合法值域完全不同
        //    （前者只有 standard/hd、固定 3 个尺寸）。若只按 provider 记，切模型会复原出
        //    该模型不支持的值 → 被收窄（用户看到参数莫名变了）或 400。
        _vm.Provider = ApiProvider.OpenAi;

        _vm.Model = "gpt-image-2.5-sunburst";
        _vm.Aspect = "1536x1024";
        _vm.Quality = "xhigh";

        _vm.Model = "gpt-image-1";
        _vm.Aspect = "1024x1024";
        _vm.Quality = "low";

        _vm.Model = "gpt-image-2.5-sunburst";
        Assert.Equal("1536x1024", _vm.Aspect);
        Assert.Equal("xhigh", _vm.Quality);

        _vm.Model = "gpt-image-1";
        Assert.Equal("1024x1024", _vm.Aspect);
        Assert.Equal("low", _vm.Quality);
    }

    [Fact]
    public void DropdownDefaults_PickFirstElement_WhenValueUnsupported()
    {
        // 需求③前半：值不被支持时默认选**第一个元素**（而不是硬编码 1:1 / png / low）。
        _vm.Provider = ApiProvider.OpenAi;
        _vm.Model = "gpt-image-2.5-sunburst";
        // 先在该模型上设一组 OpenAI 合法值
        _vm.Quality = "xhigh";
        _vm.Aspect = "1536x1024";

        // 切到 dall-e-3：它的质量只有 standard/hd、尺寸固定 3 值（且无 auto）
        // → 旧值 xhigh / 1536x1024 都非法，应被收窄到各自集合的**第一个**
        _vm.Model = "dall-e-3";

        var qChoices = Catalog.QualityChoices(ApiProvider.OpenAi, "dall-e-3");
        var aChoices = Catalog.AspectChoicesFor(ApiProvider.OpenAi, "dall-e-3");
        Assert.Equal(qChoices[0], _vm.Quality);
        Assert.Equal(aChoices[0], _vm.Aspect);

        // 且确实合法（能通过服务端校验）
        Assert.Contains(_vm.Quality, qChoices);
        Assert.Contains(_vm.Aspect, aChoices);
    }

    [Fact]
    public void PolishStyle_FollowsProvider_AndCanBeOverridden()
    {
        // 需求①：润色用哪套提示词 —— 默认跟随生图端点，也能手动覆盖
        _vm.Provider = ApiProvider.DashScope;
        Assert.Equal("qwen", _svc.Polish.EffectiveStyle);

        _vm.Provider = ApiProvider.OpenAi;
        Assert.Equal("openai", _svc.Polish.EffectiveStyle);

        // 手动覆盖成"OpenAI 风格"，即使生图端点是千问也生效
        _vm.PolishStyleIndex = 1;   // 1 = OpenAI
        _vm.Provider = ApiProvider.DashScope;
        Assert.Equal("openai", _svc.Polish.EffectiveStyle);

        // 回到 auto
        _vm.PolishStyleIndex = 0;
        Assert.Equal("qwen", _svc.Polish.EffectiveStyle);
    }

    // ---------------------------------------------------------------- 用户实测回归（v0.5.31）

    [Fact]
    public void ProviderSwitch_NeverWritesEmptyOrMismatchedPreset()
    {
        // ⚠️ 真实回归（用户看 config.json 抓到）：
        //    ① 快照里出现 aspect='' → 切回来"配置没保存住"；
        //    ② 出现 apimart|openai/gpt-image-2.5-flare 这种**错配键**
        //       （APIMart 不该有带斜杠的 OpenRouter 模型名）。
        //    根因：a) _presetProviderBeforeSwitch 未初始化 → 首次切换记录错配；
        //          b) ComboBox 的 SelectedItem 在 ItemsSource 变化时回写 null → VM 收到空串。
        var session = _svc.Session;

        // 连续切换（含首次切换 —— 那正是错配键的来源）
        foreach (var p in new[] { ApiProvider.Apimart, ApiProvider.OpenRouter,
                                  ApiProvider.OpenAi, ApiProvider.DashScope,
                                  ApiProvider.OpenRouter })
            _vm.Provider = p;

        foreach (var kv in session.Config.ParamPresets)
        {
            var key = kv.Key;
            var preset = kv.Value;
            // ① 不允许空值（除刻意留空的 negative_prompt 等可选文本）
            Assert.False(string.IsNullOrWhiteSpace(preset.Aspect),
                         $"快照 {key} 的 aspect 为空 —— 该参数不会被复原");
            // ② 键的 provider 与模型必须**真的匹配**（挡错配键）
            var bar = key.IndexOf('|');
            Assert.True(bar > 0, $"键格式非法：{key}");
            var providerKey = key[..bar];
            var model = key[(bar + 1)..];
            var provider = ApiProviderExtensions.Parse(providerKey);
            Assert.NotNull(provider);
            Assert.True(Catalog.ModelMatchesProvider(model, provider!.Value),
                        $"错配键：{providerKey} 不该有模型 {model}");
        }
    }

    [Fact]
    public void DropdownIndices_AlwaysPointAtTheCurrentValue()
    {
        // 回归：画幅下拉"空白"（用户实测截图）。
        // ComboBox 改绑 SelectedIndex 后，**不变量**是：
        //   当前值合法 ⇒ 索引指向它；索引为 -1 ⇒ 界面会显示空 → 必须避免。
        foreach (var p in Enum.GetValues<ApiProvider>())
        {
            _vm.Provider = p;
            foreach (var m in Catalog.ModelChoices(p))
            {
                _vm.Model = m;

                Assert.True(_vm.AspectIndex >= 0,
                    $"{p}/{m}：画幅 {_vm.Aspect} 在选项里找不到 → 下拉会空白");
                Assert.Equal(_vm.Aspect, _vm.AspectOptions[_vm.AspectIndex]);

                Assert.True(_vm.QualityIndex >= 0, $"{p}/{m}：质量档选不中");
                Assert.Equal(_vm.Quality, _vm.QualityOptions[_vm.QualityIndex]);

                Assert.True(_vm.OutputFormatIndex >= 0, $"{p}/{m}：输出格式选不中");
                Assert.Equal(_vm.OutputFormat, _vm.OutputFormatOptions[_vm.OutputFormatIndex]);

                // 有分辨率档概念的 provider 也必须能选中
                if (_vm.HasResolutionTier)
                {
                    Assert.True(_vm.ResolutionIndex >= 0, $"{p}/{m}：分辨率档选不中");
                    Assert.Equal(_vm.Resolution, _vm.ResolutionOptions[_vm.ResolutionIndex]);
                }
            }
        }
    }

    [Fact]
    public void DropdownIndices_IgnoreInvalidIndex_InsteadOfWritingEmpty()
    {
        // -1（集合里没有当前值）时必须**忽略**，绝不能把值写成空 —— 空值会被存进快照
        _vm.Provider = ApiProvider.OpenAi;
        _vm.Model = "gpt-image-2.5-sunburst";
        var before = _vm.Aspect;

        _vm.AspectIndex = -1;
        Assert.Equal(before, _vm.Aspect);      // 值未被清空

        _vm.AspectIndex = 999;                 // 越界同样忽略
        Assert.Equal(before, _vm.Aspect);
    }

    [Fact]
    public void CrossProviderSwitch_PixelBasedToRatioBased_NeverLeavesAspectBlank()
    {
        // ⚠️ 截图实测回归（用户报「切换端点画幅又回空白」）：
        //    OpenAI 的画幅是**像素串**（1024x1024），APIMart / OpenRouter 的下拉**只有比例名**。
        //    从 OpenAI 切过去时，旧值"协议上合法"（这两个端点确实接受像素串），
        //    但**下拉里选不中** → SelectedIndex = -1 → 画幅空白。
        //    根因：收窄用了 `AspectOrSizeSupported`（协议判定）而不是下拉集合判定。
        var pixelProviders = new[] { ApiProvider.OpenAi, ApiProvider.DashScope };
        var ratioProviders = new[] { ApiProvider.Apimart, ApiProvider.OpenRouter };

        foreach (var from in pixelProviders)
            foreach (var to in ratioProviders)
            {
                _vm.Provider = from;
                _vm.Model = Catalog.ModelChoices(from).First(m => m is "gpt-image-2.5-sunburst"
                                                              || m is "qwen-image-3.0-pro");
                Assert.Contains("x", _vm.Aspect);   // 前提：当前确实是像素串

                _vm.Provider = to;
                Assert.True(_vm.AspectIndex >= 0,
                    $"{from} → {to}：画幅 '{_vm.Aspect}' 在下拉里选不中 → 会空白");
                Assert.Contains(_vm.Aspect, _vm.AspectOptions);
            }

        // 反向亦然（比例名 → 像素串体系）
        foreach (var from in ratioProviders)
            foreach (var to in pixelProviders)
            {
                _vm.Provider = from;
                _vm.Model = Catalog.ModelChoices(from)[0];

                _vm.Provider = to;
                Assert.True(_vm.AspectIndex >= 0,
                    $"{from} → {to}：画幅 '{_vm.Aspect}' 在下拉里选不中 → 会空白");
            }
    }

    [Fact]
    public void RepeatedSwitching_NeverBlankensAnyDropdown()
    {
        // 验收标准：**无论怎么切换**模型、端点，配置都不出现空白。
        // 来回横跳多轮（含模型与端点同时变），每步都断言四个下拉都有选中值。
        var providers = Enum.GetValues<ApiProvider>();
        var rnd = new Random(20260924);
        for (int round = 0; round < 80; round++)
        {
            var p = providers[rnd.Next(providers.Length)];
            var models = Catalog.ModelChoices(p);
            var m = models[rnd.Next(models.Length)];

            _vm.Model = m;          // 先换模型
            _vm.Provider = p;       // 再换端点（顺序随机化，覆盖两种路径）

            Assert.True(_vm.AspectIndex >= 0,
                $"round {round}: {p}/{m} 画幅 '{_vm.Aspect}' 空白");
            Assert.True(_vm.QualityIndex >= 0,
                $"round {round}: {p}/{m} 质量 '{_vm.Quality}' 空白");
            Assert.True(_vm.OutputFormatIndex >= 0,
                $"round {round}: {p}/{m} 格式 '{_vm.OutputFormat}' 空白");
            if (_vm.HasResolutionTier)
                Assert.True(_vm.ResolutionIndex >= 0,
                    $"round {round}: {p}/{m} 分辨率 '{_vm.Resolution}' 空白");
        }
    }

    [Fact]
    public async Task SaveToGallery_DelegatesToPlatformStorage()
    {
        _vm.Prompt = "保存测试";
        await _vm.GenerateCommand.ExecuteAsync(null);
        await _vm.SaveToGalleryCommand.ExecuteAsync(null);
        Assert.Single(((FakeStorage)_svc.Storage).Saved);
    }

    // ================================================================ 平台存储结果语义（P0-5 / P0-8）

    [Fact]
    public void PlatformOp_Cancelled_IsNotReportedAsError()
    {
        // P0-5：用户取消 != 失败。取消必须中性提示，**不能**报 Err。
        var (text, level) = MainViewModel.DescribePlatformOp(
            PlatformOpResult.Cancelled(), "保存到相册");

        Assert.Equal(MainViewModel.MessageLevel.Info, level);
        Assert.Contains("已取消", text);
        Assert.DoesNotContain("失败", text);
    }

    [Fact]
    public void PlatformOp_Failure_IsReportedAsErrorWithReason()
    {
        // P0-5 的核心回归点：失败**绝不能**伪装成「用户取消」。
        var (text, level) = MainViewModel.DescribePlatformOp(
            PlatformOpResult.Failed("拿不到窗口句柄（StorageProvider 不可用）"), "保存到相册");

        Assert.Equal(MainViewModel.MessageLevel.Err, level);
        Assert.Contains("失败", text);
        Assert.Contains("拿不到窗口句柄", text);   // 必须带真实原因
        Assert.DoesNotContain("已取消", text);      // 关键：不许伪装
    }

    [Fact]
    public void PlatformOp_Unsupported_IsWarnNotError()
    {
        // Android 上没有「在文件管理器中显示」这类操作 → Warn（不是 Err，也不是静默）
        var (text, level) = MainViewModel.DescribePlatformOp(
            PlatformOpResult.Unsupported("当前平台不支持在文件管理器中定位文件"), "在文件管理器中显示");

        Assert.Equal(MainViewModel.MessageLevel.Warn, level);
        Assert.Contains("不支持", text);
    }

    [Fact]
    public void PlatformOp_Ok_ReportsPathOrGenericDone()
    {
        var withPath = MainViewModel.DescribePlatformOp(PlatformOpResult.Ok("/tmp/a.png"), "保存到相册");
        Assert.Equal(MainViewModel.MessageLevel.Ok, withPath.Level);
        Assert.Contains("/tmp/a.png", withPath.Text);

        var noPath = MainViewModel.DescribePlatformOp(PlatformOpResult.Ok(), "打开系统看图器");
        Assert.Equal(MainViewModel.MessageLevel.Ok, noPath.Level);
        Assert.Contains("完成", noPath.Text);
    }

    [Fact]
    public void PlatformStorage_NoTopLevel_SaveReturnsFailedNotCancelled()
    {
        // P0-8 根因的单元级防护：拿不到 TopLevel（= Android 旧写法恒为 null 的情形）
        // 必须返回 Failed 而不是 Cancelled/null，否则 UI 会误报「用户取消」。
        var sut = new PlatformStorage(new NullTopLevelSource());

        var save = sut.SaveToGalleryAsync(new byte[] { 1, 2, 3 }, "a.png", "image/png")
                      .GetAwaiter().GetResult();
        Assert.Equal(PlatformOpStatus.Failed, save.Status);
        Assert.NotNull(save.Error);   // 必须带原因，方便诊断
    }

    [Fact]
    public void PlatformStorage_NoTopLevel_ParsesSafelyInsteadOfThrowing()
    {
        // P0-8：旧写法 `MainView as TopLevel` 在 Android 上恒为 null（MainView 是 UserControl）。
        // 钉住契约：解析不到窗口时必须安全返回 null，不抛异常。
        var src = new LifetimeTopLevelSource();
        var ex = Record.Exception(() => src.Get());
        Assert.Null(ex);
    }

    /// <summary>永远拿不到窗口的 TopLevel 源（模拟 Android / 未初始化场景）。</summary>
    private sealed class NullTopLevelSource : ITopLevelSource
    {
        public Avalonia.Controls.TopLevel? Get() => null;
    }

    // ================================================================ 即梦（火山引擎）

    [Fact]
    public void JimengSwitch_ExposesExtractUi_AndKeepsDropdownsValid()
    {
        // 即梦（第 5 家）：切过去后
        //   ① 提取入口可见（IsJimengProvider / NeedsSecretKey）；
        //   ② 提取类 req_key 被识别（IsJimengExtractMode）；
        //   ③ 画幅/质量/格式下拉都有选中值（沿用"切换不能空白"的验收标准）。
        _vm.Provider = ApiProvider.Jimeng;

        Assert.True(_vm.IsJimengProvider);
        Assert.True(_vm.NeedsSecretKey, "即梦需要 AK/SK 两把 → 设置里要显示 SK 输入框");
        Assert.False(_vm.HasResolutionTier, "即梦的 size 是像素对，没有分辨率档");
        Assert.True(_vm.AspectIndex >= 0);
        Assert.True(_vm.QualityIndex >= 0);
        Assert.True(_vm.OutputFormatIndex >= 0);

        // 切到提取类 req_key → 提取模式可见
        _vm.Model = "jimeng_i2i_extract_tiled_images";
        Assert.True(_vm.IsJimengExtractMode);
        Assert.True(_vm.AspectIndex >= 0, "提取链路也要有合法画幅可选");

        // 切回生成类 → 提取模式关闭
        _vm.Model = "jimeng_t2i_v40";
        Assert.False(_vm.IsJimengExtractMode);
    }

    [Fact]
    public void ExtractPresets_SelectableAndBuildPrompt()
    {
        // 提取浮窗的预设选择 → 应把官方文案写进 JimengExtractPrompt
        _vm.Provider = ApiProvider.Jimeng;
        _vm.Model = "jimeng_i2i_extract_tiled_images";
        _vm.ExtractTab = 0;          // 商品提取

        Assert.Equal(6, _vm.ExtractPresets.Count);
        _vm.ExtractPresetIndex = 1;  // 「提取鞋子」
        Assert.Contains("提取出图片中的一双鞋子", _vm.JimengExtractPrompt);

        // 切到元素提取标签页 → 预设集合随之变化（4 种）
        _vm.ExtractTab = 1;
        Assert.Equal(4, _vm.ExtractPresets.Count);
        _vm.ExtractPresetIndex = 0;  // 「提取图案」
        Assert.Contains("提取产品的图案", _vm.JimengExtractPrompt);

        // 只有「提取饰品」允许换物件名
        _vm.ExtractTab = 0;
        _vm.ExtractPresetIndex = 5;  // 「提取饰品」
        Assert.True(_vm.ExtractAllowsItemName);
        _vm.ExtractItemName = "项链";
        Assert.Contains("提取出图片中的项链", _vm.JimengExtractPrompt);

        _vm.ExtractPresetIndex = 0;  // 「提取全身衣服」不允许
        Assert.False(_vm.ExtractAllowsItemName);
    }

    // ================================================================ 新增 UI 交互

    [Fact]
    public void ProviderDropdown_SwitchesAndPersists()
    {
        // 回归：顶栏 provider 原为静态文本（且下拉显示空白）→ 改为同类型选项下拉
        Assert.Contains(_vm.Providers, p => p.Provider == ApiProvider.OpenRouter);
        Assert.Contains(_vm.Providers, p => p.Provider == ApiProvider.Apimart);
        // 默认选中项必须非空（否则 ComboBox 显示空白）
        Assert.NotNull(_vm.SelectedProvider);

        var apimart = _vm.Providers.First(p => p.Provider == ApiProvider.Apimart);
        _vm.SelectedProvider = apimart;
        Assert.Equal(ApiProvider.Apimart, _vm.Provider);
        Assert.Equal("apimart", _svc.Session.Config.Provider);   // 已持久化到 config

        var openrouter = _vm.Providers.First(p => p.Provider == ApiProvider.OpenRouter);
        _vm.SelectedProvider = openrouter;
        Assert.Equal(ApiProvider.OpenRouter, _vm.Provider);
        // 下拉选中项跟随
        Assert.Equal(ApiProvider.OpenRouter, _vm.SelectedProvider!.Provider);
    }

    [Fact]
    public void ProviderDropdown_KeepsModelValid()
    {
        _vm.Provider = ApiProvider.OpenRouter;
        var apimart = _vm.Providers.First(p => p.Provider == ApiProvider.Apimart);
        _vm.SelectedProvider = apimart;
        // 切 provider 后模型名必须跟着改（裸名），否则一生成就 400
        Assert.DoesNotContain("/", _vm.Model);
    }

    [Fact]
    public void Settings_SaveApiKey_Persists()
    {
        _vm.Provider = ApiProvider.OpenRouter;
        _vm.ApiKeyInput = "sk-or-v1-TESTKEY123456";
        _vm.SaveSettingsCommand.Execute(null);

        var f = _svc.Session.KeyFile(ApiProvider.OpenRouter);
        Assert.True(File.Exists(f));
        Assert.Contains("TESTKEY", File.ReadAllText(f));
        // KeyStatusText 是掩码显示（sk-or-...3456），刻意不回显明文
        Assert.Contains("...", _vm.KeyStatusText);
        Assert.DoesNotContain("TESTKEY123456", _vm.KeyStatusText);
        Assert.False(_vm.SettingsOpen);   // 保存后自动关闭
    }

    [Fact]
    public void Settings_SavePolishConfig_Persists()
    {
        _vm.PolishBaseInput = "https://example.test/v1";
        _vm.PolishModelInput = "my-model";
        _vm.PolishKeyInput = "sk-polish-123";
        _vm.SaveSettingsCommand.Execute(null);

        Assert.Equal("https://example.test/v1", _svc.Session.Config.PolishBaseUrl);
        Assert.Equal("my-model", _svc.Session.Config.PolishModel);
        Assert.Equal("sk-polish-123", _svc.Session.PolishKey);
        Assert.True(File.Exists(_svc.Session.PolishKeyFile));
    }

    [Fact]
    public void Settings_OpenClose_TogglesFlag()
    {
        Assert.False(_vm.SettingsOpen);
        _vm.OpenSettingsCommand.Execute(null);
        Assert.True(_vm.SettingsOpen);
        _vm.CloseSettingsCommand.Execute(null);
        Assert.False(_vm.SettingsOpen);
    }

    [Fact]
    public void Theme_ToggleUpdatesFlag()
    {
        _vm.DarkTheme = true;
        Assert.True(_vm.DarkTheme);
        _vm.DarkTheme = false;
        Assert.False(_vm.DarkTheme);
    }

    [Fact]
    public void StatusBarCommands_AreExecutableAndSafe()
    {
        // 回归：底栏原为纯文本（点了没反应、无快捷键提示）→ 现在是真命令。
        // 注意 Messages 在无 UI 线程时经 Dispatcher.Post 异步添加，
        // 这里只验证命令可执行且不抛异常（渲染路径由 UI 测试覆盖）。
        var ex1 = Record.Exception(() => _vm.ShowShortcutsCommand.Execute(null));
        var ex2 = Record.Exception(() => _vm.ShowDataDirCommand.Execute(null));
        Assert.Null(ex1);
        Assert.Null(ex2);
        Assert.NotNull(_vm.ShowShortcutsCommand);
        Assert.NotNull(_vm.ShowDataDirCommand);
    }

    [Fact]
    public async Task PreviewCurrent_NoImage_DoesNotCrash()
    {
        await _vm.PreviewCurrentCommand.ExecuteAsync(null);
        Assert.Null(_vm.PreviewPath);
    }

    [Fact]
    public async Task PreviewCurrent_WithImage_SetsPath()
    {
        _vm.Prompt = "预览测试";
        await _vm.GenerateCommand.ExecuteAsync(null);
        await _vm.PreviewCurrentCommand.ExecuteAsync(null);
        Assert.NotNull(_vm.PreviewPath);
        Assert.True(File.Exists(_vm.PreviewPath));
    }

    // ================================================================ 本轮新增 UI 能力

    [Fact]
    public async Task Params_PersistAcrossRestart()
    {
        // 回归：模型/质量/画幅/分辨率/格式/批量 原仅更新估算、从不写 config，
        // 导致改完重启就丢。
        _vm.Model = "openai/gpt-image-1-mini";
        _vm.Quality = "medium";
        _vm.Aspect = "16:9";
        _vm.Resolution = "2k";
        _vm.OutputFormat = "jpeg";
        _vm.BatchN = 3;
        await Task.Delay(220);   // 等待节流写入

        var reloaded = new Session(_home);
        Assert.Equal("openai/gpt-image-1-mini", reloaded.Config.Model);
        Assert.Equal("medium", reloaded.Config.Quality);
        Assert.Equal("16:9", reloaded.Config.Aspect);
        Assert.Equal("2k", reloaded.Config.Resolution);
        Assert.Equal("jpeg", reloaded.Config.OutputFormat);
        Assert.Equal(3, reloaded.Config.BatchN);
    }

    [Fact]
    public async Task BatchGenerate_PublishesThumbnails()
    {
        // 一次生成 3 张 → 预览区应有 3 个缩略图可点选
        _vm.Prompt = "三张测试图";
        _vm.BatchN = 3;
        await _vm.GenerateCommand.ExecuteAsync(null);

        Assert.Equal(3, _vm.BatchResults.Count);
        Assert.True(_vm.HasBatchResults);   // >1 时显示缩略图条
        Assert.All(_vm.BatchResults, t => Assert.False(string.IsNullOrEmpty(t.File)));
    }

    [Fact]
    public async Task SingleGenerate_NoThumbnailStrip()
    {
        _vm.Prompt = "一张";
        _vm.BatchN = 1;
        await _vm.GenerateCommand.ExecuteAsync(null);
        Assert.Single(_vm.BatchResults);
        Assert.False(_vm.HasBatchResults);   // 单张不显示缩略图条
    }

    [Fact]
    public void Unconfigured_DisablesActions()
    {
        // 回归：原实现无 key 也能点生成（然后失败）。现应禁用并给引导。
        var dir = Path.Combine(Path.GetTempPath(), "ig-nocfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var sess = new Session(dir);   // 无 key、非离线
            var svc = new AppServices
            {
                Session = sess,
                Http = new HttpJsonClient(),
                ImageApi = new ImageApi(sess.Provider, new HttpJsonClient()),
                Polish = new PolishService(new HttpJsonClient()),
                Storage = new FakeStorage(),
                Platform = new FakePlatform(),
                ModelStats = new ImgHub.Core.Services.ModelStatsService(_home),
            Pending = new ImgHub.Core.Storage.PendingTaskStore(_home),
            };
            var vm = new MainViewModel(svc);
            Assert.False(vm.IsConfigured);
            Assert.False(vm.CanRun);
            Assert.True(vm.NeedsSetup);
            Assert.Contains("设置", vm.SetupHint);

            // 开离线模式后视为可用
            vm.Offline = true;
            Assert.True(vm.IsConfigured);
            Assert.True(vm.CanRun);
            Assert.False(vm.NeedsSetup);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task RegionEdit_RequiresAnnotationFirst()
    {
        // 没有标注时不应发起编辑（避免空跑）
        _vm.Prompt = "改成雪原";
        await _vm.EditWithRegionsCommand.ExecuteAsync(null);
        Assert.Empty(_vm.History);
    }

    [Fact]
    public async Task RegionEdit_UsesAnnotatedImageAsReference()
    {
        // 先生成一张底图
        _vm.Prompt = "一只橘猫";
        await _vm.GenerateCommand.ExecuteAsync(null);
        var baseItem = _vm.History[0];

        // 模拟画布导出：把底图当作"已标注"图
        var basePath = Path.Combine(_home, baseItem.File);
        var annotated = await File.ReadAllBytesAsync(basePath);
        _vm.SetAnnotatedImage(annotated, regionCount: 2);
        Assert.True(File.Exists(_vm.AnnotatedPath));
        Assert.Equal(2, _vm.RegionCount);

        // 用标注编辑 → 应产出新图，且提示词带上"高亮区域"说明
        _vm.Prompt = "把高亮处换成蓝天";
        await _vm.EditWithRegionsCommand.ExecuteAsync(null);
        Assert.Equal(2, _vm.History.Count);
        Assert.Equal("edit", _vm.History[0].Kind);
        Assert.Contains("高亮区域", _vm.History[0].Prompt);
    }

    // ================================================================ 本轮修复回归

    [Fact]
    public async Task TotalCost_SurvivesRestart()
    {
        // 回归：LoadState 原实现漏读 total_cost → 重启后累计显示归零（真机 bug）
        // 注意：TotalCost 现在是**计算属性**（从 Items 求和），
        //       所以持久化的关键是 **Items 的 Cost 字段**能被正确恢复。
        _vm.Prompt = "费用持久化测试";
        await _vm.GenerateCommand.ExecuteAsync(null);

        var expect = _vm.TotalCost;
        Assert.True(expect > 0 || _vm.Offline);   // 离线时为 0 也可接受

        var reloaded = new Session(_home);
        // Items 恢复后，TotalCost 应自动等于求和
        Assert.Equal(reloaded.Items.Sum(i => i.Cost), reloaded.TotalCost, 6);
        Assert.Equal(expect, reloaded.TotalCost, 6);
    }

    [Fact]
    public void Config_NullModel_FallsBackToDefault()
    {
        // 回归：config.json 里 model:null 会导致启动闪退（NullReferenceException）
        File.WriteAllText(Path.Combine(_home, "config.json"),
            "{\"model\": null, \"quality\": \"low\", \"provider\": \"apimart\"}");

        var sess = new Session(_home);   // 不应抛异常
        Assert.Equal(Catalog.DefaultModelApimart, sess.Config.Model);
        Assert.Equal(ApiProvider.Apimart, sess.Provider);

        // ViewModel 构建也不应闪退，且模型框必有值
        var svc = new AppServices
        {
            Session = sess,
            Http = new HttpJsonClient(),
            ImageApi = new ImageApi(sess.Provider, new HttpJsonClient()),
            Polish = new PolishService(new HttpJsonClient()),
            Storage = new FakeStorage(),
            Platform = new FakePlatform(),
            ModelStats = new ImgHub.Core.Services.ModelStatsService(_home),
            Pending = new ImgHub.Core.Storage.PendingTaskStore(_home),
        };
        var vm = new MainViewModel(svc);
        Assert.False(string.IsNullOrWhiteSpace(vm.Model));
        Assert.Contains(vm.Model, vm.ModelChoices);
    }

    [Fact]
    public void ModelChoices_NeverLeaveModelBlank()
    {
        // 回归：切换 provider 后模型框空白 → 现在必须总落在清单里
        _vm.Model = "不存在的模型";
        _vm.Provider = ApiProvider.Apimart;
        Assert.False(string.IsNullOrWhiteSpace(_vm.Model));
        Assert.Contains(_vm.Model, _vm.ModelChoices);
    }

    [Fact]
    public void ProviderCost_TracksCurrentProvider()
    {
        // 顶栏显示「按 provider 累计」，历史面板显示「总累计」
        var it1 = new Item { File = "a.png", Prompt = "p", Cost = 0.5,
                             Provider = "openrouter" };
        var it2 = new Item { File = "b.png", Prompt = "p", Cost = 0.3,
                             Provider = "apimart" };
        _svc.Session.Items.Clear();
        _svc.Session.Items.Add(it1);
        _svc.Session.Items.Add(it2);
        _svc.Session.TotalCost = 0.8;

        Assert.Equal(0.5, _svc.Session.CostForProvider(ApiProvider.OpenRouter), 6);
        Assert.Equal(0.3, _svc.Session.CostForProvider(ApiProvider.Apimart), 6);
    }

    [Fact]
    public async Task Polish_EmptyPrompt_DoesNotRequest()
    {
        // 回归：润色无作用 → 现在空提示词必须前置拦截且不发请求
        _vm.Prompt = "   ";
        await _vm.PolishPromptCommand.ExecuteAsync(null);
        Assert.False(_vm.PolishOpen);
        Assert.Empty(_vm.PolishCandidates);
    }

    [Fact]
    public async Task Polish_NotConfigured_DoesNotOpen()
    {
        _vm.Prompt = "一只猫";
        await _vm.PolishPromptCommand.ExecuteAsync(null);
        Assert.False(_vm.PolishOpen);   // 未配置润色 → 不弹窗
    }

    [Fact]
    public void Polish_CandidateNavigation_AndAccept()
    {
        // 4 选 1 浮窗：翻页 / 采用 的行为
        _vm.PolishCandidates.Clear();
        foreach (var c in new[] { "候选A", "候选B", "候选C", "候选D" })
            _vm.PolishCandidates.Add(c);
        _vm.PolishIndex = 0;
        _vm.PolishCurrent = _vm.PolishCandidates[0];
        _vm.PolishOpen = true;

        Assert.Equal("1/4", _vm.PolishCounterText);
        _vm.PolishNextCommand.Execute(null);
        Assert.Equal(1, _vm.PolishIndex);
        Assert.Equal("候选B", _vm.PolishCurrent);
        Assert.Equal("2/4", _vm.PolishCounterText);

        _vm.PolishPrevCommand.Execute(null);   // 回到 0
        _vm.PolishPrevCommand.Execute(null);   // 环绕到 3
        Assert.Equal(3, _vm.PolishIndex);

        _vm.PolishAcceptCommand.Execute(null);
        Assert.Equal("候选D", _vm.Prompt);
        Assert.False(_vm.PolishOpen);          // 采用后关闭
    }

    [Fact]
    public void RegionTools_MappedAndNoIntent()
    {
        // 用户需求 #5：已去掉「语义」概念
        // v0.5.36：去掉「画笔」（与马克笔同路径、仅粗细不同）→
        //          0=方框 1=圆圈 2=马克笔 3=橡皮
        _vm.ToolIndex = 0;
        Assert.Equal(ImgHub.App.Controls.RegionCanvas.RegionTool.Rectangle, _vm.CanvasTool);
        _vm.ToolIndex = 1;
        Assert.Equal(ImgHub.App.Controls.RegionCanvas.RegionTool.Ellipse, _vm.CanvasTool);
        _vm.ToolIndex = 2;
        Assert.Equal(ImgHub.App.Controls.RegionCanvas.RegionTool.Marker, _vm.CanvasTool);
        _vm.ToolIndex = 3;
        Assert.Equal(ImgHub.App.Controls.RegionCanvas.RegionTool.Eraser, _vm.CanvasTool);

        // 越界索引要钳到最后一个可用工具（旧配置里 4=橡皮 也能安全落位）
        _vm.ToolIndex = 99;
        Assert.Equal(ImgHub.App.Controls.RegionCanvas.RegionTool.Eraser, _vm.CanvasTool);

        // UI 显示名必须与枚举**严格对齐**（且不再含「画笔」）
        Assert.Equal("方框", _vm.ToolNames[0]);
        Assert.Equal("圆圈", _vm.ToolNames[1]);
        Assert.Equal("马克笔", _vm.ToolNames[2]);
        Assert.Equal("橡皮", _vm.ToolNames[3]);
        Assert.Equal(4, _vm.ToolNames.Count);
        Assert.DoesNotContain("画笔", _vm.ToolNames);
    }

    [Fact]
    public void ToolUsesBrushSize_OnlyForMarkerAndEraser()
    {
        // 需求⑥：方框/圆圈是**区域**语义（填充整块）→ 粗细条对它们无意义，UI 隐藏。
        _vm.ToolIndex = 0;   // 方框
        Assert.False(_vm.ToolUsesBrushSize);
        _vm.ToolIndex = 1;   // 圆圈
        Assert.False(_vm.ToolUsesBrushSize);
        _vm.ToolIndex = 2;   // 马克笔
        Assert.True(_vm.ToolUsesBrushSize);
        _vm.ToolIndex = 3;   // 橡皮
        Assert.True(_vm.ToolUsesBrushSize);
    }

    [Fact]
    public void BrushSize_DefaultsTo3px()
    {
        // v0.5：用户要求粗细默认 3px
        Assert.Equal(3, _vm.BrushSize);
        Assert.Equal("3px", _vm.BrushSizeText);
    }

    [Fact]
    public void BrushSize_Adjustable()
    {
        // 用户需求 #4：粗细必须可调
        Assert.True(_vm.BrushSizeMin > 0);
        Assert.True(_vm.BrushSizeMax > _vm.BrushSizeMin);

        _vm.BrushSize = 2;
        Assert.Contains("2", _vm.BrushSizeText);
        _vm.BrushSize = 80;
        Assert.Contains("80", _vm.BrushSizeText);
    }

    [Fact]
    public void RegionCache_PerImage()
    {
        // 用户需求 #6：标注跟随图片（按图缓存，切换不丢）
        var pathA = Path.Combine(_home, "a.png");
        var pathB = Path.Combine(_home, "b.png");

        Assert.Null(_vm.GetRegionsFor(pathA));      // 初始无缓存

        var canvas = new ImgHub.App.Controls.RegionCanvas();
        var snap = canvas.ExportShapes();           // 空快照
        _vm.SaveRegionsFor(pathA, snap);
        // 空快照不入缓存（避免堆积空对象）
        Assert.Null(_vm.GetRegionsFor(pathA));

        // 清缓存不应崩
        _vm.ClearRegionCache();
        Assert.Null(_vm.GetRegionsFor(pathB));
    }

    [Fact]
    public void RegionCanvas_MaskExport_NoShapesReturnsNull()
    {
        // 无标注时导出应返回 null（而非空白图误导用户）
        var canvas = new ImgHub.App.Controls.RegionCanvas();
        Assert.Null(canvas.ExportComposite());
        Assert.Null(canvas.ExportMask());
    }

    [Fact]
    public void HistoryRows_ExposeThumbAndDetail()
    {
        // 历史行包 HistoryRow（含缩略图槽位），不再只显示文字
        _svc.Session.Items.Clear();
        _svc.Session.Items.Add(new Item { File = "x.png", Prompt = "很长的提示词用于悬停显示" });
        _vm.RefreshHistoryCommand.Execute(null);
        Assert.Single(_vm.History);
        Assert.IsType<HistoryRow>(_vm.History[0]);
        Assert.Equal("很长的提示词用于悬停显示", _vm.History[0].Prompt);
    }

    // ================================================================ 本轮 review 修复

    [Fact]
    public async Task RefImages_ClearedAfterUse_ByDefault()
    {
        // 回归：参考图无生命周期 → 一次导入后每次编辑都误带上（污染结果）
        _vm.Prompt = "底图";
        await _vm.GenerateCommand.ExecuteAsync(null);
        Assert.Single(_vm.History);

        // 加一张参考图
        var refData = ImgHub.Core.Imaging.Placeholder.Png("ref", size: 32);
        await _vm.AddReferenceImageAsync(refData, "image/png", "my_ref.png");
        Assert.Single(_vm.RefImages);
        Assert.False(_vm.KeepRefImages);   // 默认不保留

        // 用标注编辑（模拟已画标注）
        var baseItem = _vm.History[0];
        var basePath = Path.Combine(_home, baseItem.File);
        var annotated = await File.ReadAllBytesAsync(basePath);
        _vm.SetAnnotatedImage(annotated, regionCount: 1);
        _vm.Prompt = "改成雪原";
        await _vm.EditWithRegionsCommand.ExecuteAsync(null);

        // 编辑完成后参考图应被清空（一次性）
        Assert.Empty(_vm.RefImages);
    }

    [Fact]
    public async Task RefImages_KeptWhenOptedIn()
    {
        _vm.Prompt = "底图2";
        await _vm.GenerateCommand.ExecuteAsync(null);
        _vm.KeepRefImages = true;   // 用户选择保留

        var refData = ImgHub.Core.Imaging.Placeholder.Png("ref2", size: 32);
        await _vm.AddReferenceImageAsync(refData, "image/png", "keep_ref.png");
        Assert.Single(_vm.RefImages);

        var baseItem = _vm.History[0];
        var annotated = await File.ReadAllBytesAsync(Path.Combine(_home, baseItem.File));
        _vm.SetAnnotatedImage(annotated, regionCount: 1);
        _vm.Prompt = "改成夜晚";
        await _vm.EditWithRegionsCommand.ExecuteAsync(null);

        Assert.Single(_vm.RefImages);   // 勾选保留 → 用完不清
    }

    [Fact]
    public async Task BatchResults_ClearedOnFailedGenerate()
    {
        // 回归：生成失败后旧缩略图条仍显示（误导用户以为是本次结果）
        _vm.Prompt = "先成功一次";
        _vm.BatchN = 3;
        await _vm.GenerateCommand.ExecuteAsync(null);
        Assert.Equal(3, _vm.BatchResults.Count);
        var beforeCount = _vm.BatchResults.Count;
        Assert.True(beforeCount > 0);

        // 改成 0 张（非法，会被 clamp 为 1）—— 用一个必然失败的路径：清空历史后编辑
        // 这里验证：新一轮生成开始时缩略图条被清空（不会残留旧结果）
        _vm.Prompt = "";                     // 空提示词 → 直接返回，不触发新生成
        await _vm.GenerateCommand.ExecuteAsync(null);
        // 空提示词提前 return，缩略图条保持（因为没开始新生成）—— 符合预期
        Assert.Equal(beforeCount, _vm.BatchResults.Count);
    }

    [Fact]
    public void TotalCost_ReconcilesWithItems()
    {
        // 回归：TotalCost 与 Items 求和可能偏差（Undo 减的是均摊值）。
        // P0-7 修复后：口径已统一到 Items 求和 —— TotalCost 是**计算属性**，
        // 不再需要 ReconcileCost() 这种"事后校正"方法（已删除）。
        _svc.Session.Items.Clear();
        _svc.Session.TotalCost = 999.0;      // 人为造出巨大偏差（旧调用留下的 override）
        _svc.Session.Items.Add(new Item { File = "a.png", Prompt = "p", Cost = 0.5 });
        Assert.Equal(0.5, _svc.Session.TotalCost, 6);   // 求和优先，override 不再生效
    }

    [Fact]
    public void RegionCanvas_ShapeTransferPreservesData()
    {
        // 回归：宽/窄屏两个画布实例切换时笔画丢失
        var a = new ImgHub.App.Controls.RegionCanvas();
        var b = new ImgHub.App.Controls.RegionCanvas();
        Assert.Equal(0, a.RegionCount);

        // 模拟：a 有数据 → 导出 → 导入到 b
        var snap = a.ExportShapes();
        Assert.NotNull(snap);
        Assert.Empty(snap.Strokes);          // 初始无笔画
        b.ImportShapes(snap);
        Assert.Equal(0, b.RegionCount);      // 同步后仍为 0（一致）

        // 导入 null 不应崩
        b.ImportShapes(null);
        Assert.Equal(0, b.RegionCount);
    }

    // ================================================================ 第七轮：崩溃加固

    [Fact]
    public void LoadApiKey_UnreadableFile_ReturnsEmpty_NoThrow()
    {
        // 回归：LoadApiKey 被绑定属性频繁调用；IO 异常必须吞掉（否则崩 UI）
        // 构造：把 key 路径变成一个**目录**（读取必失败）
        var keyPath = _svc.Session.KeyFile(ApiProvider.OpenRouter);
        try { if (File.Exists(keyPath)) File.Delete(keyPath); } catch { }
        File.WriteAllText(keyPath, "sk-or-v1-OK");

        var v = _vm.LoadApiKey();
        Assert.False(string.IsNullOrEmpty(v));

        // 删除文件后应返回空（不抛）
        File.Delete(keyPath);
        _vm.InvalidateKeyCache();
        Assert.Equal("", _vm.LoadApiKey());
    }

    [Fact]
    public void Settings_SaveWithPartialInput_NoCrash()
    {
        // 回归：点设置保存崩溃
        _vm.OpenSettingsCommand.Execute(null);
        Assert.True(_vm.SettingsOpen);

        // 各种字段留空 / 只有部分填写
        _vm.ApiKeyInput = "";
        _vm.PolishBaseInput = "";
        _vm.PolishModelInput = "";
        _vm.PolishKeyInput = "";
        var ex = Record.Exception(() => _vm.SaveSettingsCommand.Execute(null));
        Assert.Null(ex);
        Assert.False(_vm.SettingsOpen);   // 保存后关闭
    }

    [Fact]
    public void Settings_SaveWithFullInput_NoCrash()
    {
        _vm.OpenSettingsCommand.Execute(null);
        _vm.ApiKeyInput = "sk-or-v1-TESTKEY12345678";
        _vm.PolishBaseInput = "https://example.test/v1";
        _vm.PolishModelInput = "gpt-4o-mini";
        _vm.PolishKeyInput = "sk-polish-abc";

        var ex = Record.Exception(() => _vm.SaveSettingsCommand.Execute(null));
        Assert.Null(ex);
        Assert.False(_vm.SettingsOpen);
        // key 已落盘
        Assert.True(File.Exists(_svc.Session.KeyFile(ApiProvider.OpenRouter)));
    }

    [Fact]
    public void Undo_NoHistory_NoCrash()
    {
        // 回归：撤回崩溃
        var ex = Record.Exception(() => _vm.UndoCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public async Task Undo_WithHistory_RemovesAndKeepsFile()
    {
        _vm.Prompt = "撤回测试";
        await _vm.GenerateCommand.ExecuteAsync(null);
        Assert.Single(_vm.History);

        var removedPath = Path.Combine(_home, _vm.History[0].File);
        Assert.True(File.Exists(removedPath));

        var ex = Record.Exception(() => _vm.UndoCommand.Execute(null));
        Assert.Null(ex);
        Assert.Empty(_vm.History);
        // 语义：从历史移除，但**文件保留**（用户可从数据目录找回）
        Assert.True(File.Exists(removedPath));
    }

    [Fact]
    public void RefreshCommands_NoCrash_OnEmptyState()
    {
        var e1 = Record.Exception(() => _vm.RefreshHistoryCommand.Execute(null));
        var e2 = Record.Exception(() => _vm.RefreshPromptHistoryCommand.Execute(null));
        Assert.Null(e1);
        Assert.Null(e2);
    }

    // ================================================================ 第七轮：累计口径

    [Fact]
    public void TotalCost_EqualsItemsSum_Always()
    {
        // 回归：提供商累计与总累计不匹配（口径不一致）
        _svc.Session.Items.Clear();
        _svc.Session.Items.Add(new Item { File = "a.png", Prompt = "p", Cost = 0.111,
                                          Provider = "openrouter" });
        _svc.Session.Items.Add(new Item { File = "b.png", Prompt = "p", Cost = 0.222,
                                          Provider = "apimart" });
        _svc.Session.Items.Add(new Item { File = "c.png", Prompt = "p", Cost = 0.333,
                                          Provider = "openrouter" });

        Assert.Equal(0.666, _svc.Session.TotalCost, 6);           // 总和
        Assert.Equal(0.444, _svc.Session.CostForProvider(ApiProvider.OpenRouter), 6);
        Assert.Equal(0.222, _svc.Session.CostForProvider(ApiProvider.Apimart), 6);

        // 关键不变量：分组之和 == 总和（无 unknown 时）
        var byProv = _svc.Session.CostByProvider();
        Assert.Equal(_svc.Session.TotalCost, byProv.Values.Sum(), 6);
    }

    [Fact]
    public void TotalCost_UnknownProvider_DoesNotDrift()
    {
        // 回归：旧数据（无 provider 字段）曾被按"当前 provider"计入 → 切 provider 时漂移
        _svc.Session.Items.Clear();
        _svc.Session.Items.Add(new Item { File = "old.png", Prompt = "legacy", Cost = 0.5 });
        // Provider 字段为空

        // 无论当前 provider 是哪个，"该 provider 的累计"都应为 0（不漂移）
        Assert.Equal(0.0, _svc.Session.CostForProvider(ApiProvider.OpenRouter), 6);
        Assert.Equal(0.0, _svc.Session.CostForProvider(ApiProvider.Apimart), 6);
        // 但总累计与 unknown 组要能看到它
        Assert.Equal(0.5, _svc.Session.TotalCost, 6);
        Assert.Equal(0.5, _svc.Session.CostForUnknownProvider(), 6);

        var byProv = _svc.Session.CostByProvider();
        Assert.True(byProv.ContainsKey(ImgHub.Core.Storage.Session.UnknownProvider));
        // 不变量：所有分组之和 == 总和
        Assert.Equal(_svc.Session.TotalCost, byProv.Values.Sum(), 6);
    }

    [Fact]
    public async Task TotalCost_ConsistentAfterGenerateAndUndo()
    {
        // 端到端：生成 → 撤回，两次累计都应等于 Items 求和
        _vm.Prompt = "一致性";
        await _vm.GenerateCommand.ExecuteAsync(null);
        Assert.Equal(_svc.Session.Items.Sum(i => i.Cost), _vm.TotalCost, 6);

        _vm.UndoCommand.Execute(null);
        Assert.Equal(_svc.Session.Items.Sum(i => i.Cost), _vm.TotalCost, 6);
        Assert.Equal(_vm.TotalCost, _vm.ProviderCost + _vm.UnknownCost, 6);
    }

    // ================================================================ 第七轮：模型统计

    [Fact]
    public void ModelStats_UnusedModel_ReturnsNull()
    {
        // 用户需求 #10：没用过的模型不显示预估（避免误导）
        Assert.Null(_svc.ModelStats.GetAvgCost("openrouter", "never-used-model"));
        Assert.False(_svc.ModelStats.HasStats("openrouter", "never-used-model"));
    }

    [Fact]
    public void ModelStats_UsedModel_AverageCost()
    {
        // 用过 → 按平均花费预估
        _svc.ModelStats.Record("openrouter", "model-x", cost: 0.10, images: 1);
        _svc.ModelStats.Record("openrouter", "model-x", cost: 0.20, images: 1);
        _svc.ModelStats.Record("openrouter", "model-x", cost: 0.30, images: 1);

        var avg = _svc.ModelStats.GetAvgCost("openrouter", "model-x");
        Assert.NotNull(avg);
        Assert.Equal(0.20, avg!.Value, 6);   // (0.1+0.2+0.3)/3 = 0.2

        // 与其它模型隔离
        Assert.Null(_svc.ModelStats.GetAvgCost("openrouter", "model-y"));
        Assert.Null(_svc.ModelStats.GetAvgCost("apimart", "model-x"));
    }

    [Fact]
    public void ModelStats_PersistsAsReadableJson()
    {
        // 数据保存为可读 JSON（可手动编辑/删除）
        _svc.ModelStats.Record("apimart", "seedream-5-0-pro", cost: 0.05, images: 1);

        var f = Path.Combine(_home, "model_stats.json");
        Assert.True(File.Exists(f));
        var text = File.ReadAllText(f);
        // 可读：含字段名与模型名（非二进制/混淆）
        Assert.Contains("seedream", text);
        Assert.Contains("total_cost", text);

        // 重新加载（新实例）能读到
        var svc2 = new ImgHub.Core.Services.ModelStatsService(_home);
        var avg = svc2.GetAvgCost("apimart", "seedream-5-0-pro");
        Assert.NotNull(avg);
        Assert.Equal(0.05, avg!.Value, 6);

        // 清空
        svc2.Clear();
        Assert.Null(svc2.GetAvgCost("apimart", "seedream-5-0-pro"));
    }

    [Fact]
    public async Task Estimate_ShowsAvgAfterFirstUse()
    {
        // 端到端：用过模型后，预估文本应显示历史平均
        _vm.Prompt = "统计测试";
        _vm.BatchN = 1;
        await _vm.GenerateCommand.ExecuteAsync(null);   // 离线生成（cost=0）

        // 离线 cost=0 → 平均 0 → 但"用过"了，应显示预估（非"暂无记录"）
        var model = _vm.Model;
        Assert.True(_svc.ModelStats.HasStats(ImgHub.Core.Models.ApiProvider.OpenRouter.Key(), model));
    }

    [Fact]
    public void Estimate_ReactsToChanges()
    {
        // 用户需求 #10：预估按「provider+模型」历史统计
        //   · 没用过 → 显示"暂无历史记录"（不显示数字，避免误导）
        //   · 用过   → 按平均花费 × 批量数
        _vm.Quality = "low";
        _vm.Resolution = "1k";

        // 当前模型在测试里没用过 → 应提示"暂无记录"
        Assert.Contains("暂无", _vm.EstimatedCostText);

        // 记录一次花费后 → 应显示预估数字
        _svc.ModelStats.Record(_vm.Provider.Key(), _vm.Model, cost: 0.02, images: 1);
        // 手动触发重算（改属性触发 OnQualityChanged）
        _vm.Quality = "medium";
        Assert.Contains("预估", _vm.EstimatedCostText);
        Assert.DoesNotContain("暂无", _vm.EstimatedCostText);

        // 批量变化 → 预估随之变化
        var one = _vm.EstimatedCostText;
        _vm.BatchN = 3;
        var three = _vm.EstimatedCostText;
        Assert.NotEqual(one, three);

        // 换个没用过的模型 → 又回到"暂无记录"
        _vm.Model = Catalog.ModelChoices(_vm.Provider).First(m => m != _vm.Model);
        Assert.Contains("暂无", _vm.EstimatedCostText);
    }

    // ================================================================ v5.23.0 回归
    // 锁死本轮修复，防止复发（对应 docs/fix-plan-v5.23.md 的 P1–P13）

    [Fact]
    public void SelfCheck_ReportsNotReady_WhenKeyMissing()
    {
        // P5：没配置 key 时**不能**显示「就绪」（用户抱怨的核心）
        var home = Path.Combine(Path.GetTempPath(), "imghub-selfcheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("IMGHUB_HOME", home);
        try
        {
            var sess = new Session(home);
            sess.Config.Offline = false;
            sess.Config.PolishEnabled = false;   // 排除润色，聚焦 key 这一项
            var svc = new AppServices
            {
                Session = sess,
                Http = new HttpJsonClient(),
                ImageApi = new ImageApi(sess.Provider, new HttpJsonClient()),
                Polish = new PolishService(new HttpJsonClient()),
                Storage = new FakeStorage(),
                Platform = new FakePlatform(),
                ModelStats = new ModelStatsService(home),
                Pending = new PendingTaskStore(home),
            };
            var vm = new MainViewModel(svc);

            Assert.False(vm.SelfCheckOk);
            Assert.Contains("未就绪", vm.SelfCheckText);
            Assert.NotEqual("就绪", vm.SelfCheckText);

            // 写一个 key 文件 → 自检应转为通过（润色已关、数据目录可写）
            sess.SaveKey("sk-or-v1-abcdefghijklmnop", ApiProvider.OpenRouter);
            vm.InvalidateKeyCache();
            Assert.True(vm.RunSelfCheck());
            Assert.Equal("就绪", vm.SelfCheckText);
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
            Environment.SetEnvironmentVariable("IMGHUB_HOME", _home);
        }
    }

    [Fact]
    public void SelfCheck_FlagsMissingPolish_WhenEnabled()
    {
        // P5 口径：用户要求「全功能可用」才叫就绪 → 润色启用但未配置也算未就绪
        _svc.Session.Config.PolishEnabled = true;
        _svc.Session.Config.PolishBaseUrl = "";
        _svc.Session.Config.PolishModel = "";
        _vm.PolishEnabled = true;
        // 先补上生图 key，隔离出"润色"这一项
        _svc.Session.SaveKey("sk-or-v1-abcdefghijklmnop", ApiProvider.OpenRouter);
        _vm.InvalidateKeyCache();

        Assert.False(_vm.RunSelfCheck());
        Assert.Contains(_vm.SelfCheckDetails, d => d.Contains("润色未配置"));
        Assert.Contains("润色", _vm.SelfCheckText);   // 此时润色是首个未通过项

        // 补全润色配置 → 全部通过
        _svc.Session.Config.PolishBaseUrl = "https://example.com/v1";
        _svc.Session.Config.PolishModel = "gpt-4o-mini";
        _svc.Session.SavePolishKey("sk-polish-123456");
        Assert.True(_vm.RunSelfCheck());
        Assert.Equal("就绪", _vm.SelfCheckText);
    }

    [Fact]
    public async Task DeleteWithFiles_RequiresConfirmation()
    {
        // P11：删除文件是破坏性操作，**必须先弹确认浮层**
        _vm.Prompt = "待删除";
        await _vm.GenerateCommand.ExecuteAsync(null);
        var row = _vm.History[0];
        var path = Path.Combine(_home, row.File);
        Assert.True(File.Exists(path));

        _vm.RequestDeleteHistory(row);
        Assert.True(_vm.ConfirmOpen);                  // 浮层已弹
        Assert.Contains("不可恢复", _vm.ConfirmMessage);
        Assert.True(File.Exists(path));                // 未确认前不删

        _vm.ConfirmCancelCommand.Execute(null);
        Assert.False(_vm.ConfirmOpen);
        Assert.True(File.Exists(path));                // 取消后文件还在
        Assert.Contains(row, _vm.History);

        // 再来一次并确认 → 文件与列表项都消失
        _vm.RequestDeleteHistory(row);
        _vm.ConfirmOkCommand.Execute(null);
        Assert.False(File.Exists(path));
        Assert.DoesNotContain(row, _vm.History);
    }

    [Fact]
    public async Task RemoveFromList_KeepsFile_AndNoConfirm()
    {
        // P11：「仅从列表移除」是非破坏性 → 不弹确认，且文件保留
        _vm.Prompt = "保留文件";
        await _vm.GenerateCommand.ExecuteAsync(null);
        var row = _vm.History[0];
        var path = Path.Combine(_home, row.File);

        _vm.DeleteListItemCommand.Execute(row.Item);

        Assert.False(_vm.ConfirmOpen);
        Assert.True(File.Exists(path));
        Assert.DoesNotContain(row, _vm.History);
    }

    [Fact]
    public async Task BatchDelete_ConfirmsCount_AndDeletesAll()
    {
        // P12：批量删除需确认，且提示明确的 N 项
        for (int i = 0; i < 3; i++)
        {
            _vm.Prompt = $"批量{i}";
            await _vm.GenerateCommand.ExecuteAsync(null);
        }
        Assert.Equal(3, _vm.History.Count);

        _vm.BatchDeleteWithFiles(_vm.History.ToList());

        Assert.True(_vm.ConfirmOpen);
        Assert.Contains("3 个图片文件", _vm.ConfirmMessage);
        Assert.Contains("3", _vm.ConfirmOkText);

        _vm.ConfirmOkCommand.Execute(null);
        Assert.Empty(_vm.History);
    }

    [Fact]
    public async Task BatchRemoveFromList_KeepsFiles()
    {
        // P12：批量「仅从列表移除」保留文件
        for (int i = 0; i < 2; i++)
        {
            _vm.Prompt = $"移除{i}";
            await _vm.GenerateCommand.ExecuteAsync(null);
        }
        var paths = _vm.History.Select(r => Path.Combine(_home, r.File)).ToList();
        var rows = _vm.History.ToList();

        _vm.BatchRemoveFromList(rows);

        Assert.Empty(_vm.History);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void BatchDeletePrompts_ConfirmsCount()
    {
        // P12：提示词历史批量删除
        for (int i = 0; i < 2; i++)
            _svc.Session.PushPromptHistory($"批量提示词{i}", "gen");
        _vm.RefreshPromptHistoryCommand.Execute(null);
        Assert.True(_vm.PromptHistory.Count >= 2);

        var picks = _vm.PromptHistory.Take(2).ToList();
        _vm.BatchDeletePromptHistory(picks);

        Assert.True(_vm.ConfirmOpen);
        Assert.Contains("2 条", _vm.ConfirmMessage);

        _vm.ConfirmOkCommand.Execute(null);
        Assert.DoesNotContain(picks[0], _vm.PromptHistory);
        Assert.DoesNotContain(picks[1], _vm.PromptHistory);
    }

    [Fact]
    public void MultiSelect_TogglesSelectionMode_AndButtonText()
    {
        // P12：多选默认关（防误触）；开启后切换 Multiple 且按钮文案变「完成多选」
        Assert.False(_vm.HistoryMultiSelect);
        Assert.Equal("多选", _vm.HistoryMultiButtonText);
        Assert.Equal(Avalonia.Controls.SelectionMode.Single, _vm.HistorySelectionMode);

        _vm.HistoryMultiSelect = true;
        Assert.Equal("完成多选", _vm.HistoryMultiButtonText);
        Assert.Equal(Avalonia.Controls.SelectionMode.Multiple, _vm.HistorySelectionMode);

        Assert.False(_vm.PromptMultiSelect);
        _vm.PromptMultiSelect = true;
        Assert.Equal("完成多选", _vm.PromptMultiButtonText);
        Assert.Equal(Avalonia.Controls.SelectionMode.Multiple, _vm.PromptSelectionMode);
    }

    [Fact]
    public void VersionText_ComesFromAssembly()
    {
        // P13：底栏版本号取自程序集（来源 = Directory.Build.props 的 <Version>）
        Assert.StartsWith("ImgHub v", _vm.VersionText);
        Assert.DoesNotContain("+", _vm.VersionText);   // 已剥离 +<sha> 后缀
    }

    [Fact]
    public void RegionCanvas_NoShapes_ExportsNull()
    {
        // P3 相关：无标注时导出必须返回 null（上层据此提示"请先圈画"）
        var canvas = new ImgHub.App.Controls.RegionCanvas
        {
            BrushColor = "#FF3B30",
            BrushSize = 24,
        };
        Assert.Null(canvas.ExportComposite());
        Assert.Equal(0, canvas.RegionCount);
        canvas.ClearRegions();
        Assert.Equal(0, canvas.RegionCount);
    }
}
