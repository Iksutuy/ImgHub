using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ImgHub.Core.Http;
using ImgHub.Core.Models;

namespace ImgHub.Core.Services;

/// <summary>提示词润色服务。对应 Python 的 <c>polish.py</c>（4 候选批量模式）。</summary>
public interface IPolishService
{
    bool Enabled { get; }
    bool Configured { get; }
    string Describe();

    /// <summary>开关（用户可关）与端点/模型/key 配置（持久化到 config.json / key 文件）。</summary>
    bool SwitchOn { get; set; }
    string BaseUrl { get; set; }
    string Model { get; set; }
    string ApiKey { get; set; }

    /// <summary>
    /// 润色提示词风格：<c>auto</c>（跟随 <see cref="ActiveStyle"/> 指向的生图端点）/
    /// <c>openai</c> / <c>qwen</c>（手动指定）。
    /// </summary>
    string Style { get; set; }

    /// <summary>
    /// 当前**生效的**风格键（<c>openai</c> / <c>qwen</c>）。
    /// 当 <see cref="Style"/> 为 auto 时由 <see cref="ActiveStyle"/> 决定。
    /// </summary>
    string EffectiveStyle { get; }

    /// <summary>
    /// 生图端点的**当前 provider**（用于 auto 选风格）。UI 在切 provider 时写入。
    /// 这不是"润色端点"，而是"要为哪个生图端点优化提示词"。
    /// </summary>
    ApiProvider ActiveStyle { get; set; }

    Task<List<string>> PicksAsync(string prompt, string kind = "gen",
                                  string aspect = "", string subject = "",
                                  CancellationToken ct = default);
    Task<string> RunAsync(string prompt, string kind = "gen", string aspect = "",
                          CancellationToken ct = default);
}

public sealed class PolishService : IPolishService
{
    /// <summary>建议端点（用户未配置时的占位，**不含任何凭据**）。</summary>
    public const string DefaultBase = "https://api.openai.com/v1";
    public const string DefaultModel = "gpt-4o-mini";

    /// <summary>四个选项之间的约定分隔符（LLM 必须原样输出）。</summary>
    public const string Divider = "---DIVIDER---";

    private const int MaxTokens = 3000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private readonly HttpJsonClient _http;

    /// <summary>用户在设置里配置的润色参数（持久化到 config.json / key 文件）。</summary>
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public bool SwitchOn { get; set; } = true;

    /// <summary>润色提示词风格：auto / openai / qwen（见 <see cref="PromptGuide"/>）。</summary>
    public string Style { get; set; } = "auto";

    /// <summary>当前生图 provider（auto 风格时据此选择专用提示词）。</summary>
    public ApiProvider ActiveStyle { get; set; } = ApiProvider.OpenAi;

    /// <summary>
    /// 生效的风格键。auto → 跟随当前生图 provider；手动指定 → 用指定的那套。
    ///
    /// ⚠️ 为什么 auto 要跟随生图 provider 而不是润色端点：
    ///   润色端点（用户自配的 OpenAI 兼容 chat 端点）**和生图画什么模型无关** ——
    ///   它只是"帮我把话写清楚"的工具。真正决定提示词该怎么写的是
    ///   **最终要喂给哪个生图模型**（GPT Image 的八条基本功 vs 千问的 800 Token/单 text 约束）。
    ///   所以默认跟随生图 provider，同时允许用户在浮窗里手动覆盖。
    /// </summary>
    public string EffectiveStyle =>
        (Style ?? "").Trim().ToLowerInvariant() switch
        {
            "openai" => "openai",
            "qwen" or "dashscope" => "qwen",
            // auto（或任何未知值）→ 按生图 provider 判定
            _ => ActiveStyle == ApiProvider.DashScope ? "qwen" : "openai",
        };

    public PolishService(HttpJsonClient http) { _http = http; }

