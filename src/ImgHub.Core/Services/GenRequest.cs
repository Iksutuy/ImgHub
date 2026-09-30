using ImgHub.Core.Models;

namespace ImgHub.Core.Services;

/// <summary>
/// 生图/编辑请求参数（**唯一真源**）。
///
/// 逐项对应两份官方文档：
///   · APIMart GPT-Image-2.5：docs.apimart.ai/cn/api-reference/images/gpt-image-2.5/generation
///   · OpenRouter Image Generation：openrouter.ai/docs/guides/overview/multimodal/image-generation
///
/// ⚠️ 为什么用参数对象而不是继续加可选参数（v5.27.0 重构）：
///   <see cref="ImageApi.GenerateAsync"/> 原签名已有 12 个位置参数，
///   继续追加 background / output_compression / moderation / seed / provider 路由
///   会变成 20+ 个位置参数 —— 调用方每加一个参数都要改全部调用点，
///   且极易把相邻的 string 参数传错（例如 quality 与 aspect 位置互换，编译期不报错）。
///   改用对象后：新增 API 参数只改这里 + 实现，调用方零改动。
/// </summary>
public sealed class GenRequest
{
    /// <summary>提示词（必填，文档 prompt）。</summary>
    public string Prompt { get; set; } = "";

    /// <summary>API key。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// **第二把密钥**（SecretAccessKey）—— 仅即梦（火山引擎 AK/SK 签名）需要。
    /// 其余 provider 留空即可。
    ///
    /// ⚠️ 为什么不改 <c>ImageApi</c> 构造签名：那会波及工厂、DI 与全部测试；
    ///   而"需要两把密钥"是**请求级别**的信息（跟随当前 provider），放请求对象最自然。
    /// </summary>
    public string ApiSecret { get; set; } = "";

    /// <summary>模型名（APIMart 裸名 / OpenRouter provider/model）。</summary>
    public string Model { get; set; } = "";

    /// <summary>质量档（auto/low/medium/high[/xhigh/max]）。</summary>
    public string Quality { get; set; } = "auto";

    /// <summary>
    /// 画幅：比例名（"16:9"）或**精确像素尺寸**（"1600x1200"）或 "auto"。
    /// 文档：size 支持比例名与精确像素；精确像素时 resolution 被忽略。
    /// </summary>
    public string Aspect { get; set; } = "1:1";

    /// <summary>张数：APIMart 1~4，OpenRouter 1~10（超出各自钳制）。</summary>
    public int N { get; set; } = 1;

    /// <summary>
    /// 参考图（按上传顺序，**第 1 位是主体**）。
    /// 文档：APIMart image_urls（最多 16，需公网 URL）；
    /// OpenRouter input_references（最多 16，URL 或 base64 data URL）。
    /// </summary>
    public IReadOnlyList<(byte[] Data, string Media)>? Refs { get; set; }

    /// <summary>
    /// 局部重绘蒙版（PNG，带 Alpha 通道）。
    /// 文档语义：**alpha=0（完全透明）的区域 = 需要修改**，其余保留；
    /// 尺寸必须与 Refs[0] **完全一致**；仅在同时提供参考图时生效。
    /// 见 <see cref="Catalog.ValidateMask"/>。
    /// </summary>
    public byte[]? Mask { get; set; }

    /// <summary>离线模式（占位图，不发请求）。</summary>
    public bool Offline { get; set; }

    /// <summary>占位图种子扰动（离线时使每张不同）。</summary>
    public int Step { get; set; }

    /// <summary>
    /// 分辨率档位（与比例形式的 size 配合）。内部统一小写 1k/2k/4k，
    /// 发往 OpenRouter 时由 <see cref="Catalog.OpenRouterResolution"/> 映射为 1K/2K/4K。
    /// </summary>
    public string Resolution { get; set; } = "1k";

    /// <summary>输出格式：png / jpeg / webp（OpenRouter 另有 svg）。</summary>
    public string OutputFormat { get; set; } = "png";

