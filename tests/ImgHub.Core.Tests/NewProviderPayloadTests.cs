using System.Text.Json;
using ImgHub.Core;
using ImgHub.Core.Http;
using ImgHub.Core.Models;
using ImgHub.Core.Services;

namespace ImgHub.Core.Tests;

/// <summary>
/// 新增 provider（OpenAI 官方直连 / 千问 DashScope 原生协议）的请求体与能力矩阵契约测试。
///
/// 为什么必须测到"序列化后的字节"这一层（沿用 ImageRequestPayloadTests 的教训）：
///   参数发不出去不会抛异常、UI 也全绿 —— 只是"功能看起来在、实际没生效"。
///   对这两家尤其危险，因为它们的参数名/取值域与既有 provider 差异极大：
///     · OpenAI：size 是像素串（没有 aspect_ratio）、response_format 已废弃、
///       GPT Image 2/2.5 不收 background=transparent、input_fidelity 只给 gpt-image-1；
///     · 千问：协议是 input.messages[].content，3.0 走 multimodal（同步）、
///       2.0/max/plus 走 text2image（异步），size 在 text2image 里用 "宽*高"（星号）。
///   任何一处写错都是 400，且错误来自服务端，用户看不出是哪一项。
/// </summary>
public sealed class NewProviderPayloadTests
{
    // ================================================================ OpenAI 官方

    [Fact]
    public void OpenAi_Generation_SendsPixelSize_NotAspectRatio()
    {
        // 文档：size 就是 WIDTHxHEIGHT（或 auto）；**没有** aspect_ratio / resolution 参数
        var req = new GenRequest
        {
            Prompt = "一只水獭", Model = "gpt-image-2.5-sunburst",
            Aspect = "1536x1024", N = 1, Quality = "high",
        };
        var json = ImageApi.BuildOpenAiGenerationPayload(req).ToJsonLine();

        Assert.Contains("\"size\":\"1536x1024\"", json);
        Assert.Contains("\"quality\":\"high\"", json);
        Assert.DoesNotContain("aspect_ratio", json);
        Assert.DoesNotContain("resolution", json);
    }

    [Fact]
    public void OpenAi_Generation_DefaultAspect_BecomesAuto()
    {
        // 内部默认画幅是 "1:1"（其他 provider 的比例名），对 OpenAI 无意义 →
        // 必须转成 auto，而不是把 "1:1" 当 size 发出去（会被 400 拒绝）。
        var req = new GenRequest { Prompt = "x", Model = "gpt-image-2", Aspect = "1:1" };
        var json = ImageApi.BuildOpenAiGenerationPayload(req).ToJsonLine();
        Assert.Contains("\"size\":\"auto\"", json);
        Assert.DoesNotContain("1:1", json);
    }

    [Fact]
    public void OpenAi_Generation_GptImage25_UsesXhighAndMaxQuality()
    {
        // 文档："gpt-image-2.5-* 新增 xhigh 与 max 质量档"；早期模型最高只到 high
        var req = new GenRequest
        {
            Prompt = "x", Model = "gpt-image-2.5-sunburst", Quality = "xhigh",
        };
        Assert.Contains("\"quality\":\"xhigh\"",
                        ImageApi.BuildOpenAiGenerationPayload(req).ToJsonLine());

        var old = new GenRequest { Prompt = "x", Model = "gpt-image-1", Quality = "xhigh" };
        // gpt-image-1 不支持 xhigh → 必须被收窄成它支持的档位，而不是原样发出
        Assert.DoesNotContain("\"quality\":\"xhigh\"",
                              ImageApi.BuildOpenAiGenerationPayload(old).ToJsonLine());
    }

    [Fact]
    public void OpenAi_DallE3_UsesStandardOrHd_AndNeverAuto()
    {
        // ⚠️ dall-e-3 的质量值与 GPT Image 系**完全不同**（standard/hd）。
        //    若把默认的 "auto" 发给它会被 400 拒绝 —— 必须有明确的回退。
        var req = new GenRequest { Prompt = "x", Model = "dall-e-3", Quality = "auto" };
        var json = ImageApi.BuildOpenAiGenerationPayload(req).ToJsonLine();
        Assert.DoesNotContain("\"quality\":\"auto\"", json);
        Assert.Contains("\"quality\":\"standard\"", json);

        var hd = new GenRequest { Prompt = "x", Model = "dall-e-3", Quality = "hd" };
        Assert.Contains("\"quality\":\"hd\"",
                        ImageApi.BuildOpenAiGenerationPayload(hd).ToJsonLine());
    }

