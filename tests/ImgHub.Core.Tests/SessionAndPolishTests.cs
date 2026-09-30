using System.Text.Json;
using ImgHub.Core;
using ImgHub.Core.Models;
using ImgHub.Core.Services;
using ImgHub.Core.Storage;

namespace ImgHub.Core.Tests;

/// <summary>
/// 存储 + 配置净化 + 提示词历史 + 润色清洗的回归测试。
/// 对应 Python 的 session sanitize / prompt history / polish cleaning。
/// </summary>
public class SessionAndPolishTests : IDisposable
{
    private readonly string _home;
    public SessionAndPolishTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "imghub-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    [Fact]
    public void Sanitize_FixesInvalidQualityForProvider()
    {
        // 回归：上次在 APIMart 选了 xhigh，但 config 里 provider 是 openrouter
        // → 启动后质量仍是 xhigh，一生成就 400
        var s = new Session(_home);
        s.Config.Provider = "openrouter";
        s.Config.Model = "openai/gpt-image-2.5-flare";
        s.Config.Quality = "xhigh";
        s.Sanitize();
        // xhigh 在 OpenRouter 下非法 → 必须回退到 auto（auto 恒合法，永不 400）
        Assert.Equal("auto", s.Config.Quality);
    }

    [Fact]
    public void Sanitize_FixesModelProviderMismatch()
    {
        var s = new Session(_home);
        s.Config.Provider = "apimart";
        s.Config.Model = "openai/gpt-image-2";   // 带斜杠 → 与 APIMart 不符
        s.Sanitize();
        Assert.Equal(Catalog.DefaultModelApimart, s.Config.Model);
    }

    [Fact]
    public void Sanitize_ClampsResolutionFormatBatch()
    {
        var s = new Session(_home);
        s.Config.Resolution = "8k";
        s.Config.OutputFormat = "tiff";
        s.Config.BatchN = 99;
        s.Sanitize();
        Assert.Equal("1k", s.Config.Resolution);
        Assert.Equal("png", s.Config.OutputFormat);
        Assert.Equal(1, s.Config.BatchN);
    }

    [Fact]
    public void Config_ReadsLegacySnakeCaseFile()
    {
        // 回归：原 Python 版 config.json 用 snake_case（batch_n / output_format …），
        // C# 属性是 PascalCase。若不做映射，整份配置读不进来 → 参数无法恢复。
        var legacy = "{\n \"model\": \"openai/gpt-image-1-mini\",\n \"quality\": \"medium\",\n \"aspect\": \"16:9\",\n \"offline\": false,\n \"preview\": true,\n \"resolution\": \"2k\",\n \"output_format\": \"jpeg\",\n \"batch_n\": 3,\n \"total_cost\": 0.0,\n \"provider\": \"openrouter\",\n \"key_saved\": true\n}";
        File.WriteAllText(Path.Combine(_home, "config.json"), legacy);

        var s = new Session(_home);
        Assert.Equal("openai/gpt-image-1-mini", s.Config.Model);
        Assert.Equal("medium", s.Config.Quality);
        Assert.Equal("16:9", s.Config.Aspect);
        Assert.Equal("2k", s.Config.Resolution);
        Assert.Equal("jpeg", s.Config.OutputFormat);
        Assert.Equal(3, s.Config.BatchN);
        Assert.Equal("openrouter", s.Config.Provider);
    }

    [Fact]
    public void Config_RoundTripKeepsSnakeCaseKeys()
    {
        // 写回的键名必须与旧版一致（保证与 Python 版文件互通）
        var s = new Session(_home);
        s.Config.BatchN = 2;
        s.Config.OutputFormat = "webp";
        s.SaveConfig();

        var text = File.ReadAllText(Path.Combine(_home, "config.json"));
        Assert.Contains("\"batch_n\"", text);
        Assert.Contains("\"output_format\"", text);

        var s2 = new Session(_home);
        Assert.Equal(2, s2.Config.BatchN);
        Assert.Equal("webp", s2.Config.OutputFormat);
    }

