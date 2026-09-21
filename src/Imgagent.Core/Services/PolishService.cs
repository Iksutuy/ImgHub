using System.Text.Json;
using System.Text.RegularExpressions;
using Imgagent.Core.Http;

namespace Imgagent.Core.Services;

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

    public PolishService(HttpJsonClient http) { _http = http; }

    private string EffectiveKey =>
        !string.IsNullOrEmpty(ApiKey)
            ? ApiKey
            : (Environment.GetEnvironmentVariable("IMGAGENT_POLISH_API_KEY") ?? "");

    private string EffectiveBase =>
        (string.IsNullOrEmpty(BaseUrl)
            ? (Environment.GetEnvironmentVariable("IMGAGENT_POLISH_BASE_URL") ?? DefaultBase)
            : BaseUrl).TrimEnd('/');

    private string EffectiveModel =>
        !string.IsNullOrEmpty(Model)
            ? Model
            : (Environment.GetEnvironmentVariable("IMGAGENT_POLISH_MODEL") ?? DefaultModel);

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
        var sys = (kind == "edit" ? SysEditBatch : SysBatch);
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
        var sys = kind == "edit" ? SysSingleEdit : SysSingleGen;
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
                             + "或设环境变量 IMGAGENT_POLISH_API_KEY");

        var payload = new Dictionary<string, object?>
        {
            ["model"] = EffectiveModel,
            ["messages"] = new object[]
            {
                new Dictionary<string, string> { ["role"] = "system", ["content"] = sysText },
                new Dictionary<string, string> { ["role"] = "user", ["content"] = userText },
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
            // 与 DIVIDER 的相似度 ≥ 60% 才认作分隔符（避免误伤 "---END---" 之类）
            if (word.Length == 0) return m.Value;
            int same = 0;
            var target = "DIVIDER";
            for (int i = 0; i < Math.Min(word.Length, target.Length); i++)
                if (word[i] == target[i]) same++;
            double ratio = (double)same / Math.Max(word.Length, target.Length);
            return ratio >= 0.6 ? Divider : m.Value;
        });
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

    // ================================================================ 系统指令
    private static readonly string SysBatch = BuildSysBatch();
    private static readonly string SysEditBatch = BuildSysEditBatch();

    private static string BuildSysBatch()
    {
        var d = Divider;
        return "你是一个图像生成提示词专家。用户给一个想法（可能是简短描述，也可能是完整提示词），"
             + "你要写出 4 条基于用户原意的、完整可用的中文提示词。\n\n"
             + "【核心原则】你的任务是**扩写和优化**用户的原意，不是重新创作：\n"
             + "  - 如果用户已经写了很长的详细描述（>30字），**必须保留用户描述的所有关键内容**"
             + "    （主体、场景、氛围、风格等），只能在其基础上补充细节（光线、构图、材质、镜头等）。\n"
             + "  - 如果用户写得很短（<15字），可以充分发挥补充细节。\n"
             + "  - 禁止替换、删除、忽略用户已描述的内容。用户说「丧尸围攻清朝僵尸」，你的结果里必须有丧尸和清朝僵尸。\n\n"
             + $"直接输出 4 条提示词，中间用下面这行分隔，不要有任何其他文字：\n{d}\n\n"
             + "示例（只看格式，内容另写）：\n"
             + "雨夜中孤独灯塔的水彩插画，暖色台灯映在湿漉漉的岩石上，画面柔和安静\n"
             + $"{d}\n姜黄色猫咪面部写实特写，翠绿眼睛，浅景深虚化背景，影棚柔光\n"
             + $"{d}\n赛博朋克霓虹风格的城市夜景插画，雨夜街道，冷暖对比强烈\n"
             + $"{d}\n黎明山脉的极简线条速写，单线连续勾勒，大量留白\n\n"
             + "要求：\n"
             + "1. 每条提示词直接以内容开头，不要写「提示词1：」「润色结果1」「1.」之类的编号或标签。\n"
             + "2. 4 条各用不同风格：写实摄影 / 插画 / 电影感 / 其他，彼此差异明显。\n"
             + "3. 每条 50~100 字，说清主体、环境、光线、风格、构图，可直接生图。\n"
             + "4. **全部用中文**。\n"
             + "5. 只输出这 4 条和 3 个分隔符，其他什么都不要。\n";
    }

    private static string BuildSysEditBatch()
    {
        var d = Divider;
        return "你是一个图像编辑提示词专家。用户有一张照片要修改，你要写出 4 条完整可用的中文编辑指令。\n\n"
             + "【核心原则】编辑指令必须**保留原图的核心内容**：\n"
             + "  - 用户说「丧尸围攻清朝僵尸」，编辑结果里丧尸和清朝僵尸必须还在。\n"
             + "  - 用户说「把背景换成雪原」，只能改背景，不能换主体、不能加新人物。\n"
             + "  - 如果当前图的提示词里描述了特定主体（如「橘猫」「清朝僵尸」），"
             + "编辑指令必须明确保留该主体。\n\n"
             + $"直接输出 4 条，中间用下面这行分隔，不要有任何其他文字：\n{d}\n\n"
             + "示例（只看格式，内容另写）：\n"
             + "保持主体外观和姿势不变，把背景换成冬季雪原，冷调光线，地面覆盖积雪\n"
             + $"{d}\n保留主体和构图，改为黄昏逆光，暖橙色调，加长影子，空气中有金色微尘\n"
             + $"{d}\n主体不变，替换为柔和米白色影棚背景，加自然投影，商业产品摄影风格\n"
             + $"{d}\n保留主体，整体转为黑白胶片质感，提高对比度，加轻微颗粒\n\n"
             + "要求：\n"
             + "1. 每条直接以内容开头，不要写编号或标签。\n"
             + "2. 4 条各用不同方向：季节 / 光线 / 背景 / 风格 / 天气等。\n"
             + "3. 每条都要明确「保持不变的是什么」和「要改的是什么」。\n"
             + "4. 不要添加新的主体。\n"
             + "5. **全部用中文**。40~70 字一条。只输出这 4 条和 3 个分隔符。\n";
    }

    private const string SysSingleGen =
        "You are an expert prompt engineer for text-to-image models.\n"
        + "Rewrite the user's idea into ONE high-quality image prompt.\n"
        + "Output ONLY the final prompt text. No explanations, no quotes, no markdown.";

    private const string SysSingleEdit =
        "You are an expert prompt engineer for IMAGE-EDITING models.\n"
        + "Rewrite the user's instruction into ONE precise editing prompt.\n"
        + "Output ONLY the instruction text. No explanations, no quotes, no markdown.";
}