    [Fact]
    public void OpenAi_DallE3_SupportsStyle_OthersDont()
    {
        // style（vivid/natural）是 dall-e-3 专有；发给 GPT Image 系会被拒
        var d3 = new GenRequest { Prompt = "x", Model = "dall-e-3", Style = "vivid" };
        Assert.Contains("\"style\":\"vivid\"",
                        ImageApi.BuildOpenAiGenerationPayload(d3).ToJsonLine());

        var g2 = new GenRequest { Prompt = "x", Model = "gpt-image-2", Style = "vivid" };
        Assert.DoesNotContain("style", ImageApi.BuildOpenAiGenerationPayload(g2).ToJsonLine());
    }

    [Fact]
    public void OpenAi_DallE3_OmitsOutputFormatAndCompression()
    {
        // dall-e-3 没有 output_format / output_compression（文档只给 response_format）
        var req = new GenRequest
        {
            Prompt = "x", Model = "dall-e-3", OutputFormat = "webp", OutputCompression = 80,
        };
        var json = ImageApi.BuildOpenAiGenerationPayload(req).ToJsonLine();
        Assert.DoesNotContain("output_format", json);
        Assert.DoesNotContain("output_compression", json);
    }

    [Fact]
    public void OpenAi_GptImage2_RejectsTransparentBackground()
    {
        // 文档明确：gpt-image-2 / 2.5 传 background=transparent 会**报错**
        var req = new GenRequest
        {
            Prompt = "x", Model = "gpt-image-2", Background = "transparent",
        };
        Assert.DoesNotContain("background",
                              ImageApi.BuildOpenAiGenerationPayload(req).ToJsonLine());

        // gpt-image-1 支持 → 应当发出
        var ok = new GenRequest
        {
            Prompt = "x", Model = "gpt-image-1", Background = "transparent",
            OutputFormat = "png",
        };
        Assert.Contains("\"background\":\"transparent\"",
                        ImageApi.BuildOpenAiGenerationPayload(ok).ToJsonLine());
    }

    [Fact]
    public void OpenAi_InputFidelity_OnlyForGptImage1()
    {
        // 文档：input_fidelity 仅 gpt-image-1 支持；**gpt-image-2 必须省略**（恒高保真）
        var g1 = new GenRequest
        {
            Prompt = "x", Model = "gpt-image-1", InputFidelity = "high",
        };
        Assert.Contains("\"input_fidelity\":\"high\"",
                        ImageApi.BuildOpenAiGenerationPayload(g1).ToJsonLine());

        var g2 = new GenRequest
        {
            Prompt = "x", Model = "gpt-image-2", InputFidelity = "high",
        };
        Assert.DoesNotContain("input_fidelity",
                              ImageApi.BuildOpenAiGenerationPayload(g2).ToJsonLine());
    }

    [Fact]
    public void OpenAi_ClampsN_DallE3ToSingleImage()
    {
        // 文档：GPT Image 系 n 1~10；dall-e-3 **仅 n=1**
        var req = new GenRequest { Prompt = "x", Model = "dall-e-3", N = 4 };
        Assert.Contains("\"n\":1", ImageApi.BuildOpenAiGenerationPayload(req).ToJsonLine());

        var g2 = new GenRequest { Prompt = "x", Model = "gpt-image-2", N = 4 };
        Assert.Contains("\"n\":4", ImageApi.BuildOpenAiGenerationPayload(g2).ToJsonLine());
    }

    [Fact]
    public void OpenAi_EditFields_FlattenModelPromptAndControls()
    {
        // /images/edits 是 multipart：model/prompt/size/quality 等必须是**文本字段**（与文件同级）
        var req = new GenRequest
        {
            Prompt = "换成红色", Model = "gpt-image-2.5-flare",
            Aspect = "1024x1024", Quality = "medium", OutputFormat = "png",
        };
        var fields = ImageApi.BuildOpenAiEditFields(req);
        var dict = fields.ToDictionary(k => k.Key, v => v.Value);

        Assert.Equal("gpt-image-2.5-flare", dict["model"]);
        Assert.Equal("换成红色", dict["prompt"]);
        Assert.Equal("1024x1024", dict["size"]);
        Assert.Equal("medium", dict["quality"]);
        Assert.Equal("png", dict["output_format"]);
    }

