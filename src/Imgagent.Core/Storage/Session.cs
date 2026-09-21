using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Imgagent.Core.Models;

namespace Imgagent.Core.Storage;

/// <summary>
/// 本地存储：配置 + 历史 + 提示词历史 + 图片落盘。
/// 对应 Python 的 <c>store.py</c>（Session + 各 jsonl）。
///
/// 与 Python 版差异：数据目录由调用方注入（<see cref="HomePath"/>），
/// 不再用"探测一堆候选路径"的做法 —— GUI 端由平台层给标准目录。
/// </summary>
public sealed class Session
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // 旧版 config.json 可能含本工程未用的键（key_saved 等）——
        // 宽松读取，避免因未知字段而整份配置读失败。
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string HomePath { get; private set; }

    /// <summary>
    /// 配置/状态写锁：FlushConfig（UI 线程）与节流 Timer 回调（线程池）
    /// 可能并发调用 SaveConfig/SaveState，Windows 上 File.Move 并发会抛 IOException。
    /// </summary>
    private readonly object _writeLock = new();

    public List<Item> Items { get; } = new();   // Items[0] = 当前
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

    /// <summary>
    /// 按 provider 累计花费（USD）。
    /// 旧数据没有 provider 字段时按「当前 provider」计入，保证不丢账。
    /// </summary>
    /// <summary>
    /// 保留的空操作（兼容旧调用）。TotalCost 现在是**计算属性**（从 Items 求和），
    /// 天然与历史一致，无需再"校正"。
    /// </summary>
    public void ReconcileCost()
    {
        // 无操作：口径已统一到 Items 求和
    }

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
    public string KeyFile(ApiProvider p) => Path.Combine(HomePath,
        p == ApiProvider.Apimart ? ".imgagent_apimart_key" : ".imgagent_key");
    public string PolishKeyFile => Path.Combine(HomePath, ".imgagent_polish_key");

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
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigFile));
            if (cfg is not null) Config = cfg;
        }
        catch { /* 忽略损坏配置 */ }
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
        catch { /* 忽略损坏状态 */ }
    }

    private void LoadKeysFromDisk()
    {
        // 环境变量优先；否则读文件（由平台层/调用方决定是否用安全存储）
        var pk = Environment.GetEnvironmentVariable("IMGAGENT_POLISH_API_KEY");
        if (string.IsNullOrEmpty(pk) && File.Exists(PolishKeyFile))
            PolishKey = File.ReadAllText(PolishKeyFile).Trim();
        else PolishKey = pk ?? "";
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
        if (!Catalog.Resolutions.Contains(Config.Resolution)) Config.Resolution = "1k";
        if (!Catalog.OutputFormats.Contains(Config.OutputFormat)) Config.OutputFormat = "png";
        if (Config.BatchN is < 1 or > 4) Config.BatchN = 1;
    }

    // ---------------------------------------------------------------- 写
    private void AtomicWrite(string path, string text)
    {
        lock (_writeLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, text);
                File.Move(tmp, path, overwrite: true);
            }
            catch { /* 写失败不阻断 */ }
        }
    }

    public void SaveConfig()
    {
        AtomicWrite(ConfigFile, JsonSerializer.Serialize(Config, JsonOpts));
    }

    public void SaveState()
    {
        var d = new Dictionary<string, object?>
        {
            ["model"] = Config.Model,
            ["quality"] = Config.Quality,
            ["aspect"] = Config.Aspect,
            ["offline"] = Config.Offline,
            ["preview"] = Config.AutoPreview,
            ["total_cost"] = Math.Round(TotalCost, 6),
            ["counter"] = Counter,
            ["items"] = Items.Take(60).Select(it => new Dictionary<string, object?>
            {
                ["file"] = it.File, ["prompt"] = it.Prompt, ["kind"] = it.Kind,
                ["ts"] = it.Ts, ["model"] = it.Model, ["quality"] = it.Quality,
                ["cost"] = it.Cost, ["tokens"] = it.Tokens, ["note"] = it.Note,
                ["provider"] = it.Provider,
            }).ToList(),
        };
        var json = JsonSerializer.Serialize(d, JsonOpts);
        AtomicWrite(StateFile, json);
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
            var rec = new Dictionary<string, object?>
            {
                ["file"] = item.File, ["prompt"] = item.Prompt, ["kind"] = item.Kind,
                ["ts"] = item.Ts, ["model"] = item.Model, ["quality"] = item.Quality,
                ["cost"] = item.Cost, ["tokens"] = item.Tokens,
                ["provider"] = item.Provider,
                ["total"] = Math.Round(TotalCost, 6),
            };
            File.AppendAllText(HistoryLog,
                JsonSerializer.Serialize(rec) + "\n");
        }
        catch { /* 忽略 */ }
    }

    public void TrimLog()
    {
        try
        {
            if (!File.Exists(HistoryLog)) return;
            var lines = File.ReadAllLines(HistoryLog);
            if (lines.Length <= Catalog.HistoryMax) return;
            File.WriteAllText(HistoryLog,
                string.Join("\n", lines[^Catalog.HistoryMax..]) + "\n");
        }
        catch { /* 忽略 */ }
    }

    // ---------------------------------------------------------------- 提示词历史
    public void PushPromptHistory(string prompt, string kind = "gen")
    {
        var text = (prompt ?? "").Trim();
        if (text.Length == 0) return;
        try
        {
            var rec = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                ["kind"] = kind,
                ["prompt"] = text,
            });
            File.AppendAllText(PromptHistoryFile, rec + "\n");
        }
        catch { /* 忽略 */ }
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
        catch { /* 忽略 */ }
        return outList;
    }

    public void TrimPromptHistory(int maxLines = Catalog.PromptHistoryMax * 2)
    {
        try
        {
            if (!File.Exists(PromptHistoryFile)) return;
            var lines = File.ReadAllLines(PromptHistoryFile);
            if (lines.Length <= maxLines) return;
            File.WriteAllText(PromptHistoryFile,
                string.Join("\n", lines[^maxLines..]) + "\n");
        }
        catch { /* 忽略 */ }
    }

    // ---------------------------------------------------------------- key
    public void SaveKey(string apiKey, ApiProvider p) => SaveKeyToFile(apiKey, KeyFile(p), p);
    public void SavePolishKey(string k) => SaveKeyToFile(k, PolishKeyFile, null, isPolish: true);

    private void SaveKeyToFile(string k, string path, ApiProvider? provider,
                               bool isPolish = false)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, (k ?? "").Trim() + "\n");
            File.Move(tmp, path, overwrite: true);
            TryOwnerOnly(path);
        }
        catch { /* 写不了则不阻断 */ }
        if (isPolish) PolishKey = k ?? "";
    }

    /// <summary>尽力限制为"仅所有者可读"（Windows ACL / Unix 0600）。失败不抛。</summary>
    private static void TryOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { }
        }
    }

    /// <summary>图片落盘（先写 HOME，相册目录由平台层另行处理）。</summary>
    public string SaveImage(byte[] data, string media, string prompt, int seq)
    {
        var name = SafeFilename($"MMdd_HHmmss_{seq:000}", prompt,
                                ".png", limit: 24);
        var path = Path.Combine(HomePath, name);
        File.WriteAllBytes(path, data);
        return path;
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
