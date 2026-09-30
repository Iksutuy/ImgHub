using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ImgHub.Core;
using ImgHub.Core.Diagnostics;
using ImgHub.Core.Models;

namespace ImgHub.Core.Storage;

/// <summary>
/// 本地存储：配置 + 历史 + 提示词历史 + 图片落盘。
/// 对应 Python 的 <c>store.py</c>（Session + 各 jsonl）。
///
/// 与 Python 版差异：数据目录由调用方注入（<see cref="HomePath"/>），
/// 不再用"探测一堆候选路径"的做法 —— GUI 端由平台层给标准目录。
/// </summary>
public sealed class Session
{
    // ⚠️ AOT 约束（CONSTRAINTS H1）：固定结构走 source-gen 的 AppJson；
    //    动态结构走 JsonObject + JsonSafe.ToReadableJson()。
    //    **不再使用** JsonSerializer 的反射重载（AOT 下会抛异常）。

    public string HomePath { get; private set; }

    /// <summary>
    /// 配置/状态写锁：FlushConfig（UI 线程）与节流 Timer 回调（线程池）
    /// 可能并发调用 SaveConfig/SaveState，Windows 上 File.Move 并发会抛 IOException。
    /// </summary>
    private readonly object _writeLock = new();

    /// <summary>
    /// 历史项（<c>[0]</c> = 当前）。**线程安全**（P0-4）：见 <see cref="ItemStore"/>。
    /// 并发写入来自 <c>ResumePendingAsync</c> 等 <c>ConfigureAwait(false)</c> 链路（线程池线程），
    /// 并发读取来自 UI 线程刷新历史 → 裸 List 会抛「集合被修改」并可能丢行。
    /// </summary>
    public ItemStore Items { get; } = new();
    public AppConfig Config { get; private set; } = new();
    /// <summary>
    /// 累计花费（USD）。**单一真源 = Items 求和**。
    /// </summary>
    /// <remarks>
    /// 历史问题：曾单独累加 API 返回的 res.Cost，而 Items[].Cost 是均摊值
    /// （res.Cost / N），两者天然不等 → 界面"总累计"与历史求和不一致。
    /// 现在读取时一律从 Items 求和（setter 保留仅为兼容旧调用）。
    /// </remarks>
    private double _totalCostOverride;
    public double TotalCost
    {
        get
        {
            if (Items.Count == 0) return _totalCostOverride;
            double sum = 0;
            foreach (var it in Items) sum += it.Cost;
            return sum;
        }
        set => _totalCostOverride = value;
    }
    public int Counter { get; set; }

    public Session(string homePath)
    {
        HomePath = homePath;
        Directory.CreateDirectory(HomePath);
        Load();
    }

    public Item? Current => Items.Count > 0 ? Items[0] : null;

    /// <summary>按 provider 累计花费。**与 TotalCost 同源**（都是 Items 求和）。</summary>
    /// <remarks>
    /// 旧数据无 provider 字段时归入 <see cref="UnknownProvider"/> 组，
    /// **不再**按"当前 provider"计入（否则切换 provider 时数字漂移）。
    /// </remarks>
    public double CostForProvider(ApiProvider provider)
    {
        var key = provider.Key();
        double sum = 0;
        foreach (var it in Items)
        {
            if (string.Equals(it.Provider, key, StringComparison.OrdinalIgnoreCase))
                sum += it.Cost;
        }
        return sum;
    }

    /// <summary>无 provider 标记的旧数据（金额计入此组）。</summary>
    public const string UnknownProvider = "unknown";

    /// <summary>无 provider 标记的旧数据累计（UI 可提示"未归类"）。</summary>
    public double CostForUnknownProvider()
    {
        double sum = 0;
        foreach (var it in Items)
        {
            if (string.IsNullOrEmpty(it.Provider)) sum += it.Cost;
        }
        return sum;
    }

