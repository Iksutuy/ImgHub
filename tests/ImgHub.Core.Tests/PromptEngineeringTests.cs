using ImgHub.Core;
using ImgHub.Core.Models;
using ImgHub.Core.Services;
using ImgHub.Core.Storage;

namespace ImgHub.Core.Tests;

/// <summary>
/// 提示词工程（v0.5.31 三项需求）的回归测试：
///   ① 按端点各自专门优化一套润色提示词（OpenAI / 千问），生成时用对的那套；
///   ② 两份文档提炼成的提示词工程指南（浮窗里按标签页展示）内容完整且不串端点；
///   ③ 按「端点 + 模型」记录并复原上次使用的配置。
///
/// 为什么这些必须测：
///   ① 用错提示词**不会报错**，只会让千问用户拿到超长提示词（被静默截断）或
///      让 OpenAI 用户拿到千问风格的输出 —— 属于"看起来能用、实际打折"的缺陷；
///   ③ 参数复原写错方向（先收窄再覆盖）会把服务端不支持的值塞回去 → 400，
///      而错误来自服务端，用户看不出是哪一项。
/// </summary>
public sealed class PromptEngineeringTests : IDisposable
{
    private readonly string _home;

    public PromptEngineeringTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "imghub-pe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    // ================================================================ ① 按端点分派润色提示词

    [Fact]
    public void EffectiveStyle_FollowsGenerationProvider_WhenAuto()
    {
        var svc = new PolishService(new ImgHub.Core.Http.HttpJsonClient()) { Style = "auto" };

        svc.ActiveStyle = ApiProvider.DashScope;
        Assert.Equal("qwen", svc.EffectiveStyle);

        svc.ActiveStyle = ApiProvider.OpenAi;
        Assert.Equal("openai", svc.EffectiveStyle);

        // OpenRouter / APIMart 没有自己的 prompting 规范 → 按 OpenAI 那套写
        svc.ActiveStyle = ApiProvider.OpenRouter;
        Assert.Equal("openai", svc.EffectiveStyle);
        svc.ActiveStyle = ApiProvider.Apimart;
        Assert.Equal("openai", svc.EffectiveStyle);
    }

    [Fact]
    public void EffectiveStyle_ManualOverride_WinsOverProvider()
    {
        // 需求："自动选 + 允许手动覆盖"
        var svc = new PolishService(new ImgHub.Core.Http.HttpJsonClient()) { Style = "qwen" };
        svc.ActiveStyle = ApiProvider.OpenAi;      // 生图端点是 OpenAI
        Assert.Equal("qwen", svc.EffectiveStyle);  // 但用户手动指定了千问 → 用千问

        svc.Style = "openai";
        svc.ActiveStyle = ApiProvider.DashScope;
        Assert.Equal("openai", svc.EffectiveStyle);
    }

    [Fact]
    public void EffectiveStyle_UnknownValue_FallsBackToAuto()
    {
        // config.json 被手改坏时不能抛异常，也不能一直用错的风格 → 回退 auto 语义
        var svc = new PolishService(new ImgHub.Core.Http.HttpJsonClient()) { Style = "garbage" };
        svc.ActiveStyle = ApiProvider.DashScope;
        Assert.Equal("qwen", svc.EffectiveStyle);
    }

