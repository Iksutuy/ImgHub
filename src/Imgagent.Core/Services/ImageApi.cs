using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Imgagent.Core.Http;
using Imgagent.Core.Imaging;
using Imgagent.Core.Models;

namespace Imgagent.Core.Services;

/// <summary>生图 API 的抽象（双 provider 统一契约）。</summary>
public interface IImageApi
{
    Task<GenResult> GenerateAsync(
        string prompt,
        string apiKey,
        string model,
        string quality,
        string aspect,
        int n = 1,
        IReadOnlyList<(byte[] Data, string Media)>? refs = null,
        bool offline = false,
        int step = 0,
        string resolution = "1k",
        string outputFormat = "png",
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListModelsAsync(string? apiKey = null,
                                                bool imagesOnly = true,
                                                CancellationToken ct = default);

    Task<KeyInfo> CheckKeyAsync(string apiKey, CancellationToken ct = default);
}

public sealed record KeyInfo(bool IsApimart, int ModelCount,
                             double? Usage, double? Limit);

/// <summary>
/// 双 provider 生图实现。对应 Python 的 <c>api.py</c>。
///
/// 契约对照（ARCHITECTURE 第五节）：
///   · OpenRouter：同步，POST /images，"aspect_ratio" + "input_references"(base64)
///   · APIMart   ：异步，POST /images/generations → 轮询 /tasks/{id}；
///                 "size" + "image_urls"（公网 URL，需先 POST /uploads/images）
/// </summary>
public sealed class ImageApi : IImageApi
{
    private readonly HttpJsonClient _http;
    private readonly ApiProvider _provider;
    private readonly string _baseUrl;

    // 参考图上传 URL 缓存（同一会话内同一图片只上传一次）
    private readonly Dictionary<string, string> _uploadCache = new();

    public ImageApi(ApiProvider provider, HttpJsonClient http,
                    string? baseUrl = null)
    {
        _provider = provider;
        _http = http;
        _baseUrl = (baseUrl ?? (provider == ApiProvider.Apimart
            ? Catalog.ApimartBaseDefault
            : Catalog.OpenRouterBaseDefault)).TrimEnd('/');
    }

    // ================================================================ 生成
    public async Task<GenResult> GenerateAsync(
        string prompt, string apiKey, string model, string quality, string aspect,
        int n = 1, IReadOnlyList<(byte[] Data, string Media)>? refs = null,
        bool offline = false, int step = 0, string resolution = "1k",
        string outputFormat = "png", IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (offline)
        {
            // 离线占位图：尊重批量张数（n），每张 step 不同 → 画面不同，便于验证多图流程
            await Task.Delay(400, ct).ConfigureAwait(false);
            int count = Math.Max(1, n);
            var imgs = new List<(byte[], string)>(count);
            for (int i = 0; i < count; i++)
                imgs.Add((Placeholder.Png(prompt, step: step + i), "image/png"));
            return new GenResult(imgs);
        }

        return _provider == ApiProvider.Apimart
            ? await GenerateApimartAsync(prompt, apiKey, model, quality, aspect, n,
                                         refs, resolution, outputFormat, progress, ct)
                .ConfigureAwait(false)
            : await GenerateOpenRouterAsync(prompt, apiKey, model, quality, aspect, n,
                                            refs, resolution, outputFormat, ct)
                .ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- OpenRouter（同步）
    private async Task<GenResult> GenerateOpenRouterAsync(
        string prompt, string apiKey, string model, string quality, string aspect,
        int n, IReadOnlyList<(byte[] Data, string Media)>? refs,
        string resolution, string outputFormat, CancellationToken ct)
    {
        n = Math.Max(1, Math.Min(10, n));
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["quality"] = quality,
            ["aspect_ratio"] = aspect,
            ["n"] = n,
        };
        if (refs is { Count: > 0 })
        {
            var list = new List<object>();
            foreach (var (data, media) in refs.Take(Catalog.MaxRefs))
            {
                var b64 = Convert.ToBase64String(data);
                list.Add(new Dictionary<string, object?>
                {
                    ["type"] = "image_url",
                    ["image_url"] = new Dictionary<string, object?>
                    {
                        ["url"] = $"data:{media};base64,{b64}",
                    },
                });
            }
            payload["input_references"] = list;
        }

        using var doc = await _http.PostJsonAsync($"{_baseUrl}/images", payload, apiKey,
                                                  ct: ct).ConfigureAwait(false);
        return ParseOpenRouterResponse(doc.RootElement);
    }

