using ImgHub.Core.Models;

namespace ImgHub.Core;

/// <summary>
/// 即梦「素材提取」的预设指令。
///
/// 来源（仓库内一手文档，**逐字照抄**官方给定的文案 —— 不要改写）：
///   · <c>docs/即梦AI-素材提取(商品提取)-接口文档.md</c>
///     → req_key <c>jimeng_i2i_extract_tiled_images</c>，参数名 <c>edit_prompt</c>，**6 种**
///   · <c>docs/即梦AI-素材提取(提取元素转为平面设计图)-接口文档.md</c>
///     → req_key <c>i2i_material_extraction</c>，参数名 <c>image_edit_prompt</c>，**4 种**
///
/// ⚠️ 为什么必须**逐字**用官方文案：两份文档都写明"支持以下 N 种类型（N 选 1）"，
///   模型是按这些**特定措辞**训练/约束的。用户自己改写成"帮我把衣服抠出来"
///   会让提取退化（背景不白、不成平铺图、比例不对）。所以 UI 只让用户**选**，
///   不允许自由编辑（商品提取那条"饰品可替换为具体物品"的说明是文档允许的扩展点，
///   见 <see cref="IsCustomizable"/>）。
/// </summary>
public static class JimengExtract
{
    /// <summary>一个提取预设。</summary>
    /// <param name="Kind">所属链路（决定 req_key 与参数名）。</param>
    /// <param name="Name">UI 显示名（简短）。</param>
    /// <param name="Prompt">官方指令原文（**逐字**）。</param>
    /// <param name="Customizable">
    /// 是否允许用户把末尾的物件名换掉。
    /// 文档原文：「提取饰品时，饰品可以替换为具体的物品，如耳坠、项链等。」
    /// —— 只有这一条给了这个自由度。
    /// </param>
    public sealed record Preset(JimengExtractKind Kind, string Name, string Prompt,
                                bool Customizable = false);

    /// <summary>提取链路类型（对应两个不同的 req_key）。</summary>
    public enum JimengExtractKind
    {
        /// <summary>商品提取：<c>jimeng_i2i_extract_tiled_images</c>，参数 <c>edit_prompt</c>。</summary>
        Product,
        /// <summary>元素提取：<c>i2i_material_extraction</c>，参数 <c>image_edit_prompt</c>。</summary>
        Element,
    }

    /// <summary>该链路对应的 req_key。</summary>
    public static string ReqKey(JimengExtractKind kind) => kind switch
    {
        JimengExtractKind.Product => "jimeng_i2i_extract_tiled_images",
        _ => "i2i_material_extraction",
    };

    /// <summary>
    /// 该链路承载指令的**参数名** —— 两份文档刻意用了不同的名字，
    /// 传错字段服务端会说"缺少必选参数"。
    /// </summary>
    public static string PromptField(JimengExtractKind kind) => kind switch
    {
        JimengExtractKind.Product => "edit_prompt",
        _ => "image_edit_prompt",
    };

    /// <summary>从 req_key 反推链路。</summary>
    public static JimengExtractKind KindOfReqKey(string? reqKey) =>
        string.Equals(reqKey, ReqKey(JimengExtractKind.Element), StringComparison.OrdinalIgnoreCase)
            ? JimengExtractKind.Element
            : JimengExtractKind.Product;

    // ---------------------------------------------------------------- 商品提取（6 种）
    /// <summary>商品提取的 6 种预设（文档「6选1」）。</summary>
    public static readonly IReadOnlyList<Preset> ProductPresets = new[]
    {
        new Preset(JimengExtractKind.Product, "提取全身衣服",
            "提取出图片中的衣服、帽子、鞋子和包，生成一张平铺图，背景为纯白色。"),
        new Preset(JimengExtractKind.Product, "提取鞋子",
            "提取出图片中的一双鞋子，生成一张正45度图，背景为纯白色。"),
        new Preset(JimengExtractKind.Product, "提取包包",
            "提取出图片中的完整的包包和包带，正视图，背景为纯白色。"),
        new Preset(JimengExtractKind.Product, "提取沙发",
            "提取出图片中的完整的沙发，生成一张正视图，背景为纯白色。"),
        new Preset(JimengExtractKind.Product, "提取日用品",
            "提取出图片中的日用品，生成一张正视图，背景为纯白色。"),
        // 文档：「提取饰品时，饰品可以替换为具体的物品，如耳坠、项链等。」
        new Preset(JimengExtractKind.Product, "提取饰品",
            "提取出图片中的饰品，生成一张正视图，背景为纯白色。",
            Customizable: true),
    };

    // ---------------------------------------------------------------- 元素提取（4 种）
    /// <summary>元素提取的 4 种预设（文档「四选一」）。</summary>
    public static readonly IReadOnlyList<Preset> ElementPresets = new[]
    {
        new Preset(JimengExtractKind.Element, "提取图案",
            "提取产品的图案，生成一张平面图展示其图案，去除产品本身。"),
        new Preset(JimengExtractKind.Element, "提取包装",
            "提取产品的包装图案，生成一张平面图展示其图案，去除产品本身。"),
        new Preset(JimengExtractKind.Element, "提取logo",
            "提取产品的logo，生成一张平面图展示其logo，去除产品本身。"),
        new Preset(JimengExtractKind.Element, "提取纹理",
            "提取产品的纹理，生成一张平面图平铺展示其纹理，去除产品本身。"),
    };

    /// <summary>某链路的全部预设。</summary>
    public static IReadOnlyList<Preset> PresetsOf(JimengExtractKind kind) => kind switch
    {
        JimengExtractKind.Product => ProductPresets,
        _ => ElementPresets,
    };

    /// <summary>全部预设（10 条：6 商品 + 4 元素）。</summary>
    public static IReadOnlyList<Preset> All =>
        ProductPresets.Concat(ElementPresets).ToArray();

    /// <summary>
    /// 把「可替换物件名」的预设改写成具体物品。
    /// 文档示例：「提取出图片中的耳坠，生成一张正视图，背景为纯白色。」
    ///
    /// ⚠️ 只对 <see cref="Preset.Customizable"/> 为真的预设生效；
    ///   其余预设传了也不会被改写（避免用户误把官方文案改成模型不认的措辞）。
    /// </summary>
    public static string Customize(Preset preset, string? itemName)
    {
        if (!preset.Customizable || string.IsNullOrWhiteSpace(itemName)) return preset.Prompt;
        // 保持官方句式，只换物件名
        return $"提取出图片中的{itemName.Trim()}，生成一张正视图，背景为纯白色。";
    }
}