    [Fact]
    public void PolishSystemPrompt_DiffersByEndpoint_AndCarriesTheirOwnHardRules()
    {
        // 两套提示词必须**真的不同**，且各自带上该端点的关键约束。
        // 用反射取私有静态字段（它们正是发给 LLM 的 system prompt）。
        var qwenGen = GetPrompt("SysQwenGenBatch");
        var openaiGen = GetPrompt("SysOpenAiGenBatch");
        var qwenEdit = GetPrompt("SysQwenEditBatch");
        var openaiEdit = GetPrompt("SysOpenAiEditBatch");

        Assert.NotEqual(openaiGen, qwenGen);
        Assert.NotEqual(openaiEdit, qwenEdit);

        // 千问：必须交代"长度上限会被截断"与"只能一段正文"（协议硬约束）
        Assert.Contains("截断", qwenGen);
        Assert.Contains("800", qwenGen);
        Assert.Contains("negative_prompt", qwenGen);
        Assert.Contains("截断", qwenEdit);
        Assert.Contains("一个 text", qwenEdit);

        // OpenAI：必须带上官方 prompting 的基本功（先定义结果 / 加引号 / 分开写改与保）
        Assert.Contains("先定义结果", openaiGen);
        Assert.Contains("引号", openaiGen);
        Assert.Contains("保持不变", openaiEdit);
    }

    [Fact]
    public void PolishSinglePrompts_AlsoDifferByEndpoint()
    {
        var qwen = GetPrompt("SysQwenSingleGen");
        var openai = GetPrompt("SysOpenAiSingleGen");
        Assert.NotEqual(openai, qwen);
        // 千问单条版同样要交代长度上限与"单段"约束
        Assert.Contains("800", qwen);
        Assert.Contains("negative_prompt", qwen);
        Assert.Contains("Chinese", qwen);
        Assert.Contains("Chinese", openai);
    }

    /// <summary>取 PolishService 的私有静态 system prompt 字段。</summary>
    private static string GetPrompt(string fieldName)
    {
        var f = typeof(PolishService).GetField(fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(f);
        var v = f!.GetValue(null) as string;
        Assert.False(string.IsNullOrWhiteSpace(v), $"{fieldName} 不应为空");
        return v!;
    }

    // ================================================================ ② 提示词工程指南

    [Fact]
    public void Guide_HasOverviewPlusBothEndpoints_WithContent()
    {
        // 需求：浮窗里用标签页手动切换**两个端点**的提示；另加一个**总览**页放在最前。
        Assert.Equal(3, PromptGuide.All.Count);
        Assert.Equal("overview", PromptGuide.All[0].Key);   // 总览在最前
        Assert.Contains(PromptGuide.All, g => g.Key == "openai");
        Assert.Contains(PromptGuide.All, g => g.Key == "qwen");

        foreach (var g in PromptGuide.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(g.Title));
            // Subtitle 应标明来源（可追溯，不是凭记忆写的）
            Assert.Contains("docs/", g.Subtitle);
            Assert.NotEmpty(g.Sections);
            foreach (var s in g.Sections)
            {
                Assert.False(string.IsNullOrWhiteSpace(s.Heading));
                Assert.NotEmpty(s.Items);
                foreach (var it in s.Items)
                    Assert.False(string.IsNullOrWhiteSpace(it.Text));
            }
        }
    }

    [Fact]
    public void OverviewGuide_CoversAllEightDimensions()
    {
        // 需求：总览要**按 8 个维度分类**，并且每一项都要有说明（不是只列词表）
        var overview = PromptGuide.Overview;
        var headings = overview.Sections.Select(s => s.Heading).ToList();

        foreach (var dim in new[] { "主题", "媒介", "环境", "灯光", "颜色", "情绪", "构图" })
            Assert.Contains(headings, h => h.Contains(dim));

        // 第八个维度（风格与约束）与"怎么用"、"检查"三节
        Assert.Contains(headings, h => h.Contains("风格与约束"));
        Assert.Contains(headings, h => h.Contains("怎么用"));
        Assert.Contains(headings, h => h.Contains("检查"));

        // 每个维度都要**扩充说明**（≥3 条），不能只有一行词表
        foreach (var s in overview.Sections.Where(x => x.Heading.Contains("①") ||
                                                       x.Heading.Contains("②") ||
                                                       x.Heading.Contains("③") ||
                                                       x.Heading.Contains("④") ||
                                                       x.Heading.Contains("⑤") ||
                                                       x.Heading.Contains("⑥") ||
                                                       x.Heading.Contains("⑦") ||
                                                       x.Heading.Contains("⑧")))
            Assert.True(s.Items.Count >= 3,
                        $"{s.Heading} 只有 {s.Items.Count} 条，说明太少");

        // 用户给的 8 维度词表要逐项覆盖（含中英对照里的具体例子）
        var all = string.Join("\n", overview.Sections.SelectMany(s => s.Items).Select(i => i.Text));
        foreach (var kw in new[] { "人、动物、角色、地点、物体", "雕塑", "水下", "霓虹",
                                   "单色", "鸟瞰", "挂毯" })
            Assert.Contains(kw, all);
    }

