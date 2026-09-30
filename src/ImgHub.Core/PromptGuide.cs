using ImgHub.Core.Models;

namespace ImgHub.Core;

/// <summary>
/// 各生图端点的**提示词工程指南**（UI 浮窗里按标签页展示）。
///
/// 来源（仓库内一手快照，逐条对应，不臆测）：
///   · <c>docs/GPT Image prompting.md</c>（OpenAI 官方 prompting 指南）
///   · <c>docs/千问-图像编辑API参考.md</c>（千问图像编辑：模型能力/图像要求/常见问题）
///   · <c>docs/千问-文生图API参考.md</c> / <c>docs/千问-图像生成与编辑3.0 API参考.md</c>
///
/// ⚠️ 为什么放在 Core 而不是 XAML 里写死：
///   ① 这些文案是**行为契约**（对应官方文档的明确说法），要能被单测断言；
///   ② 浮窗内容与「按端点选哪套润色 prompt」必须共用同一份「哪个端点」的定义，
///      否则两处会漂移（指南讲 OpenAI、实际发千问的风格）。
/// </summary>
public static class PromptGuide
{
    /// <summary>一个端点的指南：标题 + 分组条目。</summary>
    public sealed record Guide(string Key, string Title, string Subtitle,
                               IReadOnlyList<GuideSection> Sections);

    /// <summary>指南分组（小标题 + 条目列表）。</summary>
    public sealed record GuideSection(string Heading, IReadOnlyList<GuideItem> Items);

    /// <summary>
    /// 一条建议。<paramref name="Good"/> / <paramref name="Bad"/> 是可选的对照示例
    /// （官方文档里给了正反例的地方就带上，便于用户直接照着改）。
    /// </summary>
    public sealed record GuideItem(string Text, string? Good = null, string? Bad = null);

    /// <summary>OpenAI 官方（GPT Image 系）的指南。</summary>
    public static readonly Guide OpenAi = new(
        Key: "openai",
        Title: "OpenAI（GPT Image）",
        Subtitle: "来源：docs/GPT Image prompting.md",
        Sections: new[]
        {
            new GuideSection("八条基本功（官方 Prompting fundamentals）", new[]
            {
                new GuideItem("**先定义结果**：点名主体与用途（商品图/海报/信息图），"
                            + "并写清构图、画幅、关键位置约束。复杂需求按"
                            + "「场景 → 主体 → 细节 → 约束」分段组织。"),
                new GuideItem("**选可维护的格式**：短句、描述段、类 JSON、指令、标签都行 —— "
                            + "挑「最容易读和改」的那种，不要迷信特殊语法。"),
                new GuideItem("**只描述看得见的东西**：材质、光线、颜色、媒介。"
                            + "要写实就明确写 photorealistic / real photograph；"
                            + "镜头参数只当「外观线索」，不等于物理仿真。"),
                new GuideItem("**说清人和动作**：身体取景（full body visible, feet included）、"
                            + "相对大小、视线方向、与物体的交互"
                            + "（looking down at the open book / hands naturally gripping the handlebars）。"),
                new GuideItem("**要精确文字就加引号**：把文字放进引号，"
                            + "并描述它的位置与字体；生僻词/品牌名可逐字母拼写。"
                            + "再加一句「不要出现其他文字」，出图后核对拼写。",
                              Good: "The sign reads \"Relax & Unwind\" in a handwritten serif font, "
                                  + "centered at the top. No other text.",
                              Bad: "加个招牌"),
                new GuideItem("**把「要改的」和「要保的」分开写**（编辑）："
                            + "用 change only X，再逐项列出要保留的（身份、几何、布局、光线、标签），"
                            + "并声明排除项（多余文字、logo、水印）。"),
                new GuideItem("**给参考图分配角色**：按编号说明每张图的作用"
                            + "（主体 / 风格 / 服装 / 背景），并说明它们怎么组合、哪些元素放哪里。"),
                new GuideItem("**一次只改一件事**：把上一轮输出当作下一轮输入，只提一个改动，"
                            + "并**重复**要保留的细节（结果漂移时尤其要重申关键约束）。"),
            }),
            new GuideSection("官方反复强调的三个习惯", new[]
            {
                new GuideItem("编辑时**永远**同时交代「保持不变的是什么」—— "
                            + "官方示例几乎每条都带 Do not change ... 的清单。"),
                new GuideItem("写实、宽画幅、暗光、雨夜、霓虹这类场景，"
                            + "要写**尺度、氛围、颜色**，别只丢一个情绪词。"),
                new GuideItem("文字密集 / 多字体 / 小字场景，把 quality 调到 medium 或 high 再比一比。"),
            }),
            new GuideSection("编辑专用模板（官方 Preserve identity 示例的结构）", new[]
            {
                new GuideItem("`Edit the image to <只改这一件事>. `"
                            + "`Do not change <身份/脸/体型/姿势/表情/发型> in any way. `"
                            + "`Preserve <要保的细节清单>. `"
                            + "`Match lighting, shadows, and color temperature to the original. `"
                            + "`Do not change the background, camera angle, framing, or image quality, "
                            + "and do not add accessories, text, logos, or watermarks.`"),
            }),
        });