    /// <summary>背景模式：auto / transparent / opaque（transparent 仅 png/webp）。</summary>
    public string Background { get; set; } = "";

    /// <summary>输出压缩强度 0~100，仅 jpeg/webp 生效（≤0 表示不传）。</summary>
    public int OutputCompression { get; set; }

    /// <summary>内容审核强度：auto / low（仅 APIMart GPT-Image-2.5）。空 = 用服务端默认 low。</summary>
    public string Moderation { get; set; } = "";

    /// <summary>确定性种子（OpenRouter 文档 seed，部分模型支持）。空 = 不传。</summary>
    public string Seed { get; set; } = "";

    /// <summary>
    /// OpenRouter provider 路由（provider.only / order / ignore / sort / allow_fallbacks / options）。
    /// APIMart 无此概念，会自动忽略。null = 不传。
    /// </summary>
    public ProviderRouting? Routing { get; set; }

    /// <summary>流式部分图（OpenRouter SSE，仅 supports_streaming 的模型）。</summary>
    public bool Stream { get; set; }

    // ---------------------------------------------------------------- v0.5.31 新增（OpenAI 官方 / 千问）

    /// <summary>
    /// 流式时请求的部分图张数（OpenAI <c>partial_images</c>，取值 0~3）。
    /// 仅在 <see cref="Stream"/> 为 true 且 provider = OpenAI 官方时生效。
    /// >0 = 边生成边回传部分图（体验更好），0 = 只在最后给一张。
    /// </summary>
    public int PartialImages { get; set; } = 2;

    /// <summary>
    /// OpenAI 官方的 <c>input_fidelity</c>（high / low）—— 控制对输入图细节（尤其人脸）的保真强度。
    /// 文档：仅 gpt-image-1 支持；**gpt-image-2 必须省略**（该模型恒为高保真、不允许改）。
    /// 空 = 不传（用服务端默认）。
    /// </summary>
    public string InputFidelity { get; set; } = "";

    /// <summary>
    /// dall-e-3 的 <c>style</c>（vivid / natural）。其他模型不认这个参数，空 = 不传。
    /// </summary>
    public string Style { get; set; } = "";

    /// <summary>
    /// 反向提示词（千问 DashScope <c>negative_prompt</c>）。
    /// 其他 provider 无此概念，会自动忽略。
    /// </summary>
    public string NegativePrompt { get; set; } = "";

    /// <summary>
    /// 千问 <c>prompt_extend</c>：是否开启提示词智能改写（文档默认 true，建议开启）。
    /// </summary>
    public bool PromptExtend { get; set; } = true;

    /// <summary>
    /// 千问 <c>prompt_extend_mode</c>：direct（默认，T2I/I2I 都支持）/
    /// agent（仅 T2I，I2I 传它会 400）。空 = 不传（用服务端默认 direct）。
    /// </summary>
    public string PromptExtendMode { get; set; } = "";

    /// <summary>千问 <c>watermark</c>：是否给生成图加水印（文档默认 false）。</summary>
    public bool Watermark { get; set; }

    // ---------------------------------------------------------------- 即梦（火山引擎）

    /// <summary>
    /// 即梦 <c>scale</c>：文本描述影响程度（0~1，默认 0.5）。
    /// 越大 = 文本影响越强、输入图影响越弱。仅生成链路（jimeng_t2i_v40）有。
    /// <c>null</c> = 不传（用服务端默认 0.5）。
    /// </summary>
    public double? JimengScale { get; set; }

    /// <summary>
    /// 即梦 <c>force_single</c>：是否强制只生成 1 张。
    /// 文档：模型默认按 prompt 意图决定出图数量（最多 15 张），
    /// 对延迟/价格敏感时应置 true。
    /// </summary>
    public bool JimengForceSingle { get; set; }