    [Fact]
    public void OverviewGuide_IncludesReferenceImageStyleAdvice()
    {
        // 用户明确要求把「参考图场景的风格词建议」纳入（来自其提供的第三方经验）：
        //   保持简洁 / 有选择地加风格词 / 描述内容而非下指令
        var all = string.Join("\n", PromptGuide.Overview.Sections
            .SelectMany(s => s.Items).Select(i => i.Text + i.Good + i.Bad));

        Assert.Contains("保持文字提示简洁", all);
        Assert.Contains("冲突", all);
        Assert.Contains("有选择地", all);
        Assert.Contains("描述内容", all);
    }

    [Fact]
    public void Guide_For_SelectsEndpointPage()
    {
        Assert.Equal("qwen", PromptGuide.For(ApiProvider.DashScope).Key);
        // OpenRouter / APIMart 没有自己的 prompting 规范 → 归到 OpenAI 那页
        Assert.Equal("openai", PromptGuide.For(ApiProvider.OpenAi).Key);
        Assert.Equal("openai", PromptGuide.For(ApiProvider.OpenRouter).Key);
        Assert.Equal("openai", PromptGuide.For(ApiProvider.Apimart).Key);
    }

    [Fact]
    public void Guide_ContentsMatchTheirOwnDocuments_NotSwapped()
    {
        // ⚠️ 最怕的错是"两页内容串了"（指南讲 OpenAI、实际发千问的风格）。
        //    这里用各自文档里**独有**的硬事实做指纹。
        var openai = string.Join("\n", PromptGuide.For(ApiProvider.OpenAi).Sections
            .SelectMany(s => s.Items).Select(i => i.Text));
        var qwen = string.Join("\n", PromptGuide.For(ApiProvider.DashScope).Sections
            .SelectMany(s => s.Items).Select(i => i.Text));

        // OpenAI 页应当出现官方 prompting 的专有说法
        Assert.Contains("photorealistic", openai);
        Assert.Contains("change only X", openai);
        Assert.DoesNotContain("1300 Token", openai);

        // 千问页应当出现千问文档的专有约束
        Assert.Contains("简体中文", qwen);
        Assert.Contains("800 Token", qwen);
        Assert.Contains("自动截断", qwen);
        Assert.DoesNotContain("photorealistic", qwen);

        // 千问页必须提到"多图顺序以最后一张定比例"（协议细节，极易漏）
        Assert.Contains("最后一张", qwen);
    }

    [Fact]
    public void Guide_OpenAiPage_DocumentsTheOfficialEightFundamentals()
    {
        var items = PromptGuide.For(ApiProvider.OpenAi).Sections
            .SelectMany(s => s.Items).Select(i => i.Text).ToList();
        // 官方 "Prompting fundamentals" 的 8 条 → 我们的条目里要能对上关键词
        foreach (var kw in new[] { "定义结果", "格式", "看得见", "人和动作",
                                   "引号", "分开写", "角色", "一次只改一件事" })
            Assert.Contains(items, t => t.Contains(kw));
    }

    // ================================================================ ③ 按端点记配置并复原

