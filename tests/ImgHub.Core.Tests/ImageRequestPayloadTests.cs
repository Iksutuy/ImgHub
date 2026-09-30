using System.Net;
using System.Text.Json;
using ImgHub.Core;
using ImgHub.Core.Http;
using ImgHub.Core.Imaging;
using ImgHub.Core.Models;
using ImgHub.Core.Services;
using SkiaSharp;

namespace ImgHub.Core.Tests;

/// <summary>
/// 生图/编辑/参考图/蒙版的**请求体契约**测试（v5.27.0）。
///
/// 为什么必须测到"请求体"这一层：
///   用户报告的现象是「上传的参考图、编辑历史图，出来的图总是不相关」。
///   这类缺陷不会抛异常、不会失败、UI 也全绿 —— 它只是**没把参数发出去**。
///   所以断言必须落在**实际序列化出来的 payload 字节**上，而不是"调用没报错"。
/// </summary>
public sealed class ImageRequestPayloadTests
{
    // ---------------------------------------------------------------- APIMart
    [Fact]
    public void Apimart_Payload_SendsRefsAsImageUrls_NotInlineBase64()
    {
        // 文档：image_urls 只接受公网 HTTP(S) URL，不接受 base64 data URL
        var req = new GenRequest
        {
            Prompt = "把背景换成雪原", Model = "gpt-image-2.5-flare",
            Quality = "medium", Aspect = "16:9", Resolution = "2k", N = 2,
        };
        var payload = ImageApi.BuildApimartPayload(req,
            new[] { "https://upload.apimart.ai/f/image/a.png",
                    "https://upload.apimart.ai/f/image/b.png" }, null);

        var json = payload.ToJsonLine();
        Assert.Contains("\"image_urls\":[\"https://upload.apimart.ai/f/image/a.png\"", json);
        // 第 1 位必须是主体图（顺序敏感）
        Assert.True(json.IndexOf("a.png", StringComparison.Ordinal)
                  < json.IndexOf("b.png", StringComparison.Ordinal));
        // size 用 APIMart 的字段名（不是 aspect_ratio）
        Assert.Contains("\"size\":\"16:9\"", json);
        Assert.Contains("\"resolution\":\"2k\"", json);
        Assert.DoesNotContain("aspect_ratio", json);
        Assert.Contains("\"n\":2", json);
    }

    [Fact]
    public void Apimart_Payload_SendsMaskUrl_OnlyWithRefs()
    {
        // 文档：mask_url 仅在同时传 image_urls 时生效
        var req = new GenRequest { Prompt = "只改蒙版区域", Model = "gpt-image-2.5-sunburst" };
        var withRefs = ImageApi.BuildApimartPayload(req,
            new[] { "https://x/p.png" }, "https://x/mask.png");
        Assert.Contains("\"mask_url\":\"https://x/mask.png\"", withRefs.ToJsonLine());

        // 没有参考图 → 不应发 mask_url（否则是无效字段）
        var noRefs = ImageApi.BuildApimartPayload(req,
            Array.Empty<string>(), "https://x/mask.png");
        Assert.DoesNotContain("mask_url", noRefs.ToJsonLine());
    }

    [Fact]
    public void Apimart_Payload_SendsNewDocumentedParams()
    {
        // 文档参数：output_format / output_compression / background / moderation
        var req = new GenRequest
        {
            Prompt = "透明背景商品图", Model = "gpt-image-2.5-flare",
            Quality = "high", Aspect = "1:1", OutputFormat = "webp",
            OutputCompression = 80, Background = "transparent", Moderation = "low",
        };
        var json = ImageApi.BuildApimartPayload(req, Array.Empty<string>(), null).ToJsonLine();
        Assert.Contains("\"output_format\":\"webp\"", json);
        Assert.Contains("\"output_compression\":80", json);
        Assert.Contains("\"background\":\"transparent\"", json);
        Assert.Contains("\"moderation\":\"low\"", json);
    }

    [Fact]
    public void Apimart_Payload_ExactPixelSize_OmitsResolution()
    {
        // 文档：size 为精确像素时 resolution 被忽略 → 不再发送（避免歧义）
        var req = new GenRequest
        {
            Prompt = "主视觉", Model = "gpt-image-2.5-flare",
            Aspect = "1600x1200", Resolution = "4k",
        };
        var json = ImageApi.BuildApimartPayload(req, Array.Empty<string>(), null).ToJsonLine();
        Assert.Contains("\"size\":\"1600x1200\"", json);
        Assert.DoesNotContain("resolution", json);
    }