    private string EffectiveKey =>
        !string.IsNullOrEmpty(ApiKey)
            ? ApiKey
            : (Environment.GetEnvironmentVariable("IMGHUB_POLISH_API_KEY") ?? "");

    private string EffectiveBase =>
        (string.IsNullOrEmpty(BaseUrl)
            ? (Environment.GetEnvironmentVariable("IMGHUB_POLISH_BASE_URL") ?? DefaultBase)
            : BaseUrl).TrimEnd('/');

    private string EffectiveModel =>
        !string.IsNullOrEmpty(Model)
            ? Model
            : (Environment.GetEnvironmentVariable("IMGHUB_POLISH_MODEL") ?? DefaultModel);

    private string Endpoint =>
        EffectiveBase.EndsWith("/chat/completions") ? EffectiveBase
                                                    : EffectiveBase + "/chat/completions";

    public bool Configured =>
        EffectiveKey.Length > 0 && EffectiveBase.Length > 0 && EffectiveModel.Length > 0;

    public bool Enabled => SwitchOn && Configured;

    public string Describe() =>
        EffectiveKey.Length == 0
            ? "未配置（设置里填端点/模型/key 后可用）"
            : $"{EffectiveModel} @ {Endpoint}";

    // ================================================================ 公开接口
    public async Task<List<string>> PicksAsync(string prompt, string kind = "gen",
                                               string aspect = "", string subject = "",
                                               CancellationToken ct = default)
    {
        var sys = PickSystem(kind, batch: true);
        var user = BuildUserText(prompt, kind, aspect, subject);
        var raw = await CallAsync(sys, user, temperature: 0.9, ct: ct)
                        .ConfigureAwait(false);
        var outList = new List<string>();
        foreach (var opt in NormalizeDividers(raw).Split(Divider))
        {
            var cleaned = StripLabel(opt);
            if (cleaned.Length > 0) outList.Add(cleaned);
        }
        return outList;
    }

    public async Task<string> RunAsync(string prompt, string kind = "gen",
                                       string aspect = "", CancellationToken ct = default)
    {
        var sys = PickSystem(kind, batch: false);
        return await CallAsync(sys, BuildUserText(prompt, kind, aspect), ct: ct)
                     .ConfigureAwait(false);
    }

    public static string BuildUserText(string prompt, string kind, string aspect = "",
                                       string subject = "")
    {
        if (kind == "edit")
        {
            var ctx = "";
            if (!string.IsNullOrEmpty(subject))
                ctx = $"【当前照片的内容（来自生成该图时的提示词，供你判断主体是什么）】\n{subject}\n\n";
            return $"{ctx}用户的编辑要求：\n{prompt}\n\n"
                 + "生成 4 个不同方向的中文编辑指令。\n"
                 + "重要：主体到底是什么请依据上面给出的照片内容判断"
                 + "（可能是猫、狗、人物、商品、风景…），不要凭空假设成「人物」。\n"
                 + "每条要明确：保持不变的（主体外观/姿势/构图）与要修改的。";
        }
        var extra = string.IsNullOrEmpty(aspect) ? "" : $"\n目标画幅：{aspect}。";
        return $"用户想画的图（简短描述）：\n{prompt}{extra}\n\n"
             + "生成 4 个不同风格的中文提示词（每条要详细、可直接用于生图）。";
    }

    // ================================================================ 核心调用
    private async Task<string> CallAsync(string sysText, string userText,
                                         double temperature = 0.7,
                                         CancellationToken ct = default)
    {
        if (EffectiveKey.Length == 0)
            throw new ApiError("润色未配置：请在设置里填端点/模型/key，"
                             + "或设环境变量 IMGHUB_POLISH_API_KEY");

        // JsonObject：AOT 安全（CONSTRAINTS H1）
        var payload = new JsonObject
        {
            ["model"] = EffectiveModel,
            ["messages"] = new JsonArray
            {
                (JsonNode)new JsonObject { ["role"] = "system", ["content"] = sysText },
                (JsonNode)new JsonObject { ["role"] = "user", ["content"] = userText },
            },
            ["temperature"] = temperature,
            ["max_tokens"] = MaxTokens,
            ["stream"] = false,
        };

        using var doc = await _http.PostJsonAsync(Endpoint, payload, EffectiveKey,
                                                  Timeout, ct).ConfigureAwait(false);
        return PickText(doc.RootElement);
    }

