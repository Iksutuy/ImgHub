using Imgagent.Core;
using Imgagent.Core.Models;
using Imgagent.Core.Services;
using Imgagent.Core.Storage;

namespace Imgagent.Core.Tests;

/// <summary>
/// 存储 + 配置净化 + 提示词历史 + 润色清洗的回归测试。
/// 对应 Python 的 session sanitize / prompt history / polish cleaning。
/// </summary>
public class SessionAndPolishTests : IDisposable
{
    private readonly string _home;

    public SessionAndPolishTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "imgagent-test-" + Guid.NewGuid().ToString("N"));
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
        File.WriteAllBytes(path, Imgagent.Core.Imaging.Placeholder.Png("t", size: 8));
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
    public void PolishService_NotConfigured_IsDisabled()
    {
        var svc = new PolishService(new Imgagent.Core.Http.HttpJsonClient());
        Assert.False(svc.Configured);
        Assert.False(svc.Enabled);
        Assert.Contains("未配置", svc.Describe());
    }

    [Fact]
    public async Task PolishService_NoKey_ThrowsInsteadOfRequesting()
    {
        // 回归：未配置 key 时绝不发请求（也避免把空 Bearer 打出去）
        var svc = new PolishService(new Imgagent.Core.Http.HttpJsonClient());
        await Assert.ThrowsAsync<Imgagent.Core.Http.ApiError>(
            () => svc.PicksAsync("测试"));
    }

    [Fact]
    public void PolishService_ConfiguredWhenKeySet()
    {
        var svc = new PolishService(new Imgagent.Core.Http.HttpJsonClient())
        {
            ApiKey = "sk-test",
            BaseUrl = "https://example.test/v1",
            Model = "m",
        };
        Assert.True(svc.Configured);
        Assert.True(svc.Enabled);
    }
}