    [Fact]
    public void ParamPreset_RoundTrips_ThroughConfigFile()
    {
        // 键 = provider|model（按端点 + 模型分别记）
        var s1 = new Session(_home);
        var key = AppConfig.PresetKey(ApiProvider.OpenAi, "gpt-image-2.5-sunburst");
        Assert.Equal("openai|gpt-image-2.5-sunburst", key);

        s1.Config.ParamPresets[key] = new AppConfig.ParamPreset
        {
            Quality = "xhigh", Aspect = "1536x1024", OutputFormat = "webp",
            N = 3, PartialImages = 3, InputFidelity = "high", Stream = true,
        };
        s1.SaveConfig();

        var s2 = new Session(_home);
        Assert.True(s2.Config.ParamPresets.ContainsKey(key));
        var p = s2.Config.ParamPresets[key];
        Assert.Equal("xhigh", p.Quality);
        Assert.Equal("1536x1024", p.Aspect);
        Assert.Equal(3, p.N);
        Assert.Equal(3, p.PartialImages);
        Assert.Equal("high", p.InputFidelity);
        Assert.True(p.Stream);
    }

    [Fact]
    public void ParamPreset_KeyIsProviderScoped_NotSharedAcrossProviders()
    {
        // 同一模型名出现在两个 provider 下时不能互相覆盖
        var a = AppConfig.PresetKey(ApiProvider.OpenRouter, "openai/gpt-image-2");
        var b = AppConfig.PresetKey(ApiProvider.Apimart, "gpt-image-2");
        Assert.NotEqual(a, b);
        Assert.StartsWith("openrouter|", a);
        Assert.StartsWith("apimart|", b);
    }

    [Fact]
    public void ParamPreset_KeyNormalizesModelCaseAndSpaces()
    {
        // 用户手输模型名时大小写/空格不可控 → 键必须归一化，否则"复原不回来"
        Assert.Equal(AppConfig.PresetKey(ApiProvider.OpenAi, "GPT-Image-2"),
                     AppConfig.PresetKey(ApiProvider.OpenAi, "  gpt-image-2  "));
    }

    [Fact]
    public void QwenPresetFields_SurviveRoundTrip()
    {
        // 千问专属字段（negative_prompt / prompt_extend / watermark）也要能记住
        var s1 = new Session(_home);
        var key = AppConfig.PresetKey(ApiProvider.DashScope, "qwen-image-3.0-pro");
        s1.Config.ParamPresets[key] = new AppConfig.ParamPreset
        {
            NegativePrompt = "低分辨率，扭曲", PromptExtend = false,
            PromptExtendMode = "agent", Watermark = true, N = 4,
        };
        s1.SaveConfig();

        var s2 = new Session(_home);
        var p = s2.Config.ParamPresets[key];
        Assert.Equal("低分辨率，扭曲", p.NegativePrompt);
        Assert.False(p.PromptExtend);
        Assert.Equal("agent", p.PromptExtendMode);
        Assert.True(p.Watermark);
        Assert.Equal(4, p.N);
    }

    [Fact]
    public void PolishStyle_RoundTrips_ThroughConfig()
    {
        var s1 = new Session(_home);
        s1.Config.PolishStyle = "qwen";
        s1.SaveConfig();

        var s2 = new Session(_home);
        Assert.Equal("qwen", s2.Config.PolishStyle);
    }

    [Fact]
    public void Sanitize_SurvivesNullParamPresets()
    {
        // JSON 里显式 null 会让 ParamPresets 变 null → 首次写入必须能自愈（CONSTRAINTS D5/B4）
        File.WriteAllText(Path.Combine(_home, "config.json"),
            "{ \"model\": \"gpt-image-2\", \"provider\": \"openai\", \"param_presets\": null }");
        var s = new Session(_home);
        s.Config.ParamPresets ??= new();
        s.Config.ParamPresets[AppConfig.PresetKey(ApiProvider.OpenAi, "gpt-image-2")] =
            new AppConfig.ParamPreset { Quality = "high" };
        s.SaveConfig();

        var s2 = new Session(_home);
        Assert.Equal("high",
            s2.Config.ParamPresets[AppConfig.PresetKey(ApiProvider.OpenAi, "gpt-image-2")].Quality);
    }