    /// <summary>千问（Qwen-Image，DashScope 原生协议）的指南。</summary>
    public static readonly Guide Qwen = new(
        Key: "qwen",
        Title: "千问（Qwen-Image）",
        Subtitle: "来源：docs/千问-图像编辑API参考.md、docs/千问-文生图API参考.md",
        Sections: new[]
        {
            new GuideSection("协议层硬约束（写提示词时就要照顾到）", new[]
            {
                new GuideItem("**语言**：官方正式支持**简体中文与英文**；"
                            + "其他语言可试但效果不确定 —— 所以润色默认输出中文。"),
                new GuideItem("**长度上限**：文生图 2.0 系 1300 Token，"
                            + "其余（含编辑）800 Token，超出**自动截断**。"
                            + "别堆砌修饰词把关键信息挤掉。"),
                new GuideItem("**一次只有一个 text**：官方明确「不传或传入多个将报错」，"
                            + "所以编辑指令必须是**一段连续的描述**，不要分成多条。"),
                new GuideItem("**多图输入的顺序有意义**：输出比例以**最后一张**为准。"
                            + "所以「主体图在前、风格/参考图在后」要写进指令里说明。"),
            }),
            new GuideSection("编辑（I2I）写法", new[]
            {
                new GuideItem("官方示例的常见结构："
                            + "① 指定每张输入图的角色（「使用图一的城市照片作为底图」）；"
                            + "② 明确禁止改动的部分（「请勿更改照片中的真实建筑、街道、车辆或人物。"
                            + "保持照片的真实性。」）；"
                            + "③ 再描述要加入/修改的内容及其风格与位置。"),
                new GuideItem("**局部文字类需求**要把文字、字体、位置、大小都写全："
                            + "官方示例会写到「以浅灰墨色手写体题写…字体为行楷风格，"
                            + "笔触自然流畅、略带飞白，约占画面高度1/10」这种颗粒度。"),
                new GuideItem("**风格迁移**要说清「保留什么风格、加到什么位置、用什么画法」："
                            + "如「该形象应采用扁平化的图形风格绘制，轮廓清晰，"
                            + "类似于壁画或海报插图」。"),
                new GuideItem("**保留主体一致性**时，点名要保的属性（面部特征/发型/服装/姿势），"
                            + "并说明新场景；这类需求 2.0-pro 与 edit-max 的「语义遵循/角色一致性」更强。"),
            }),
            new GuideSection("选模型与参数（与提示词配合）", new[]
            {
                new GuideItem("**1-3 张参考图**：3.0 系与编辑系支持；"
                            + "「图像编辑请参考」时用编辑系模型更合适。"),
                new GuideItem("**n 与分辨率**：编辑系多为 1-6 张；"
                            + "3.0 系总像素须在 512×512 ~ 2048×2048。"
                            + "尺寸写进 parameters，不要写进提示词正文。"),
                new GuideItem("**prompt_extend**（提示词智能改写）默认开启："
                            + "提示词写得简略时收益明显；"
                            + "若你希望**更可控**、不被模型自行补充，就关掉它。"),
                new GuideItem("**negative_prompt**（反向提示词）单独填，"
                            + "不要把「不要 XX」塞进正向描述里 —— 协议有专门字段。"
                            + "官方示例的常用值：「低分辨率，低画质，肢体畸形，画面过饱和，"
                            + "人脸无细节，过度光滑，AI 感」。"),
            }),
        });