    private static string PickText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new ApiError($"空 choices：{Trunc(root.ToString(), 200)}");

        var ch = choices[0];
        var finish = ch.TryGetProperty("finish_reason", out var fr)
            ? (fr.GetString() ?? "").ToLowerInvariant() : "";
        var text = "";

        if (ch.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in new[] { "content", "reasoning_content", "reasoning", "text" })
            {
                if (msg.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = (v.GetString() ?? "").Trim();
                    if (s.Length > 0) { text = s; break; }
                }
            }
        }
        if (text.Length == 0 && ch.TryGetProperty("text", out var t) &&
            t.ValueKind == JsonValueKind.String)
            text = (t.GetString() ?? "").Trim();

        if (text.Length == 0)
        {
            if (finish == "length")
                throw new ApiError("模型把 token 全用在思考上了，正文没写出来。"
                                 + $"请调大润色 max_tokens（当前={MaxTokens}）");
            throw new ApiError($"模型返回空内容（finish_reason={(finish.Length > 0 ? finish : "?")}）");
        }
        return Clean(text);
    }

    // ================================================================ 清洗
    private static readonly Regex LabelRe = new(
        @"^\s*(?:\[|【)?\s*(?:润色结果|编辑方向|提示词|版本|方案|风格|选项|prompt|option|variant|version|result)\s*[A-Za-z0-9一二三四五]*\s*(?:\]|】)?\s*[:：.、\)\-—–]?\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NumRe = new(
        @"^\s*(?:\(|（|\[)?\s*(?:第)?\s*[1-9一二三四五]\s*(?:\)|）|\]|\.|、|:|：)?\s+",
        RegexOptions.Compiled);

    /// <summary>
    /// 归一化 LLM 输出的分隔符。
    /// LLM 经常把 `---DIVIDER---` 拼错/变形（实测：`---DIDIER---`、`---DIVDER---`、
    /// `--DIVIDER--`），直接 Split 会漏分 → 两条候选黏成一条。
    /// 策略：找到所有形如 `---Xxxx---` 的行（横线包裹、中间有字母），
    /// 若与 Divider 的编辑距离足够近（≥60% 字符相同），替换为标准 Divider。
    /// </summary>
    public static string NormalizeDividers(string text)
    {
        // 匹配被横线包裹的分隔符行（至少 2 根横线 + 中间字母）
        var re = new Regex(
            @"-{2,}\s*([A-Za-z][A-Za-z\s]{2,15}?)\s*-{2,}",
            RegexOptions.Compiled);
        return re.Replace(text, m =>
        {
            var word = m.Groups[1].Value.Trim().ToUpperInvariant().Replace(" ", "");
            if (word.Length == 0) return m.Value;
            // 与 DIVIDER 的相似度 ≥ 60% 才认作分隔符（避免误伤 "---END---" 之类）
            // 用 Levenshtein 编辑距离（对插入/删除/替换都稳健）；
            // 旧实现逐位比较，对 DIDIER（一处字母序错乱）只有 57% 相同而漏判。
            double sim = Similarity(word, "DIVIDER");
            return sim >= 0.6 ? Divider : m.Value;
        });
    }

    /// <summary>两字符串的相似度（0~1），基于 Levenshtein 编辑距离。</summary>
    public static double Similarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 1.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        return 1.0 - (double)d[a.Length, b.Length] / Math.Max(a.Length, b.Length);
    }

    /// <summary>剥掉行首的编号/标签（模型常无视指令照抄示例标签）。</summary>
    public static string StripLabel(string line)
    {
        var o = line.Trim();
        for (int i = 0; i < 3; i++)
        {
            var before = o;
            o = LabelRe.Replace(o, "").Trim();
            o = NumRe.Replace(o, "").Trim();
            if (o == before) break;
        }
        return o;
    }

    public static string Clean(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("```"))
        {
            var lines = t.Split('\n').ToList();
            if (lines.Count >= 2) lines.RemoveAt(0);
            if (lines.Count > 0 && lines[^1].TrimStart().StartsWith("```"))
                lines.RemoveAt(lines.Count - 1);
            t = string.Join("\n", lines).Trim();
        }
        string[] prefixes =
        {
            "Here is the prompt:", "Here is the final prompt:", "Prompt:",
            "Here is the rewritten prompt:", "Sure, here is", "Sure! Here is",
            "润色后的提示词：", "提示词：",
        };
        foreach (var pre in prefixes)
        {
            if (t.StartsWith(pre, StringComparison.OrdinalIgnoreCase))
            {
                t = t[pre.Length..].Trim();
                break;
            }
        }
        if (t.Length >= 2 && "\"'“”".Contains(t[0]) && "\"'“”".Contains(t[^1]))
            t = t[1..^1].Trim();
        t = StripLabel(t);
        return string.Join(" ", t.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Trunc(string s, int n) => s.Length > n ? s[..n] : s;

    // ================================================================ 按端点分派（v0.5.31）
    /// <summary>
    /// 取当前生效端点（<see cref="EffectiveStyle"/>）的专用 system prompt。
    ///
    /// ⚠️ 为什么必须分端点（而不是一套通用提示词）：
    ///   两个端点的"好提示词"写法**不一样**，一套通用规则必然在某一边出错：
    ///     · OpenAI（docs/GPT Image prompting.md）：强调
    ///       「先定义结果 / 只描述看得见的 / 要文字就加引号 / 编辑要分开写"改什么·保什么"」；
    ///     · 千问（docs/千问-*.md）：有**协议级硬约束** ——
    ///       单轮单 text（多条会报错）、长度 800/1300 Token 超了自动截断、
    ///       仅中英文可靠、多图顺序以最后一张定比例、"不要 XX" 要走 negative_prompt。
    ///       这些约束必须在生成提示词时就照顾到，否则用户拿到的提示词会被截断或直接报错。
    /// </summary>
    private string PickSystem(string kind, bool batch) => EffectiveStyle switch
    {
        "qwen" => batch
            ? (kind == "edit" ? SysQwenEditBatch : SysQwenGenBatch)
            : (kind == "edit" ? SysQwenSingleEdit : SysQwenSingleGen),
        _ => batch
            ? (kind == "edit" ? SysOpenAiEditBatch : SysOpenAiGenBatch)
            : (kind == "edit" ? SysOpenAiSingleEdit : SysOpenAiSingleGen),
    };

    // ---------------------------------------------------------------- OpenAI（GPT Image）
    /// <summary>
    /// OpenAI 版（4 候选 · 文生图）。逐条对应 docs/GPT Image prompting.md
    /// 的 "Prompting fundamentals" 八条。
    /// </summary>
    private static readonly string SysOpenAiGenBatch = BuildOpenAiGenBatch();

    private static string BuildOpenAiGenBatch()
    {
        var d = Divider;
        return "你是 **GPT Image 系（OpenAI 官方）** 的图像提示词专家。"
             + "用户给一个想法（可能很短，也可能是完整描述），你要写出 4 条可直接生图的中文提示词。\n\n"
             + "【必须遵守 OpenAI 官方 prompting 指南的做法】\n"
             + "1. 先定义结果：点明主体与用途（商品图/海报/插画/信息图），写清构图、画幅、关键位置约束。\n"
             + "2. 只描述「看得见」的东西：材质、光线、颜色、媒介、取景与质感。\n"
             + "3. 要写实就明确写「写实照片 / photorealistic」，不要只堆情绪词。\n"
             + "4. 有人物就交代取景与动作（全身可见含脚 / 低头看书 / 手握着把手）。\n"
             + "5. 若需要画面中出现确切文字，**把文字放进引号**并说明位置与字体，"
             + "并补一句「不要出现其他文字」。\n"
             + "6. 复杂需求按「场景 → 主体 → 细节 → 约束」分段组织，而不是把形容词堆成一团。\n\n"
             + "【核心原则】你的任务是**扩写并优化**用户原意，不是重新创作：\n"
             + "  - 用户已写得很详细（>30字）→ **必须保留其全部关键内容**（主体/场景/氛围/风格），"
             + "只在其基础上补充光线、构图、材质、镜头等细节。\n"
             + "  - 用户写得很短（<15字）→ 可充分补充细节。\n"
             + "  - **禁止**替换、删除、忽略用户已描述的内容。用户说「丧尸围攻清朝僵尸」，"
             + "结果里就必须有丧尸和清朝僵尸。\n\n"
             + $"直接输出 4 条提示词，中间用下面这行分隔，不要有任何其他文字：\n{d}\n\n"
             + "示例（只看粒度与格式，内容另写）：\n"
             + "写实照片：雨夜中孤独的灯塔，暖色台灯映在湿漉漉的岩石上，低角度构图，"
             + "冷蓝环境光与暖色灯光形成对比\n"
             + $"{d}\n水彩插画：姜黄色猫咪面部特写，翠绿眼睛，浅景深虚化背景，柔和的影棚光线\n"
             + $"{d}\n电影感画面：赛博朋克霓虹城市夜景，雨夜街道，冷暖对比强烈，"
             + "广角镜头，地面积水反射招牌光\n"
             + $"{d}\n极简线条速写：黎明山脉，单线连续勾勒，大量留白，黑白\n\n"
             + "要求：\n"
             + "1. 每条直接以内容开头，不要写「提示词1：」「润色结果1」「1.」之类的编号或标签。\n"
             + "2. 4 条各用不同风格：写实摄影 / 插画 / 电影感 / 其他，彼此差异明显。\n"
             + "3. 每条 50~100 字，说清主体、环境、光线、风格、构图。\n"
             + "4. **全部用中文**。\n"
             + "5. 只输出这 4 条和 3 个分隔符，其他什么都不要。\n";
    }

    /// <summary>OpenAI 版（4 候选 · 编辑）：对应官方 "Separate changes from constraints" 与
    /// "Preserve identity and change clothing" 示例的结构。</summary>
    private static readonly string SysOpenAiEditBatch = BuildOpenAiEditBatch();

    private static string BuildOpenAiEditBatch()
    {
        var d = Divider;
        return "你是 **GPT Image 系（OpenAI 官方）** 的图像**编辑**提示词专家。"
             + "用户有一张照片要改，你要写出 4 条可直接使用的中文编辑指令。\n\n"
             + "【必须遵守 OpenAI 官方对编辑的要求】官方反复强调："
             + "**把「要改的」和「要保的」分开写清楚**，并声明排除项。每条指令都要包含：\n"
             + "  · change only X：明确只改这一件事；\n"
             + "  · 逐项列出要**保持不变**的（主体身份/面部特征/体型/姿势/表情/发型/几何/布局/光线/标签）；\n"
             + "  · 让新内容与原图**匹配光线、阴影、色温**，避免「贴上去」的观感；\n"
             + "  · 声明不要加的（多余文字、logo、水印）。\n\n"
             + "【核心原则】编辑指令必须**保留原图的核心内容**：\n"
             + "  - 用户说「把背景换成雪原」→ 只能改背景，不能换主体、不能加新人物。\n"
             + "  - 若当前图的提示词里描述了特定主体（如「橘猫」「清朝僵尸」），"
             + "指令必须明确保留该主体。\n\n"
             + $"直接输出 4 条，中间用下面这行分隔，不要有任何其他文字：\n{d}\n\n"
             + "示例（只看结构与粒度，内容另写）：\n"
             + "只把背景换成冬季雪原：保持主体外观、姿势与构图完全不变，"
             + "冷调光线，地面覆盖积雪，让新背景与原图的光线方向和色温匹配，"
             + "不要添加任何文字、logo 或水印\n"
             + $"{d}\n只替换背景为柔和米白色影棚背景：主体与投影位置保持不变，"
             + "补充自然的落地阴影，商业产品摄影风格，不改变相机角度与取景\n"
             + $"{d}\n只调整影调为黑白胶片质感：主体、构图、背景内容均不变，"
             + "提高对比度，加轻微颗粒，保留原有光线方向\n"
             + $"{d}\n只更换服装：面部特征、发型、肤色、体型、姿势全部保持不变，"
             + "新衣服自然贴合现有姿势与身体几何，布料垂坠真实，"
             + "匹配原有光线、阴影与色温，不改背景与画质，不加配饰\n\n"
             + "要求：\n"
             + "1. 每条直接以内容开头，不要写编号或标签。\n"
             + "2. 4 条各用不同方向：季节 / 光线 / 背景 / 风格 / 天气等。\n"
             + "3. 每条 40~80 字，都要明确「保持不变的是什么」和「要改的是什么」。\n"
             + "4. 不要添加新的主体。\n"
             + "5. **全部用中文**。只输出这 4 条和 3 个分隔符。\n";
    }

    private const string SysOpenAiSingleGen =
        "You are an expert prompt engineer for OpenAI GPT Image models.\n"
        + "Follow the official prompting fundamentals: define the intended result and use, "
        + "describe only visible details (materials, lighting, colors, medium), "
        + "state framing and action for people, and put any required on-image text in quotes "
        + "together with its position and typography.\n"
        + "Expand and refine the user's intent; never drop or replace what they already described.\n"
        + "Rewrite the user's idea into ONE high-quality image prompt, in Chinese.\n"
        + "Output ONLY the final prompt text. No explanations, no quotes, no markdown.";

    private const string SysOpenAiSingleEdit =
        "You are an expert prompt engineer for OpenAI GPT Image IMAGE-EDITING.\n"
        + "Always separate what changes from what must stay: say 'change only X', "
        + "list the details to preserve (identity, facial features, body shape, pose, "
        + "geometry, layout, lighting, labels), ask the new content to match the original "
        + "lighting/shadows/color temperature, and state exclusions "
        + "(no extra text, logos, or watermarks).\n"
        + "Rewrite the user's instruction into ONE precise editing prompt, in Chinese.\n"
        + "Output ONLY the instruction text. No explanations, no quotes, no markdown.";

    // ---------------------------------------------------------------- 千问（Qwen-Image / DashScope）
    /// <summary>
    /// 千问版（4 候选 · 文生图）。逐条对应 docs/千问-*.md 的**协议硬约束**
    /// （单 text、长度上限、仅中英文、negative_prompt 独立字段）。
    /// </summary>
    private static readonly string SysQwenGenBatch = BuildQwenGenBatch();

    private static string BuildQwenGenBatch()
    {
        var d = Divider;
        return "你是 **千问 Qwen-Image（阿里云百炼）** 的图像提示词专家。"
             + "用户给一个想法，你要写出 4 条可直接生图的中文提示词。\n\n"
             + "【千问的协议硬约束 —— 必须照顾到，否则用户拿去用会被截断或报错】\n"
             + "1. **只支持中文与英文**：其他语言效果不确定 → 一律输出中文。\n"
             + "2. **长度有上限**：文生图 2.0 系 1300 Token，其余约 800 Token，"
             + "**超出会被自动截断** → 宁可精炼，别堆修饰词把关键信息挤掉。\n"
             + "3. **只能有一段正文**：不要把内容拆成多条或以列表输出（协议只收一个 text）。\n"
             + "4. **「不要出现的」不要写进正文**：那是 negative_prompt 字段的职责"
             + "（常用值：低分辨率，低画质，肢体畸形，画面过饱和，人脸无细节，过度光滑，AI 感）。\n\n"
             + "【写法要点】\n"
             + "  · 主体 + 场景 + 风格 + 质感 + 光线，按顺序写清楚，颗粒度参考千问官方示例；\n"
             + "  · 需要画面文字时，把**文字内容、字体风格、位置、大小占比**都写全"
             + "（官方示例会写到「约占画面高度1/10」这种精度）；\n"
             + "  · 写实需求要强调「真实质感/写实摄影」，千问对语义遵循与文字渲染较强。\n\n"
             + "【核心原则】**扩写并优化**用户原意，不是重新创作：\n"
             + "  - 用户已写详细内容 → 必须全部保留，只补充细节；\n"
             + "  - 用户写得很短 → 可充分补充；\n"
             + "  - **禁止**替换、删除、忽略用户已描述的内容。\n\n"
             + $"直接输出 4 条提示词，中间用下面这行分隔，不要有任何其他文字：\n{d}\n\n"
             + "示例（只看粒度与格式，内容另写）：\n"
             + "写实摄影：雨后黄昏的青石桥畔，一位素手拈花的女子闭目而立，"
             + "水墨淡雅的江南氛围，柔和侧逆光，中景构图，浅景深\n"
             + $"{d}\n水彩插画：姜黄色猫咪面部特写，翠绿眼睛，虚化背景，柔和影棚光线，细腻毛发质感\n"
             + $"{d}\n竖幅人像：都市职场女性半身像，香槟色真丝衬衫配深灰西装，"
             + "高端咖啡店内，午后侧向自然光，背景景深虚化，画面清晰细腻\n"
             + $"{d}\n信息图：一杯手冲咖啡的完整制作流程，从上到下依次标注研磨、注水、萃取，"
             + "扁平化插画风格，米白底色，线条简洁\n\n"
             + "要求：\n"
             + "1. 每条直接以内容开头，不要写编号或标签。\n"
             + "2. 4 条各用不同风格/方向，彼此差异明显。\n"
             + "3. 每条 40~90 字（照顾长度上限）。\n"
             + "4. **全部用中文**。只输出这 4 条和 3 个分隔符。\n";
    }

    /// <summary>千问版（4 候选 · 编辑）：对应 docs/千问-图像编辑API参考.md
    /// 的官方示例结构（指定输入图角色 → 禁止改动项 → 再描述要改的）。</summary>
    private static readonly string SysQwenEditBatch = BuildQwenEditBatch();

    private static string BuildQwenEditBatch()
    {
        var d = Divider;
        return "你是 **千问 Qwen-Image Edit（阿里云百炼）** 的图像**编辑**提示词专家。"
             + "用户有一张（或多张）图要改，你要写出 4 条可直接使用的中文编辑指令。\n\n"
             + "【千问编辑的协议硬约束】\n"
             + "1. **只支持中文与英文** → 一律输出中文。\n"
             + "2. **编辑模型上限约 800 Token，超出自动截断** → 精炼，把关键约束写在前面。\n"
             + "3. **一条指令 = 一段连续描述**：协议「仅支持传入一个 text，不传或传入多个将报错」，"
             + "所以不要拆成多条、不要用编号列表。\n"
             + "4. **多图输入时输出比例以最后一张为准** → 若有多张参考图，"
             + "指令里要说明每张图的角色与顺序含义。\n"
             + "5. 「不要出现的」留给 negative_prompt 字段，不要塞进正文。\n\n"
             + "【千问官方示例的写法结构 —— 照这个骨架写】\n"
             + "  ① 先指定每张输入图的角色（如「使用图一的城市照片作为底图」）；\n"
             + "  ② 明确**禁止改动**的部分（如「请勿更改照片中的真实建筑、街道、车辆或人物。"
             + "保持照片的真实性。」）；\n"
             + "  ③ 再描述要加入/修改的内容，连同**画法、风格、位置、大小**一起写清。\n\n"
             + "【核心原则】必须**保留原图的核心内容**：\n"
             + "  - 用户说「把背景换成雪原」→ 只能改背景，不能换主体、不能加新人物；\n"
             + "  - 原图主体（如「橘猫」「清朝僵尸」）必须在指令里被点名保留。\n\n"
             + $"直接输出 4 条，中间用下面这行分隔，不要有任何其他文字：\n{d}\n\n"
             + "示例（只看结构与粒度，内容另写）：\n"
             + "只更换背景为冬季雪原：保持主体的外观、姿势与构图完全不变，"
             + "冷调光线，地面覆盖积雪，画面真实自然，不添加任何文字或水印\n"
             + $"{d}\n只替换背景为柔和米白色影棚背景：主体与投影位置保持不变，"
             + "补充自然的落地阴影，商业产品摄影风格，不改变相机角度与取景\n"
             + $"{d}\n只调整影调为黑白胶片质感：主体、构图与背景内容均不变，"
             + "提高对比度，加入轻微颗粒，保留原有光线方向\n"
             + $"{d}\n在画面右下角加入一行白色手写体文字「江南」，"
             + "行楷风格笔触自然，约占画面高度1/12，位置与原图留白协调，"
             + "除此之外画面其余部分完全不变\n\n"
             + "要求：\n"
             + "1. 每条直接以内容开头，不要写编号或标签。\n"
             + "2. 4 条各用不同方向：季节 / 光线 / 背景 / 风格 / 文字等。\n"
             + "3. 每条 40~70 字，都要明确「保持不变的是什么」和「要改的是什么」。\n"
             + "4. 不要添加新的主体。\n"
             + "5. **全部用中文**。只输出这 4 条和 3 个分隔符。\n";
    }

    private const string SysQwenSingleGen =
        "You are an expert prompt engineer for Qwen-Image (Alibaba Cloud DashScope).\n"
        + "Hard protocol constraints you must respect: only Chinese or English is reliably "
        + "supported, and the text has a length cap (about 800 tokens; 1300 for the 2.0 series) "
        + "beyond which it is silently truncated — so be precise, not verbose. "
        + "Output exactly ONE paragraph (the API accepts only a single text). "
        + "Do not put 'avoid X' items in the prompt body; those belong to negative_prompt.\n"
        + "Expand and refine the user's intent without dropping anything they described.\n"
        + "Rewrite the user's idea into ONE high-quality image prompt, in Chinese.\n"
        + "Output ONLY the final prompt text. No explanations, no quotes, no markdown.";

    private const string SysQwenSingleEdit =
        "You are an expert prompt engineer for Qwen-Image Edit (Alibaba Cloud DashScope).\n"
        + "Hard protocol constraints: only Chinese or English is reliably supported; the edit "
        + "text has a length cap (about 800 tokens) and is truncated beyond it; the API accepts "
        + "ONLY ONE text, so write a single continuous instruction (never a numbered list). "
        + "When multiple input images are involved, remember the output aspect ratio follows the "
        + "LAST image, so state each image's role and order. Keep 'avoid X' in negative_prompt.\n"
        + "Follow the official example structure: name each input image's role first, then state "
        + "explicitly what must NOT change, then describe what to add or modify together with its "
        + "style, position and size.\n"
        + "Rewrite the user's instruction into ONE precise editing prompt, in Chinese.\n"
        + "Output ONLY the instruction text. No explanations, no quotes, no markdown.";
}
