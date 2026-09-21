using Imgagent.Core.Models;

namespace Imgagent.Core;

/// <summary>
/// 常量目录：端点、模型清单、质量档、画幅、成本估算。
/// 逐条对应 Python 的 <c>settings.py</c>（唯一真源）。
/// </summary>
public static class Catalog
{
    // ---------------------------------------------------------------- 端点
    public const string OpenRouterBaseDefault = "https://openrouter.ai/api/v1";
    public const string ApimartBaseDefault = "https://api.apimart.ai/v1";

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

    public const string DefaultModelOpenRouter = "openai/gpt-image-2.5-flare";
    public const string DefaultModelApimart = "gpt-image-2.5-flare";

    public static string[] ModelChoices(ApiProvider p) =>
        p == ApiProvider.Apimart ? ModelChoicesApimart : ModelChoicesOpenRouter;

    public static string DefaultModel(ApiProvider p) =>
        p == ApiProvider.Apimart ? DefaultModelApimart : DefaultModelOpenRouter;

    /// <summary>模型名是否与 provider 命名习惯相符（OpenRouter 用 provider/model，APIMart 用裸名）。</summary>
    public static bool ModelMatchesProvider(string? model, ApiProvider p) =>
        p == ApiProvider.Apimart
            ? !string.IsNullOrEmpty(model) && !model.Contains('/')
            : !string.IsNullOrEmpty(model) && model.Contains('/');

    // ---------------------------------------------------------------- 质量档
    /// <summary>OpenRouter：auto/low/medium/high（传 xhigh/max 会 400）。</summary>
    public static readonly string[] QualitiesOpenRouter = { "auto", "low", "medium", "high" };

    /// <summary>APIMart：多两档，但 xhigh/max 只有 gpt-image-2.5 系支持。</summary>
    public static readonly string[] QualitiesApimart = { "auto", "low", "medium", "high", "xhigh", "max" };

    private static readonly string[] ApimartHighQualityModelPrefixes = { "gpt-image-2.5" };

    /// <summary>当前 provider（+ 指定模型）下可用的质量档位。</summary>
    public static string[] QualityChoices(ApiProvider p, string? model = "")
    {
        if (p != ApiProvider.Apimart) return QualitiesOpenRouter;
        if (!string.IsNullOrEmpty(model) &&
            !ApimartHighQualityModelPrefixes.Any(pre => model.StartsWith(pre, StringComparison.Ordinal)))
        {
            return QualitiesApimart.Where(q => q is not ("xhigh" or "max")).ToArray();
        }
        return QualitiesApimart;
    }

    public static bool QualitySupported(ApiProvider p, string? quality, string? model = "") =>
        !string.IsNullOrEmpty(quality) && QualityChoices(p, model).Contains(quality);

    // ---------------------------------------------------------------- 画幅/分辨率/格式
    public static readonly string[] Aspects =
        { "1:1", "16:9", "9:16", "3:2", "2:3", "4:3", "3:4", "21:9", "auto" };

    public static readonly string[] Resolutions = { "1k", "2k", "4k" };

    public static readonly string[] OutputFormats = { "png", "jpeg", "webp" };

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
    public const int MaxRefs = 16;                 // input_references 上限
    public const long MaxUpload = 12L * 1024 * 1024;  // 参考图上限
    public const int HistoryMax = 200;
    public const int UploadCacheMax = 256;
    public const double Timeout = 300.0;
    public const int MaxAttempts = 3;
    public const double BackoffBase = 1.5;
    public const int PromptHistoryMax = 200;

    public static readonly string[] ImageExts =
        { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };

    // provider 清单（key, 显示名）
    public static readonly (ApiProvider Provider, string Label)[] Providers =
    {
        (ApiProvider.OpenRouter, "OpenRouter"),
        (ApiProvider.Apimart, "APIMart（API Mart AI）"),
    };

    public static string KeyHint(ApiProvider p) =>
        p == ApiProvider.Apimart ? "形如 sk-xxxxxxxx" : "形如 sk-or-v1-xxxxxxxx";

    /// <summary>粗略判断 key 格式是否像该 provider 的（只提示，不拦截）。</summary>
    public static bool LooksLikeKey(string k, ApiProvider p)
    {
        k = (k ?? "").Trim();
        if (k.Length == 0) return false;
        return p == ApiProvider.Apimart ? k.StartsWith("sk-") : k.StartsWith("sk-or-");
    }
}