    [Fact]
    public void Config_PersistsAcrossSessions()
    {
        var s1 = new Session(_home);
        s1.Config.Quality = "medium";
        s1.Config.Aspect = "16:9";
        s1.Config.TotalCost = 1.25;
        s1.SaveConfig();

        var s2 = new Session(_home);
        Assert.Equal("medium", s2.Config.Quality);
        Assert.Equal("16:9", s2.Config.Aspect);
    }

    [Fact]
    public void PromptHistory_DedupAndMostRecentFirst()
    {
        var s = new Session(_home);
        s.PushPromptHistory("A提示", "gen");
        s.PushPromptHistory("B提示", "gen");
        s.PushPromptHistory("C提示", "edit");
        s.PushPromptHistory("A提示", "gen");   // 重复

        var h = s.LoadPromptHistory();
        Assert.Equal(3, h.Count);
        Assert.Equal("A提示", h[0]);           // 最近推送的在最前
        Assert.Equal("C提示", h[1]);
        Assert.Equal("B提示", h[2]);
    }

    [Fact]
    public void PromptHistory_KindFilter()
    {
        var s = new Session(_home);
        s.PushPromptHistory("A", "gen");
        s.PushPromptHistory("C", "edit");
        Assert.Equal(new[] { "A" }, s.LoadPromptHistory(kind: "gen"));
        Assert.Equal(new[] { "C" }, s.LoadPromptHistory(kind: "edit"));
    }

    [Fact]
    public void PromptHistory_EmptyNotRecorded()
    {
        var s = new Session(_home);
        s.PushPromptHistory("");
        s.PushPromptHistory("   ");
        Assert.Empty(s.LoadPromptHistory());
    }

    [Fact]
    public void Push_IncrementsCounterAndPutsCurrentFirst()
    {
        var s = new Session(_home);
        var path = Path.Combine(_home, "a.png");
        File.WriteAllBytes(path, ImgHub.Core.Imaging.Placeholder.Png("t", size: 8));
        s.Push(new Item { File = "a.png", Prompt = "p", Kind = "gen" });
        Assert.Equal(1, s.Counter);
        Assert.NotNull(s.Current);
        Assert.Equal("a.png", s.Current!.File);
    }

    // ---------------------------------------------------------------- Polish 清洗
    [Theory]
    [InlineData("```\n一只猫\n```", "一只猫")]
    [InlineData("Here is the prompt: 红色的车", "红色的车")]
    [InlineData("\"红色的车\"", "红色的车")]
    [InlineData("  多余   空格   在这里  ", "多余 空格 在这里")]
    [InlineData("润色结果1：赛博朋克城市", "赛博朋克城市")]
    [InlineData("1. 一只橘猫", "一只橘猫")]
    public void PolishClean_StripsDecoration(string raw, string expected)
    {
        Assert.Equal(expected, PolishService.Clean(raw));
    }

    [Fact]
    public void PolishService_NormalizeDividers_FixesTypos()
    {
        // 回归：LLM 把 ---DIVIDER--- 拼成 ---DIDIER--- 等变体 → 分割漏分
        var raw = "候选一---DIDIER---候选二---DIVIDER---候选三---DIDIER---候选四";
        var normalized = ImgHub.Core.Services.PolishService.NormalizeDividers(raw);
        Assert.DoesNotContain("DIDIER", normalized);
        Assert.Equal(3, normalized.Split("---DIVIDER---").Length - 1);

        // 正常分隔符不受影响
        var ok = "A---DIVIDER---B---DIVIDER---C---DIVIDER---D";
        Assert.Equal(ok, ImgHub.Core.Services.PolishService.NormalizeDividers(ok));

        // 非分隔符的横线不误伤（如 ---END--- 与 DIVIDER 相似度过低）
        var end = "A---END---B";
        Assert.Equal(end, ImgHub.Core.Services.PolishService.NormalizeDividers(end));
    }

    [Fact]
    public void PolishService_NotConfigured_IsDisabled()
    {
        var svc = new PolishService(new ImgHub.Core.Http.HttpJsonClient());
        Assert.False(svc.Configured);
        Assert.False(svc.Enabled);
        Assert.Contains("未配置", svc.Describe());
    }

