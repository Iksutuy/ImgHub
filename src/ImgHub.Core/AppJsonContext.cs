using System.Text.Json;
using System.Text.Json.Serialization;
using ImgHub.Core.Models;

namespace ImgHub.Core;

/// <summary>
/// System.Text.Json **源生成**（source generation）上下文。
///
/// ⚠️ 为什么必须用它（AOT 硬约束，踩坑记录见 docs/CONSTRAINTS.md H1）：
///   Native AOT 默认**禁用反射序列化**（`JsonSerializer.IsReflectionEnabledByDefault == false`）。
///   任何 `JsonSerializer.Serialize(obj)` / `Deserialize&lt;T&gt;(json)` 的**反射重载**
///   在 AOT 产物里都会抛：
///       InvalidOperationException: Reflection-based serialization has been disabled
///       for this application. Either use the source generator APIs or explicitly
///       configure the 'JsonSerializerOptions.TypeInfoResolver' property.
///   → 表现为「生成失败」「参数不保存」「统计不更新」（后者因 catch 吞掉而不报错）。
///
/// 用法：
///   · **固定结构** → 走本上下文的 `JsonTypeInfo`（如 `AppJson.Default.AppConfig`）
///   · **动态结构**（HTTP 请求体、state.json 行）→ 用 `JsonObject` / `JsonArray`（DOM，天生 AOT 安全）
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    // ⚠️ 必须显式声明 snake_case：config.json / state.json 字段名与 Python 版互通
    //    （batch_n / output_format / total_cost …），默认 PascalCase 会导致读不出来。
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    // 宽松读取：兼容旧版可能残留的未知字段 / 注释 / 尾逗号
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UseStringEnumConverter = false)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(ModelStatsFile))]
[JsonSerializable(typeof(ModelStat))]
[JsonSerializable(typeof(ModelStatsFileEntry))]
[JsonSerializable(typeof(Dictionary<string, ModelStatsFileEntry>))]
internal sealed partial class AppJson : JsonSerializerContext
{
    /// <summary>
    /// 不转义非 ASCII（保持中文可读，与旧版行为一致）。
    /// 注意：`Encoder` 不能放在 `[JsonSourceGenerationOptions]` 上（源生成器不支持），
    /// 只能在运行时 Options 里设置 —— 这里提供一个共享实例。
    /// </summary>
    public static readonly JsonSerializerOptions Readable = new()
    {
        TypeInfoResolver = Default,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