    [Fact]
    public void OpenAi_Parse_ReadsB64AndUsageTokens()
    {
        // GPT Image 系恒返回 data[].b64_json；usage 里给 token 数（可用于成本核算）
        using var doc = JsonDocument.Parse("""
        {
          "created": 1700000000,
          "data": [ { "b64_json": "AQID" } ],
          "usage": { "input_tokens": 10, "output_tokens": 200, "total_tokens": 210 }
        }
        """);
        var res = ImageApi.ParseOpenAiResponse(doc.RootElement, "png");

        Assert.Single(res.Images);
        Assert.Equal(new byte[] { 1, 2, 3 }, res.Images[0].Data);
        Assert.Equal("image/png", res.Images[0].Media);
        Assert.Equal(210, res.Tokens);
    }

    [Fact]
    public void OpenAi_Parse_UrlResult_IsMarkedForDownload()
    {
        // dall-e 系在 response_format=url 时只给 URL → 必须打标记，交给下载环节
        using var doc = JsonDocument.Parse("""
        { "data": [ { "url": "https://example.com/a.png" } ] }
        """);
        var res = ImageApi.ParseOpenAiResponse(doc.RootElement, "png");
        Assert.Equal(ImageApi.UrlMediaMarker, res.Images[0].Media);
    }

    // ================================================================ 千问 DashScope

    [Fact]
    public void DashScope_30_Multimodal_SendsMessagesContent_NotPromptField()
    {
        // 文档（3.0 DashScope 同步）：model / input.messages[0].content = [{text}] / parameters
        var req = new GenRequest
        {
            Prompt = "一只橘猫", Model = "qwen-image-3.0-pro",
            Aspect = "1024x1024", N = 2,
        };
        var json = ImageApi.BuildDashScopeMultimodalPayload(req).ToJsonLine();

        Assert.Contains("\"model\":\"qwen-image-3.0-pro\"", json);
        Assert.Contains("\"role\":\"user\"", json);
        Assert.Contains("\"text\":\"一只橘猫\"", json);
        // ⚠️ 不是扁平 prompt（那是 OpenAI 兼容模式的写法）
        Assert.DoesNotContain("\"prompt\":", json);
        Assert.Contains("\"n\":2", json);
        Assert.Contains("\"size\":\"1024x1024\"", json);
    }

    [Fact]
    public void DashScope_30_RefsGoIntoContent_AsDataUrls_BeforeText()
    {
        // 文档：I2I 的 content 里**先**参考图（按数组顺序定义图像顺序）**后**文本
        var req = new GenRequest
        {
            Prompt = "保留主体换背景", Model = "qwen-image-3.0-pro",
            Refs = new List<(byte[], string)>
            {
                (new byte[] { 1, 2, 3 }, "image/png"),
            },
        };
        var json = ImageApi.BuildDashScopeMultimodalPayload(req).ToJsonLine();

        Assert.Contains("\"image\":\"data:image/png;base64,AQID\"", json);
        int imagePos = json.IndexOf("\"image\":", StringComparison.Ordinal);
        int textPos = json.IndexOf("\"text\":", StringComparison.Ordinal);
        Assert.True(imagePos > 0 && textPos > 0 && imagePos < textPos,
                    "参考图必须排在 text 之前（文档：按数组顺序定义图像顺序）");
    }

    [Fact]
    public void DashScope_30_CapsRefsAtThree()
    {
        // 文档：I2I 支持传入 **1-3 张**图像（多于 3 张会被拒）
        var refs = Enumerable.Range(0, 6)
            .Select(i => (new byte[] { (byte)i }, "image/png"))
            .ToList();
        var req = new GenRequest { Prompt = "x", Model = "qwen-image-3.0", Refs = refs };
        var json = ImageApi.BuildDashScopeMultimodalPayload(req).ToJsonLine();

        int count = json.Split("\"image\":").Length - 1;
        Assert.Equal(ImageApi.DashScopeMaxRefImages, count);
    }