    [Fact]
    public async Task PolishService_NoKey_ThrowsInsteadOfRequesting()
    {
        // 回归：未配置 key 时绝不发请求（也避免把空 Bearer 打出去）
        var svc = new PolishService(new ImgHub.Core.Http.HttpJsonClient());
        await Assert.ThrowsAsync<ImgHub.Core.Http.ApiError>(
            () => svc.PicksAsync("测试"));
    }

    [Fact]
    public void PolishService_ConfiguredWhenKeySet()
    {
        var svc = new PolishService(new ImgHub.Core.Http.HttpJsonClient())
        {
            ApiKey = "sk-test",
            BaseUrl = "https://example.test/v1",
            Model = "m",
        };
        Assert.True(svc.Configured);
        Assert.True(svc.Enabled);
    }
}

/// <summary>
/// 改名（Imgagent → ImgHub）的**数据兼容**回归测试。
///
/// ⚠️ 为什么必须锁定这些行为：
///   改名很容易顺手把"旧名字"当成残留删掉 —— 但这里的旧名字是**用户数据的位置**。
///   删了它，用户的历史图库、已保存的 API key 会在升级后"凭空消失"，
///   而且表现为"需要重新配置 + 历史清空"，很难联想到是改名导致的。
/// </summary>
public sealed class RenameCompatibilityTests : IDisposable
{
    private readonly string _home;

    public RenameCompatibilityTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "imghub-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    [Fact]
    public void KeyFile_UsesNewName_AndLegacyNameIsKnown()
    {
        var sess = new Session(_home);
        Assert.EndsWith(".imghub_key", sess.KeyFile(ApiProvider.OpenRouter));
        Assert.EndsWith(".imghub_apimart_key", sess.KeyFile(ApiProvider.Apimart));
        // 改名前的名字必须仍被记录（供回退读取）
        Assert.EndsWith(".imgagent_key", sess.LegacyKeyFile(ApiProvider.OpenRouter));
        Assert.EndsWith(".imgagent_apimart_key", sess.LegacyKeyFile(ApiProvider.Apimart));
        Assert.EndsWith(".imghub_polish_key", sess.PolishKeyFile);
        Assert.EndsWith(".imgagent_polish_key", sess.LegacyPolishKeyFile);
    }

    [Fact]
    public void ReadKeyFileCompat_PrefersNewFile()
    {
        var neu = Path.Combine(_home, ".imghub_key");
        var old = Path.Combine(_home, ".imgagent_key");
        File.WriteAllText(neu, "sk-new");
        File.WriteAllText(old, "sk-old");

        // 新文件存在 → 用新文件（旧文件不干扰）
        Assert.Equal("sk-new", Session.ReadKeyFileCompat(neu, old));
    }

    [Fact]
    public void ReadKeyFileCompat_FallsBackToLegacy_WhenNewMissing()
    {
        // 关键场景：用户升级后还没重新保存过 key —— 必须仍然可用
        var neu = Path.Combine(_home, ".imghub_key");
        var old = Path.Combine(_home, ".imgagent_key");
        File.WriteAllText(old, "  sk-legacy  ");

        Assert.Equal("sk-legacy", Session.ReadKeyFileCompat(neu, old));
    }

    [Fact]
    public void ReadKeyFileCompat_ReturnsEmpty_WhenNeitherExists()
    {
        var neu = Path.Combine(_home, ".imghub_key");
        var old = Path.Combine(_home, ".imgagent_key");
        Assert.Equal("", Session.ReadKeyFileCompat(neu, old));
    }

    [Fact]
    public void SaveKey_WritesNewNameOnly()
    {
        // 保存时只写新名（不双写，避免两处不一致）
        var sess = new Session(_home);
        sess.SaveKey("sk-fresh", ApiProvider.OpenRouter);

        Assert.True(File.Exists(Path.Combine(_home, ".imghub_key")));
        Assert.False(File.Exists(Path.Combine(_home, ".imgagent_key")));
    }

