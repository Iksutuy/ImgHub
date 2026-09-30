using ImgHub.Core.Models;

namespace ImgHub.Core;

/// <summary>
/// 常量目录：端点、模型清单、质量档、画幅、成本估算。
/// 逐条对应 Python 的 <c>settings.py</c>（唯一真源）。
/// </summary>
public static class Catalog
{
    // ---------------------------------------------------------------- 端点
    public const string OpenRouterBaseDefault = "https://openrouter.ai/api/v1";
    public const string ApimartBaseDefault = "https://api.apimart.ai/v1";
    /// <summary>OpenAI 官方直连（文档：POST /v1/images/generations 与 /v1/images/edits）。</summary>
    public const string OpenAiBaseDefault = "https://api.openai.com/v1";
    /// <summary>
    /// 阿里云百炼 DashScope 原生协议。
    ///
    /// ⚠️ 两个域名形态：
    ///   · 通用域名 <c>https://dashscope.aliyuncs.com</c>（北京）/ <c>dashscope-intl.aliyuncs.com</c>（新加坡）
    ///     —— **无需 WorkspaceId**，开箱可用，故作为默认值；
    ///   · 业务空间专属域名 <c>https://{WorkspaceId}.cn-beijing.maas.aliyuncs.com</c>
    ///     —— 官方推荐（性能/稳定性更好），由用户在设置里填进 base_url 覆盖。
    /// 跨地域调用会鉴权失败，所以 base_url 必须与 API Key 同地域。
    /// </summary>
    public const string DashScopeBaseDefault = "https://dashscope.aliyuncs.com";

    /// <summary>
    /// 即梦（字节跳动 / 火山引擎视觉智能）。
    ///
    /// ⚠️ 与其它 provider 的**根本差异**：这里不是 REST 资源路径，而是
    /// **"Action + Version 写在 query 上"** 的 RPC 风格接口，且鉴权是
    /// **火山引擎 AK/SK 签名**（Region=cn-north-1、Service=cv），不是 Bearer token。
    /// 见 <c>Services/VolcSigner</c>。
    /// </summary>
    public const string JimengBaseDefault = "https://visual.volcengineapi.com";

    /// <summary>即梦：固定 Region（文档明确"本服务固定值"）。</summary>
    public const string JimengRegion = "cn-north-1";
    /// <summary>即梦：固定 Service（文档明确"本服务固定值"）。</summary>
    public const string JimengService = "cv";
    /// <summary>即梦：API 版本号（文档 Query 参数 Version）。</summary>
    public const string JimengVersion = "2022-08-31";
    /// <summary>即梦：提交任务 Action。</summary>
    public const string JimengSubmitAction = "CVSync2AsyncSubmitTask";
    /// <summary>即梦：查询结果 Action。</summary>
    public const string JimengGetResultAction = "CVSync2AsyncGetResult";

    /// <summary>取某 provider 的官方默认端点（配置里没有覆盖值时用）。</summary>
    public static string BaseUrlDefault(ApiProvider p) => p switch
    {
        ApiProvider.Apimart => ApimartBaseDefault,
        ApiProvider.OpenAi => OpenAiBaseDefault,
        ApiProvider.DashScope => DashScopeBaseDefault,
        ApiProvider.Jimeng => JimengBaseDefault,
        _ => OpenRouterBaseDefault,
    };

    // ---------------------------------------------------------------- 模型清单
    /// <summary>OpenRouter 备选清单（都在账号 provider 白名单内）。</summary>
    public static readonly string[] ModelChoicesOpenRouter =
    {
        "openai/gpt-image-2.5-flare",
        "openai/gpt-image-2.5-sunburst",
        "openai/gpt-image-2",
        "google/gemini-2.5-flash-image",
        "google/gemini-3.1-flash-image",
        "google/gemini-3.1-flash-lite-image",
        "openai/gpt-image-1-mini",
    };

    /// <summary>APIMart 模型清单（裸模型名，端点是 /images/generations）。
    /// 2026-09-18 用真实 key 调 GET /v1/models 实测得到，与官网文档有出入。</summary>
    public static readonly string[] ModelChoicesApimart =
    {
        // OpenAI 系
        "gpt-image-2.5-flare", "gpt-image-2.5-sunburst", "gpt-image-2.5-ext",
        "gpt-image-2", "gpt-image-1.5", "gpt-image-1", "gpt-image-1-mini",
        "chatgpt-image-latest", "dall-e-3",
        // Google 系
        "gemini-3.1-flash-image", "gemini-3.1-flash-lite-image",
        "gemini-3-pro-image-preview", "gemini-2.5-flash-image-preview",
        "imagen-4.0-apimart",
        // 国产
        "seedream-5-0-pro", "seedream-5-0-lite", "seedream-4-5", "seedream-4-0",
        "wan2.7-image", "wan2.7-image-pro",
        "qwen-image-3.0-pro", "qwen-image-3.0",
        "qwen-image-2.0-pro", "qwen-image-2.0",
        "z-image-turbo",
        // 其他
        "flux-2-pro", "flux-2-max", "flux-2-flex",
        "flux-kontext-pro", "flux-kontext-max",
        "grok-imagine-image-2.0", "grok-imagine-image-quality",
    };

    /// <summary>
    /// OpenAI 官方直连的图像模型清单（裸名）。
    ///
    /// 来源：docs/GPT Image generation.md（"Overview" 与 "Earlier GPT Image models"）。
    /// **顺序即推荐度**：2.5 两兄弟置顶（官方 "For new integrations, use one of the
    /// GPT Image 2.5 models"），其后是 gpt-image-2 与更早的 1.x 系。
    /// </summary>
    public static readonly string[] ModelChoicesOpenAi =
    {
        // GPT Image 2.5（当前主力；新增 xhigh/max 质量档、支持任意 WxH）
        "gpt-image-2.5-sunburst", "gpt-image-2.5-flare",
        // GPT Image 2（1K~4K 任意合规分辨率）
        "gpt-image-2",
        "gpt-image-1.5", "gpt-image-1", "gpt-image-1-mini",
        "chatgpt-image-latest",
        // 早期模型（参数域更窄，见 QualityChoices/ModelMaxN）
        "dall-e-3",
    };

    /// <summary>
    /// 千问（DashScope 原生协议）的图像模型清单（裸名）。
    ///
    /// 来源：docs/千问-文生图API参考.md 与 docs/千问-图像生成与编辑3.0 API参考.md。
    /// 3.0 系走 multimodal-generation（文生图/图生图同一接口，支持 1-3 张参考图）；
    /// 2.0/plus/max 系走 text2image（异步，固定 1 张）。
    /// </summary>
    public static readonly string[] ModelChoicesDashScope =
    {
        // 3.0 系（推荐：同时支持 T2I / I2I，参考图 1-3 张，n 1-6）
        "qwen-image-3.0-pro", "qwen-image-3.0",
        // 2.0 系（自由设置宽高，n 1-6）
        "qwen-image-2.0-pro", "qwen-image-2.0",
        // max / plus 系（分辨率档位受限，n 固定 1）
        "qwen-image-max", "qwen-image-plus", "qwen-image",
    };