    private static GenResult ParseOpenRouterResponse(JsonElement root)
    {
        var images = new List<(byte[], string)>();
        if (root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("b64_json", out var b64El)) continue;
                var b64 = b64El.GetString();
                if (string.IsNullOrEmpty(b64)) continue;
                var media = item.TryGetProperty("media_type", out var mt)
                    ? (mt.GetString() ?? "image/png") : "image/png";
                images.Add((Convert.FromBase64String(b64), media));
            }
        }
        if (images.Count == 0)
            throw new ApiError($"响应里没有图像数据：{Trunc(root.ToString(), 200)}");

        double cost = 0;
        int tokens = 0;
        if (root.TryGetProperty("usage", out var usage) &&
            usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number)
                cost = c.GetDouble();
            if (usage.TryGetProperty("total_tokens", out var t) && t.ValueKind == JsonValueKind.Number)
                tokens = t.GetInt32();
        }
        return new GenResult(images, cost, tokens);
    }

    // ---------------------------------------------------------------- APIMart（异步）
    private async Task<GenResult> GenerateApimartAsync(
        string prompt, string apiKey, string model, string quality, string aspect,
        int n, IReadOnlyList<(byte[] Data, string Media)>? refs,
        string resolution, string outputFormat, IProgress<string>? progress,
        CancellationToken ct)
    {
        // ① 上传参考图 → 公网 URL（失败必须明确报错，绝不静默降级 —— CONSTRAINTS D6）
        var imageUrls = new List<string>();
        if (refs is { Count: > 0 })
        {
            int idx = 0;
            foreach (var (data, media) in refs.Take(Catalog.MaxRefs))
            {
                idx++;
                progress?.Report($"上传参考图 {idx}/{refs.Count}（{data.Length / 1024}KB）…");
                imageUrls.Add(await UploadImageAsync(data, media, apiKey, ct)
                                  .ConfigureAwait(false));
            }
            progress?.Report($"参考图已上传 {imageUrls.Count} 张");
        }

        // ② 提交任务
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["quality"] = quality,
            ["size"] = aspect,
            ["n"] = Math.Max(1, Math.Min(4, n)),
        };
        if (!string.IsNullOrEmpty(resolution) && resolution != "1k")
            payload["resolution"] = resolution;
        if (!string.IsNullOrEmpty(outputFormat))
            payload["output_format"] = outputFormat;
        if (imageUrls.Count > 0)
            payload["image_urls"] = imageUrls.Take(16).ToList();

        using var submitDoc = await _http.PostJsonAsync($"{_baseUrl}/images/generations",
            payload, apiKey, ct: ct).ConfigureAwait(false);
        var submit = submitDoc.RootElement;

        int code = submit.TryGetProperty("code", out var codeEl) ? codeEl.GetInt32() : 200;
        if (code != 200)
        {
            var msg = submit.TryGetProperty("error", out var err) &&
                      err.TryGetProperty("message", out var m)
                ? m.GetString() : submit.ToString();
            throw new ApiError($"提交失败：{msg}");
        }

        var tasks = new List<string>();
        if (submit.TryGetProperty("data", out var dataEl) &&
            dataEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in dataEl.EnumerateArray())
            {
                if (t.TryGetProperty("task_id", out var id) && id.GetString() is { Length: > 0 } s)
                    tasks.Add(s);
            }
        }
        if (tasks.Count == 0)
            throw new ApiError($"提交成功但没有 task_id：{Trunc(submit.ToString(), 200)}");

        // ③ 并发轮询所有任务（n 张同时等，总时间 ≈ 最慢那张，而非串行累加）
        progress?.Report($"提交 {tasks.Count} 个任务，开始并行轮询…");
        var pollTasks = tasks.Select(id => PollOneAsync(id, apiKey, ct)).ToArray();
        var results = await Task.WhenAll(pollTasks).ConfigureAwait(false);

        var outImages = new List<(byte[], string)>();
        double totalCost = 0;
        int totalTokens = 0;
        int failed = 0;
        int dlFailures = 0;
        var failureReasons = new List<string>();
        foreach (var r in results)
        {
            if (r.Error is not null)
            {
                failed++;
                failureReasons.Add(r.Error.Message);
                continue;
            }
            totalCost += r.Cost;
            totalTokens += r.Tokens;
            dlFailures += r.DownloadFailures;
            outImages.AddRange(r.Images);
        }
        // 部分失败要让用户知道（不能静默少给图）
        if (failed > 0 || dlFailures > 0)
        {
            var detail = failed > 0 ? $"，{failed} 个任务失败" : "";
            var detail2 = dlFailures > 0 ? $"，{dlFailures} 张下载失败" : "";
            progress?.Report($"⚠ 部分产出缺失{detail}{detail2}");
            if (failureReasons.Count > 0)
                progress?.Report("失败原因：" + string.Join("；", failureReasons.Take(3)));
        }
        if (outImages.Count == 0)
        {
            var why = failureReasons.Count > 0
                ? string.Join("；", failureReasons.Take(2))
                : "无图像数据";
            throw new ApiError($"所有任务都没有产出图片（tasks={tasks.Count}，失败={failed}）：{why}");
        }
        return new GenResult(outImages, totalCost, totalTokens,
                             new Dictionary<string, object?> { ["total_tokens"] = totalTokens });
    }

    private sealed record PollOutcome(List<(byte[], string)> Images, double Cost,
                                      int Tokens, Exception? Error,
                                      int DownloadFailures = 0);

    private async Task<PollOutcome> PollOneAsync(string taskId, string apiKey,
                                                 CancellationToken ct)
    {
        try
        {
            var td = await PollTaskAsync(taskId, apiKey, ct).ConfigureAwait(false);
            var images = new List<(byte[], string)>();
            double cost = td.TryGetProperty("cost", out var c) &&
                          c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0;
            int tokens = 0;
            if (td.TryGetProperty("usage", out var usage) &&
                usage.TryGetProperty("total_tokens", out var tk) &&
                tk.ValueKind == JsonValueKind.Number)
                tokens = tk.GetInt32();

            int downloadFailures = 0;
            if (td.TryGetProperty("result", out var result) &&
                result.TryGetProperty("images", out var imgs) &&
                imgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in imgs.EnumerateArray())
                {
                    if (!entry.TryGetProperty("url", out var urlEl)) continue;
                    string? url = urlEl.ValueKind == JsonValueKind.Array
                        ? urlEl.EnumerateArray().Select(x => x.GetString())
                               .FirstOrDefault(s => !string.IsNullOrEmpty(s))
                        : urlEl.GetString();
                    if (string.IsNullOrEmpty(url)) continue;
                    try
                    {
                        var bytes = await _http.GetBytesAsync(url, apiKey,
                            TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                        var media = "image/png";
                        var ct2 = entry.TryGetProperty("content_type", out var ctv)
                            ? (ctv.GetString() ?? "") : "";
                        if (ct2.Contains("jpeg") || ct2.Contains("jpg")) media = "image/jpeg";
                        else if (ct2.Contains("webp")) media = "image/webp";
                        images.Add((bytes, media));
                    }
                    catch
                    {
                        // 单张下载失败不影响其它，但必须**计数**（否则用户不知道少了几张）
                        downloadFailures++;
                    }
                }
            }
            return new PollOutcome(images, cost, tokens, null, downloadFailures);
        }
        catch (Exception ex)
        {
            return new PollOutcome(new(), 0, 0, ex);
        }
    }

    /// <summary>轮询任务直至 completed/failed 或超时。</summary>
    public async Task<JsonElement> PollTaskAsync(string taskId, string apiKey,
                                                 CancellationToken ct,
                                                 double maxWait = 300,
                                                 double pollInterval = 3.0)
    {
        var url = $"{_baseUrl}/tasks/{taskId}";
        double elapsed = 0;
        while (elapsed < maxWait)
        {
            ct.ThrowIfCancellationRequested();
            using var doc = await _http.GetJsonAsync(url, apiKey,
                TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            var root = doc.RootElement;

            int code = root.TryGetProperty("code", out var codeEl) ? codeEl.GetInt32() : 200;
            if (code != 200)
            {
                var msg = root.TryGetProperty("error", out var err) &&
                          err.TryGetProperty("message", out var m) ? m.GetString() : root.ToString();
                throw new ApiError($"查询任务失败：{msg}");
            }
            if (!root.TryGetProperty("data", out var data)) 
                throw new ApiError($"任务响应缺少 data：{Trunc(root.ToString(), 200)}");

            var status = data.TryGetProperty("status", out var st) ? (st.GetString() ?? "") : "";
            if (status == "completed") return data.Clone();
            if (status == "failed")
            {
                var errMsg = data.TryGetProperty("error", out var e2) &&
                             e2.TryGetProperty("message", out var m2)
                    ? m2.GetString() : "未知错误";
                throw new ApiError($"任务失败 [{taskId}]：{errMsg}");
            }
            await Task.Delay(TimeSpan.FromSeconds(pollInterval
                + Random.Shared.NextDouble() * 0.5), ct).ConfigureAwait(false);
            elapsed += pollInterval + 0.5;
        }
        throw new ApiError($"任务超时（>{maxWait:0}s）：{taskId}");
    }

    // ---------------------------------------------------------------- 上传参考图
    public async Task<string> UploadImageAsync(byte[] data, string media, string apiKey,
                                               CancellationToken ct = default)
    {
        var cacheKey = UploadKey(data);
        if (_uploadCache.TryGetValue(cacheKey, out var cached)) return cached;

        var fname = "ref." + ImageCodec.ExtFor(media);
        using var doc = await _http.PostMultipartAsync($"{_baseUrl}/uploads/images",
            data, fname, media, apiKey, ct: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        string? url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(url) && root.TryGetProperty("data", out var d))
        {
            url = d.ValueKind == JsonValueKind.Object
                ? (d.TryGetProperty("url", out var iu) ? iu.GetString() : null)
                : d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0
                    ? (d[0].TryGetProperty("url", out var au) ? au.GetString() : null)
                    : null;
        }
        if (string.IsNullOrEmpty(url))
            throw new ApiError($"上传成功但响应里没有 url：{Trunc(root.ToString(), 200)}");

        if (_uploadCache.Count >= Catalog.UploadCacheMax)
        {
            var first = _uploadCache.Keys.First();
            _uploadCache.Remove(first);
        }
        _uploadCache[cacheKey] = url;
        return url;
    }

    /// <summary>轻量哈希：头尾各 512 字节拼一起。</summary>
    private static string UploadKey(byte[] data)
    {
        var head = data.Take(Math.Min(512, data.Length));
        var tail = data.Skip(Math.Max(0, data.Length - 512));
        var buf = head.Concat(tail).ToArray();
        return Convert.ToHexString(SHA1.HashData(buf));
    }

    // ---------------------------------------------------------------- 其它
    public async Task<IReadOnlyList<string>> ListModelsAsync(string? apiKey = null,
                                                             bool imagesOnly = true,
                                                             CancellationToken ct = default)
    {
        var key = apiKey ?? "";
        if (_provider == ApiProvider.Apimart)
        {
            using var doc = await _http.GetJsonAsync($"{_baseUrl}/models?expand=1", key,
                ct: ct).ConfigureAwait(false);
            var items = doc.RootElement.TryGetProperty("data", out var d) &&
                        d.ValueKind == JsonValueKind.Array
                ? d.EnumerateArray().ToList() : new List<JsonElement>();
            var all = items.Where(m => m.TryGetProperty("id", out var id)
                                       && !string.IsNullOrEmpty(id.GetString()))
                           .Select(m => m.GetProperty("id").GetString()!).ToList();
            if (imagesOnly)
            {
                var filtered = items.Where(m =>
                {
                    var cat = m.TryGetProperty("category", out var c) ? (c.GetString() ?? "") : "";
                    var id = m.TryGetProperty("id", out var i2) ? (i2.GetString() ?? "") : "";
                    return cat.Equals("image", StringComparison.OrdinalIgnoreCase)
                           || LooksLikeImageModel(id);
                }).Select(m => m.GetProperty("id").GetString()!).ToList();
                return filtered.Count > 0 ? filtered : all;
            }
            return all;
        }
        else
        {
            using var doc = await _http.GetJsonAsync($"{_baseUrl}/images/models", key,
                ct: ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("data", out var d)) return new List<string>();
            return d.EnumerateArray()
                    .Where(m => m.TryGetProperty("id", out _))
                    .Select(m => m.GetProperty("id").GetString()!)
                    .ToList();
        }
    }

    private static readonly string[] ImageHints =
    {
        "gpt-image", "gemini-.*-image", "seedream", "wan-", "flux",
        "nano.*banana", "qwen-image", "dall.?e", "stable.?diffus", "sdxl",
    };

    private static bool LooksLikeImageModel(string id)
    {
        var mid = (id ?? "").ToLowerInvariant();
        return ImageHints.Any(p =>
            System.Text.RegularExpressions.Regex.IsMatch(mid, p));
    }

    public async Task<KeyInfo> CheckKeyAsync(string apiKey, CancellationToken ct = default)
    {
        if (_provider == ApiProvider.Apimart)
        {
            using var doc = await _http.GetJsonAsync($"{_baseUrl}/models", apiKey,
                ct: ct).ConfigureAwait(false);
            int n = doc.RootElement.TryGetProperty("data", out var d) &&
                    d.ValueKind == JsonValueKind.Array ? d.GetArrayLength() : 0;
            return new KeyInfo(true, n, null, null);
        }
        else
        {
            using var doc = await _http.GetJsonAsync($"{_baseUrl}/key", apiKey,
                ct: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            double? usage = null, limit = null;
            if (root.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Number)
                    usage = u.GetDouble();
                if (data.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number)
                    limit = l.GetDouble();
            }
            return new KeyInfo(false, 0, usage, limit);
        }
    }

    private static string Trunc(string s, int n) => s.Length > n ? s[..n] : s;
}