    /// <summary>按端点取指南。</summary>
    public static Guide For(ApiProvider p) => p switch
    {
        ApiProvider.DashScope => Qwen,
        _ => OpenAi,
    };

    /// <summary>
    /// 「总览」页 —— **两个端点通用**的提示词约束指南，按 8 个维度分类。
    ///
    /// 放在标签页**最前面**（用户先看它建立全局认识，再看具体端点的差异）。
    ///
    /// 维度框架（用户给定的 8 个，逐条扩充）：
    ///   主题 / 媒介 / 环境 / 灯光 / 颜色 / 情绪 / 构图 + 风格与约束
    /// 每个维度给出「该写什么」+「怎么写才对」+「常见错误」。
    ///
    /// 来源（仓库内一手快照）：
    ///   · docs/GPT Image prompting.md（官方 8 条 prompting 基本功与各示例的写法）
    ///   · docs/千问-图像编辑API参考.md（官方示例的颗粒度：字体/位置/大小占比）
    ///   · docs/千问-文生图API参考.md、docs/千问-图像生成与编辑3.0 API参考.md
    /// </summary>
    public static readonly Guide Overview = new(
        Key: "overview",
        Title: "总览（8 个维度）",
        Subtitle: "两个端点通用 · 来源：docs/GPT Image prompting.md + docs/千问-*.md",
        Sections: new[]
        {
            new GuideSection("怎么用这份清单", new[]
            {
                new GuideItem("**一句话原则**：先把**要什么**说清楚（主题/媒介/环境），"
                            + "再补**长什么样**（灯光/颜色/情绪/构图），最后**收口约束**"
                            + "（风格用词、不要什么、保留什么）。\n"
                            + "官方原话：*先想清楚你要的图，再描述主体、构图、风格与约束。*"),
                new GuideItem("**不必八个维度全写**。按需要挑 3~5 个最关键的展开；"
                            + "八个都堆满反而会让模型抓不到重点（且千问有长度上限，会被截断）。"),
                new GuideItem("**顺序建议**：主题 → 媒介 → 环境 → 构图 → 灯光 → 颜色 → 情绪 → 约束。"
                            + "先定「画什么/怎么框」，再定「氛围质感」 —— 与模型的注意力顺序一致。"),
            }),

            new GuideSection("① 主题 Subject —— 谁 / 什么", new[]
            {
                new GuideItem("**写什么**：主体是谁或什么。人、动物、角色、地点、物体都算。"
                            + "要具体到**可辨识**：不要只写「一个人」，写「穿米白羊绒大衣的年轻女人」。"),
                new GuideItem("**数量与身份要明确**：单人/多人、他们的关系与相对位置。"
                            + "多主体时点名各自的外形特征，避免模型合并或遗漏。"),
                new GuideItem("**人物还要交代取景与动作**（官方明确要求）："
                            + "`full body visible, feet included`（全身可见含脚）、"
                            + "视线方向（`looking down at the open book`）、"
                            + "与物体的交互（`hands naturally gripping the handlebars`）。",
                              Good: "一名年轻女性站在画面中央，回头面向镜头微笑，双手抱着一束玫瑰，"
                                  + "全身可见。",
                              Bad: "一个女孩"),
                new GuideItem("**编辑场景**：主体必须**点名保留**。"
                            + "「只改背景」时要写「保持主体外观、姿势与构图完全不变」。"),
            }),

            new GuideSection("② 媒介 Medium —— 以什么形式呈现", new[]
            {
                new GuideItem("**写什么**：照片 / 绘画 / 插画 / 雕塑 / 涂鸦 / 挂毯 / 渲染图 / 信息图。"
                            + "它决定整张图的「物理质感」，比风格词更基础。"),
                new GuideItem("**要写实就明确写真**：官方建议直接写 "
                            + "`photorealistic` / `real photograph` / 「写实照片」，"
                            + "**不要**只写「好看」「高级感」这类无法落地的形容词。"),
                new GuideItem("**镜头参数只当「外观线索」**：官方原文 —— "
                            + "把相机规格当作**外观提示**，而不保证真实物理模拟。"
                            + "写 `85mm portrait lens, shallow depth of field` 是让它长得像那样，"
                            + "不是在做光学仿真。"),
                new GuideItem("**混合媒介要分层说明**：如「真实城市照片作底图 + 扁平化卡通形象叠加」，"
                            + "要分别指明**哪部分是实拍、哪部分是绘制**（千问官方示例即此写法）。"),
            }),

            new GuideSection("③ 环境 Environment —— 在哪里", new[]
            {
                new GuideItem("**写什么**：室内/室外、城市/自然、水下/太空、具体场所"
                            + "（高端咖啡店、青石桥畔、白色摄影棚）。"),
                new GuideItem("**给环境一点结构**：前景/中景/背景各有什么，"
                            + "不要只写一个地点名。官方示例会写到"
                            + "「背景是通透的落地玻璃窗，窗外隐约可见繁华的城市街景」。"),
                new GuideItem("**背景要虚化就说明**：`浅景深虚化背景` / `背景景深虚化` —— "
                            + "这是把主体「推」出来的最有效手段之一。"),
                new GuideItem("**编辑场景**：换环境是**最常被越界**的操作。"
                            + "务必写「只更换背景…主体与构图保持不变…不改变相机角度与取景」。"),
            }),

            new GuideSection("④ 灯光 Lighting —— 什么类型", new[]
            {
                new GuideItem("**写什么**：柔光 / 环境光 / 阴天漫射 / 霓虹 / 影棚灯 / 逆光 / 侧逆光 / 自然光。"),
                new GuideItem("**要写方向和时机**（官方强调暗光/雨夜场景要写尺度与氛围）："
                            + "「午后柔和的自然光从侧面透过落地窗洒入」比「好光线」有效得多。",
                              Good: "午后侧向自然光透过落地窗，在面部轮廓与衣物褶皱上留下细腻的光影过渡",
                              Bad: "光线很好"),
                new GuideItem("**冷暖对比**是画面张力的常用手段：`冷蓝环境光与暖色灯光形成对比`、"
                            + "`冷暖对比强烈`。"),
                new GuideItem("**纯透明背景**要配合 `background=transparent` + "
                            + "`output_format` 为 png/webp（JPEG 无 Alpha 通道）。"),
            }),

            new GuideSection("⑤ 颜色 Color —— 什么色调", new[]
            {
                new GuideItem("**写什么**：鲜艳 / 柔和 / 明亮 / 单色 / 多彩 / 黑白 / 粉彩。"
                            + "更有效的是**点名主色**：「暖灰与金黄」「大地色、灰色和暖白色」。"),
                new GuideItem("**颜色要说明分布**，而不只给一个色板："
                            + "「人物身穿黑色细肩带连衣裙，花束以橙色、杏色、粉色玫瑰为主」。"),
                new GuideItem("**胶片感 / 颗粒 / 对比度**属同一维度的「成像特征」，"
                            + "可以一起写：「暖色胶片感处理，细腻颗粒，柔和对比」。"),
                new GuideItem("**黑白或单色**要明确写，否则模型默认给彩色；"
                            + "需要保留原图只改影调时，写「只调整影调为黑白胶片质感，"
                            + "主体、构图、背景内容均不变」。"),
            }),

            new GuideSection("⑥ 情绪 Mood —— 唤起什么感受", new[]
            {
                new GuideItem("**这一维度最容易写空**。官方提醒："
                            + "不要只丢情绪词，要说**是什么让画面有这种情绪**"
                            + "（尺度、氛围、颜色的具体来源）。",
                              Good: "浪漫、明亮、都市漫步式的氛围 —— 由逆光边缘光、"
                                  + "暖色胶片调与虚化的街景色块共同营造",
                              Bad: "温暖的氛围"),
                new GuideItem("**情绪常通过前五个维度「渗出来」**：柔光 + 低饱和 = 平静；"
                            + "霓虹 + 高对比 = 躁动；逆光 + 暖调 = 浪漫。"
                            + "所以先把 ①~⑤ 写对，情绪往往自动成立。"),
                new GuideItem("**人物情绪写表情与姿态**：「嘴角挂着优雅自信的微笑」「慵懒而放松的办公姿态」"
                            + "比「心情很好」可执行。"),
            }),

            new GuideSection("⑦ 构图 Composition —— 如何取景", new[]
            {
                new GuideItem("**写什么**：肖像 / 头像 / 特写 / 全身 / 七分身 / 鸟瞰 / 低角度 / 广角。"
                            + "以及主体在画面中的**位置与占比**。"),
                new GuideItem("**位置要具体**：官方示例会写「人物位于画面视觉中心略偏右」"
                            + "「主体位于画面右侧」这类明确指示。"),
                new GuideItem("**画幅与构图是两件事**：画幅（16:9 / 1024x1024）在**参数里**选，"
                            + "构图（竖幅七分身、经典肖像视角）在**提示词里**写。"),
                new GuideItem("**留白要说明用途**：需要放文字的图，先留出区域"
                            + "（「左上角留白用于标题」），否则文字会压在主体上。"),
            }),

            new GuideSection("⑧ 风格与约束 Style & Constraints —— 收口", new[]
            {
                new GuideItem("**风格词要克制**（用户给的第三方经验，与官方「只描述看得见的」一致）："
                            + "**保持文字提示简洁**，避免加入可能与参考图外观**冲突**的风格词；"
                            + "只有「特定风格难以达到」时，才**有选择地**补与参考图匹配的描述词。"),
                new GuideItem("**描述内容，而不是下指令**：用提示词描述**你想看到什么**，"
                            + "而不是「你希望模型怎么改这张图」。"
                            + "「背景变成雪原」优于「把背景替换掉」。"),
                new GuideItem("**编辑必须写「保留清单」**（官方反复强调）："
                            + "`change only X` + 逐项列出要保的（身份/面部/体型/姿势/几何/布局/光线/标签）"
                            + "+ 排除项（多余文字、logo、水印）。"),
                new GuideItem("**文字类需求**：把文字放进**引号**并说明位置与字体，"
                            + "再补一句「不要出现其他文字」，出图后核对拼写。"
                            + "千问官方示例的颗粒度：「行楷风格，笔触自然流畅、略带飞白，"
                            + "约占画面高度 1/10」。"),
                new GuideItem("**负向约束**：OpenAI 系没有独立字段 → 写在正文"
                            + "（`do not add text, logos, or watermarks`）；"
                            + "千问有 **negative_prompt** 独立字段 → 不要塞进正文。"),
            }),

            new GuideSection("三条通用检查（出图前自问）", new[]
            {
                new GuideItem("**① 只描述看得见的东西了吗？** 删掉无法落地的形容词"
                            + "（高级感、氛围感），换成材质/光线/颜色的具体描述。"),
                new GuideItem("**② 编辑时说清「改什么 + 保什么」了吗？** 缺了「保什么」，"
                            + "模型可能顺手改掉你不希望动的地方。"),
                new GuideItem("**③ 长度合规吗？** OpenAI 上限极宽松；"
                            + "千问约 800 Token（2.0 系 1300）**超出自动截断** → 关键约束放前面。"),
            }),
        });

    /// <summary>
    /// 全部指南（UI 标签页顺序即此顺序）。
    /// **总览在最前**（用户先建立全局认识，再看两个端点的差异）。
    /// </summary>
    public static readonly IReadOnlyList<Guide> All = new[] { Overview, OpenAi, Qwen };
}
