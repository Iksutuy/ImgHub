namespace Imgagent.Core.Models;

/// <summary>生图 API 提供商。</summary>
public enum ApiProvider
{
    OpenRouter,
    Apimart,
}

public static class ApiProviderExtensions
{
    public static string Key(this ApiProvider p) => p switch
    {
        ApiProvider.Apimart => "apimart",
        _ => "openrouter",
    };

    public static string Label(this ApiProvider p) => p switch
    {
        ApiProvider.Apimart => "APIMart",
        _ => "OpenRouter",
    };

    public static ApiProvider? Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "apimart" => ApiProvider.Apimart,
        "openrouter" => ApiProvider.OpenRouter,
        _ => null,
    };
}