    // ---------------------------------------------------------------- OpenRouter
    [Fact]
    public void OpenRouter_Payload_SendsRefsAsDataUrls()
    {
        // 文档：input_references 支持 base64 data URL
        var req = new GenRequest
        {
            Prompt = "变成水彩画", Model = "openai/gpt-image-2.5-flare",
            Quality = "high", Aspect = "16:9", N = 1,
            Refs = new List<(byte[], string)> { (new byte[] { 1, 2, 3 }, "image/png") },
        };
        var json = ImageApi.BuildOpenRouterPayload(req).ToJsonLine();
        Assert.Contains("\"input_references\":[{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,AQID\"", json);
        Assert.Contains("\"aspect_ratio\":\"16:9\"", json);
    }

    [Fact]
    public void OpenRouter_Payload_UsesUppercaseResolutionTier()
    {
        // 文档：resolution 档名是 512 / 1K / 2K / 4K（不是 APIMart 的小写 1k/2k/4k）
        var req = new GenRequest
        {
            Prompt = "风景", Model = "google/gemini-3-pro-image",
            Aspect = "16:9", Resolution = "2k",
        };
        var json = ImageApi.BuildOpenRouterPayload(req).ToJsonLine();
        Assert.Contains("\"resolution\":\"2K\"", json);
        Assert.DoesNotContain("\"resolution\":\"2k\"", json);
    }

    [Fact]
    public void OpenRouter_Payload_ClampsN_ByModel_NotOnlyByProvider()
    {
        // 回归（v0.5.30）：payload 曾只按 provider 钳 n（OpenRouterMaxN=10），
        // 但 gemini 图像系实测 n.max = 1 → 配置里残留 n>1 会原样发出被服务端拒绝。
        // 现在必须与 UI 侧（Catalog.MaxNFor）用**同一真源**。
        var gemini = ImageApi.BuildOpenRouterPayload(new GenRequest
        {
            Prompt = "x", Model = "google/gemini-3.1-flash-image", N = 4,
        }).ToJsonLine();
        Assert.Contains("\"n\":1", gemini);           // 按模型收窄到 1

        var openai = ImageApi.BuildOpenRouterPayload(new GenRequest
        {
            Prompt = "x", Model = "openai/gpt-image-2.5-flare", N = 4,
        }).ToJsonLine();
        Assert.Contains("\"n\":4", openai);            // openai 系支持 4

        // 超出 provider 上限也要钳（OpenRouter 10）
        var over = ImageApi.BuildOpenRouterPayload(new GenRequest
        {
            Prompt = "x", Model = "openai/gpt-image-2.5-flare", N = 99,
        }).ToJsonLine();
        Assert.Contains("\"n\":10", over);
    }

    [Fact]
    public void Apimart_Payload_ClampsN_ByProviderLimits()
    {
        // APIMart 文档 n 取值 1~4；即使模型名是 gemini（APIMart 侧仍按 4）
        var json = ImageApi.BuildApimartPayload(new GenRequest
        {
            Prompt = "x", Model = "gpt-image-2.5-flare", N = 99,
        }, Array.Empty<string>(), null).ToJsonLine();
        Assert.Contains("\"n\":4", json);
    }

    [Fact]
    public void OpenRouter_Payload_ExplicitPixels_DropsAspectAndResolution()
    {
        // 文档：显式像素是权威值，带 aspect_ratio / resolution 会被 400 拒绝
        var req = new GenRequest
        {
            Prompt = "海报", Model = "openai/gpt-image-1",
            Aspect = "2048x2048", Resolution = "2k",
        };
        var json = ImageApi.BuildOpenRouterPayload(req).ToJsonLine();
        Assert.Contains("\"size\":\"2048x2048\"", json);
        Assert.DoesNotContain("aspect_ratio", json);
        Assert.DoesNotContain("resolution", json);
    }

    [Fact]
    public void OpenRouter_Payload_SendsRoutingSeedStream()
    {
        // 文档：provider.only/order/ignore/sort/allow_fallbacks/options、seed、stream
        var req = new GenRequest
        {
            Prompt = "一只红熊猫", Model = "google/gemini-2.5-flash-image",
            Seed = "42", Stream = true,
            Routing = new ProviderRouting
            {
                Only = new List<string> { "google-ai-studio" },
                Sort = "price",
                AllowFallbacks = false,
                Options = new Dictionary<string, Dictionary<string, string>>
                {
                    ["black-forest-labs"] = new() { ["steps"] = "40", ["guidance"] = "3" },
                },
            },
        };
        var json = ImageApi.BuildOpenRouterPayload(req).ToJsonLine();
        Assert.Contains("\"seed\":42", json);
        Assert.Contains("\"stream\":true", json);
        Assert.Contains("\"only\":[\"google-ai-studio\"]", json);
        Assert.Contains("\"sort\":\"price\"", json);
        Assert.Contains("\"allow_fallbacks\":false", json);
        Assert.Contains("\"steps\":\"40\"", json);
    }

