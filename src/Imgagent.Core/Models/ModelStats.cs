using System.Text.Json.Serialization;

namespace Imgagent.Core.Models;

/// <summary>
/// 单个「provider+模型」的历史花费统计。
/// 用于价格预估：用过 → 按平均花费估；没用过 → 不显示预估（避免误导）。
/// 持久化为 model_stats.json（人类可读）。
/// </summary>
public sealed class ModelStat
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("calls")]
    public int Calls { get; set; }

    [JsonPropertyName("total_cost")]
    public double TotalCost { get; set; }

    [JsonPropertyName("total_images")]
    public int TotalImages { get; set; }

    /// <summary>平均单次花费（USD）。</summary>
    [JsonPropertyName("avg_cost")]
    public double AvgCost => Calls > 0 ? TotalCost / Calls : 0;

    /// <summary>平均每张图花费（USD）。</summary>
    [JsonPropertyName("avg_cost_per_image")]
    public double AvgCostPerImage => TotalImages > 0 ? TotalCost / TotalImages : 0;

    [JsonPropertyName("last_used")]
    public double LastUsed { get; set; }
}

/// <summary>model_stats.json 的根对象。</summary>
public sealed class ModelStatsFile
{
    /// <summary>键 = "provider|model"。</summary>
    [JsonPropertyName("models")]
    public Dictionary<string, ModelStatsFileEntry> Models { get; set; } = new();

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;
}

/// <summary>JSON 文件里的条目（可读格式）。</summary>
public sealed class ModelStatsFileEntry
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("calls")]
    public int Calls { get; set; }

    [JsonPropertyName("total_cost")]
    public double TotalCost { get; set; }

    [JsonPropertyName("total_images")]
    public int TotalImages { get; set; }

    [JsonPropertyName("avg_cost")]
    public double AvgCost { get; set; }

    [JsonPropertyName("last_used")]
    public double LastUsed { get; set; }
}