    public const string DefaultModelOpenRouter = "openai/gpt-image-2.5-flare";
    public const string DefaultModelApimart = "gpt-image-2.5-flare";
    public const string DefaultModelOpenAi = "gpt-image-2.5-sunburst";
    public const string DefaultModelDashScope = "qwen-image-3.0-pro";
    public const string DefaultModelJimeng = "jimeng_t2i_v40";

    /// <summary>
    /// 即梦的"模型"清单 —— 严格说这里列出的是**服务标识（req_key）**：
    /// 火山引擎用 <c>req_key</c> 区分能力，而**不是** model 名。
    ///
    /// 来源（仓库内三份文档）：
    ///   · <c>docs/即梦AI-图片生成4.md</c> → <c>jimeng_t2i_v40</c>（生成 / 编辑 / 多图组合）
    ///   · <c>docs/即梦AI-素材提取(商品提取)-接口文档.md</c> → <c>jimeng_i2i_extract_tiled_images</c>
    ///   · <c>docs/即梦AI-素材提取(提取元素转为平面设计图)-接口文档.md</c> → <c>i2i_material_extraction</c>
    ///
    /// ⚠️ 第三个是"提取"链路（走工具栏「提取」按钮），前两个走「生成」按钮。
    ///   为让 UI 的下拉与能力矩阵统一处理，三者都列进清单，由
    ///   <see cref="IsJimengExtractReqKey"/> 区分行为。
    /// </summary>
    public static readonly string[] ModelChoicesJimeng =
    {
        "jimeng_t2i_v40",                    // 生成（文生图 / 编辑 / 多图组合）
        "jimeng_i2i_extract_tiled_images",   // 提取：商品（衣服/鞋/包/沙发/日用品/饰品）
        "i2i_material_extraction",           // 提取：图案 / 包装 / logo / 纹理
    };

    /// <summary>即梦是否是"提取"类 req_key（走提取链路，而不是生成链路）。</summary>
    public static bool IsJimengExtractReqKey(string? reqKey)
    {
        var k = (reqKey ?? "").Trim();
        return k.Equals("jimeng_i2i_extract_tiled_images", StringComparison.OrdinalIgnoreCase)
            || k.Equals("i2i_material_extraction", StringComparison.OrdinalIgnoreCase);
    }

    public static string[] ModelChoices(ApiProvider p) => p switch
    {
        ApiProvider.Apimart => ModelChoicesApimart,
        ApiProvider.OpenAi => ModelChoicesOpenAi,
        ApiProvider.DashScope => ModelChoicesDashScope,
        ApiProvider.Jimeng => ModelChoicesJimeng,
        _ => ModelChoicesOpenRouter,
    };

    public static string DefaultModel(ApiProvider p) => p switch
    {
        ApiProvider.Apimart => DefaultModelApimart,
        ApiProvider.OpenAi => DefaultModelOpenAi,
        ApiProvider.DashScope => DefaultModelDashScope,
        ApiProvider.Jimeng => DefaultModelJimeng,
        _ => DefaultModelOpenRouter,
    };

    /// <summary>
    /// 各模型「单次生成张数」上限（`n` 的 max）。
    ///
    /// ⚠️ 为什么必须区分模型而不是只看 provider（2026-09-22 实测
    /// `GET /api/v1/images/models` 的 `supported_parameters.n.max`）：
    ///   · `openai/*`、`bytedance*`：n 上限 10
    ///   · **所有 gemini 图像模型：n 上限 1**（传 n&gt;1 会被服务端拒绝）
    ///   若 UI 一律给 1~4，用户在 gemini 上拉到 2 就会失败，且错误来自服务端、
    ///   用户完全不知道为什么。
    /// APIMart 侧按其文档统一为 1~4。
    /// </summary>
    private static readonly Dictionary<string, int> ModelMaxN = new(StringComparer.OrdinalIgnoreCase)
    {
        // OpenRouter（实测 supported_parameters.n.max）
        ["google/gemini-2.5-flash-image"] = 1,
        ["google/gemini-3.1-flash-image"] = 1,
        ["google/gemini-3.1-flash-lite-image"] = 1,
        ["google/gemini-3-pro-image"] = 1,
        ["google/gemini-3-pro-image-preview"] = 1,
        ["google/gemini-3.1-flash-image-preview"] = 1,
    };

    /// <summary>
    /// 该 provider + 模型下 `n` 的最大值。
    /// 未登记的模型回退到 provider 上限
    /// （APIMart 4 / OpenRouter 10 / OpenAI 10 / DashScope 6）。
    /// </summary>
    public static int MaxNFor(ApiProvider p, string? model)
    {
        if (p == ApiProvider.Apimart) return ApimartMaxN;
        if (p == ApiProvider.OpenAi) return OpenAiMaxNFor(model);
        if (p == ApiProvider.DashScope) return DashScopeMaxNFor(model);
        // 即梦没有 n 参数（出图张数由模型按 prompt 决定，或用 force_single 强制 1 张）
        if (p == ApiProvider.Jimeng) return JimengMaxN;
        if (!string.IsNullOrEmpty(model) &&
            ModelMaxN.TryGetValue(model, out var max)) return Math.Max(1, max);
        // gemini 系兜底：命名里含 gemini 的图像模型一律按 1 处理（宁可保守）
        if (!string.IsNullOrEmpty(model) &&
            model.Contains("gemini", StringComparison.OrdinalIgnoreCase)) return 1;
        return OpenRouterMaxN;
    }

    /// <summary>
    /// OpenAI 官方的 n 上限。
    /// 文档：GPT Image 系 1~10；**dall-e-3 仅支持 n=1**（传 2 会被拒绝）。
    /// </summary>
    private static int OpenAiMaxNFor(string? model)
    {
        if (string.Equals(model, "dall-e-3", StringComparison.OrdinalIgnoreCase)) return 1;
        return OpenAiMaxN;
    }

    /// <summary>
    /// 千问 DashScope 的 n 上限。
    /// 文档：3.0 系与 2.0 系 1~6；**max / plus / qwen-image 固定 1**（传其他值报错）。
    /// </summary>
    private static int DashScopeMaxNFor(string? model)
    {
        var m = (model ?? "").Trim().ToLowerInvariant();
        if (m.StartsWith("qwen-image-3.0", StringComparison.Ordinal) ||
            m.StartsWith("qwen-image-2.0", StringComparison.Ordinal))
            return DashScopeMultiNMax;    // 6
        return 1;                          // qwen-image-max / plus / qwen-image 固定 1 张
    }