    [Fact]
    public void OpenRouter_Payload_OmitsEmptyRouting()
    {
        var req = new GenRequest
        {
            Prompt = "x", Model = "openai/gpt-image-1",
            Routing = new ProviderRouting(),
        };
        Assert.DoesNotContain("provider", ImageApi.BuildOpenRouterPayload(req).ToJsonLine());
    }

    // ---------------------------------------------------------------- 前置校验
    [Theory]
    [InlineData("transparent", "jpeg")]   // 文档：JPEG 无 Alpha 通道
    [InlineData("transparent", "jpg")]
    public void ValidateBackground_RejectsTransparentWithJpeg(string bg, string fmt)
    {
        Assert.NotNull(Catalog.ValidateBackground(bg, fmt));
    }

    [Theory]
    [InlineData("transparent", "png")]
    [InlineData("transparent", "webp")]
    [InlineData("opaque", "jpeg")]
    [InlineData("auto", "jpeg")]
    [InlineData("", "jpeg")]
    public void ValidateBackground_AcceptsValidCombos(string bg, string fmt)
    {
        Assert.Null(Catalog.ValidateBackground(bg, fmt));
    }

    [Theory]
    [InlineData("1600x1200", null)]      // 16 的倍数，比例/像素都合规
    [InlineData("1024x1024", null)]
    [InlineData("1601x1200", "16 的倍数")]  // 宽不是 16 的倍数
    [InlineData("4096x1024", "3840")]    // 单边超限（先于比例检查被拦）
    [InlineData("3840x1024", "3:1")]     // 单边合法但比例 3.75:1 超过 3:1
    [InlineData("512x512", "总像素")]     // 总像素低于下限
    public void ValidatePixelSize_EnforcesDocumentRules(string size, string? expectFragment)
    {
        var reason = Catalog.ValidatePixelSizeString(size);
        if (expectFragment is null) Assert.Null(reason);
        else Assert.Contains(expectFragment, reason);
    }

    [Fact]
    public void ParsePixelSize_DistinguishesRatioFromPixels()
    {
        Assert.Null(Catalog.ParsePixelSize("16:9"));
        Assert.Null(Catalog.ParsePixelSize("auto"));
        Assert.Equal((1600, 1200), Catalog.ParsePixelSize("1600x1200"));
        Assert.Equal((2048, 2048), Catalog.ParsePixelSize("2048X2048"));
    }

    // ---------------------------------------------------------------- 蒙版校验
    [Fact]
    public void ValidateMask_RequiresRefs_MatchingSize_AndSizeLimit()
    {
        // 没参考图 → 文档说 mask 不生效
        Assert.NotNull(Catalog.ValidateMask(new byte[] { 1 }, (10, 10), (10, 10), hasRefs: false));
        // 尺寸不一致 → APIMart 不做预校验，必须调用方保证 → 我们主动拦
        Assert.Contains("完全一致",
            Catalog.ValidateMask(new byte[] { 1 }, (10, 10), (20, 20), hasRefs: true));
        // 合规
        Assert.Null(Catalog.ValidateMask(new byte[] { 1 }, (10, 10), (10, 10), hasRefs: true));
    }

    [Fact]
    public void PngHasAlpha_DetectsColorType()
    {
        // 造一个最小 PNG 头：签名 + IHDR + 颜色类型
        static byte[] PngHeader(byte colorType)
        {
            var b = new byte[26];
            b[0] = 0x89; b[1] = 0x50; b[2] = 0x4E; b[3] = 0x47;
            b[4] = 0x0D; b[5] = 0x0A; b[6] = 0x1A; b[7] = 0x0A;
            b[8] = 0; b[9] = 0; b[10] = 0; b[11] = 13;
            b[12] = (byte)'I'; b[13] = (byte)'H'; b[14] = (byte)'D'; b[15] = (byte)'R';
            b[25] = colorType;
            return b;
        }
        Assert.True(Catalog.PngHasAlpha(PngHeader(6)));    // RGBA
        Assert.True(Catalog.PngHasAlpha(PngHeader(4)));    // Gray+Alpha
        Assert.False(Catalog.PngHasAlpha(PngHeader(2)));   // RGB（无 Alpha）
        Assert.False(Catalog.PngHasAlpha(PngHeader(0)));   // Gray
        Assert.False(Catalog.PngHasAlpha(new byte[] { 1, 2, 3 }));
    }

    // ---------------------------------------------------------------- 参考图保真
    [Fact]
    public void ReferenceImage_UnderLimit_IsSentByteIdentical()
    {
        // 关键回归：未被压过的参考图必须**原样**进入请求（旧实现无条件压到 1024，
        // 主体/文字细节被削弱 → 用户感知为"参考图不生效"）
        var png = MakePng(300, 200);
        var req = new GenRequest
        {
            Prompt = "保留主体", Model = "openai/gpt-image-2.5-flare",
            Refs = new List<(byte[], string)> { (png, "image/png") },
        };
        var payload = ImageApi.BuildOpenRouterPayload(req);
        var url = payload["input_references"]![0]!["image_url"]!["url"]!.GetValue<string>();
        var b64 = url[(url.IndexOf("base64,", StringComparison.Ordinal) + 7)..];
        Assert.Equal(png, Convert.FromBase64String(b64));
    }