    /// <summary>按 provider 分组统计（provider -> 金额）。</summary>
    public Dictionary<string, double> CostByProvider()
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in Items)
        {
            var key = string.IsNullOrEmpty(it.Provider) ? UnknownProvider : it.Provider;
            map[key] = map.GetValueOrDefault(key) + it.Cost;
        }
        return map;
    }
    public ApiProvider Provider =>
        ApiProviderExtensions.Parse(Config.Provider) ?? ApiProvider.OpenRouter;

    // ---------------------------------------------------------------- 文件路径
    public string ConfigFile => Path.Combine(HomePath, "config.json");
    public string StateFile => Path.Combine(HomePath, "state.json");
    public string HistoryLog => Path.Combine(HomePath, "history.jsonl");
    public string PromptHistoryFile => Path.Combine(HomePath, "prompt_history.jsonl");
    public string KeyFile(ApiProvider p) => Path.Combine(HomePath, p switch
    {
        ApiProvider.Apimart => ".imghub_apimart_key",
        ApiProvider.OpenAi => ".imghub_openai_key",
        ApiProvider.DashScope => ".imghub_dashscope_key",
        ApiProvider.Jimeng => ".imghub_jimeng_key",
        _ => ".imghub_key",
    });
    public string PolishKeyFile => Path.Combine(HomePath, ".imghub_polish_key");

    /// <summary>
    /// 即梦的**第二把密钥**文件（SecretAccessKey）。
    ///
    /// ⚠️ 为什么单独一个文件而不是塞进同一文件：即梦要 **AK + SK 两把**，
    ///   而其余 provider 都是单 key。把两个值拼进一个文件会让
    ///   <see cref="ReadKeyFileCompat"/> 的读取逻辑（trim 后当单值用）出错；
    ///   分成两个文件则完全复用现有读写/权限/兼容机制。
    /// </summary>
    public string SecretKeyFile(ApiProvider p) => Path.Combine(HomePath, p switch
    {
        ApiProvider.Jimeng => ".imghub_jimeng_secret",
        _ => ".imghub_secret",   // 其它 provider 用不到，仅保持路径统一
    });

    /// <summary>
    /// 改名前的 key 文件名（v5.28.0 由 imgagent 改为 ImgHub）。
    /// 仅在**新文件不存在**时回退读取 —— 改名不该让用户已保存的 key 失效。
    /// 新增的 provider（OpenAI/千问/即梦）在改名之前并不存在，本就没有对应的旧文件；
    /// 仍返回旧式路径只是让回退逻辑保持统一（文件不存在自然不会读到）。
    /// </summary>
    public string LegacyKeyFile(ApiProvider p) => Path.Combine(HomePath, p switch
    {
        ApiProvider.Apimart => ".imgagent_apimart_key",
        ApiProvider.OpenAi => ".imgagent_openai_key",
        ApiProvider.DashScope => ".imgagent_dashscope_key",
        ApiProvider.Jimeng => ".imgagent_jimeng_key",
        _ => ".imgagent_key",
    });
    public string LegacyPolishKeyFile => Path.Combine(HomePath, ".imgagent_polish_key");

    /// <summary>
    /// 某 provider 的生图端点：配置里有覆盖就用它，否则用官方默认。
    ///
    /// ⚠️ 为什么必须做成方法而不是常量（v0.5.31）：
    ///   千问 DashScope 的官方推荐端点是**业务空间专属域名**
    ///   <c>https://{WorkspaceId}.cn-beijing.maas.aliyuncs.com</c>，其中 WorkspaceId 属于
    ///   用户账号，程序无法预置；只有让用户填进配置这一条路。
    ///   OpenAI 官方 / OpenRouter 也支持自定义（如企业代理网关）。
    /// </summary>
    public string BaseUrlFor(ApiProvider p)
    {
        var map = Config.BaseUrls;
        if (map is not null && map.TryGetValue(p.Key(), out var v) &&
            !string.IsNullOrWhiteSpace(v))
            return v.Trim();

        // 兼容旧字段：早期只有 OpenRouter 用过单值 base_url_override
        if (p == ApiProvider.OpenRouter && !string.IsNullOrWhiteSpace(Config.BaseUrlOverride))
            return Config.BaseUrlOverride.Trim();

        return Catalog.BaseUrlDefault(p);
    }

    /// <summary>写入某 provider 的端点覆盖（空值 = 清除，回到官方默认）。</summary>
    public void SetBaseUrl(ApiProvider p, string? url)
    {
        Config.BaseUrls ??= new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(url)) Config.BaseUrls.Remove(p.Key());
        else Config.BaseUrls[p.Key()] = url.Trim();
    }

    /// <summary>
    /// 读 key 文件：优先新名，新名不存在则回退旧名（兼容改名前的用户数据）。
    /// </summary>
    public static string ReadKeyFileCompat(string newPath, string legacyPath)
    {
        try
        {
            if (File.Exists(newPath)) return File.ReadAllText(newPath).Trim();
            if (File.Exists(legacyPath))
            {
                var v = File.ReadAllText(legacyPath).Trim();
                AppLog.Info($"沿用改名前的 key 文件：{Path.GetFileName(legacyPath)}",
                            "Session.ReadKeyFileCompat");
                return v;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("读取 key 文件失败", "Session.ReadKeyFileCompat", ex: ex);
        }
        return "";
    }

    // ---------------------------------------------------------------- 读
    public void Load()
    {
        LoadConfig();
        LoadState();
        Sanitize();
        LoadKeysFromDisk();
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return;
            // source-gen（AOT 安全）
            var cfg = JsonSerializer.Deserialize(
                File.ReadAllText(ConfigFile), AppJson.Default.AppConfig);
            if (cfg is not null) Config = cfg;
        }
        catch (Exception ex)
        {
            // 损坏配置不阻断启动，但必须留痕（用户要求：写清原因与位置）
            AppLog.Warn("config.json 读取失败，使用默认配置", "Session.LoadConfig", ex: ex);
        }
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(StateFile));
            var root = doc.RootElement;
            if (root.TryGetProperty("counter", out var c)) Counter = c.GetInt32();
            // ⚠️ 关键：total_cost 必须恢复，否则重启后累计显示归零（真机 bug）
            if (root.TryGetProperty("total_cost", out var tc) &&
                tc.ValueKind == JsonValueKind.Number)
                TotalCost = tc.GetDouble();
            if (root.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                foreach (var raw in items.EnumerateArray())
                {
                    var it = new Item
                    {
                        File = raw.TryGetProperty("file", out var f) ? (f.GetString() ?? "") : "",
                        Prompt = raw.TryGetProperty("prompt", out var p) ? (p.GetString() ?? "") : "",
                        Kind = raw.TryGetProperty("kind", out var k) ? (k.GetString() ?? "gen") : "gen",
                        Model = raw.TryGetProperty("model", out var m) ? (m.GetString() ?? "") : "",
                        Quality = raw.TryGetProperty("quality", out var q) ? (q.GetString() ?? "") : "",
                        Cost = raw.TryGetProperty("cost", out var co) ? co.GetDouble() : 0,
                        Tokens = raw.TryGetProperty("tokens", out var tk) ? tk.GetInt32() : 0,
                        Provider = raw.TryGetProperty("provider", out var pv) ? (pv.GetString() ?? "") : "",
                    };
                    // 只保留文件仍存在的
                    if (it.File.Length > 0 && File.Exists(it.Path(HomePath)))
                        Items.Add(it);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("state.json 读取失败，历史仅产生于本次会话",
                        "Session.LoadState", ex: ex);
        }
    }

    private void LoadKeysFromDisk()
    {
        // 环境变量优先；否则读文件（由平台层/调用方决定是否用安全存储）
        // 兼容：改名前的 IMGAGENT_POLISH_API_KEY 仍可识别
        var pk = Environment.GetEnvironmentVariable("IMGHUB_POLISH_API_KEY")
              ?? Environment.GetEnvironmentVariable("IMGAGENT_POLISH_API_KEY");
        if (!string.IsNullOrEmpty(pk)) PolishKey = pk;
        else PolishKey = ReadKeyFileCompat(PolishKeyFile, LegacyPolishKeyFile);
    }

    public string PolishKey { get; set; } = "";

    /// <summary>纠正配置文件与 provider/模型不一致的字段（CONSTRAINTS D5）。</summary>
    public void Sanitize()
    {
        // JSON 允许显式 null（例如 model:null），先归一化，防止启动闪退。
        Config.Model ??= "";
        Config.Quality ??= "auto";
        Config.Aspect ??= "1:1";
        Config.Resolution ??= "1k";
        Config.OutputFormat ??= "png";
        Config.Provider ??= "openrouter";
        Config.PolishBaseUrl ??= "";
        Config.PolishModel ??= "";

        var p = Provider;
        if (!Catalog.QualitySupported(p, Config.Quality, Config.Model))
            Config.Quality = "auto";
        if (!Catalog.ModelMatchesProvider(Config.Model, p))
            Config.Model = Catalog.DefaultModel(p);
        if (!Catalog.QualitySupported(p, Config.Quality, Config.Model))
            Config.Quality = "auto";
        // ⚠️ Resolution 在本项目里**内部统一小写**（1k/2k/4k），只在发往 OpenRouter 时才由
        //    Catalog.OpenRouterResolution 映射成大写的 512/1K/2K/4K。
        //    所以这里**不能用** ResolutionChoicesFor（那是"UI 下拉里显示什么"的集合，含大写），
        //    否则历史配置里合法的 "2k" 会被误判成非法并被重置（回归过）。
        //    只需判断：该 provider 有没有"分辨率档"这个概念（OpenAI/千问没有）。
        var resChoices = Catalog.ResolutionChoicesFor(p, Config.Model);
        bool hasTier = resChoices.Any(x => !string.Equals(x, "1k", StringComparison.OrdinalIgnoreCase))
                       || resChoices.Length > 1;
        if (!hasTier || !Catalog.Resolutions.Contains(Config.Resolution))
            Config.Resolution = "1k";
        if (!Catalog.OutputFormatChoices(p, Config.Model).Contains(Config.OutputFormat))
            Config.OutputFormat = "png";
        // ⚠️ 同理，n 上限必须取"当前 provider + 模型"：OpenAI 的 dall-e-3 只能 n=1、
        //    千问的 max/plus 固定 1 张。写死 4 会让用户切过去后仍带着旧 batch_n → 服务端拒绝。
        if (Config.BatchN < 1 || Config.BatchN > Catalog.MaxNFor(p, Config.Model))
            Config.BatchN = 1;
        // ⚠️ 这里**不**做画幅收窄：画幅的合法集合随模型变化（见 Catalog.ModelAspects），
        //    而"什么时候该收窄"已有专门的地方负责 —— UI 切换 provider/模型时走
        //    MainViewModel.ClampParamsToCapabilities。若在这里也收窄，历史配置会在
        //    用户毫无操作的情况下被静默改写（例如 gpt-image-1-mini 的 16:9 → 1:1），
        //    既违背"配置读取应当无损"的预期，也让排查变得困难。
        Config.BaseUrlOverride ??= "";
        Config.PolishStyle ??= "auto";
        Config.BaseUrls ??= new Dictionary<string, string>();
        SanitizeParamPresets();
    }

    /// <summary>
    /// 清理「按端点记参数」里的**坏快照**（v0.5.31）。
    ///
    /// 只删两类明确无意义的项，**不动**其他用户数据：
    ///   ① **键与模型不匹配**的项（如 <c>apimart|openai/gpt-image-2.5-flare</c> ——
    ///      APIMart 不可能有带斜杠的 OpenRouter 模型名）。这类键是早期版本切换时
    ///      记错 provider 产生的，留着只会在下拉集合变化时被读到、产生困惑；
    ///   ② **完全无信息**的项（所有关键字段都空 —— 早期 ComboBox 回写 null 的产物）。
    ///      ⚠️ 判据是"**全部**字段都空"而不是"Aspect 为空"：用户可能只改了一个下拉，
    ///        那种最小快照是**合法**的，过严的判据会把它删掉（回归过）。
    ///
    /// ⚠️ 为什么要自愈而不是不管：用户已经落盘的 config.json 里就有这些坏数据，
    ///   修了代码但不清理，用户仍会看到「配置没保存住」，会以为修复无效。
    /// </summary>
    private void SanitizeParamPresets()
    {
        if (Config.ParamPresets is not { Count: > 0 } map) return;
        var dead = new List<string>();
        bool changed = false;
        foreach (var kv in map)
        {
            var bar = kv.Key.IndexOf('|');
            if (bar <= 0) { dead.Add(kv.Key); continue; }
            var provider = ApiProviderExtensions.Parse(kv.Key[..bar]);
            var model = kv.Key[(bar + 1)..];
            if (provider is null || !Catalog.ModelMatchesProvider(model, provider.Value))
            {
                dead.Add(kv.Key);   // ① 错配键
                continue;
            }
            if (kv.Value is null) { dead.Add(kv.Key); continue; }   // ② 空值
            // ③ **完全无信息**的快照才删（早期 ComboBox 回写 null 的产物）。
            //    ⚠️ 判据必须覆盖**所有**字段，不能只看 aspect/quality：
            //       用户可能只改了千问的 negative_prompt 或勾了 watermark，
            //       那也是**合法**记录，过严的判据会把它删掉（本仓库回归过两次）。
            if (IsPresetEmpty(kv.Value)) { dead.Add(kv.Key); continue; }

            // ④ **画幅不在该端点下拉集合里** → 修正（不是删除）。
            //    ⚠️ 这是"跨端点串味"的产物：例如从 OpenAI 带过来的 "1024x1024"
            //       被写进了 apimart 槽位，而 APIMart 的下拉只列比例名 →
            //       切回去时下拉选不中 → **画幅空白**（v0.5.31 实测回归）。
            //    做法：就地改成该端点集合的首项，而不是删掉整条快照
            //    （其余字段如质量/格式/张数仍然有效，删了会让用户白丢配置）。
            var choices = Catalog.AspectChoicesFor(provider.Value, model);
            if (choices.Length > 0 && !choices.Contains(kv.Value.Aspect))
            {
                var fixedAspect = choices[0];
                AppLog.Info($"修正快照画幅：{kv.Key} {kv.Value.Aspect} → {fixedAspect}",
                            "Session.SanitizeParamPresets");
                kv.Value.Aspect = fixedAspect;
                changed = true;
            }
        }
        if (dead.Count == 0 && !changed) return;
        foreach (var k in dead)
        {
            AppLog.Info($"清理无效的参数快照：{k}", "Session.SanitizeParamPresets");
            map.Remove(k);
        }
        // ⚠️ 必须**立即回写磁盘**（v0.5.31 实测：只改内存的话，用户打开 config.json
        //    仍会看到那些坏数据，会以为修复没生效）。写失败不阻断启动。
        try { SaveConfig(); }
        catch (Exception ex)
        {
            AppLog.Warn("清理参数快照后回写配置失败（不影响本次会话）",
                        "Session.SanitizeParamPresets", ex: ex);
        }
    }

    /// <summary>
    /// 快照是否**完全没有信息**（所有字段都是默认/空）。
    ///
    /// ⚠️ 必须逐字段判、且覆盖**全部**字段：只看 aspect/quality 会误删
    /// "只改过 negative_prompt"或"只勾了 watermark"这类合法的最小记录。
    ///   · 字符串型：空 或 与该类型的常见默认值相同；
    ///   · 数值/布尔型：为 0 / false（= 从未改过）。
    /// </summary>
    private static bool IsPresetEmpty(AppConfig.ParamPreset v) =>
        string.IsNullOrWhiteSpace(v.Aspect)
        && string.IsNullOrWhiteSpace(v.Quality)
        && string.IsNullOrWhiteSpace(v.Resolution)
        && string.IsNullOrWhiteSpace(v.OutputFormat)
        && string.IsNullOrWhiteSpace(v.Background)
        && string.IsNullOrWhiteSpace(v.Moderation)
        && string.IsNullOrWhiteSpace(v.Seed)
        && string.IsNullOrWhiteSpace(v.InputFidelity)
        && string.IsNullOrWhiteSpace(v.Style)
        && string.IsNullOrWhiteSpace(v.NegativePrompt)
        && string.IsNullOrWhiteSpace(v.PromptExtendMode)
        && v.N <= 0
        && v.OutputCompression <= 0
        && v.PartialImages <= 0
        && !v.Stream
        && !v.Watermark
        && v.PromptExtend is null;

    // ---------------------------------------------------------------- 写

    /// <summary>
    /// 原子写（临时文件 + Move，避免读到半截文件）。
    /// ⚠️ 内容用 <see cref="Func{TResult}"/> 延迟求值 —— **序列化必须在 try 内**，
    /// 否则序列化异常会绕过这里的 catch 直接飞给调用方（P0-1，历史上表现为
    /// 「参数不保存但日志里什么都没有」）。
    /// </summary>
    private void AtomicWrite(string path, Func<string> content)
    {
        lock (_writeLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                // ← 求值点必须在 try 内（本方法第一行 lock 之后）
                var text = content();
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, text);
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLog.Error($"写入失败：{path}", "Session.AtomicWrite", ex: ex);
            }
        }
    }

    /// <summary>
    /// 统一的加锁写入口（P0-3）。所有会写 <see cref="HomePath"/> 下文件的操作
    /// 都必须走这里 —— 旧实现只有 <see cref="AtomicWrite"/> 持锁，
    /// 其余 5 个写盘点与读并发时会丢流水 / 截断文件。
    /// </summary>
    private void WriteLocked(Action action)
    {
        lock (_writeLock)
        {
            action();
        }
    }

    public void SaveConfig()
    {
        // source-gen（AOT 安全）；序列化延迟到 AtomicWrite 的 try 内执行
        AtomicWrite(ConfigFile, () => JsonSerializer.Serialize(Config, AppJson.Default.AppConfig));
    }

    public void SaveState()
    {
        // JsonObject（动态结构，AOT 安全）
        // ⚠️ P0-1b：**只写 LoadState 真的会读回的键**（counter / total_cost / items）。
        //    曾额外写 aspect / offline / preview，但 LoadState 从不读它们 ——
        //    它们的真源是 config.json，写在这里只会让后来者以为「这些字段走 state 恢复」。
        var d = new JsonObject
        {
            ["total_cost"] = Math.Round(TotalCost, 6),
            ["counter"] = Counter,
        };
        var itemArr = new JsonArray();
        // P0-4：用快照枚举，且不依赖 LINQ 的 IList 快速路径（并发删除会越界）
        foreach (var it in Items.Snapshot().Take(60))
        {
            itemArr.AddNode(new JsonObject
            {
                ["file"] = it.File, ["prompt"] = it.Prompt, ["kind"] = it.Kind,
                ["ts"] = it.Ts, ["model"] = it.Model, ["quality"] = it.Quality,
                ["cost"] = it.Cost, ["tokens"] = it.Tokens, ["note"] = it.Note,
                ["provider"] = it.Provider,
            });
        }
        d["items"] = itemArr;
        AtomicWrite(StateFile, () => d.ToReadableJson());
        SaveConfig();
        TrimLog();
    }

    public void Push(Item item)
    {
        Items.Insert(0, item);
        Counter++;
        SaveState();
    }

    public void Log(Item item)
    {
        try
        {
            var rec = new JsonObject
            {
                ["file"] = item.File, ["prompt"] = item.Prompt, ["kind"] = item.Kind,
                ["ts"] = item.Ts, ["model"] = item.Model, ["quality"] = item.Quality,
                ["cost"] = item.Cost, ["tokens"] = item.Tokens,
                ["provider"] = item.Provider,
                ["total"] = Math.Round(TotalCost, 6),
            };
            // ⚠️ JSONL 必须单行（ToJsonLine，非缩进），否则每条记录会变多行
            // ⚠️ 必须持写锁：否则与 TrimLog 的全量重写并发会丢流水 / 截断（P0-3）
            WriteLocked(() =>
                File.AppendAllText(HistoryLog, rec.ToJsonLine() + "\n"));
        }
        catch (Exception ex)
        {
            // 流水写失败不阻断主流程，但要留痕（否则用户不知道历史丢了）
            AppLog.Warn("history.jsonl 写入失败", "Session.Log", ex: ex);
        }
    }

    public void TrimLog()
    {
        try
        {
            // 读+写必须在同一把锁内（P0-3）：分开持锁仍会有「读到旧内容后覆盖新内容」的窗口
            WriteLocked(() =>
            {
                if (!File.Exists(HistoryLog)) return;
                var lines = File.ReadAllLines(HistoryLog);
                if (lines.Length <= Catalog.HistoryMax) return;
                File.WriteAllText(HistoryLog,
                    string.Join("\n", lines[^Catalog.HistoryMax..]) + "\n");
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("history.jsonl 裁剪失败", "Session.TrimLog", ex: ex);
        }
    }

    // ---------------------------------------------------------------- 提示词历史
    public void PushPromptHistory(string prompt, string kind = "gen")
    {
        var text = (prompt ?? "").Trim();
        if (text.Length == 0) return;
        try
        {
            var rec = new JsonObject
            {
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                ["kind"] = kind,
                ["prompt"] = text,
            };
            // ⚠️ 同上：JSONL 用单行序列化；并纳入写锁（P0-3，与 RemovePromptHistory 互斥）
            WriteLocked(() =>
                File.AppendAllText(PromptHistoryFile, rec.ToJsonLine() + "\n"));
        }
        catch (Exception ex)
        {
            AppLog.Warn("prompt_history.jsonl 写入失败", "Session.PushPromptHistory", ex: ex);
        }
    }

    /// <summary>读提示词历史：最近的在前，按内容去重。</summary>
    public List<string> LoadPromptHistory(int limit = Catalog.PromptHistoryMax, string kind = "")
    {
        var outList = new List<string>();
        try
        {
            if (!File.Exists(PromptHistoryFile)) return outList;
            var lines = File.ReadAllLines(PromptHistoryFile);
            var seen = new HashSet<string>();
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); } catch { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    var text = root.TryGetProperty("prompt", out var p)
                        ? (p.GetString() ?? "").Trim() : "";
                    if (text.Length == 0) continue;
                    if (seen.Contains(text)) continue;
                    if (kind.Length > 0 &&
                        (!root.TryGetProperty("kind", out var k) ||
                         (k.GetString() ?? "") != kind)) continue;
                    seen.Add(text);
                    outList.Add(text);
                    if (outList.Count >= limit) break;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("操作失败", "Session", ex: ex);
        }
        return outList;
    }

    /// <summary>
    /// 删除一条提示词历史（用户需求 #11/#12）。重写整个文件，保持可读可编辑。
    /// </summary>
    public void RemovePromptHistory(string prompt)
    {
        try
        {
            // 读+重写必须同一把锁（P0-3）：否则与 PushPromptHistory 的 Append 并发会丢条目
            WriteLocked(() =>
            {
                if (!File.Exists(PromptHistoryFile)) return;
                var keep = new List<string>();
                foreach (var line in File.ReadAllLines(PromptHistoryFile))
                {
                    var keepLine = true;
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(line);
                        if (doc.RootElement.TryGetProperty("prompt", out var pEl) &&
                            string.Equals(pEl.GetString(), prompt, StringComparison.Ordinal))
                            keepLine = false;
                    }
                    catch (Exception ex)
                    {
                        // 非 JSON 行保留（旧版本可能写过纯文本）——但要留痕便于排查
                        AppLog.Warn("跳过一条不可解析的提示词历史行",
                                    "Session.RemovePromptHistory", ex: ex);
                    }
                    if (keepLine) keep.Add(line);
                }
                File.WriteAllLines(PromptHistoryFile, keep);
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("prompt_history.jsonl 重写失败",
                        "Session.RemovePromptHistory", ex: ex);
        }
    }

    /// <summary>从当前历史列表移除一项（不删磁盘文件；语义 = 从列表移除）。</summary>
    public bool RemoveItem(Item item) => Items.Remove(item);


    // ---------------------------------------------------------------- key
    public void SaveKey(string apiKey, ApiProvider p) => SaveKeyToFile(apiKey, KeyFile(p), p);
    public void SavePolishKey(string k) => SaveKeyToFile(k, PolishKeyFile, null, isPolish: true);

    /// <summary>
    /// 保存即梦的第二把密钥（SecretAccessKey）。
    /// 与 <see cref="SaveKey"/> 分开，避免影响其它 provider 的单 key 语义。
    /// </summary>
    public void SaveSecret(string secret, ApiProvider p)
        => SaveKeyToFile(secret, SecretKeyFile(p), p);

    /// <summary>
    /// 读取某 provider 的第二把密钥（目前只有即梦用）。没有则返回空串。
    /// </summary>
    public string LoadSecret(ApiProvider p)
    {
        try
        {
            var path = SecretKeyFile(p);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch (Exception ex)
        {
            // 与 LoadApiKey 同样的策略：读取失败返回空，绝不抛（否则启动闪退）
            AppLog.Warn("读取第二把密钥失败（按未配置处理）",
                        "Session.LoadSecret", detail: SecretKeyFile(p), ex: ex);
            return "";
        }
    }

    private void SaveKeyToFile(string k, string path, ApiProvider? provider,
                               bool isPolish = false)
    {
        try
        {
            // 临时文件 + Move 必须同一把锁（P0-3）：否则与读取并发可能读到半截 key
            WriteLocked(() =>
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, (k ?? "").Trim() + "\n");
                File.Move(tmp, path, overwrite: true);
            });
            TryOwnerOnly(path);
        }
        catch (Exception ex)
        {
            // key 写失败不阻断，但必须告诉用户（否则下次启动发现 key 丢了）
            AppLog.Error("API key 写入失败", "Session.SaveKeyToFile",
                         detail: "路径：" + path, ex: ex);
        }
        if (isPolish) PolishKey = k ?? "";
    }

    /// <summary>
    /// 尽力限制为"仅所有者可读"（Unix 0600）。
    ///
    /// ⚠️ **已知边界（如实说明，勿误以为 Windows 也受保护）**：
    ///   · Unix / Android：设置 0600（UserRead|UserWrite），**真实生效**；
    ///   · **Windows：当前不做任何事** —— 依赖用户目录
    ///     （`%LOCALAPPDATA%`）的默认 ACL（仅当前用户 + Administrators 可访问）。
    ///     本项目主力平台是 Windows，所以这一层**实际是"靠系统默认权限"而非主动加固**。
    ///
    /// 若日后要真正加固 Windows：需引入 `System.Security.AccessControl`
    /// （`FileSecurity` + `SetAccessRuleProtection`），或在 `File.Encrypt`（EFS）上做取舍 ——
    /// 都会新增依赖/平台行为差异，属独立决策，不在本方法里悄悄改。
    ///
    /// 失败不抛（权限设置失败不应阻断"保存 key"这个主流程）。
    /// </summary>
    private static void TryOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows()) return;   // 见上方说明：Windows 依赖目录默认 ACL

        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception ex)
        {
            AppLog.Warn("设置文件权限失败（不影响使用）",
                        "Session.TryOwnerOnly", detail: path, ex: ex);
        }
    }


    // ---------------------------------------------------------------- 文件名安全
    private static readonly Regex Unsafe = new(@"[^\w\u4e00-\u9fff.\-]+",
        RegexOptions.Compiled);

    /// <summary>生成安全文件名（三道防线：白名单字符 + basename + 剥首尾点 + 去控制符）。</summary>
    public static string SafeFilename(string prefix, string stem, string suffix, int limit = 40)
    {
        var dt = DateTime.Now;
        prefix = prefix.Replace("MMdd", dt.ToString("MMdd"))
                       .Replace("HHmmss", dt.ToString("HHmmss"))
                       .Replace("yyMMdd", dt.ToString("yyMMdd"))
                       .Replace("HHmmssff", dt.ToString("HHmmssff"))
                       .Replace("yyyyMMdd", dt.ToString("yyyyMMdd"));
        // 先剔除控制字符（含 NUL）—— \w 不覆盖它们，必须显式处理
        stem = new string((stem ?? "").Where(c => !char.IsControl(c)).ToArray());
        var b = Unsafe.Replace(stem, "_");
        b = Path.GetFileName(b).Trim('_', '.', '-');
        if (b.Length > limit) b = b[..limit];
        if (b.Length == 0) b = "image";
        var suf = suffix.StartsWith('.') ? suffix : "." + suffix;
        suf = Regex.Replace(suf, @"[^A-Za-z0-9.]", "");
        if (suf.Length > 8) suf = suf[..8];
        if (suf.Length == 0) suf = ".png";
        return Path.GetFileName($"{prefix}_{b}{suf}");
    }
}
