using System.Text;
using System.Text.Json;
using ImgHub.Core;
using ImgHub.Core.Http;
using ImgHub.Core.Models;
using ImgHub.Core.Services;
using ImgHub.Core.Storage;

namespace ImgHub.Core.Tests;

/// <summary>
/// 即梦（火山引擎）接入的契约测试。
///
/// 为什么这些必须测（沿用 ImageRequestPayloadTests 的教训）：
///   参数发不出去 / 发错字段名**不会抛异常**，只会 400；
///   签名算错只会 403，且服务端**不会告诉你哪一步错了** ——
///   所以必须把"算出来的值"和"文档/规范要求的形态"用断言钉死。
/// </summary>
public sealed class JimengTests
{
    // ================================================================ 火山引擎签名

    [Fact]
    public void VolcSigner_ProducesStableOutput_ForFixedInput()
    {
        // 固定输入 → 固定输出（回归基线：任何改动导致签名形态变化都会在这里暴露）
        var signed = VolcSigner.Sign(
            accessKeyId: "AKLTtest", secretAccessKey: "secret-test",
            region: Catalog.JimengRegion, service: Catalog.JimengService,
            host: "visual.volcengineapi.com",
            method: "POST", path: "/",
            query: new[]
            {
                new KeyValuePair<string, string>("Action", Catalog.JimengSubmitAction),
                new KeyValuePair<string, string>("Version", Catalog.JimengVersion),
            },
            body: "{\"req_key\":\"jimeng_t2i_v40\"}",
            now: new DateTimeOffset(2026, 9, 24, 7, 30, 0, TimeSpan.Zero));

        // X-Date 必须是 UTC 的 yyyyMMdd'T'HHmmss'Z'
        Assert.Equal("20260924T073000Z", signed.XDate);
        // 请求体哈希是小写 hex 的 SHA256（64 字符）
        Assert.Equal(64, signed.ContentSha256.Length);
        Assert.Matches("^[0-9a-f]{64}$", signed.ContentSha256);

        // Authorization 必须含三段：算法 / Credential / SignedHeaders / Signature
        Assert.StartsWith("HMAC-SHA256 Credential=AKLTtest/20260924/cn-north-1/cv/request, ",
                          signed.Authorization);
        Assert.Contains("SignedHeaders=host;x-content-sha256;x-date", signed.Authorization);
        Assert.Matches("Signature=[0-9a-f]{64}$", signed.Authorization);
    }

    [Fact]
    public void VolcSigner_SortsQueryByName_SoParameterOrderDoesNotMatter()
    {
        // ⚠️ CanonicalQuery 必须按名排序 —— 若按传入顺序拼，同样的参数换个顺序
        //    就会算出不同签名（服务端按排序后的算 → 403）。
        var a = VolcSigner.Sign("ak", "sk", "cn-north-1", "cv", "h.example.com",
            "POST", "/",
            new[]
            {
                new KeyValuePair<string, string>("Version", "2022-08-31"),
                new KeyValuePair<string, string>("Action", "X"),
            },
            "{}", DateTimeOffset.UnixEpoch);

        var b = VolcSigner.Sign("ak", "sk", "cn-north-1", "cv", "h.example.com",
            "POST", "/",
            new[]
            {
                new KeyValuePair<string, string>("Action", "X"),
                new KeyValuePair<string, string>("Version", "2022-08-31"),
            },
            "{}", DateTimeOffset.UnixEpoch);

        Assert.Equal(a.Authorization, b.Authorization);
    }

    [Fact]
    public void VolcSigner_ChangesWithBody_Timestamp_AndSecret()
    {
        // 三个"必须影响签名"的因素，各改一个 → 签名必须变
        var baseline = Sign(body: "{}", now: DateTimeOffset.UnixEpoch, sk: "sk1");
        Assert.NotEqual(baseline, Sign(body: "{\"a\":1}", now: DateTimeOffset.UnixEpoch, sk: "sk1"));
        Assert.NotEqual(baseline, Sign(body: "{}", now: DateTimeOffset.UnixEpoch.AddSeconds(1), sk: "sk1"));
        Assert.NotEqual(baseline, Sign(body: "{}", now: DateTimeOffset.UnixEpoch, sk: "sk2"));

        static string Sign(string body, DateTimeOffset now, string sk) =>
            VolcSigner.Sign("ak", sk, "cn-north-1", "cv", "h.example.com",
                "POST", "/", Array.Empty<KeyValuePair<string, string>>(), body, now)
                .Authorization;
    }