    [Fact]
    public void PrepareRefs_ShrinksOnlyWhenOversized()
    {
        // 小图 → 不压；超大长边 → 压到 RefShrinkMaxSide 以内，且蒙版同步缩放保证尺寸一致
        var small = MakePng(400, 300);
        var reqSmall = new GenRequest
        {
            Prompt = "x", Model = "m",
            Refs = new List<(byte[], string)> { (small, "image/png") },
        };
        ImageApi.PrepareRefsForTest(reqSmall);
        Assert.Equal(small, reqSmall.Refs![0].Data);

        var big = MakePng(3000, 1500);
        var mask = MakeAlphaPng(3000, 1500);
        var reqBig = new GenRequest
        {
            Prompt = "x", Model = "m",
            Refs = new List<(byte[], string)> { (big, "image/png") },
            Mask = mask,
        };
        ImageApi.PrepareRefsForTest(reqBig);
        var refSize = ImageCodec.Size(reqBig.Refs![0].Data);
        var maskSize = ImageCodec.Size(reqBig.Mask!);
        Assert.NotNull(refSize);
        Assert.Equal(refSize, maskSize);                    // 尺寸必须逐像素一致
        Assert.True(Math.Max(refSize!.Value.Width, refSize.Value.Height)
                    <= Catalog.RefShrinkMaxSide);
        Assert.True(Catalog.PngHasAlpha(reqBig.Mask!));     // 缩放后仍带 Alpha
    }

    [Fact]
    public async Task Generate_RejectsMaskWithoutAlpha_BeforeSpending()
    {
        // 文档：普通黑白图不能当蒙版 → 必须在花钱之前拦住
        var handler = new NeverCalledHandler();
        var http = new HttpJsonClient(new System.Net.Http.HttpClient(handler));
        var api = new ImageApi(ApiProvider.Apimart, http, "https://api.test/v1");

        var opaquePng = MakePng(64, 64);   // RGB 无 Alpha
        var ex = await Assert.ThrowsAsync<ApiError>(() => api.GenerateAsync(new GenRequest
        {
            Prompt = "改这里", ApiKey = "sk-test", Model = "gpt-image-2.5-flare",
            Refs = new List<(byte[], string)> { (MakePng(64, 64), "image/png") },
            Mask = opaquePng,
        }));
        Assert.Contains("Alpha", ex.Message);
        Assert.False(handler.Called);      // 关键：没有发起任何请求（未扣费）
    }

    [Fact]
    public async Task Generate_RejectsMaskSizeMismatch_BeforeSpending()
    {
        var handler = new NeverCalledHandler();
        var http = new HttpJsonClient(new System.Net.Http.HttpClient(handler));
        var api = new ImageApi(ApiProvider.Apimart, http, "https://api.test/v1");

        var ex = await Assert.ThrowsAsync<ApiError>(() => api.GenerateAsync(new GenRequest
        {
            Prompt = "改这里", ApiKey = "sk-test", Model = "gpt-image-2.5-flare",
            Refs = new List<(byte[], string)> { (MakePng(128, 128), "image/png") },
            Mask = MakeAlphaPng(64, 64),        // 尺寸不匹配
        }));
        Assert.Contains("完全一致", ex.Message);
        Assert.False(handler.Called);
    }

    // ---------------------------------------------------------------- 辅助
    /// <summary>生成指定尺寸的不透明 PNG（无 Alpha 通道）。</summary>
    private static byte[] MakePng(int w, int h)
    {
        using var bmp = new SKBitmap(w, h, SKColorType.Rgb888x, SKAlphaType.Opaque);
        using (var c = new SKCanvas(bmp)) c.Clear(new SKColor(10, 120, 200));
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    /// <summary>生成指定尺寸的**带 Alpha** PNG（RGBA）。</summary>
    private static byte[] MakeAlphaPng(int w, int h)
    {
        using var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(SKColors.Transparent);
            using var p = new SKPaint { Color = new SKColor(255, 0, 0, 255) };
            c.DrawRect(new SKRect(0, 0, w / 2f, h / 2f), p);
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>断言"没有发出任何 HTTP 请求"—— 用于验证前置校验真的省钱。</summary>
    private sealed class NeverCalledHandler : System.Net.Http.HttpMessageHandler
    {
        public bool Called { get; private set; }
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken ct)
        {
            Called = true;
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8,
                                                           "application/json"),
            });
        }
    }
}