    [Fact]
    public void DashScope_30_AgentMode_DowngradedForImageToImage()
    {
        // 文档：prompt_extend_mode=agent **仅支持 T2I**，I2I 传入会返回 400
        var t2i = new GenRequest
        {
            Prompt = "x", Model = "qwen-image-3.0", PromptExtendMode = "agent",
        };
        Assert.Contains("\"prompt_extend_mode\":\"agent\"",
                        ImageApi.BuildDashScopeMultimodalPayload(t2i).ToJsonLine());

        var i2i = new GenRequest
        {
            Prompt = "x", Model = "qwen-image-3.0", PromptExtendMode = "agent",
            Refs = new List<(byte[], string)> { (new byte[] { 1 }, "image/png") },
        };
        var json = ImageApi.BuildDashScopeMultimodalPayload(i2i).ToJsonLine();
        Assert.Contains("\"prompt_extend_mode\":\"direct\"", json);
        Assert.DoesNotContain("agent", json);
    }

    [Fact]
    public void DashScope_30_SendsNegativePromptAndWatermark()
    {
        var req = new GenRequest
        {
            Prompt = "x", Model = "qwen-image-3.0",
            NegativePrompt = "低分辨率，扭曲", Watermark = true,
        };
        var json = ImageApi.BuildDashScopeMultimodalPayload(req).ToJsonLine();
        Assert.Contains("\"negative_prompt\":\"低分辨率，扭曲\"", json);
        Assert.Contains("\"watermark\":true", json);
    }

    [Fact]
    public void DashScope_Text2Image_UsesStarDelimiter_AndOmitsN()
    {
        // ⚠️ text2image 协议与 multimodal 有两处差异：
        //   ① size 用 "宽*高"（星号），不是 "宽x高"；
        //   ② n 当前固定为 1，**传其他值会报错** → 索性不传。
        var req = new GenRequest
        {
            Prompt = "一只猫", Model = "qwen-image-plus", Aspect = "1664x928", N = 3,
        };
        var json = ImageApi.BuildDashScopeText2ImagePayload(req).ToJsonLine();

        Assert.Contains("\"size\":\"1664*928\"", json);
        Assert.DoesNotContain("1664x928", json);
        Assert.Contains("\"prompt\":\"一只猫\"", json);
        Assert.DoesNotContain("\"n\":", json);
    }

    [Fact]
    public void DashScope_Parse_ReadsImageUrlFromChoices()
    {
        // 文档：output.choices[].message.content[].image（URL，有效期 24 小时）
        using var doc = JsonDocument.Parse("""
        {
          "output": { "choices": [ { "finish_reason": "stop",
            "message": { "role": "assistant",
              "content": [ { "image": "https://dashscope-result.oss-cn.aliyuncs.com/a.png" } ] } } ] },
          "usage": { "image_count": 1, "width": 1024, "height": 1024 },
          "request_id": "abc"
        }
        """);
        var res = ImageApi.ParseDashScopeResponse(doc.RootElement);

        Assert.Single(res.Images);
        Assert.Equal(ImageApi.UrlMediaMarker, res.Images[0].Media);
    }

    [Fact]
    public void DashScope_Parse_ThrowsOnErrorCode()
    {
        // 文档：失败时顶层返回 code + message（HTTP 可能仍是 200）
        using var doc = JsonDocument.Parse("""
        { "code": "InvalidParameter", "message": "n must be 1", "request_id": "x" }
        """);
        var ex = Assert.Throws<ImgHub.Core.Http.ApiError>(
            () => ImageApi.ParseDashScopeResponse(doc.RootElement));
        Assert.Contains("n must be 1", ex.Message);
    }

    // ================================================================ 能力矩阵 / provider 元数据

    [Theory]
    [InlineData(ApiProvider.OpenRouter, "openrouter")]
    [InlineData(ApiProvider.Apimart, "apimart")]
    [InlineData(ApiProvider.OpenAi, "openai")]
    [InlineData(ApiProvider.DashScope, "dashscope")]
    public void ProviderKeys_RoundTrip(ApiProvider p, string key)
    {
        // 稳定键写进 config.json 与历史项 → 必须能原样回读（否则配置/金额分组失配）
        Assert.Equal(key, p.Key());
        Assert.Equal(p, ApiProviderExtensions.Parse(key));
        Assert.Equal(p, ApiProviderExtensions.Parse(key.ToUpperInvariant()));
    }