    [Fact]
    public void VolcSigner_UsesUtcTime_NotLocalTime()
    {
        // ⚠️ 用本地时间会差几个时区 → 服务端直接判"签名过期"。
        //    给一个带偏移的时间，结果必须与等价的 UTC 时刻完全一致。
        var withOffset = VolcSigner.Sign("ak", "sk", "cn-north-1", "cv", "h",
            "POST", "/", Array.Empty<KeyValuePair<string, string>>(), "{}",
            new DateTimeOffset(2026, 9, 24, 15, 30, 0, TimeSpan.FromHours(8)));   // = 07:30Z

        var utc = VolcSigner.Sign("ak", "sk", "cn-north-1", "cv", "h",
            "POST", "/", Array.Empty<KeyValuePair<string, string>>(), "{}",
            new DateTimeOffset(2026, 9, 24, 7, 30, 0, TimeSpan.Zero));

        Assert.Equal(utc.XDate, withOffset.XDate);
        Assert.Equal(utc.Authorization, withOffset.Authorization);
    }

    [Fact]
    public void JimengConstants_MatchDocuments()
    {
        // 文档明确"本服务固定值：Region 为 cn-north-1，Service 为 cv"
        Assert.Equal("cn-north-1", Catalog.JimengRegion);
        Assert.Equal("cv", Catalog.JimengService);
        Assert.Equal("2022-08-31", Catalog.JimengVersion);
        Assert.Equal("CVSync2AsyncSubmitTask", Catalog.JimengSubmitAction);
        Assert.Equal("CVSync2AsyncGetResult", Catalog.JimengGetResultAction);
        Assert.Equal("https://visual.volcengineapi.com", Catalog.JimengBaseDefault);
    }

    [Fact]
    public void JimengBaseUrl_HasNoPath_SoSigningPathIsRoot()
    {
        // 签名要用 uri.AbsolutePath；即梦是纯 host（无路径），拼出来必须是 "/"
        var uri = new Uri(Catalog.JimengBaseDefault);
        Assert.Equal("/", uri.AbsolutePath);
    }

    // ================================================================ 提取预设（10 种）