    /// <summary>
    /// 即梦提取链路的<b>指令提示词</b>。
    ///
    /// ⚠️ 为什么单独一个字段而不是拼进 <see cref="Prompt"/>：
    ///   两份提取文档的参数名不同（商品提取叫 <c>edit_prompt</c>、
    ///   元素提取叫 <c>image_edit_prompt</c>），且**必须是文档给定的 10 种预设文案之一**
    ///   （6 种商品 + 4 种元素）。混进普通 prompt 会让"提取"退化成普通编辑。
    ///   见 <see cref="JimengExtract"/>。
    /// </summary>
    public string JimengExtractPrompt { get; set; } = "";

    /// <summary>
    /// 即梦 <c>lora_weight</c>：仅元素提取（<c>i2i_material_extraction</c>）支持，默认 1.0。
    /// <c>null</c> = 不传。
    /// </summary>
    public double? JimengLoraWeight { get; set; }

    /// <summary>
    /// 即梦 <c>req_json</c> 里的 <c>return_url</c>：查询结果是否同时返回图片**链接**。
    /// 文档：链接有效期 24 小时；base64 与链接都给（base64 更稳，链接便于排查）。
    /// </summary>
    public bool JimengReturnUrl { get; set; } = true;

    /// <summary>
    /// 即梦 <c>req_json.logo_info</c>：明水印配置。null = 不加。
    /// </summary>
    public JimengLogoInfo? JimengLogo { get; set; }

    /// <summary>进度回调。</summary>
    public IProgress<string>? Progress { get; set; }

    /// <summary>取消令牌。</summary>
    public CancellationToken Ct { get; set; } = CancellationToken.None;
}

/// <summary>
/// 即梦水印配置（<c>req_json.logo_info</c>）。
/// 来源：docs/即梦AI-图片生成4.md 的 ReqJson 表。
/// </summary>
public sealed class JimengLogoInfo
{
    /// <summary>是否添加水印。</summary>
    public bool AddLogo { get; set; }

    /// <summary>水印位置（文档：0 等整数，含义见官网；默认 0）。</summary>
    public int Position { get; set; }

    /// <summary>水印语言（文档：0 等整数）。</summary>
    public int Language { get; set; }

    /// <summary>水印透明度（0~1）。</summary>
    public double Opacity { get; set; } = 1;

    /// <summary>水印文字内容。</summary>
    public string TextContent { get; set; } = "";
}

/// <summary>
/// OpenRouter provider 路由（image-generation 文档 "Provider Routing" / "Provider-Specific Options"）。
/// </summary>
public sealed class ProviderRouting
{
    /// <summary>只允许这些 provider slug。</summary>
    public List<string>? Only { get; set; }

    /// <summary>按此顺序优先尝试。</summary>
    public List<string>? Order { get; set; }

    /// <summary>排除这些 provider slug。</summary>
    public List<string>? Ignore { get; set; }

    /// <summary>排序：price / throughput / latency。</summary>
    public string? Sort { get; set; }

    /// <summary>主 provider 失败后是否允许回退到其它 provider。null = 不传（用服务端默认）。</summary>
    public bool? AllowFallbacks { get; set; }

    /// <summary>provider 专属透传参数，按 slug 分组（provider.options[slug]）。</summary>
    public Dictionary<string, Dictionary<string, string>>? Options { get; set; }

    /// <summary>是否为空（全 null → 不发 provider 字段）。</summary>
    public bool IsEmpty =>
        (Only is null or { Count: 0 }) && (Order is null or { Count: 0 }) &&
        (Ignore is null or { Count: 0 }) && string.IsNullOrWhiteSpace(Sort) &&
        AllowFallbacks is null && (Options is null or { Count: 0 });
}

/// <summary>
/// 流式部分图回调（OpenRouter SSE：image_generation.partial_image）。
/// </summary>
/// <param name="Index">部分图序号。</param>
/// <param name="PngBytes">部分图字节。</param>
public delegate void PartialImageHandler(int Index, byte[] PngBytes);
