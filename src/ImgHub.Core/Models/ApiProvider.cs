namespace ImgHub.Core.Models;

/// <summary>
/// 生图 API 提供商。
///
/// ⚠️ 新增 provider 时必须同步四处（漏一处就是"选得到但跑不通"）：
///   ① <see cref="ApiProviderExtensions.Key"/> 的稳定字符串键（写进 config.json 与历史 provider 分组）
///   ② <see cref="ApiProviderExtensions.Label"/> 的显示名（顶栏/设置浮层下拉）
///   ③ <see cref="ApiProviderExtensions.Parse"/> 的回读分支（旧配置反序列化）
///   ④ <c>Catalog</c> 里各 provider 的能力集合（模型/质量/画幅/n/格式/端点）
/// </summary>
public enum ApiProvider
{
    OpenRouter,
    Apimart,
    /// <summary>OpenAI 官方直连（api.openai.com）：gpt-image 系，generations + edits(multipart)。</summary>
    OpenAi,
    /// <summary>阿里云百炼 DashScope 原生协议（千问 Qwen-Image 系）。</summary>
    DashScope,
    /// <summary>字节跳动即梦（火山引擎 visual.volcengineapi.com，CVSync2Async 协议，AK/SK 签名）。</summary>
    Jimeng,
}

public static class ApiProviderExtensions
{
    /// <summary>
    /// 稳定键：写进 <c>config.json</c> 的 <c>provider</c> 字段与历史项的 <c>provider</c>。
    /// **一旦发布就不能改**（改了用户的配置与历史金额分组会失配）。
    /// </summary>
    public static string Key(this ApiProvider p) => p switch
    {
        ApiProvider.Apimart => "apimart",
        ApiProvider.OpenAi => "openai",
        ApiProvider.DashScope => "dashscope",
        ApiProvider.Jimeng => "jimeng",
        _ => "openrouter",
    };

    public static string Label(this ApiProvider p) => p switch
    {
        ApiProvider.Apimart => "APIMart（API Mart AI）",
        ApiProvider.OpenAi => "OpenAI 官方",
        ApiProvider.DashScope => "千问 DashScope",
        ApiProvider.Jimeng => "即梦（火山引擎）",
        _ => "OpenRouter",
    };

    /// <summary>
    /// 回读旧配置。<paramref name="s"/> 为 null/空白/未知值时返回 null，
    /// 由调用方决定回退（<c>Session.Provider</c> 回退 OpenRouter）。
    /// </summary>
    public static ApiProvider? Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "apimart" => ApiProvider.Apimart,
        "openrouter" => ApiProvider.OpenRouter,
        // 兼容几种自然写法，避免用户手改 config.json 时静默回退到 OpenRouter
        "openai" or "open-ai" or "openai-official" => ApiProvider.OpenAi,
        "dashscope" or "qwen" or "bailian" => ApiProvider.DashScope,
        "jimeng" or "即梦" or "volc" or "volcengine" or "seedream" => ApiProvider.Jimeng,
        _ => null,
    };

    /// <summary>
    /// 该 provider 的 key 是否需要**两个值**（AccessKeyId + SecretAccessKey）。
    /// 目前只有即梦（火山引擎 SigV4 签名）需要；其余都是单 key。
    /// </summary>
    public static bool NeedsAccessKeyPair(this ApiProvider p) => p == ApiProvider.Jimeng;
}