    [Fact]
    public void Session_OnLegacyHome_ReadsConfigAndHistory()
    {
        // 模拟"数据目录仍是改名前的路径"这一真实升级场景：
        // 目录里的 config.json / state.json 必须照常读出，历史不丢。
        var legacyHome = Path.Combine(_home, "imgagent-home");
        Directory.CreateDirectory(legacyHome);
        File.WriteAllText(Path.Combine(legacyHome, "config.json"),
            """{"model":"gpt-image-2.5-flare","quality":"high","batch_n":3,"provider":"apimart"}""");
        // ⚠️ LoadState 只保留**磁盘上文件仍存在**的历史项 → 必须真的建出图片文件，
        //    否则这条断言会因"文件不存在被过滤"而假失败。
        File.WriteAllBytes(Path.Combine(legacyHome, "a.png"), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(legacyHome, "state.json"),
            """{"counter":7,"items":[{"file":"a.png","prompt":"p","kind":"gen","cost":0.5}]}""");

        var sess = new Session(legacyHome);

        Assert.Equal("gpt-image-2.5-flare", sess.Config.Model);
        Assert.Equal("high", sess.Config.Quality);
        Assert.Equal(3, sess.Config.BatchN);
        Assert.Equal(ApiProvider.Apimart, sess.Provider);
        Assert.Single(sess.Items);
        Assert.Equal("a.png", sess.Items[0].File);
        Assert.Equal(0.5, sess.Items[0].Cost, 6);
    }

    // ================================================================ P0-1b：state.json 键的单一真源

    [Fact]
    public void SaveState_DoesNotWriteConfigMirrorKeys()
    {
        // P0-1b：state.json 曾写 aspect / offline / preview 三个键，但 LoadState **从不读回**它们
        //（真正生效的是 config.json）→ 纯误导性冗余，会让后来者以为这些字段走 state.json 恢复。
        var s = new Session(_home);
        s.Config.Aspect = "16:9";
        s.Config.Offline = true;

        s.SaveState();

        using var doc = JsonDocument.Parse(File.ReadAllText(s.StateFile));
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();

        // 5 个「config.json 的镜像键」都不该出现在 state.json 里
        // （C# 的 LoadState 一个都不读；它们的真源是 config.json）
        Assert.DoesNotContain("model", keys);
        Assert.DoesNotContain("quality", keys);
        Assert.DoesNotContain("aspect", keys);
        Assert.DoesNotContain("offline", keys);
        Assert.DoesNotContain("preview", keys);

        // state.json 只保留它真正负责的三件事
        Assert.Contains("counter", keys);
        Assert.Contains("total_cost", keys);
        Assert.Contains("items", keys);
    }

    [Fact]
    public void AspectAndOffline_RoundTripThroughConfigJson_NotStateJson()
    {
        // 与上一条配对：删掉冗余键**不能**导致这两个设置丢失 ——
        // 它们的真源是 config.json（SaveState 内部会连带 SaveConfig）。
        var s = new Session(_home);
        s.Config.Aspect = "16:9";
        s.Config.Offline = true;
        s.SaveState();

        var reloaded = new Session(_home);

        Assert.Equal("16:9", reloaded.Config.Aspect);
        Assert.True(reloaded.Config.Offline);
    }

    [Fact]
    public void AutoPreview_KeptForConfigCompatibility_ButNotWiredInCs()
    {
        // P0-1b 收尾（用户决策 dec-8898aa06f5706b5d）：**保留** AutoPreview 字段。
        // 原因：legacy Python 真的从 config.json 读 `preview`（store.py:165），
        // 且 C# 沿用同一数据目录 → 该字段是「与 legacy 的 1:1 配置契约」，
        // 不是可以随手删的死字段。代价：C# 侧目前无「生成后自动预览」功能。
        var names = typeof(AppConfig).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.Contains("AutoPreview", names);

        // 且它必须仍能在 config.json 里往返（键名 preview，snake_case 互通）
        var s = new Session(_home);
        s.Config.AutoPreview = false;
        s.SaveConfig();

        var raw = File.ReadAllText(s.ConfigFile);
        Assert.Contains("\"preview\"", raw);

        var reloaded = new Session(_home);
        Assert.False(reloaded.Config.AutoPreview);
    }
}
