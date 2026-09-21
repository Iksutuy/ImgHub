using System.Text.Json.Serialization;

namespace Imgagent.Core.Models;

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
}
