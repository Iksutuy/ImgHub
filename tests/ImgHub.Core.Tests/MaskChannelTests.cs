using ImgHub.Core;
using ImgHub.Core.Models;
using ImgHub.Core.Services;

namespace ImgHub.Core.Tests;

/// <summary>
/// 蒙版通道能力真源的回归（用户问题②：「编辑工具是否真的以蒙版形式传给服务端」）。
///
/// 核查结论（本轮实测）：
///   · **APIMart** → 真的走 <c>mask_url</c>（<c>ImageApi.GenerateApimartAsync</c>：
///     先上传蒙版换公网 URL，随 <c>image_urls</c> 一起发）；
///   · **OpenAI 官方** → 真的走 <c>POST /images/edits</c> 的 multipart <c>mask</c> 字段
///     （<c>ImageApi.GenerateOpenAiEditAsync</c>）；
///   · **OpenRouter / 千问 / 即梦** → 文档无 mask 字段，按设计改用
///     「原图 + 标注合成图 + 提示词说明」表达区域（不是 bug）。
///
/// 这里钉住两件事：① 真源本身；② 真源与 <c>ImageApi</c> 实现**不允许漂移**。
/// </summary>
public class MaskChannelTests
{
    [Fact]
    public void SupportsMaskChannel_MatchesDocumentedImplementations()
    {
        Assert.True(Catalog.SupportsMaskChannel(ApiProvider.Apimart),
            "APIMart 有 mask_url（文档明确）");
        Assert.True(Catalog.SupportsMaskChannel(ApiProvider.OpenAi),
            "OpenAI /images/edits 有 multipart mask 字段（文档明确）");

        Assert.False(Catalog.SupportsMaskChannel(ApiProvider.OpenRouter),
            "OpenRouter 的 Image Generation 文档**没有** mask 字段 —— "
            + "塞进去会被当成'要融合的另一张素材'，属发明行为");
        Assert.False(Catalog.SupportsMaskChannel(ApiProvider.DashScope),
            "千问 DashScope 文档无 mask 字段");
        Assert.False(Catalog.SupportsMaskChannel(ApiProvider.Jimeng),
            "即梦文档无 mask 字段");
    }

    [Fact]
    public void ImageApiSource_ActuallyConsumesMask_ForEachSupportedProvider()
    {
        // ⚠️ 这条是**防漂移**测试：声称支持蒙版的 provider，其实现里必须真的出现 mask 传参。
        //    历史坑：App 层曾只认 APIMart，而提示文案告诉 OpenAI 用户"将发送真正的 Alpha 蒙版"
        //    → 文案与行为不符，蒙版被无谓丢弃。
        var src = ReadCoreSource("Services/ImageApi.cs");

        // APIMart：mask_url
        Assert.Contains("mask_url", src, StringComparison.Ordinal);

        // OpenAI：multipart 的 mask 文件字段
        Assert.Contains("(\"mask\", \"mask.png\"", src, StringComparison.Ordinal);

        // 两个分支都必须真实存在
        Assert.Contains("GenerateApimartAsync", src, StringComparison.Ordinal);
        Assert.Contains("GenerateOpenAiEditAsync", src, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_IsDropped_WhenNoReferenceImage()
    {
        // 文档：mask 必须与 image_urls 同时使用。没有参考图时蒙版无意义 →
        // ImageApi.PrepareRefs 必须显式清掉它（并留日志），而不是原样发出。
        var req = new GenRequest
        {
            Model = "x", Prompt = "p", ApiKey = "k",
            Refs = null,
            Mask = new byte[] { 1, 2, 3 },
        };

        var api = new ImgHub.Core.Services.ImageApi(ApiProvider.OpenRouter,
                                                   new ImgHub.Core.Http.HttpJsonClient());
        _ = api;   // PrepareRefs 是 static，经测试入口调用

        ImgHub.Core.Services.ImageApi.PrepareRefsForTest(req);
        Assert.Null(req.Mask);
    }

    private static string ReadCoreSource(string relative)
    {
        // 从测试输出目录回溯到仓库源码（bin/Debug/net10.0 → 仓库根）
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "src", "ImgHub.Core", relative);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException($"找不到源码：{relative}");
    }
}
