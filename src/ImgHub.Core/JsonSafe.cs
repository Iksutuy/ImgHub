using System.Text.Json.Nodes;

namespace ImgHub.Core;

/// <summary>
/// AOT 安全的 JSON 构造辅助。
///
/// ⚠️ 为什么需要它（踩坑记录见 docs/CONSTRAINTS.md H1b）：
///   `JsonArray.Add&lt;T&gt;(T)` 是**泛型重载**，内部会用**反射**把 T 包成 `JsonValue`：
///       · IL2026 RequiresUnreferencedCode —— 裁剪下可能丢成员
///       · IL3050 RequiresDynamicCode —— AOT 下需要运行时生成代码
///   在 AOT 发布时会刷出大量警告。虽然实测**功能可用**，但：
///       ① 警告会淹没真正的 AOT 问题（我们正是被这类“无害警告”误导过一次）；
///       ② 泛型重载对**非基元类型**（如自定义 POCO）确实会在 AOT 下不可靠。
///   因此统一改用 **显式接口实现** `IList&lt;JsonNode?&gt;.Add(JsonNode?)` ——
///   它接受已构造好的 `JsonNode`，**零反射、零动态代码**，IL 警告彻底消除（已实测）。
///
/// 用法：
/// <code>
/// var arr = new JsonArray();
/// arr.AddNode(new JsonObject { ["k"] = "v" });   // 对象
/// arr.AddNode(JsonValue.Create("https://x"));    // 字符串
/// arr.AddNode(JsonValue.Create(42));             // 数字
/// </code>
/// </summary>
public static class JsonSafe
{
    /// <summary>
    /// 中文/非 ASCII **不转义**的序列化选项（保持文件可读，与旧版行为一致）。
    /// 注意必须绑定 TypeInfoResolver（AppJson）以满足 AOT 要求。
    /// </summary>
    public static readonly System.Text.Json.JsonSerializerOptions Readable = AppJson.Readable;

    /// <summary>
    /// 单行（非缩进）+ 中文不转义的选项 —— 用于 **JSONL**（history.jsonl /
    /// prompt_history.jsonl，必须"一行一条记录"）。
    /// ⚠️ 用 `Readable`（带 WriteIndented）写 JSONL 会把一条记录拆成多行，
    ///    导致解析全失败（曾经踩过：历史项数变 0）。
    /// </summary>
    public static readonly System.Text.Json.JsonSerializerOptions ReadableSingleLine = new()
    {
        TypeInfoResolver = AppJson.Default,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>把一个 <see cref="JsonNode"/> 追加进数组（避开泛型重载 → AOT 安全）。</summary>
    public static JsonArray AddNode(this JsonArray array, JsonNode? node)
    {
        ((IList<JsonNode?>)array).Add(node);
        return array;
    }

    /// <summary>追加字符串（内部用 JsonValue.Create，同样避开泛型重载）。</summary>
    public static JsonArray AddString(this JsonArray array, string value)
        => array.AddNode(JsonValue.Create(value));

    /// <summary>追加数字。</summary>
    public static JsonArray AddNumber(this JsonArray array, double value)
        => array.AddNode(JsonValue.Create(value));

    /// <summary>追加布尔。</summary>
    public static JsonArray AddBool(this JsonArray array, bool value)
        => array.AddNode(JsonValue.Create(value));

    /// <summary>序列化为「中文可读」的 JSON（不转义非 ASCII，可缩进）。</summary>
    public static string ToReadableJson(this JsonNode node)
        => node.ToJsonString(Readable);

    /// <summary>序列化为「单行 + 中文可读」的 JSON —— 用于 JSONL（一行一条）。</summary>
    public static string ToJsonLine(this JsonNode node)
        => node.ToJsonString(ReadableSingleLine);
}
