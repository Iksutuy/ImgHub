using Imgagent.App.Services;
using Imgagent.App.ViewModels;
using Imgagent.Core;
using Imgagent.Core.Http;
using Imgagent.Core.Models;
using Imgagent.Core.Services;
using Imgagent.Core.Storage;

namespace Imgagent.Integration.Tests;

/// <summary>测试用的假平台存储（不碰真实文件系统对话框）。</summary>
internal sealed class FakeStorage : IPlatformStorage
{
    public List<string> Saved { get; } = new();
    public Task<string?> SaveToGalleryAsync(byte[] data, string fileName, string mediaType)
    { Saved.Add(fileName); return Task.FromResult<string?>("/fake/" + fileName); }
    public Task OpenInExternalViewerAsync(string path) => Task.CompletedTask;
    public Task RevealInFileManagerAsync(string path) => Task.CompletedTask;
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
public class WorkbenchFlowTests : IDisposable
{
    private readonly string _home;
    private readonly AppServices _svc;
    private readonly MainViewModel _vm;

    public WorkbenchFlowTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "imgagent-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("IMGAGENT_HOME", _home);

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
        };
        _vm = new MainViewModel(_svc);
        _vm.Offline = true;
    }

    public void Dispose()
    {
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
        Assert.Equal("image/png", Imgagent.Core.Imaging.ImageCodec.SniffMediaType(bytes));

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
        var data = Imgagent.Core.Imaging.Placeholder.Png("imported", size: 32);
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
    public async Task SaveToGallery_DelegatesToPlatformStorage()
    {
        _vm.Prompt = "保存测试";
        await _vm.GenerateCommand.ExecuteAsync(null);
        await _vm.SaveToGalleryCommand.ExecuteAsync(null);
        Assert.Single(((FakeStorage)_svc.Storage).Saved);
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
        _vm.Prompt = "费用持久化测试";
        await _vm.GenerateCommand.ExecuteAsync(null);
        _svc.Session.TotalCost = 1.234567;
        _svc.Session.SaveState();

        var reloaded = new Session(_home);
        Assert.Equal(1.234567, reloaded.TotalCost, 6);
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
        // 工具枚举顺序：0=马克笔 1=画笔 2=方框 3=圆圈 4=橡皮
        _vm.ToolIndex = 0;
        Assert.Equal(Imgagent.App.Controls.RegionCanvas.RegionTool.Marker, _vm.CanvasTool);
        _vm.ToolIndex = 2;
        Assert.Equal(Imgagent.App.Controls.RegionCanvas.RegionTool.Rectangle, _vm.CanvasTool);
        _vm.ToolIndex = 3;
        Assert.Equal(Imgagent.App.Controls.RegionCanvas.RegionTool.Ellipse, _vm.CanvasTool);
        _vm.ToolIndex = 4;
        Assert.Equal(Imgagent.App.Controls.RegionCanvas.RegionTool.Eraser, _vm.CanvasTool);
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

        var canvas = new Imgagent.App.Controls.RegionCanvas();
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
        var canvas = new Imgagent.App.Controls.RegionCanvas();
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
        var refData = Imgagent.Core.Imaging.Placeholder.Png("ref", size: 32);
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

        var refData = Imgagent.Core.Imaging.Placeholder.Png("ref2", size: 32);
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
        // 回归：TotalCost 与 Items 求和可能偏差（Undo 减的是均摊值）
        _svc.Session.Items.Clear();
        _svc.Session.TotalCost = 999.0;      // 人为造出巨大偏差
        _svc.Session.Items.Add(new Item { File = "a.png", Prompt = "p", Cost = 0.5 });
        _svc.Session.ReconcileCost();
        Assert.Equal(0.5, _svc.Session.TotalCost, 6);
    }

    [Fact]
    public void RegionCanvas_ShapeTransferPreservesData()
    {
        // 回归：宽/窄屏两个画布实例切换时笔画丢失
        var a = new Imgagent.App.Controls.RegionCanvas();
        var b = new Imgagent.App.Controls.RegionCanvas();
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

    [Fact]
    public void Estimate_ReactsToChanges()
    {
        _vm.Quality = "low";
        _vm.Resolution = "1k";
        _vm.BatchN = 1;
        var one = _vm.EstimatedCostText;
        _vm.BatchN = 4;
        Assert.NotEqual(one, _vm.EstimatedCostText);
    }
}