    [Fact]
    public void AllProviders_HaveModelList_AndDefaultInIt()
    {
        // 每个 provider 都必须有非空模型清单，且默认模型在清单里
        // （否则启动即被 Sanitize 重置成空模型 → 生成必失败）
        foreach (var (provider, _) in Catalog.Providers)
        {
            var models = Catalog.ModelChoices(provider);
            Assert.NotEmpty(models);
            Assert.Contains(Catalog.DefaultModel(provider), models);
        }
    }

    [Fact]
    public void ModelMatchesProvider_AcceptsBareNames_ForBareProviders()
    {
        // ⚠️ 回归：模型匹配曾是"含斜杠 = OpenRouter，否则 APIMart"的二元判断，
        //    对新增的裸名 provider（OpenAI 官方 / 千问）会**恒为假** →
        //    每次启动/切 provider 都把用户手填的模型名重置为默认值。
        Assert.True(Catalog.ModelMatchesProvider("gpt-image-2.5-sunburst", ApiProvider.OpenAi));
        Assert.True(Catalog.ModelMatchesProvider("qwen-image-3.0-pro", ApiProvider.DashScope));
        Assert.False(Catalog.ModelMatchesProvider("openai/gpt-image-2", ApiProvider.OpenAi));
        Assert.False(Catalog.ModelMatchesProvider("gpt-image-2", ApiProvider.OpenRouter));
    }

    [Fact]
    public void QualityChoices_DifferPerProvider_AndMatchDocs()
    {
        // 各家的合法质量档完全不同，任何一家都不能"借"别家的值
        Assert.Contains("xhigh", Catalog.QualityChoices(ApiProvider.OpenAi, "gpt-image-2.5-flare"));
        Assert.DoesNotContain("xhigh", Catalog.QualityChoices(ApiProvider.OpenAi, "gpt-image-1"));
        Assert.Equal(new[] { "standard", "hd" },
                     Catalog.QualityChoices(ApiProvider.OpenAi, "dall-e-3"));
        Assert.Equal(new[] { "auto" }, Catalog.QualityChoices(ApiProvider.DashScope, "qwen-image-3.0"));
        Assert.DoesNotContain("xhigh", Catalog.QualityChoices(ApiProvider.OpenRouter, "openai/gpt-image-2"));
    }

    [Fact]
    public void MaxNFor_DiffersByModel_ForNewProviders()
    {
        Assert.Equal(10, Catalog.MaxNFor(ApiProvider.OpenAi, "gpt-image-2.5-sunburst"));
        Assert.Equal(1, Catalog.MaxNFor(ApiProvider.OpenAi, "dall-e-3"));
        Assert.Equal(6, Catalog.MaxNFor(ApiProvider.DashScope, "qwen-image-3.0-pro"));
        Assert.Equal(6, Catalog.MaxNFor(ApiProvider.DashScope, "qwen-image-2.0"));
        Assert.Equal(1, Catalog.MaxNFor(ApiProvider.DashScope, "qwen-image-max"));
        Assert.Equal(1, Catalog.MaxNFor(ApiProvider.DashScope, "qwen-image-plus"));
    }

    [Fact]
    public void MaxRefsFor_QwenIsThree_OthersSixteen()
    {
        Assert.Equal(3, Catalog.MaxRefsFor(ApiProvider.DashScope));
        Assert.Equal(16, Catalog.MaxRefsFor(ApiProvider.OpenAi));
        Assert.Equal(16, Catalog.MaxRefsFor(ApiProvider.Apimart));
        Assert.Equal(16, Catalog.MaxRefsFor(ApiProvider.OpenRouter));
    }

