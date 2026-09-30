using System.Text.Json.Serialization;

namespace ImgHub.Core.Models;

/// <summary>
/// 设置（等价 Python 的 config.json）。
///
/// ⚠️ 关键：原 Python 版的 config.json 用的是 snake_case 字段名
/// （batch_n / output_format / total_cost / polish_base_url …），
/// 而 C# 属性是 PascalCase。若不做映射，**读进来全是默认值**（参数不恢复）。
/// 所以这里显式标注 JsonPropertyName，保持与旧版文件格式互通。
/// </summary>
public sealed class AppConfig
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("quality")]
    /// <summary>默认 auto（对所有 provider/模型都合法，且实测恒落 low 档价格）。</summary>
    public string Quality { get; set; } = "auto";

    /// <summary>是否显示按钮图标（用户需求 #7：可在设置里开关）。</summary>
    [JsonPropertyName("use_icons")]
    public bool UseIcons { get; set; } = true;

    /// <summary>按钮鼠标悬停动画开关（v5.24.0 用户要求），默认开。</summary>
    [JsonPropertyName("use_hover_animation")]
    public bool UseHoverAnimation { get; set; } = true;

    /// <summary>悬停说明（ToolTip）开关（v5.24.0 用户要求），默认开。</summary>
    [JsonPropertyName("use_tooltips")]
    public bool UseToolTips { get; set; } = true;

    /// <summary>
    /// 界面语言（v0.5.40）：<c>zh</c> / <c>en</c> / <c>ja</c>，默认中文。
    ///
    /// ⚠️ 与 legacy Python 的关系：legacy 没有这个字段 —— 那是**新增**能力，
    ///   不是 1:1 契约的一部分。旧 config.json 里没有它 → 读出来是默认 "zh"，
    ///   符合"老配置保持原样（中文）"的预期。
    /// </summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = "zh";

    [JsonPropertyName("aspect")]
    public string Aspect { get; set; } = "1:1";

    [JsonPropertyName("offline")]
    public bool Offline { get; set; }

    /// <summary>旧版键名是 preview（生成后自动问预览）。</summary>
    [JsonPropertyName("preview")]
    public bool AutoPreview { get; set; } = true;

    [JsonPropertyName("resolution")]
    public string Resolution { get; set; } = "1k";

    [JsonPropertyName("output_format")]
    public string OutputFormat { get; set; } = "png";

    // ---------------------------------------------------------------- 文档对齐参数（v5.27.0）
    // 逐项对应官方文档参数名；默认值 = "不改动既有行为"（空/0 表示不发送该字段）。

    /// <summary>背景模式：auto / transparent / opaque（transparent 仅 png/webp）。</summary>
    [JsonPropertyName("background")]
    public string Background { get; set; } = "";

    /// <summary>输出压缩强度 0~100，仅 jpeg/webp 生效（0 = 不传）。</summary>
    [JsonPropertyName("output_compression")]
    public int OutputCompression { get; set; }

    /// <summary>内容审核强度 auto/low（仅 APIMart GPT-Image-2.5；空 = 服务端默认 low）。</summary>
    [JsonPropertyName("moderation")]
    public string Moderation { get; set; } = "";

    /// <summary>确定性种子（OpenRouter seed，部分模型支持）。</summary>
    [JsonPropertyName("seed")]
    public string Seed { get; set; } = "";

    /// <summary>流式部分图（OpenRouter SSE）。</summary>
    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    // ---------------------------------------------------------------- v0.5.31 新增（OpenAI 官方 / 千问）

    /// <summary>OpenAI <c>partial_images</c>：流式时请求的部分图张数（0~3）。</summary>
    [JsonPropertyName("partial_images")]
    public int PartialImages { get; set; } = 2;

    /// <summary>OpenAI <c>input_fidelity</c>（high/low；仅 gpt-image-1 系；空 = 不传）。</summary>
    [JsonPropertyName("input_fidelity")]
    public string InputFidelity { get; set; } = "";

    /// <summary>dall-e-3 <c>style</c>（vivid/natural；空 = 不传）。</summary>
    [JsonPropertyName("style")]
    public string Style { get; set; } = "";

    /// <summary>千问 <c>negative_prompt</c>（反向提示词）。</summary>
    [JsonPropertyName("negative_prompt")]
    public string NegativePrompt { get; set; } = "";

    /// <summary>千问 <c>prompt_extend</c>（提示词智能改写，文档默认 true）。</summary>
    [JsonPropertyName("prompt_extend")]
    public bool PromptExtend { get; set; } = true;

    /// <summary>千问 <c>prompt_extend_mode</c>（""/direct/agent；agent 仅 T2I 支持）。</summary>
    [JsonPropertyName("prompt_extend_mode")]
    public string PromptExtendMode { get; set; } = "";

    /// <summary>千问 <c>watermark</c>（是否加水印，文档默认 false）。</summary>
    [JsonPropertyName("watermark")]
    public bool Watermark { get; set; }

    // ---------------------------------------------------------------- v0.5.31 新增（即梦 / 火山引擎）

    /// <summary>即梦 <c>scale</c>：文本描述影响程度（0~1，默认 0.5）。</summary>
    [JsonPropertyName("jimeng_scale")]
    public double JimengScale { get; set; } = 0.5;

    /// <summary>即梦 <c>force_single</c>：强制只出 1 张。</summary>
    [JsonPropertyName("jimeng_force_single")]
    public bool JimengForceSingle { get; set; }

    /// <summary>即梦元素提取的 <c>lora_weight</c>（默认 1.0）。</summary>
    [JsonPropertyName("jimeng_lora_weight")]
    public double JimengLoraWeight { get; set; } = 1.0;

    /// <summary>即梦查询是否返回图片链接（<c>req_json.return_url</c>）。</summary>
    [JsonPropertyName("jimeng_return_url")]
    public bool JimengReturnUrl { get; set; } = true;

    /// <summary>即梦是否加明水印（<c>req_json.logo_info.add_logo</c>）。</summary>
    [JsonPropertyName("jimeng_logo_enabled")]
    public bool JimengLogoEnabled { get; set; }

    /// <summary>即梦水印位置。</summary>
    [JsonPropertyName("jimeng_logo_position")]
    public int JimengLogoPosition { get; set; }

    /// <summary>即梦水印语言。</summary>
    [JsonPropertyName("jimeng_logo_language")]
    public int JimengLogoLanguage { get; set; }

    /// <summary>即梦水印透明度（0~1）。</summary>
    [JsonPropertyName("jimeng_logo_opacity")]
    public double JimengLogoOpacity { get; set; } = 1;

    /// <summary>即梦水印文字内容。</summary>
    [JsonPropertyName("jimeng_logo_text")]
    public string JimengLogoText { get; set; } = "";

    /// <summary>OpenRouter provider 路由：只允许这些 slug。</summary>
    [JsonPropertyName("provider_only")]
    public string ProviderOnly { get; set; } = "";

    /// <summary>OpenRouter provider 路由：排除这些 slug。</summary>
    [JsonPropertyName("provider_ignore")]
    public string ProviderIgnore { get; set; } = "";

    /// <summary>OpenRouter provider 路由：优先顺序。</summary>
    [JsonPropertyName("provider_order")]
    public string ProviderOrder { get; set; } = "";

    /// <summary>OpenRouter provider 路由排序：price / throughput / latency。</summary>
    [JsonPropertyName("provider_sort")]
    public string ProviderSort { get; set; } = "";

    /// <summary>主 provider 失败后是否允许回退（null = 不传）。</summary>
    [JsonPropertyName("allow_fallbacks")]
    public bool? AllowFallbacks { get; set; }

    [JsonPropertyName("batch_n")]
    public int BatchN { get; set; } = 1;

    [JsonPropertyName("total_cost")]
    public double TotalCost { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "openrouter";

    // 润色（与生图分开）
    [JsonPropertyName("polish_base_url")]
    public string PolishBaseUrl { get; set; } = "";

    [JsonPropertyName("polish_model")]
    public string PolishModel { get; set; } = "";

    [JsonPropertyName("polish_enabled")]
    public bool PolishEnabled { get; set; } = true;

    /// <summary>
    /// 润色提示词风格：<c>auto</c>（默认，跟随当前生图 provider）/
    /// <c>openai</c> / <c>qwen</c>（手动指定用哪套专用提示词）。
    /// 见 <see cref="PromptGuide"/> 与 <c>PolishService</c>。
    /// </summary>
    [JsonPropertyName("polish_style")]
    public string PolishStyle { get; set; } = "auto";

    // ---------------------------------------------------------------- 按端点记参数（v0.5.31）

    /// <summary>
    /// 「上次使用的参数」快照，键 = <c>provider|model</c>（**按端点 + 模型分别记**）。
    /// 切回某端点/模型时自动复原，避免每次切换都要重设一遍画幅/质量/格式。
    ///
    /// ⚠️ 为什么键要带模型：同一 provider 下的模型能力差异很大
    ///   （如 OpenAI 的 dall-e-3 只有 standard/hd 质量、固定 3 个尺寸），
    ///   只按 provider 记会让"切模型"时复原出该模型不支持的值。
    /// ⚠️ 只存"随模型变化的参数"，不存 prompt/参考图等一次性内容。
    /// </summary>
    [JsonPropertyName("param_presets")]
    public Dictionary<string, ParamPreset> ParamPresets { get; set; } = new();

    /// <summary>
    /// 一个端点+模型下的参数快照。
    /// 字段名刻意用短名（json 体积）但语义与 <see cref="AppConfig"/> 一致。
    /// </summary>
    public sealed class ParamPreset
    {
        [JsonPropertyName("quality")] public string Quality { get; set; } = "";
        [JsonPropertyName("aspect")] public string Aspect { get; set; } = "";
        [JsonPropertyName("resolution")] public string Resolution { get; set; } = "";
        [JsonPropertyName("output_format")] public string OutputFormat { get; set; } = "";
        [JsonPropertyName("n")] public int N { get; set; }
        [JsonPropertyName("background")] public string Background { get; set; } = "";
        [JsonPropertyName("output_compression")] public int OutputCompression { get; set; }
        [JsonPropertyName("moderation")] public string Moderation { get; set; } = "";
        [JsonPropertyName("seed")] public string Seed { get; set; } = "";
        [JsonPropertyName("stream")] public bool Stream { get; set; }
        // v0.5.31 新增（OpenAI 官方 / 千问）
        [JsonPropertyName("partial_images")] public int PartialImages { get; set; }
        [JsonPropertyName("input_fidelity")] public string InputFidelity { get; set; } = "";
        [JsonPropertyName("style")] public string Style { get; set; } = "";
        [JsonPropertyName("negative_prompt")] public string NegativePrompt { get; set; } = "";
        [JsonPropertyName("prompt_extend")] public bool? PromptExtend { get; set; }
        [JsonPropertyName("prompt_extend_mode")] public string PromptExtendMode { get; set; } = "";
        [JsonPropertyName("watermark")] public bool Watermark { get; set; }
    }

    /// <summary>预置快照的键（provider + 模型，模型可能被用户手改 → 统一 trim + 小写）。</summary>
    public static string PresetKey(ApiProvider p, string? model) =>
        $"{p.Key()}|{(model ?? "").Trim().ToLowerInvariant()}";

    // ---------------------------------------------------------------- provider 端点（v0.5.31）

    /// <summary>
    /// 生图端点覆盖（按 provider 分别保存，键 = <c>ApiProviderExtensions.Key()</c>）。
    ///
    /// ⚠️ 为什么必须有（千问 DashScope 的硬需求）：
    ///   百炼的**业务空间专属域名**是 <c>https://{WorkspaceId}.cn-beijing.maas.aliyuncs.com</c>，
    ///   其中 WorkspaceId 属于用户账号，程序不可能预置；而通用域名
    ///   <c>dashscope.aliyuncs.com</c> 虽仍可用，官方明确"建议迁移到专属域名"。
    ///   没有这个字段，用户就只能改代码、或被迫用通用域名。
    /// 空 / 缺键 = 用该 provider 的官方默认端点（见 <see cref="Catalog.BaseUrlDefault"/>）。
    /// </summary>
    [JsonPropertyName("base_urls")]
    public Dictionary<string, string> BaseUrls { get; set; } = new();

    /// <summary>
    /// 兼容旧字段名（单值）。读到就并入 <see cref="BaseUrls"/> 的 "openrouter" 槽位，
    /// 避免早期手改过配置的用户端点丢失。
    /// </summary>
    [JsonPropertyName("base_url_override")]
    public string BaseUrlOverride { get; set; } = "";
}