    [Fact]
    public void Sanitize_DropsMismatchedKeysAndEmptyPresets()
    {
        // ⚠️ 真实回归（v0.5.31，用户在 config.json 里肉眼抓到）：
        //    早期版本写出了 ① 错配键（apimart|带斜杠的 OpenRouter 模型名）
        //    ② 空快照（ComboBox 回写 null → aspect=''）。
        //    两者都会让用户觉得「配置没保存住」，所以 Load 时必须自愈清理。
        File.WriteAllText(Path.Combine(_home, "config.json"), """
        {
          "model": "gpt-image-2.5-sunburst",
          "provider": "openai",
          "param_presets": {
            "apimart|openai/gpt-image-2.5-flare": { "quality": "auto", "aspect": "1024x1024" },
            "openai|gpt-image-2.5-sunburst":     { "quality": "", "aspect": "" },
            "openai|gpt-image-2":                { "quality": "medium", "aspect": "1536x1024" },
            "bogus-key-without-bar":             { "quality": "low", "aspect": "1024x1024" }
          }
        }
        """);

        var s = new Session(_home);

        // ① 错配键被清掉
        Assert.False(s.Config.ParamPresets.ContainsKey("apimart|openai/gpt-image-2.5-flare"));
        // ② 空快照被清掉
        Assert.False(s.Config.ParamPresets.ContainsKey("openai|gpt-image-2.5-sunburst"));
        // ③ 键格式非法被清掉
        Assert.False(s.Config.ParamPresets.ContainsKey("bogus-key-without-bar"));
        // ④ 合法且完整的**必须保留**（不能顺手删掉用户的正常数据）
        Assert.True(s.Config.ParamPresets.ContainsKey("openai|gpt-image-2"));
        Assert.Equal("1536x1024", s.Config.ParamPresets["openai|gpt-image-2"].Aspect);

        // ⑤ **必须立即回写磁盘** —— 只清内存的话，用户打开 config.json 仍会看到坏数据，
        //    会以为修复没生效（v0.5.31 实测踩到：日志清了但文件没变）。
        var onDisk = File.ReadAllText(Path.Combine(_home, "config.json"));
        Assert.DoesNotContain("apimart|openai/gpt-image-2.5-flare", onDisk);
        Assert.DoesNotContain("bogus-key-without-bar", onDisk);
        Assert.Contains("openai|gpt-image-2", onDisk);
    }

    [Fact]
    public void Sanitize_CleansInvalidPresetsOnLoad_WithoutExtraSave()
    {
        // 干净启动（无任何后续操作）后，磁盘上的坏数据就应当已经消失
        File.WriteAllText(Path.Combine(_home, "config.json"), """
        { "provider": "openai", "model": "gpt-image-2",
          "param_presets": { "apimart|openai/gpt-image-2.5-flare":
                             { "aspect": "1024x1024", "quality": "auto" } } }
        """);

        _ = new Session(_home);   // 只构造，不做任何写入操作

        var text = File.ReadAllText(Path.Combine(_home, "config.json"));
        Assert.DoesNotContain("apimart|openai/gpt-image-2.5-flare", text);
    }

    [Fact]
    public void Sanitize_KeepsValidPresetsForAllProviders()
    {
        // 自愈清理不能误伤合法数据：四家各放一条合法快照
        foreach (var (provider, _) in Catalog.Providers)
        {
            var s = new Session(_home);
            var model = Catalog.DefaultModel(provider);
            s.Config.ParamPresets[AppConfig.PresetKey(provider, model)] =
                new AppConfig.ParamPreset { Aspect = "1024x1024", Quality = "auto" };
            s.SaveConfig();

            var s2 = new Session(_home);
            Assert.True(s2.Config.ParamPresets.ContainsKey(AppConfig.PresetKey(provider, model)),
                        $"{provider} 的合法快照被误删");
        }
    }
}