    [Fact]
    public void AspectChoices_MatchEachProvidersDocs()
    {
        // OpenAI：像素串（dall-e-3 固定三值且**没有 auto**）
        var d3 = Catalog.AspectChoicesFor(ApiProvider.OpenAi, "dall-e-3");
        Assert.DoesNotContain("auto", d3);
        Assert.Contains("1792x1024", d3);

        var g25 = Catalog.AspectChoicesFor(ApiProvider.OpenAi, "gpt-image-2.5-sunburst");
        Assert.Contains("auto", g25);
        Assert.Contains("1024x1024", g25);

        // 千问：max/plus 系是固定 5 档（不能自由设置）
        var fixedAspects = Catalog.AspectChoicesFor(ApiProvider.DashScope, "qwen-image-plus");
        Assert.Equal(Catalog.AspectsDashScopeFixed.Length, fixedAspects.Length);
        var free25 = Catalog.AspectChoicesFor(ApiProvider.DashScope, "qwen-image-3.0-pro");
        Assert.True(free25.Length > fixedAspects.Length);
    }

    [Fact]
    public void OutputFormatChoices_DallE3AndQwenArePngOnly()
    {
        Assert.Equal(new[] { "png" }, Catalog.OutputFormatChoices(ApiProvider.OpenAi, "dall-e-3"));
        Assert.Equal(new[] { "png" },
                     Catalog.OutputFormatChoices(ApiProvider.DashScope, "qwen-image-3.0"));
        Assert.Contains("webp", Catalog.OutputFormatChoices(ApiProvider.OpenAi, "gpt-image-2"));
    }

    [Fact]
    public void ResolutionChoices_NewProvidersHaveNoTier()
    {
        // OpenAI 官方与千问的 size 就是精确像素，没有"分辨率档"概念 →
        // 下拉必须退化成单值，否则用户会看到一个实际不生效的档位选择
        Assert.Single(Catalog.ResolutionChoicesFor(ApiProvider.OpenAi, "gpt-image-2"));
        Assert.Single(Catalog.ResolutionChoicesFor(ApiProvider.DashScope, "qwen-image-3.0"));
        Assert.True(Catalog.ResolutionChoicesFor(ApiProvider.Apimart, "gpt-image-2.5-flare").Length > 1);
    }

    [Fact]
    public void ValidatePixelSizeFor_EnforcesProviderSpecificPixels()
    {
        // 千问：面积须在 512*512 ~ 2048*2048（不要求 16 的倍数）
        Assert.Null(Catalog.ValidatePixelSizeFor(ApiProvider.DashScope, "qwen-image-3.0", "1024x1024"));
        Assert.NotNull(Catalog.ValidatePixelSizeFor(ApiProvider.DashScope, "qwen-image-3.0", "4096x4096"));

        // OpenAI 官方与 APIMart 共用"16 的倍数 / 单边 ≤3840 / 3:1 / 面积区间"规则
        Assert.Null(Catalog.ValidatePixelSizeFor(ApiProvider.OpenAi, "gpt-image-2", "1536x1024"));
        Assert.NotNull(Catalog.ValidatePixelSizeFor(ApiProvider.OpenAi, "gpt-image-2", "1023x1024"));
    }