    [Fact]
    public void ExtractPresets_CoverAllDocumentedTypes()
    {
        // 文档：商品提取「6选1」、元素提取「四选一」
        Assert.Equal(6, JimengExtract.ProductPresets.Count);
        Assert.Equal(4, JimengExtract.ElementPresets.Count);
        Assert.Equal(10, JimengExtract.All.Count);

        Assert.Equal(new[] { "提取全身衣服", "提取鞋子", "提取包包", "提取沙发", "提取日用品", "提取饰品" },
                     JimengExtract.ProductPresets.Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "提取图案", "提取包装", "提取logo", "提取纹理" },
                     JimengExtract.ElementPresets.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void ExtractPresets_UseDocumentationWording_Verbatim()
    {
        // ⚠️ 必须**逐字**用官方文案 —— 模型是按这些特定措辞约束的。
        //    这里挑几条做指纹（改了措辞会让提取退化）。
        var byName = JimengExtract.All.ToDictionary(p => p.Name, p => p.Prompt);

        Assert.Equal("提取出图片中的衣服、帽子、鞋子和包，生成一张平铺图，背景为纯白色。",
                     byName["提取全身衣服"]);
        Assert.Equal("提取出图片中的一双鞋子，生成一张正45度图，背景为纯白色。",
                     byName["提取鞋子"]);
        Assert.Equal("提取出图片中的完整的包包和包带，正视图，背景为纯白色。",
                     byName["提取包包"]);
        Assert.Equal("提取产品的图案，生成一张平面图展示其图案，去除产品本身。",
                     byName["提取图案"]);
        Assert.Equal("提取产品的纹理，生成一张平面图平铺展示其纹理，去除产品本身。",
                     byName["提取纹理"]);

        // 每条都非空，且都以句号收尾（保持官方句式）
        foreach (var p in JimengExtract.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Prompt));
            Assert.EndsWith("。", p.Prompt);
        }
    }

    [Fact]
    public void ExtractReqKeys_AndFieldNames_MatchDocuments()
    {
        // ⚠️ 两份文档用了**不同**的 req_key 与**不同**的参数名 —— 传错字段 = "缺少必选参数"
        Assert.Equal("jimeng_i2i_extract_tiled_images",
                     JimengExtract.ReqKey(JimengExtract.JimengExtractKind.Product));
        Assert.Equal("edit_prompt",
                     JimengExtract.PromptField(JimengExtract.JimengExtractKind.Product));

        Assert.Equal("i2i_material_extraction",
                     JimengExtract.ReqKey(JimengExtract.JimengExtractKind.Element));
        Assert.Equal("image_edit_prompt",
                     JimengExtract.PromptField(JimengExtract.JimengExtractKind.Element));

        // req_key 反查链路
        Assert.Equal(JimengExtract.JimengExtractKind.Element,
                     JimengExtract.KindOfReqKey("i2i_material_extraction"));
        Assert.Equal(JimengExtract.JimengExtractKind.Product,
                     JimengExtract.KindOfReqKey("jimeng_i2i_extract_tiled_images"));
    }

    [Fact]
    public void OnlyAccessoryPreset_AllowsCustomItemName()
    {
        // 文档原文：「提取饰品时，饰品可以替换为具体的物品，如耳坠、项链等。」
        // → 只有这一条开放替换
        var customizable = JimengExtract.All.Where(p => p.Customizable).ToList();
        Assert.Single(customizable);
        Assert.Equal("提取饰品", customizable[0].Name);

        // 替换后的句式与文档示例一致
        var accessory = customizable[0];
        Assert.Equal("提取出图片中的耳坠，生成一张正视图，背景为纯白色。",
                     JimengExtract.Customize(accessory, "耳坠"));

        // 其它预设：即使传了物件名也不改（避免把官方文案改坏）
        var shoes = JimengExtract.ProductPresets.First(p => p.Name == "提取鞋子");
        Assert.Equal(shoes.Prompt, JimengExtract.Customize(shoes, "运动鞋"));

        // 传空/空白 → 保持原文
        Assert.Equal(accessory.Prompt, JimengExtract.Customize(accessory, "  "));
    }

    // ================================================================ 请求体字段

    [Fact]
    public void GenerationPayload_UsesPixels_WhenAspectIsPixelPair()
    {
        // 文档："面积和宽高同时输入时，优先使用宽高"；宽高必须**同时**传
        var req = new GenRequest
        {
            Prompt = "一只猫", Model = "jimeng_t2i_v40", Aspect = "2048x2048",
        };
        var json = ImageApi.BuildJimengGenerationPayload(req, req.Model).ToJsonLine();

        Assert.Contains("\"req_key\":\"jimeng_t2i_v40\"", json);
        Assert.Contains("\"prompt\":\"一只猫\"", json);
        Assert.Contains("\"width\":2048", json);
        Assert.Contains("\"height\":2048", json);
        Assert.DoesNotContain("\"size\":", json);   // 宽高优先时不传面积
    }

    [Fact]
    public void GenerationPayload_UsesAreaOnly_WhenAspectIsAuto()
    {
        // 只传面积 → 模型按 prompt 意图自判宽高比
        var req = new GenRequest
        {
            Prompt = "一只猫", Model = "jimeng_t2i_v40", Aspect = "auto",
        };
        var json = ImageApi.BuildJimengGenerationPayload(req, req.Model).ToJsonLine();

        Assert.Contains($"\"size\":{Catalog.JimengDefaultPixels}", json);
        Assert.DoesNotContain("\"width\":", json);
        Assert.DoesNotContain("\"height\":", json);
    }

    [Fact]
    public void GenerationPayload_SendsScaleAndForceSingle()
    {
        var req = new GenRequest
        {
            Prompt = "x", Model = "jimeng_t2i_v40", Aspect = "1024x1024",
            JimengScale = 0.75, JimengForceSingle = true,
        };
        var json = ImageApi.BuildJimengGenerationPayload(req, req.Model).ToJsonLine();
        Assert.Contains("\"scale\":0.75", json);
        Assert.Contains("\"force_single\":true", json);

        // 不设 scale → 不传（用服务端默认 0.5）；force_single=false → 不传
        var bare = new GenRequest { Prompt = "x", Model = "jimeng_t2i_v40", Aspect = "1024x1024" };
        var bareJson = ImageApi.BuildJimengGenerationPayload(bare, bare.Model).ToJsonLine();
        Assert.DoesNotContain("scale", bareJson);
        Assert.DoesNotContain("force_single", bareJson);
    }

    [Fact]
    public void GenerationPayload_ClampsScaleToRange()
    {
        // 文档：scale 取值范围 [0, 1]，精度支持小数点后两位
        var req = new GenRequest
        {
            Prompt = "x", Model = "jimeng_t2i_v40", Aspect = "1024x1024", JimengScale = 9.9,
        };
        Assert.Contains("\"scale\":1", ImageApi.BuildJimengGenerationPayload(req, req.Model).ToJsonLine());

        req.JimengScale = -3;
        Assert.Contains("\"scale\":0", ImageApi.BuildJimengGenerationPayload(req, req.Model).ToJsonLine());
    }

    [Fact]
    public void ExtractPayload_UsesTheRightPromptFieldName_PerKind()
    {
        // ⚠️ 商品提取 → edit_prompt；元素提取 → image_edit_prompt
        var product = new GenRequest
        {
            Prompt = "x", Model = "jimeng_i2i_extract_tiled_images",
            Aspect = "2048x2048", JimengExtractPrompt = "提取出图片中的一双鞋子，生成一张正45度图，背景为纯白色。",
        };
        var pj = ImageApi.BuildJimengExtractPayload(product, product.Model).ToJsonLine();
        Assert.Contains("\"edit_prompt\":", pj);
        Assert.DoesNotContain("image_edit_prompt", pj);

        var element = new GenRequest
        {
            Prompt = "x", Model = "i2i_material_extraction",
            Aspect = "2048x2048", JimengExtractPrompt = "提取产品的图案，生成一张平面图展示其图案，去除产品本身。",
        };
        var ej = ImageApi.BuildJimengExtractPayload(element, element.Model).ToJsonLine();
        Assert.Contains("\"image_edit_prompt\":", ej);
        Assert.DoesNotContain("\"edit_prompt\":", ej);
    }

    [Fact]
    public void ExtractPayload_LoraWeight_OnlyForElementKind()
    {
        // 文档：商品提取**没有** lora_weight 参数（只有元素提取有）
        var element = new GenRequest
        {
            Prompt = "x", Model = "i2i_material_extraction", Aspect = "2048x2048",
            JimengLoraWeight = 1.5,
        };
        Assert.Contains("\"lora_weight\":1.5",
                        ImageApi.BuildJimengExtractPayload(element, element.Model).ToJsonLine());

        var product = new GenRequest
        {
            Prompt = "x", Model = "jimeng_i2i_extract_tiled_images", Aspect = "2048x2048",
            JimengLoraWeight = 1.5,
        };
        Assert.DoesNotContain("lora_weight",
                              ImageApi.BuildJimengExtractPayload(product, product.Model).ToJsonLine());
    }

    [Fact]
    public void ExtractPayload_DefaultsTo2048_WhenAspectIsAuto()
    {
        // 文档：width/height 默认 2048、范围 [1024, 4096]
        var req = new GenRequest
        {
            Prompt = "x", Model = "jimeng_i2i_extract_tiled_images", Aspect = "auto",
        };
        var json = ImageApi.BuildJimengExtractPayload(req, req.Model).ToJsonLine();
        Assert.Contains("\"width\":2048", json);
        Assert.Contains("\"height\":2048", json);
    }

    [Fact]
    public void ExtractPayload_FallsBackToPrompt_WhenNoPresetGiven()
    {
        // 没选预设时也要有一条合法指令，而不是空字段被服务端拒
        var req = new GenRequest
        {
            Prompt = "把主体抠出来", Model = "jimeng_i2i_extract_tiled_images", Aspect = "2048x2048",
        };
        Assert.Contains("\"edit_prompt\":\"把主体抠出来\"",
                        ImageApi.BuildJimengExtractPayload(req, req.Model).ToJsonLine());
    }

    [Fact]
    public void QueryPayload_SerializesReqJsonAsString()
    {
        // ⚠️ 文档要求 req_json 是"json 序列化后的**字符串**"，不是嵌套对象！
        var req = new GenRequest
        {
            Model = "jimeng_t2i_v40", JimengReturnUrl = true,
            JimengLogo = new JimengLogoInfo
            {
                AddLogo = true, Position = 0, Language = 0, Opacity = 0.8,
                TextContent = "这里是明水印内容",
            },
        };
        var json = ImageApi.BuildJimengQueryPayload(req, "task-1").ToJsonLine();

        Assert.Contains("\"task_id\":\"task-1\"", json);
        // req_json 的值必须是字符串（带转义引号），而不是对象
        Assert.Contains("\\\"return_url\\\":true", json);
        Assert.Contains("\\\"add_logo\\\":true", json);
        Assert.Contains("这里是明水印内容", json);

        // 反解回来确认是合法 JSON 字符串
        using var doc = JsonDocument.Parse(json);
        var reqJsonText = doc.RootElement.GetProperty("req_json").GetString()!;
        using var inner = JsonDocument.Parse(reqJsonText);
        Assert.True(inner.RootElement.GetProperty("return_url").GetBoolean());
        Assert.True(inner.RootElement.GetProperty("logo_info").GetProperty("add_logo").GetBoolean());
    }

    [Fact]
    public void QueryPayload_OmitsReqJson_WhenNothingToConfigure()
    {
        var req = new GenRequest { Model = "jimeng_t2i_v40", JimengReturnUrl = false };
        Assert.DoesNotContain("req_json", ImageApi.BuildJimengQueryPayload(req, "t").ToJsonLine());
    }

    // ================================================================ 能力矩阵

    [Fact]
    public void JimengModelChoices_AreReqKeys()
    {
        var models = Catalog.ModelChoices(ApiProvider.Jimeng);
        Assert.Equal(3, models.Length);
        Assert.Contains("jimeng_t2i_v40", models);
        Assert.Contains("jimeng_i2i_extract_tiled_images", models);
        Assert.Contains("i2i_material_extraction", models);
        Assert.Contains(Catalog.DefaultModel(ApiProvider.Jimeng), models);
    }

    [Fact]
    public void ExtractReqKeys_AreRecognized()
    {
        Assert.False(Catalog.IsJimengExtractReqKey("jimeng_t2i_v40"));
        Assert.True(Catalog.IsJimengExtractReqKey("jimeng_i2i_extract_tiled_images"));
        Assert.True(Catalog.IsJimengExtractReqKey("i2i_material_extraction"));
        Assert.False(Catalog.IsJimengExtractReqKey(null));
    }

    [Fact]
    public void JimengAspects_UseDocumentedRecommendedSizes()
    {
        // 文档"推荐可选的宽高"：1K/2K/4K 各自的推荐值
        var gen = Catalog.AspectChoicesFor(ApiProvider.Jimeng, "jimeng_t2i_v40");
        foreach (var size in new[] { "1024x1024", "2048x2048", "2304x1728", "2496x1664",
                                     "2560x1440", "3024x1296", "4096x4096", "4694x3520",
                                     "4992x3328", "5404x3040", "6198x2656", "auto" })
            Assert.Contains(size, gen);

        // 提取链路用方形档（文档只给 width/height 范围，无推荐比例表）
        var extract = Catalog.AspectChoicesFor(ApiProvider.Jimeng,
                                               "jimeng_i2i_extract_tiled_images");
        Assert.Equal(Catalog.AspectsJimengExtract, extract);
    }

    [Fact]
    public void JimengValidatesPixelSize_ByAreaAndRatio_NotMultipleOf16()
    {
        // 文档：宽高乘积 [1024*1024, 4096*4096]、宽高比 [1/3, 3] —— **没有** "16 的倍数"要求
        Assert.Null(Catalog.ValidatePixelSizeFor(ApiProvider.Jimeng, "jimeng_t2i_v40", "1024x1024"));
        // 文档推荐的 3024x1296 不是 16 的倍数（3024/16=189 是；用 6198x2656 更能说明问题）
        Assert.Null(Catalog.ValidatePixelSizeFor(ApiProvider.Jimeng, "jimeng_t2i_v40", "6198x2656"));
        // 面积过小
        Assert.NotNull(Catalog.ValidatePixelSizeFor(ApiProvider.Jimeng, "jimeng_t2i_v40", "512x512"));
        // 面积过大
        Assert.NotNull(Catalog.ValidatePixelSizeFor(ApiProvider.Jimeng, "jimeng_t2i_v40", "8192x8192"));
        // 比例超 3:1
        Assert.NotNull(Catalog.ValidatePixelSizeFor(ApiProvider.Jimeng, "jimeng_t2i_v40", "4096x1024"));
    }

    [Fact]
    public void JimengMaxNIsOne_AndHasNoResolutionTier()
    {
        // 即梦没有 n 参数（用 force_single 控制）
        Assert.Equal(1, Catalog.MaxNFor(ApiProvider.Jimeng, "jimeng_t2i_v40"));
        // size 是像素对，没有"分辨率档"概念 → 单元素占位
        Assert.Single(Catalog.ResolutionChoicesFor(ApiProvider.Jimeng, "jimeng_t2i_v40"));
        // 输出格式：文档明确 png
        Assert.Equal(new[] { "png" }, Catalog.OutputFormatChoices(ApiProvider.Jimeng));
        // 没有 quality 参数
        Assert.Equal(new[] { "auto" }, Catalog.QualityChoices(ApiProvider.Jimeng));
    }

    [Fact]
    public void JimengProviderMetadata_RoundTrips()
    {
        Assert.Equal("jimeng", ApiProvider.Jimeng.Key());
        Assert.Equal(ApiProvider.Jimeng, ApiProviderExtensions.Parse("jimeng"));
        // 兼容自然写法
        Assert.Equal(ApiProvider.Jimeng, ApiProviderExtensions.Parse("即梦"));
        Assert.Equal(ApiProvider.Jimeng, ApiProviderExtensions.Parse("volcengine"));
        // 需要 AK/SK 两把
        Assert.True(ApiProvider.Jimeng.NeedsAccessKeyPair());
        Assert.False(ApiProvider.OpenAi.NeedsAccessKeyPair());
        // 五家端点互不相同
        var all = Enum.GetValues<ApiProvider>().Select(Catalog.BaseUrlDefault).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void AllProviders_StillHaveModelListAndDefault()
    {
        // 加了第 5 家后，通用不变量仍成立
        foreach (var (provider, _) in Catalog.Providers)
        {
            var models = Catalog.ModelChoices(provider);
            Assert.NotEmpty(models);
            Assert.Contains(Catalog.DefaultModel(provider), models);
            Assert.True(Catalog.ModelMatchesProvider(Catalog.DefaultModel(provider), provider));
        }
        Assert.Equal(5, Catalog.Providers.Length);
    }

    // ================================================================ AK/SK 存储

    [Fact]
    public void Secret_RoundTrips_ThroughSeparateFile()
    {
        var home = Path.Combine(Path.GetTempPath(), "imghub-jm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var s1 = new Session(home);
            s1.SaveKey("AKLT-access", ApiProvider.Jimeng);
            s1.SaveSecret("secret-xyz", ApiProvider.Jimeng);

            // 两把密钥存在**不同文件**（避免单值读取逻辑被污染）
            Assert.NotEqual(s1.KeyFile(ApiProvider.Jimeng), s1.SecretKeyFile(ApiProvider.Jimeng));
            Assert.Contains("jimeng", s1.KeyFile(ApiProvider.Jimeng));
            Assert.Contains("jimeng", s1.SecretKeyFile(ApiProvider.Jimeng));

            var s2 = new Session(home);
            Assert.Equal("AKLT-access",
                Session.ReadKeyFileCompat(s2.KeyFile(ApiProvider.Jimeng),
                                          s2.LegacyKeyFile(ApiProvider.Jimeng)));
            Assert.Equal("secret-xyz", s2.LoadSecret(ApiProvider.Jimeng));
        }
        finally { try { Directory.Delete(home, true); } catch { } }
    }

    [Fact]
    public void LoadSecret_ReturnsEmpty_WhenMissing_InsteadOfThrowing()
    {
        // 与其他 key 读取同样的容错策略：读不到返回空，绝不抛（否则启动闪退）
        var home = Path.Combine(Path.GetTempPath(), "imghub-jm2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var s = new Session(home);
            Assert.Equal("", s.LoadSecret(ApiProvider.Jimeng));
        }
        finally { try { Directory.Delete(home, true); } catch { } }
    }
}