    /// <summary>
    /// 各模型支持的可选比例子集（覆盖 ModelsApi 里"未声明 aspect_ratio"的模型）。
    /// 实测来源：GET /api/v1/images/models 的 supported_parameters.aspect_ratio.values。
    /// </summary>
    private static readonly Dictionary<string, string[]> ModelAspects = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openai/gpt-image-2.5-flare"] =
            new[] { "1:1", "3:2", "2:3", "4:3", "3:4", "16:9", "9:16", "21:9", "auto" },
        ["openai/gpt-image-2.5-sunburst"] =
            new[] { "1:1", "3:2", "2:3", "4:3", "3:4", "16:9", "9:16", "21:9", "auto" },
        ["openai/gpt-image-2"] =
            new[] { "1:1", "3:2", "2:3", "4:3", "3:4", "16:9", "9:16", "21:9", "auto" },
        ["openai/gpt-image-1"] = new[] { "1:1", "3:2", "2:3", "auto" },
        ["openai/gpt-image-1-mini"] = new[] { "1:1", "3:2", "2:3", "auto" },
        ["google/gemini-3.1-flash-image"] = new[]
        {
            "1:1", "1:4", "1:8", "2:3", "3:2", "3:4", "4:1", "4:3", "4:5",
            "5:4", "8:1", "9:16", "16:9", "21:9",
        },
        ["google/gemini-3.1-flash-lite-image"] = new[]
        {
            "1:1", "1:4", "1:8", "2:3", "3:2", "3:4", "4:1", "4:3", "4:5",
            "5:4", "8:1", "9:16", "16:9", "21:9",
        },
        ["google/gemini-2.5-flash-image"] = new[]
        {
            "1:1", "2:3", "3:2", "3:4", "4:3", "4:5", "5:4", "9:16", "16:9", "21:9",
        },
        ["google/gemini-3-pro-image"] = new[]
        {
            "1:1", "2:3", "3:2", "3:4", "4:3", "4:5", "5:4", "9:16", "16:9", "21:9",
        },
        ["google/gemini-3-pro-image-preview"] = new[]
        {
            "1:1", "2:3", "3:2", "3:4", "4:3", "4:5", "5:4", "9:16", "16:9", "21:9",
        },
        ["google/gemini-3.1-flash-image-preview"] = new[]
        {
            "1:1", "1:4", "1:8", "2:3", "3:2", "3:4", "4:1", "4:3", "4:5",
            "5:4", "8:1", "9:16", "16:9", "21:9",
        },
    };

    /// <summary>
    /// 该 provider + 模型下可选的画幅列表。
    /// 未登记的模型回退到 provider 的完整列表。
    /// </summary>
    public static string[] AspectChoicesFor(ApiProvider p, string? model)
    {
        switch (p)
        {
            case ApiProvider.Apimart:
                return AspectsApimart;
            case ApiProvider.OpenAi:
                return OpenAiAspectChoices(model);
            case ApiProvider.DashScope:
                return DashScopeAspectChoices(model);
            case ApiProvider.Jimeng:
                return JimengAspectChoices(model);
            default:
                if (!string.IsNullOrEmpty(model) &&
                    ModelAspects.TryGetValue(model, out var list)) return list;
                return AspectsOpenRouter;
        }
    }

    /// <summary>
    /// OpenAI 官方的画幅（size）取值随模型分三套（docs/GPT Image generation.md）：
    ///   · GPT Image 2.5 / 2：推荐 3 档 + 2K/4K 常用值 + auto（且支持任意合规 WxH）
    ///   · GPT Image 1 / 1.5 / 1-mini：仅 3 个标准尺寸 + auto
    ///   · dall-e-3：固定三值，无 auto
    /// ⚠️ 漏了这条分支会让用户在下拉里选到 dall-e-3 不承认的 "auto" → 请求被拒。
    /// </summary>
    private static string[] OpenAiAspectChoices(string? model)
    {
        var m = (model ?? "").Trim().ToLowerInvariant();
        if (m.StartsWith("dall-e", StringComparison.Ordinal)) return AspectsOpenAiDallE3;
        if (m.StartsWith("gpt-image-1", StringComparison.Ordinal)) return AspectsOpenAiStandard;
        return AspectsOpenAi;   // gpt-image-2.5* / gpt-image-2 / chatgpt-image-latest
    }

    /// <summary>千问的画幅取值：max/plus/qwen-image 是固定 5 档，其余（3.0/2.0）可自由设置。</summary>
    private static string[] DashScopeAspectChoices(string? model)
    {
        var m = (model ?? "").Trim().ToLowerInvariant();
        // model 为空（如 AspectChoices(p) 探测）→ 给更宽的集合，避免把自由尺寸误判为不支持
        if (m.Length == 0) return AspectsDashScope;
        if (m.StartsWith("qwen-image-3.0", StringComparison.Ordinal) ||
            m.StartsWith("qwen-image-2.0", StringComparison.Ordinal))
            return AspectsDashScope;
        return AspectsDashScopeFixed;
    }

    /// <summary>即梦的画幅取值：生成链路用文档推荐宽高表；提取链路用方形档。</summary>
    private static string[] JimengAspectChoices(string? model) =>
        IsJimengExtractReqKey(model) ? AspectsJimengExtract : AspectsJimeng;

    /// <summary>
    /// 该 provider + 模型是否允许**任意合规的像素尺寸**（而不是只认清单里那几档）。
    ///
    /// ⚠️ 为什么必须区分（v0.5.31 集成测试抓到）：
    ///   只看"是不是像素串格式"会把 dall-e-3 的 <c>1536x1024</c> 误判为合法 ——
    ///   它是合法**格式**，却不是 dall-e-3 支持的**值**（官方只给 3 个固定尺寸）。
    ///   于是用户的尺寸被原样发出 → 服务端 400，而 UI 上完全看不出哪一项不合规。
    ///
    /// 文档依据：
    ///   · OpenAI：GPT Image 2 / 2.5 接受任意 WxH；GPT Image 1 系只有 3 档；
    ///     dall-e-3 固定 3 值（且无 auto）。
    ///   · 千问：3.0 系与 2.0 系可自由设置宽高；max / plus / qwen-image 是固定 5 档。
    ///   · OpenRouter / APIMart：按比例名体系，像素串由 ValidatePixelSize 把关（本方法不影响）。
    /// </summary>
    public static bool AllowsArbitraryPixels(ApiProvider p, string? model)
    {
        var m = (model ?? "").Trim().ToLowerInvariant();
        switch (p)
        {
            case ApiProvider.OpenAi:
                // 只有 GPT Image 2 / 2.5 支持任意分辨率
                return m.StartsWith("gpt-image-2", StringComparison.Ordinal);
            case ApiProvider.DashScope:
                // 3.0 / 2.0 可自由设置；max / plus / qwen-image 固定档
                return m.StartsWith("qwen-image-3.0", StringComparison.Ordinal)
                    || m.StartsWith("qwen-image-2.0", StringComparison.Ordinal);
            case ApiProvider.Jimeng:
                // 文档：宽高乘积 [1024*1024, 4096*4096]、宽高比 [1/3, 3] 内**均可任意设置**
                //（没有"16 的倍数"这类对齐要求）→ 放行，具体规则由 ValidatePixelSizeFor 把关
                return true;
            default:
                return true;   // 比例名体系，像素串的合法性交给 ValidatePixelSizeFor
        }
    }

    /// <summary>
    /// 该 provider + 模型下「画幅/尺寸」的合法值集合。UI 下拉用 <see cref="AspectChoicesFor"/>，
    /// 校验用 <see cref="AspectSupported"/>；这里额外给"允许任意像素"的模型放行。
    /// </summary>
    public static bool AspectOrSizeSupported(ApiProvider p, string? aspect, string? model)
    {
        if (string.IsNullOrEmpty(aspect)) return false;
        if (ParsePixelSize(aspect) is not null)
            return AllowsArbitraryPixels(p, model) || AspectChoicesFor(p, model).Contains(aspect);
        return AspectChoicesFor(p, model).Contains(aspect);
    }

    /// <summary>
    /// 该 provider + 模型支持的最高分辨率档（部分模型只支持 1K）。
    /// </summary>
    private static readonly HashSet<string> OnlyOneKModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "google/gemini-3.1-flash-lite-image",   // 实测 resolution.values 仅 ["1K"]
    };

    /// <summary>该 provider + 模型下可选的分辨率列表。</summary>
    public static string[] ResolutionChoicesFor(ApiProvider p, string? model)
    {
        // OpenAI 官方、千问、即梦的 size 就是**精确像素**（没有独立的"分辨率档"概念），
        // 发请求时也不会带 resolution → 这里给单元素列表，让 UI 下拉退化为固定值。
        if (p is ApiProvider.OpenAi or ApiProvider.DashScope or ApiProvider.Jimeng)
            return SingleResolution;
        if (p == ApiProvider.Apimart) return Resolutions;
        // ⚠️ 与小写语义保持一致（见 ResolutionsOpenRouter 的说明）
        if (!string.IsNullOrEmpty(model) && OnlyOneKModels.Contains(model))
            return new[] { "1k" };
        return ResolutionsOpenRouter;
    }

    /// <summary>"该 provider 没有分辨率档"的占位（单元素，仅用于让 UI 不出现空下拉）。</summary>
    public static readonly string[] SingleResolution = { "1k" };

    /// <summary>模型名是否与 provider 命名习惯相符。</summary>
    /// <remarks>
    /// ⚠️ 为什么用"白名单"而不是"含斜杠"判断（v0.5.31 修正）：
    ///   早期写的是 <c>Apimart ? 不含斜杠 : 含斜杠</c>，对**两个** provider 成立。
    ///   但现在有 4 个 provider 且命名习惯分三类：
    ///     · OpenRouter：<c>provider/model</c>（含斜杠）
    ///     · APIMart / OpenAI 官方 / 千问 DashScope：**裸名**（都不含斜杠）
    ///   继续用二元判断会让 OpenAI/千问永远判定为"不匹配"→ 每次切 provider 或启动
    ///   都被 <c>Sanitize()</c> 重置成默认模型，用户手填的模型名静默丢失。
    ///   改为"该 provider 的合法清单里是否有它"最稳，且顺带挡住跨 provider 串模型。
    /// </remarks>
    public static bool ModelMatchesProvider(string? model, ApiProvider p)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var trimmed = model.Trim();
        // 清单内的直接命中（大小写不敏感：用户手输时大小写不可控）
        if (ModelChoices(p).Contains(trimmed, StringComparer.OrdinalIgnoreCase)) return true;
        // OpenRouter 允许清单外的 provider/model 组合（新模型随时上架），按形态放行
        if (p == ApiProvider.OpenRouter) return trimmed.Contains('/');
        return false;
    }

    // ---------------------------------------------------------------- 质量档
    /// <summary>OpenRouter：auto/low/medium/high（传 xhigh/max 会 400）。</summary>
    public static readonly string[] QualitiesOpenRouter = { "auto", "low", "medium", "high" };

    /// <summary>APIMart：多两档，但 xhigh/max 只有 gpt-image-2.5 系支持。</summary>
    public static readonly string[] QualitiesApimart = { "auto", "low", "medium", "high", "xhigh", "max" };

    /// <summary>
    /// OpenAI 官方（docs/GPT Image generation.md "Size and quality options"）：
    ///   · GPT Image 2.5 系：low / medium / high / **xhigh / max** / auto
    ///   · GPT Image 2 与更早：low / medium / high / auto
    ///   · dall-e-3：**hd / standard**（与 GPT Image 系完全不同的一套值）
    /// </summary>
    public static readonly string[] QualitiesOpenAiGpt = { "auto", "low", "medium", "high" };
    public static readonly string[] QualitiesOpenAi25 = { "auto", "low", "medium", "high", "xhigh", "max" };
    /// <summary>dall-e-3 的合法质量值（OpenAI 文档：vivid/natural 是 style，质量是 hd/standard）。</summary>
    public static readonly string[] QualitiesOpenAiDallE3 = { "standard", "hd" };

    /// <summary>
    /// 千问 DashScope（docs/千问-*.md）：**没有 quality 参数**。
    /// 用单元素数组表示"只有这一档"，让 UI 下拉自然退化为固定值、且发请求时可安全省略。
    /// </summary>
    public static readonly string[] QualitiesDashScope = { "auto" };

    /// <summary>即梦：文档**没有 quality 参数**（用 scale/force_single 等替代）→ 单元素占位。</summary>
    public static readonly string[] QualitiesJimeng = { "auto" };

    private static readonly string[]
        ApimartHighQualityModelPrefixes = { "gpt-image-2.5" };

    /// <summary>当前 provider（+ 指定模型）下可用的质量档位。</summary>
    public static string[] QualityChoices(ApiProvider p, string? model = "")
    {
        switch (p)
        {
            case ApiProvider.Apimart:
                if (!string.IsNullOrEmpty(model) &&
                    !ApimartHighQualityModelPrefixes.Any(pre => model.StartsWith(pre, StringComparison.Ordinal)))
                {
                    return QualitiesApimart.Where(q => q is not ("xhigh" or "max")).ToArray();
                }
                return QualitiesApimart;

            case ApiProvider.OpenAi:
                return OpenAiQualityChoices(model);

            case ApiProvider.DashScope:
                return QualitiesDashScope;

            case ApiProvider.Jimeng:
                return QualitiesJimeng;

            default:
                return QualitiesOpenRouter;
        }
    }

    private static string[] OpenAiQualityChoices(string? model)
    {
        var m = (model ?? "").Trim().ToLowerInvariant();
        if (m.StartsWith("dall-e", StringComparison.Ordinal)) return QualitiesOpenAiDallE3;
        if (m.StartsWith("gpt-image-2.5", StringComparison.Ordinal)) return QualitiesOpenAi25;
        return QualitiesOpenAiGpt;
    }

    public static bool QualitySupported(ApiProvider p, string? quality, string? model = "") =>
        !string.IsNullOrEmpty(quality) && QualityChoices(p, model).Contains(quality);

    // ---------------------------------------------------------------- 画幅/分辨率/格式
    /// <summary>
    /// APIMart GPT-Image-2.5 文档列出的 15 种比例 + auto。
    /// 来源：docs.apimart.ai/cn/api-reference/images/gpt-image-2.5/generation（size 参数）。
    /// </summary>
    public static readonly string[] AspectsApimart =
    {
        "1:1", "3:2", "2:3", "4:3", "3:4", "5:4", "4:5",
        "16:9", "9:16", "2:1", "1:2", "21:9", "9:21", "3:1", "1:3",
        "auto",
    };

    /// <summary>
    /// OpenRouter 文档列出的比例（含扩展比例）。实际可用值随模型不同，
    /// 由 GET /images/models 的 supported_parameters.aspect_ratio 决定
    /// （gemini 系不含 3:1/9:21 等，gpt-image 系不含 1:4/8:1 等）。
    /// </summary>
    public static readonly string[] AspectsOpenRouter =
    {
        "1:1", "3:2", "2:3", "4:3", "3:4", "5:4", "4:5",
        "16:9", "9:16", "2:1", "1:2", "21:9", "9:21",
        "1:4", "4:1", "1:8", "8:1",
        "auto",
    };

    /// <summary>
    /// OpenAI 官方 GPT Image 系的画幅取值（**像素字符串**，不是比例名 —— 官方 size 参数
    /// 收的就是 <c>WIDTHxHEIGHT</c>）。
    ///
    /// 来源：docs/GPT Image generation.md
    ///   · 推荐尺寸 <c>1024x1024</c> / <c>1536x1024</c> / <c>1024x1536</c>
    ///   · GPT Image 2.5 与 2 另支持任意合规 WxH（列几个 2K/4K 常用值方便选）
    ///   · 2.5 与 2 支持 <c>auto</c>（由模型按提示词自选）
    /// </summary>
    public static readonly string[] AspectsOpenAi =
    {
        "1024x1024", "1536x1024", "1024x1536",
        "2048x2048", "2048x1152", "3840x2160", "2160x3840",
        "auto",
    };

    /// <summary>OpenAI 早期 GPT Image 模型（1 / 1.5 / 1-mini）：仅 3 个标准尺寸 + auto。</summary>
    public static readonly string[] AspectsOpenAiStandard =
    {
        "1024x1024", "1536x1024", "1024x1536",
        "auto",
    };

    /// <summary>dall-e-3 的 size 是固定三值（文档明确），**不支持 auto**。</summary>
    public static readonly string[] AspectsOpenAiDallE3 =
    {
        "1024x1024", "1792x1024", "1024x1792",
    };

    /// <summary>
    /// 千问 DashScope 的画幅取值。
    ///
    /// ⚠️ 两套模型的 size 口径不同（docs/千问-*.md）：
    ///   · 3.0 系：像素面积须在 512*512 ~ 2048*2048，未指定时由模型自选 → 支持 auto
    ///   · 2.0 系：默认 2048*2048，可自由设置宽高（同区间）
    ///   · max / plus / qwen-image：**固定 5 档**（1664*928 等），不能自由设置
    /// 本列表按 3.0/2.0 给；固定档模型由 <see cref="DashScopeAspectChoices"/> 收窄。
    /// </summary>
    public static readonly string[] AspectsDashScope =
    {
        "1024x1024", "1536x1024", "1024x1536",
        "2048x2048", "2048x1152", "2688x1536", "1536x2688",
        "auto",
    };

    /// <summary>千问 max / plus / qwen-image 的固定分辨率档（文档明确列出，不能自由设置）。</summary>
    public static readonly string[] AspectsDashScopeFixed =
    {
        "1664x928", "1472x1104", "1328x1328", "1104x1472", "928x1664",
    };

    /// <summary>
    /// 即梦的画幅取值 —— **文档给出的"推荐可选宽高"表**（docs/即梦AI-图片生成4.md）。
    ///
    /// ⚠️ 即梦的尺寸语义是 **width/height 像素对**（不是比例名），且：
    ///   · 宽高乘积须在 [1024*1024, 4096*4096]，宽高比在 [1/3, 3]；
    ///   · **必须同时传 width 和 height 才生效**（只传一个会被忽略）；
    ///   · 也可只传面积 <c>size</c>，由模型按 prompt 自判比例（对应这里的 "auto"）。
    /// 这里把文档推荐值全列出来（1K / 2K / 4K × 各常用比例）。
    /// </summary>
    public static readonly string[] AspectsJimeng =
    {
        // 1K
        "1024x1024",
        // 2K（文档推荐）
        "2048x2048", "2304x1728", "2496x1664", "2560x1440", "3024x1296",
        // 4K（文档推荐）
        "4096x4096", "4694x3520", "4992x3328", "5404x3040", "6198x2656",
        // 只传面积、由模型自判比例
        "auto",
    };

    /// <summary>
    /// 即梦"提取"链路的画幅：文档只给 <c>width</c>/<c>height</c> 各自范围 [1024, 4096]
    /// （默认 2048×2048），**没有**推荐比例表 → 给几档合规的方形即可。
    /// </summary>
    public static readonly string[] AspectsJimengExtract =
    {
        "1024x1024", "1536x1536", "2048x2048", "2560x2560", "3072x3072", "4096x4096",
    };

    /// <summary>五个 provider 画幅的并集（老配置兼容用）。</summary>
    public static readonly string[] Aspects =
        AspectsApimart.Concat(AspectsOpenRouter)
                      .Concat(AspectsOpenAi)
                      .Concat(AspectsDashScope)
                      .Concat(AspectsJimeng)
                      .Distinct(StringComparer.Ordinal).ToArray();

    public static string[] AspectChoices(ApiProvider p) => AspectChoicesFor(p, null);

    /// <summary>
    /// 画幅是否**被协议接受**（发请求的合法性判定；与"下拉里能否选中"是两件事）。
    ///
    /// · 比例名/固定像素值 → 命中该 provider+模型的集合即可；
    /// · 任意像素串 → 仅当该模型允许任意分辨率（<see cref="AllowsArbitraryPixels"/>）时放行。
    ///
    /// ⚠️ **不要**用它做 UI 收窄：APIMart / OpenRouter 接受任意像素串，
    ///   但它们的下拉只列比例名 → 用它收窄会留下"值合法但下拉选不中"的空白画幅。
    ///   收窄请用 <see cref="AspectChoicesFor"/> 做纯集合判定。
    /// </summary>
    public static bool AspectSupported(ApiProvider p, string? aspect, string? model = "") =>
        AspectOrSizeSupported(p, aspect, model);

    /// <summary>APIMart：resolution 档位（文档：1k / 2k / 4k）。</summary>
    public static readonly string[] Resolutions = { "1k", "2k", "4k" };

    /// <summary>
    /// OpenRouter 的**可选分辨率档位（UI 下拉用）**。
    ///
    /// ⚠️ 必须与 <see cref="GenRequest.Resolution"/> 的**内部语义**一致（小写 1k/2k/4k）：
    ///   早期这里写的是大写 <c>"1K"/"2K"/"4K"</c>，于是
    ///     ① ComboBox 的 SelectedItem（内部 "1k"）在 ItemsSource 里**找不到匹配** → 下拉显示空白；
    ///     ② <c>ClampParamsToCapabilities</c> 拿大写集合校验小写值 → 永远判"不支持"，
    ///        每次切回 OpenRouter 都把用户的分辨率重置掉（v0.5.31 AOT 实跑日志抓到）。
    ///   发给 API 时的大写映射由 <see cref="OpenRouterResolution"/> 负责（那才是"协议层大写"）。
    /// </summary>
    public static readonly string[] ResolutionsOpenRouter = { "512", "1k", "2k", "4k" };

    public static string[] ResolutionChoices(ApiProvider p) =>
        p == ApiProvider.Apimart ? Resolutions : ResolutionsOpenRouter;

    /// <summary>把内部统一的小写档位映射成 OpenRouter 接受的大写档名。</summary>
    public static string OpenRouterResolution(string? resolution) =>
        (resolution ?? "").Trim().ToLowerInvariant() switch
        {
            "512" => "512",
            "2k" => "2K",
            "4k" => "4K",
            _ => "1K",
        };

    /// <summary>APIMart：png / jpeg / webp。</summary>
    public static readonly string[] OutputFormats = { "png", "jpeg", "webp" };

    /// <summary>
    /// OpenAI 官方：png / jpeg / webp（仅 GPT Image 系支持 output_format；
    /// dall-e-3 不认这个参数，由 <see cref="OutputFormatChoices"/> 收窄为单值）。
    /// </summary>
    public static readonly string[] OutputFormatsOpenAi = { "png", "jpeg", "webp" };

    /// <summary>dall-e-3：无 output_format 参数（文档只给 url/b64_json 的 response_format）。</summary>
    public static readonly string[] OutputFormatsDallE3 = { "png" };

    /// <summary>千问：文档明确输出为 PNG。</summary>
    public static readonly string[] OutputFormatsDashScope = { "png" };

    /// <summary>OpenRouter 额外支持 svg（仅向量化模型，如 Recraft）。</summary>
    public static readonly string[] OutputFormatsOpenRouter = { "png", "jpeg", "webp", "svg" };

    public static string[] OutputFormatChoices(ApiProvider p, string? model = "") => p switch
    {
        ApiProvider.Apimart => OutputFormats,
        ApiProvider.OpenAi => (model ?? "").Trim().ToLowerInvariant()
                                  .StartsWith("dall-e", StringComparison.Ordinal)
            ? OutputFormatsDallE3 : OutputFormatsOpenAi,
        ApiProvider.DashScope => OutputFormatsDashScope,
        // 即梦：文档明确"输出图片格式为 png"
        ApiProvider.Jimeng => OutputFormatsDashScope,
        _ => OutputFormatsOpenRouter,
    };

    // ---------------------------------------------------------------- 新增参数（文档对齐）
    /// <summary>背景模式：auto / transparent / opaque。</summary>
    public static readonly string[] BackgroundChoices = { "auto", "transparent", "opaque" };

    /// <summary>内容审核强度：auto / low。</summary>
    public static readonly string[] ModerationChoices = { "auto", "low" };

    /// <summary>
    /// 该 provider 是否有**真正的蒙版通道**（局部重绘），而非"标注合成图 + 提示词"降级。
    ///
    /// ⚠️ **单一真源**：必须与 <c>ImageApi</c> 的实际实现一致 ——
    ///   · <see cref="ApiProvider.Apimart"/> → <c>GenerateApimartAsync</c> 上传蒙版换公网 URL，
    ///     随 <c>image_urls</c> 一起发 <c>mask_url</c>；
    ///   · <see cref="ApiProvider.OpenAi"/> → <c>GenerateOpenAiEditAsync</c> 走
    ///     <c>POST /images/edits</c> 的 multipart <c>mask</c> 文件字段；
    ///   · 其余（OpenRouter / 千问 / 即梦）→ 文档无 mask 字段，
    ///     上层改用「原图 + 标注合成图 + 提示词说明」表达区域。
    ///
    /// 历史坑（v0.5.32 修）：App 层的 `maskSupported` 曾只认 APIMart，
    /// 而 `RegionEditTip` 同时告诉 OpenAI 用户"将发送真正的 Alpha 蒙版" ——
    /// 文案与行为不符，OpenAI 用户的蒙版被无谓丢弃（Core 明明支持）。
    /// </summary>
    public static bool SupportsMaskChannel(ApiProvider p) =>
        p is ApiProvider.Apimart or ApiProvider.OpenAi;

    /// <summary>OpenAI 官方 GPT Image 2 与 2.5 **不支持 transparent**（文档明确会报错）。</summary>
    public static bool SupportsTransparentBackground(ApiProvider p, string? model)
    {
        if (p != ApiProvider.OpenAi) return true;   // 其他 provider 由各自的文档决定
        var m = (model ?? "").Trim().ToLowerInvariant();
        return !(m.StartsWith("gpt-image-2", StringComparison.Ordinal));
    }

    /// <summary>
    /// background=transparent 只支持带 Alpha 的格式（文档明确 JPEG 不支持）。
    /// 返回 null 表示组合合法，否则返回可直接展示给用户的原因。
    /// </summary>
    public static string? ValidateBackground(string? background, string? outputFormat)
    {
        if (!string.Equals(background?.Trim(), "transparent", StringComparison.OrdinalIgnoreCase))
            return null;
        var fmt = (outputFormat ?? "").Trim().ToLowerInvariant();
        if (fmt is "png" or "webp") return null;
        return $"background=transparent 需要 output_format 为 png 或 webp（当前 {fmt}）"
             + " —— JPEG 没有 Alpha 通道";
    }

    /// <summary>output_compression 仅对 jpeg/webp 生效（文档：png 忽略）。</summary>
    public static bool CompressionApplies(string? outputFormat) =>
        (outputFormat ?? "").Trim().ToLowerInvariant() is "jpeg" or "jpg" or "webp";

    // ---------------------------------------------------------------- 精确像素尺寸
    /// <summary>
    /// 解析精确像素尺寸（"1600x1200"）。不是该格式时返回 null。
    /// </summary>
    public static (int Width, int Height)? ParsePixelSize(string? size)
    {
        var s = (size ?? "").Trim().ToLowerInvariant();
        int x = s.IndexOf('x');
        if (x <= 0 || x == s.Length - 1) return null;
        if (!int.TryParse(s[..x], out var w) || !int.TryParse(s[(x + 1)..], out var h))
            return null;
        if (w <= 0 || h <= 0) return null;
        return (w, h);
    }

    /// <summary>
    /// 校验精确像素尺寸是否满足 APIMart 文档的"尺寸规则"：
    /// 宽高均为 16 的倍数、单边 ≤ 3840、长短边比 ≤ 3:1、总像素 655,360 ~ 8,294,400。
    /// 合法返回 null，否则返回原因。
    /// </summary>
    public static string? ValidatePixelSize(int w, int h)
    {
        if (w % 16 != 0 || h % 16 != 0)
            return $"宽高必须都是 16 的倍数（当前 {w}x{h}）";
        if (w > MaxPixelSide || h > MaxPixelSide)
            return $"单边不能超过 {MaxPixelSide} 像素（当前 {w}x{h}）";
        double ratio = (double)Math.Max(w, h) / Math.Min(w, h);
        if (ratio > 3.0 + 1e-9)
            return $"长短边比例不能超过 3:1（当前 {ratio:0.00}:1）";
        long pixels = (long)w * h;
        if (pixels < MinTotalPixels || pixels > MaxTotalPixels)
            return $"总像素需在 {MinTotalPixels:N0} ~ {MaxTotalPixels:N0} 之间（当前 {pixels:N0}）";
        return null;
    }

    /// <summary>校验精确像素尺寸字符串；不是像素格式时返回 null（交由比例校验处理）。</summary>
    public static string? ValidatePixelSizeString(string? size)
    {
        if (ParsePixelSize(size) is not { } px) return null;
        return ValidatePixelSize(px.Width, px.Height);
    }

    /// <summary>
    /// APIMart 文档的"尺寸规则"下界：总像素下限。
    /// （GPT Image 2/2.5 与千问用的是各自文档的规则，见 <see cref="ValidatePixelSizeFor"/>。）
    /// </summary>
    public static string? ValidatePixelSizeFor(ApiProvider p, string? model, string? size)
    {
        if (ParsePixelSize(size) is not { } px) return null;

        // 千问：512*512 ~ 2048*2048（总面积），无"16 的倍数"要求，比例限 1:8~8:1
        if (p == ApiProvider.DashScope)
        {
            long qpixels = (long)px.Width * px.Height;
            if (qpixels < DashScopeMinPixels || qpixels > DashScopeMaxPixels)
                return $"千问 size 面积需在 {DashScopeMinPixels:N0} ~ {DashScopeMaxPixels:N0} 之间"
                     + $"（当前 {px.Width}x{px.Height} = {qpixels:N0}）";
            double qratio = (double)Math.Max(px.Width, px.Height) / Math.Min(px.Width, px.Height);
            if (qratio > 8.0 + 1e-9)
                return $"千问 size 宽高比不能超过 8:1（当前 {qratio:0.00}:1）";
            return null;
        }

        // 即梦：宽高乘积 [1024*1024, 4096*4096]、宽高比 [1/3, 3]（无"16 的倍数"要求）
        if (p == ApiProvider.Jimeng)
        {
            long jpixels = (long)px.Width * px.Height;
            if (jpixels < JimengMinPixels || jpixels > JimengMaxPixels)
                return $"即梦 width×height 需在 {JimengMinPixels:N0} ~ {JimengMaxPixels:N0} 之间"
                     + $"（当前 {px.Width}x{px.Height} = {jpixels:N0}）";
            double jratio = (double)Math.Max(px.Width, px.Height) / Math.Min(px.Width, px.Height);
            if (jratio > 3.0 + 1e-9)
                return $"即梦 宽高比需在 1:3 ~ 3:1 之间（当前 {jratio:0.00}:1）";
            return null;
        }

        // OpenAI 官方与 APIMart 都按同一套"16 倍数 / 单边 3840 / 3:1 / 总像素区间"规则
        // （OpenAI 文档 "Size constraints" 与 APIMart 一致，故共用一套校验）
        return ValidatePixelSize(px.Width, px.Height);
    }

    /// <summary>即梦 width×height 面积下界（文档：1024*1024）。</summary>
    public const long JimengMinPixels = 1024L * 1024;
    /// <summary>即梦 width×height 面积上界（文档：4096*4096）。</summary>
    public const long JimengMaxPixels = 4096L * 4096;
    /// <summary>即梦单边上限（文档：分辨率最大 4096×4096）。</summary>
    public const int JimengMaxSide = 4096;
    /// <summary>即梦 size 面积默认值（文档：4194304，即 2048×2048 = 2K）。</summary>
    public const long JimengDefaultPixels = 4194304;

    /// <summary>千问 size 面积下界（文档：512*512）。</summary>
    public const long DashScopeMinPixels = 512L * 512;
    /// <summary>千问 size 面积上界（文档：2048*2048）。</summary>
    public const long DashScopeMaxPixels = 2048L * 2048;

    /// <summary>单边像素上限（文档尺寸规则）。</summary>
    public const int MaxPixelSide = 3840;
    /// <summary>总像素下限（文档尺寸规则）。</summary>
    public const long MinTotalPixels = 655_360;
    /// <summary>总像素上限（文档尺寸规则）。</summary>
    public const long MaxTotalPixels = 8_294_400;

    // ---------------------------------------------------------------- 蒙版（局部重绘）
    /// <summary>
    /// 蒙版体积上限：文档"通用文件大小指南写的是小于 50MB，而 mask 参数说明写的是
    /// 小于 4MB；未实测前建议按更严格的 4MB 准备"。这里按 4MB 把关。
    /// </summary>
    public const long MaxMaskBytes = 4L * 1024 * 1024;

    /// <summary>
    /// 校验蒙版是否满足文档要求，合法返回 null。
    ///
    /// 文档（APIMart mask_url）三条硬要求：
    ///   ① 必须是**带 Alpha 通道的 PNG** —— 普通黑白图不能直接当蒙版
    ///      （APIMart 不会自动补 Alpha，也不预先校验，必须由调用方保证）；
    ///   ② 像素宽高必须与 **image_urls[0]（第一张参考图）完全一致**，
    ///      匹配的是第一张输入原图，**不是**输出 size；
    ///   ③ 体积建议 < 4MB。
    /// </summary>
    /// <param name="mask">蒙版字节。</param>
    /// <param name="maskSize">蒙版像素尺寸（探测失败传 null）。</param>
    /// <param name="refSize">第一张参考图的像素尺寸（探测失败传 null）。</param>
    /// <param name="hasRefs">是否提供了参考图（文档：蒙版仅在同时传 image_urls 时生效）。</param>
    public static string? ValidateMask(byte[]? mask, (int Width, int Height)? maskSize,
                                       (int Width, int Height)? refSize, bool hasRefs)
    {
        if (mask is null || mask.Length == 0) return null;   // 没蒙版 = 普通编辑，无需校验

        if (!hasRefs)
            return "蒙版仅在同时提供参考图（image_urls）时生效 —— 请把要修改的原图放在参考图第 1 位";
        if (mask.Length > MaxMaskBytes)
            return $"蒙版超过 {MaxMaskBytes / 1024 / 1024}MB（当前 {mask.Length / 1024.0 / 1024:0.0}MB）";
        if (maskSize is not { } m)
            return "蒙版不是可解析的图片";
        if (refSize is not { } r)
            return null;    // 探测不到原图尺寸 → 无法校验，交由服务端
        if (m.Width != r.Width || m.Height != r.Height)
            return $"蒙版尺寸必须与第 1 张参考图完全一致："
                 + $"蒙版 {m.Width}x{m.Height} vs 原图 {r.Width}x{r.Height}";
        return null;
    }

    /// <summary>PNG 是否带 Alpha 通道（IHDR 的 color type 4/6 = 有 Alpha）。</summary>
    public static bool PngHasAlpha(ReadOnlySpan<byte> png)
    {
        // PNG 签名 8 字节 + IHDR 长度 4 + "IHDR" 4 + 宽 4 + 高 4 + 位深 1 + 颜色类型 1
        if (png.Length < 26) return false;
        if (png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47)
            return false;
        if (png[12] != 'I' || png[13] != 'H' || png[14] != 'D' || png[15] != 'R')
            return false;
        byte colorType = png[25];
        return colorType is 4 or 6;   // 4 = 灰度+Alpha，6 = RGBA
    }

    // ---------------------------------------------------------------- 成本估算
    // 每张图的 USD 估算（1k 基准）。来源：APIMart 官方输出 token 表 + 实测校准。
    private static readonly Dictionary<string, double> CostPerImage1k = new()
    {
        ["low"] = 0.0048,
        ["medium"] = 0.0106,
        ["high"] = 0.0450,
        ["xhigh"] = 0.0800,
        ["max"] = 0.1800,
    };

    /// <summary>auto 实测恒落在 low（output_tokens 恒 196），故按 low 估。</summary>
    public const double CostAutoFallback = 0.0048;

    private static readonly Dictionary<string, double> ResMultiplier = new()
    {
        ["1k"] = 1.0, ["2k"] = 2.0, ["4k"] = 4.0,
    };

    /// <summary>估算 n 张图的花费（USD）。仅估算，实际按 token 结算且受折扣影响。</summary>
    public static double EstimateCost(string quality, string resolution = "1k", int n = 1)
    {
        double baseCost = quality == "auto"
            ? CostAutoFallback
            : CostPerImage1k.GetValueOrDefault(quality, 0.02);
        double mult = ResMultiplier.GetValueOrDefault(resolution, 1.0);
        return baseCost * mult * Math.Max(1, n);
    }

    private static readonly Dictionary<string, int> TokenTable = new()
    {
        ["low"] = 196, ["medium"] = 439, ["high"] = 1756, ["xhigh"] = 3122, ["max"] = 7024,
    };

    /// <summary>估算 (花费, token 数)。</summary>
    public static (double Cost, int Tokens) EstimateDetail(string quality,
                                                          string resolution = "1k", int n = 1)
    {
        var cost = EstimateCost(quality, resolution, n);
        int baseTokens = TokenTable.GetValueOrDefault(quality, TokenTable["low"]);
        int tokens = (int)(baseTokens * ResMultiplier.GetValueOrDefault(resolution, 1.0) * Math.Max(1, n));
        return (cost, tokens);
    }

    // ---------------------------------------------------------------- 限额
    public const int MaxRefs = 16;                 // image_urls / input_references 上限（文档：最多 16 张）

    /// <summary>
    /// 各 provider 的参考图张数上限（文档各不相同，混用会被服务端拒绝）：
    ///   · APIMart / OpenRouter / OpenAI 官方：16 张
    ///   · 千问 DashScope：**3 张**（文档 I2I "支持传入1-3张图像"）
    /// </summary>
    public static int MaxRefsFor(ApiProvider p) =>
        p == ApiProvider.DashScope ? 3 : MaxRefs;
    /// <summary>
    /// 参考图上限：APIMart 上传接口文档为 20MB/文件（POST /v1/uploads/images）。
    /// 超过即压缩，未超过则**原样上传**（保证主体/文字细节不被削弱）。
    /// </summary>
    public const long MaxUpload = 20L * 1024 * 1024;
    /// <summary>
    /// 参考图"仅在必要时压缩"的目标长边。
    /// 之所以不是 1024：实测压到 1024 会显著削弱"保留主体细节/文字"的效果，
    /// 而 APIMart 单图上限 20MB / OpenRouter base64 内联都撑得住 2048。
    /// </summary>
    public const int RefShrinkMaxSide = 2048;
    public const int HistoryMax = 200;
    public const int UploadCacheMax = 256;
    public const double Timeout = 300.0;
    public const int MaxAttempts = 3;
    public const double BackoffBase = 1.5;
    public const int PromptHistoryMax = 200;

    /// <summary>APIMart 单次生成张数上限（文档：n 取值 1~4）。</summary>
    public const int ApimartMaxN = 4;
    /// <summary>OpenRouter 单次生成张数上限（文档：n 取值 1~10）。</summary>
    public const int OpenRouterMaxN = 10;
    /// <summary>OpenAI 官方单次生成张数上限（文档：n 取值 1~10；dall-e-3 例外见 MaxNFor）。</summary>
    public const int OpenAiMaxN = 10;
    /// <summary>千问 3.0/2.0 系单次生成张数上限（文档：n 取值 1~6）。</summary>
    public const int DashScopeMultiNMax = 6;
    /// <summary>
    /// 即梦**没有 n 参数** —— 出图张数由模型按 prompt 意图决定（最多 15 张），
    /// 或用 <c>force_single</c> 强制 1 张。所以这里固定为 1（UI 不给出批量）。
    /// </summary>
    public const int JimengMaxN = 1;

    public static int MaxN(ApiProvider p) => p switch
    {
        ApiProvider.Apimart => ApimartMaxN,
        ApiProvider.OpenAi => OpenAiMaxN,
        ApiProvider.DashScope => DashScopeMultiNMax,
        ApiProvider.Jimeng => JimengMaxN,
        _ => OpenRouterMaxN,
    };

    // provider 清单（key, 显示名）
    public static readonly (ApiProvider Provider, string Label)[] Providers =
    {
        (ApiProvider.OpenRouter, "OpenRouter"),
        (ApiProvider.Apimart, "APIMart（API Mart AI）"),
        (ApiProvider.OpenAi, "OpenAI 官方"),
        (ApiProvider.DashScope, "千问 DashScope"),
        (ApiProvider.Jimeng, "即梦（火山引擎）"),
    };

    public static string KeyHint(ApiProvider p) => p switch
    {
        ApiProvider.Apimart => "形如 sk-xxxxxxxx",
        // 百炼与 OpenAI 官方都是 sk- 开头（无法从格式区分，仅作提示）
        ApiProvider.OpenAi => "形如 sk-proj-xxxxxxxx / sk-xxxxxxxx",
        ApiProvider.DashScope => "百炼 API Key，形如 sk-xxxxxxxx",
        // ⚠️ 即梦要**两个值**：AccessKeyId + SecretAccessKey（火山引擎 AK/SK 签名）
        ApiProvider.Jimeng => "AccessKeyId（AK），形如 AKLTxxxxxxxx",
        _ => "形如 sk-or-v1-xxxxxxxx",
    };

    /// <summary>粗略判断 key 格式是否像该 provider 的（只提示，不拦截）。</summary>
    public static bool LooksLikeKey(string k, ApiProvider p)
    {
        k = (k ?? "").Trim();
        if (k.Length == 0) return false;
        // ⚠️ OpenAI 官方与 DashScope 都是 sk- 前缀，且 DashScope 的 key 同样以 sk- 开头，
        //    两者格式无法区分 → 只校验公共前缀，不做更严的判定（避免误报拦截合法 key）。
        return p == ApiProvider.OpenRouter
            ? k.StartsWith("sk-or-", StringComparison.Ordinal)
            : k.StartsWith("sk-", StringComparison.Ordinal);
    }
}