    [Fact]
    public void BaseUrlDefaults_AreDistinctPerProvider()
    {
        Assert.Equal("https://api.openai.com/v1", Catalog.BaseUrlDefault(ApiProvider.OpenAi));
        Assert.Equal("https://dashscope.aliyuncs.com", Catalog.BaseUrlDefault(ApiProvider.DashScope));
        // 四家端点互不相同（共用会导致请求打到错误的服务上）
        var all = Enum.GetValues<ApiProvider>().Select(Catalog.BaseUrlDefault).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    // ================================================================ 端到端：实际发出的请求

    /// <summary>
    /// **关键**：前面测的是"payload 结构对不对"，这里测"最终打到哪个 URL、
    /// multipart 的字段名是什么、URL 结果有没有被下载成字节"。
    ///
    /// 为什么非测不可：payload 对了但端点/字段名写错，仍然是"功能看起来在、实际跑不通"，
    /// 而且只会在真实调用时以 400/404 的形式暴露 —— 属于本仓库反复踩过的坑
    /// （参考图/蒙版"发出去了但完全没生效"）。
    /// </summary>
    [Fact]
    public async Task OpenAi_NoRefs_HitsGenerationsEndpoint_WithJsonBody()
    {
        var handler = new RecordingHandler(json: """
        { "data": [ { "b64_json": "AQID" } ], "usage": { "total_tokens": 7 } }
        """);
        var api = new ImageApi(ApiProvider.OpenAi, new HttpJsonClient(new System.Net.Http.HttpClient(handler), 1, 1.0));

        var res = await api.GenerateAsync(new GenRequest
        {
            Prompt = "一只水獭", Model = "gpt-image-2.5-sunburst", ApiKey = "sk-test",
            Quality = "low", Aspect = "1024x1024",
        });

        Assert.EndsWith("/images/generations", handler.LastUri!.AbsolutePath);
        Assert.Contains("api.openai.com", handler.LastUri.Host);
        Assert.Contains("\"model\":\"gpt-image-2.5-sunburst\"", handler.LastBody);
        Assert.Contains("\"size\":\"1024x1024\"", handler.LastBody);
        // 结果必须是**字节**（不是 URL 字符串）
        Assert.Equal(new byte[] { 1, 2, 3 }, res.Images[0].Data);
        Assert.Equal(7, res.Tokens);
    }

    [Fact]
    public async Task OpenAi_WithRefsAndMask_HitsEditsEndpoint_WithMultipartFieldNames()
    {
        // 官方 curl：-F "image[]=@a.png" / -F "mask=@mask.png"
        var handler = new RecordingHandler(json: """
        { "data": [ { "b64_json": "AQID" } ] }
        """);
        var api = new ImageApi(ApiProvider.OpenAi, new HttpJsonClient(new System.Net.Http.HttpClient(handler), 1, 1.0));

        var refImg = MakePng(64, 64, withAlpha: false);
        var mask = MakePng(64, 64, withAlpha: true);

        await api.GenerateAsync(new GenRequest
        {
            Prompt = "换成红色背景", Model = "gpt-image-2.5-flare", ApiKey = "sk-test",
            Aspect = "1024x1024", Quality = "low",
            Refs = new List<(byte[], string)> { (refImg, "image/png") },
            Mask = mask,
        });

        Assert.EndsWith("/images/edits", handler.LastUri!.AbsolutePath);
        Assert.Contains("multipart/form-data", handler.LastContentType);

        var body = handler.LastBody;
        // 文件字段名与文本字段必须都出现。
        // 注意 .NET 的引号规则：含特殊字符的名字（"image[]"）带引号，普通名（mask/model）不带。
        Assert.Contains("name=\"image[]\"", body);
        Assert.Contains("name=mask;", body);
        Assert.Contains("name=model", body);
        Assert.Contains("name=prompt", body);
        Assert.Contains("name=size", body);
    }

    [Fact]
    public async Task DashScope_30_HitsMultimodalEndpoint_AndDownloadsResultUrl()
    {
        // 千问返回的是 URL（有效期 24h）→ Core 必须自己下载成字节再交给上层，
        // 否则上层会把 URL 字符串当图片字节写成一个损坏文件。
        var png = MakePng(8, 8, withAlpha: false);
        var handler = new RecordingHandler(
            json: """
            { "output": { "choices": [ { "finish_reason": "stop",
                "message": { "role": "assistant",
                  "content": [ { "image": "https://oss.example.com/a.png" } ] } } ] },
              "usage": { "image_count": 1 }, "request_id": "x" }
            """,
            binary: png);
        var api = new ImageApi(ApiProvider.DashScope, new HttpJsonClient(new System.Net.Http.HttpClient(handler), 1, 1.0));

        var res = await api.GenerateAsync(new GenRequest
        {
            Prompt = "一只猫", Model = "qwen-image-3.0-pro", ApiKey = "sk-test",
            Aspect = "1024x1024",
        });

        Assert.Contains("/api/v1/services/aigc/multimodal-generation/generation",
                        handler.FirstUri!.AbsolutePath);
        Assert.Equal("dashscope.aliyuncs.com", handler.FirstUri.Host);
        Assert.Contains("\"text\":\"一只猫\"", handler.FirstBody);
        // 结果已被下载成真实图片字节（不是 UTF-8 的 URL 文本）
        Assert.Equal(png, res.Images[0].Data);
    }

    [Fact]
    public async Task DashScope_TextToImage_SendsAsyncHeader()
    {
        // 文档：text2image 端点**必须**带 X-DashScope-Async: enable
        // （缺了会报 "current user api does not support synchronous calls"）
        var png = MakePng(8, 8, withAlpha: false);
        var handler = new RecordingHandler(
            json: """
            { "output": { "task_id": "t-1", "task_status": "PENDING" }, "request_id": "x" }
            """,
            binary: png,
            pollJson: """
            { "output": { "task_status": "SUCCEEDED",
                "choices": [ { "message": { "content": [ { "image": "https://oss.example.com/b.png" } ] } } ] },
              "request_id": "x" }
            """);
        var api = new ImageApi(ApiProvider.DashScope, new HttpJsonClient(new System.Net.Http.HttpClient(handler), 1, 1.0));

        await api.GenerateAsync(new GenRequest
        {
            Prompt = "一只猫", Model = "qwen-image-plus", ApiKey = "sk-test",
            Aspect = "1664x928",
        });

        Assert.Contains("/api/v1/services/aigc/text2image/image-synthesis",
                        handler.FirstUri!.AbsolutePath);
        Assert.True(handler.FirstHeaders.ContainsKey("X-DashScope-Async"),
                    "缺少 X-DashScope-Async: enable 请求头");
        // size 用星号分隔（text2image 协议）
        Assert.Contains("1664*928", handler.FirstBody);
        // 轮询打到 /api/v1/tasks/{id}
        Assert.Contains("/api/v1/tasks/t-1", handler.PollUri!.AbsolutePath);
    }

    // ---------------------------------------------------------------- 测试辅助

    /// <summary>记录请求（URI / body / header / content-type）并按 URL 决定返回什么。</summary>
    private sealed class RecordingHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly string _json;
        private readonly byte[] _binary;
        private readonly string? _pollJson;

        public RecordingHandler(string json, byte[]? binary = null, string? pollJson = null)
        {
            _json = json;
            _binary = binary ?? Array.Empty<byte>();
            _pollJson = pollJson;
        }

        public Uri? FirstUri { get; private set; }
        public string FirstBody { get; private set; } = "";
        public Dictionary<string, string> FirstHeaders { get; } = new();
        public Uri? LastUri { get; private set; }
        public string LastBody { get; private set; } = "";
        public string LastContentType { get; private set; } = "";
        public Uri? PollUri { get; private set; }
        private int _calls;

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            string body = "";
            string contentType = "";
            if (request.Content is { } c)
            {
                contentType = c.Headers.ContentType?.ToString() ?? "";
                body = await c.ReadAsStringAsync(ct);
            }
            _calls++;
            if (_calls == 1)
            {
                FirstUri = uri;
                FirstBody = body;
                foreach (var h in request.Headers)
                    FirstHeaders[h.Key] = string.Join(",", h.Value);
            }
            LastUri = uri;
            LastBody = body;
            LastContentType = contentType;

            // ① 结果图下载（返回图片字节，不是 JSON）
            //    ⚠️ 必须**先**判断它：图片下载同样是 GET，放到任务轮询之后会被误判成 JSON。
            if (uri.Host.Contains("oss.example.com", StringComparison.OrdinalIgnoreCase))
                return Binary(_binary);
            // ② 任务查询（GET /tasks/）→ pollJson
            if (request.Method == System.Net.Http.HttpMethod.Get)
            {
                PollUri = uri;
                return Json(_pollJson ?? _json);
            }
            // ③ 生成/提交请求
            return Json(_json);
        }

        private static System.Net.Http.HttpResponseMessage Json(string s) =>
            new(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(s, System.Text.Encoding.UTF8,
                                                           "application/json"),
            };

        private static System.Net.Http.HttpResponseMessage Binary(byte[] b) =>
            new(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.ByteArrayContent(b),
            };
    }

    /// <summary>造一张 PNG（可选带 Alpha 通道，供蒙版校验用）。</summary>
    private static byte[] MakePng(int w, int h, bool withAlpha)
    {
        var colorType = withAlpha ? SkiaSharp.SKColorType.Rgba8888
                                  : SkiaSharp.SKColorType.Rgb888x;
        var alphaType = withAlpha ? SkiaSharp.SKAlphaType.Unpremul
                                  : SkiaSharp.SKAlphaType.Opaque;
        using var bmp = new SkiaSharp.SKBitmap(w, h, colorType, alphaType);
        using (var c = new SkiaSharp.SKCanvas(bmp))
            c.Clear(withAlpha ? SkiaSharp.SKColors.Transparent
                              : new SkiaSharp.SKColor(20, 140, 90));
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }
}
