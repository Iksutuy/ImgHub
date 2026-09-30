using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text;
using System.Text.Json;
using ImgHub.Core.Diagnostics;
using ImgHub.Core.Http;
using ImgHub.Core.Imaging;
using ImgHub.Core.Models;

namespace ImgHub.Core.Services;

/// <summary>
/// 生图流程的**进度钩子**（v5.26.0）—— 让上层把"未完成任务"落盘，
/// 从而保证「提交后程序意外退出，重启仍能找回图片」。
/// Core 只提供回调点，不依赖存储实现（保持分层）。
/// </summary>
public enum SubmitPhase
{
    /// <summary>同步接口：请求**即将发出**（无任务号，只能记"可能已扣费"）。</summary>
    SyncSending,
    /// <summary>异步接口：**已拿到任务号**（关键落盘时机）。</summary>
    AsyncSubmitted,
    /// <summary>流程正常结束（成功/失败）→ 上层可清理临时记录。</summary>
    Finished,
}

/// <summary>进度回调参数。</summary>
/// <param name="Phase">阶段。</param>
/// <param name="TaskIds">任务号（仅 AsyncSubmitted 有值）。</param>
/// <param name="Prompt">提示词（便于用户辨认）。</param>
/// <param name="Model">模型。</param>
/// <param name="Success">仅 Finished 有意义。</param>
public delegate void SubmitProgressHandler(SubmitPhase Phase, IReadOnlyList<string> TaskIds,
                                           string Prompt, string Model, bool Success);

/// <summary>生图 API 的抽象（双 provider 统一契约）。</summary>
public interface IImageApi
{
    /// <summary>
    /// 生成/编辑一张（或多张）图。参数全部收在 <see cref="GenRequest"/> 里
    /// （为什么不用长位置参数列表见 GenRequest 的说明）。
    /// </summary>
    Task<GenResult> GenerateAsync(GenRequest req);

    Task<IReadOnlyList<string>> ListModelsAsync(string? apiKey = null,
                                                bool imagesOnly = true,
                                                CancellationToken ct = default);

    Task<KeyInfo> CheckKeyAsync(string apiKey, CancellationToken ct = default);

    /// <summary>
    /// 生图进度钩子（v5.26.0）：实现方在「同步请求即将发出」
    /// 「异步已拿到任务号」「流程结束」三个时机调用。
    /// 上层据此把"未完成任务"落盘 → 程序意外退出也能找回。
    /// </summary>
    SubmitProgressHandler? OnSubmitProgress { get; set; }

    /// <summary>
    /// 流式部分图回调（OpenRouter SSE）。仅当 <see cref="GenRequest.Stream"/> 为 true
    /// 且模型 supports_streaming 时才会触发。
    /// </summary>
    PartialImageHandler? OnPartialImage { get; set; }
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

    /// <summary>
    /// 生图进度钩子（v5.26.0）：拿到 task_id 后**立即**通知上层落盘。
    /// 这是"提交后意外退出也能找回图片"的关键 —— 见 PendingTaskStore。
    /// </summary>
    public SubmitProgressHandler? OnSubmitProgress { get; set; }

    /// <summary>流式部分图回调（OpenRouter SSE）。</summary>
    public PartialImageHandler? OnPartialImage { get; set; }

    public ImageApi(ApiProvider provider, HttpJsonClient http,
                    string? baseUrl = null)
    {
        _provider = provider;
        _http = http;
        _baseUrl = (string.IsNullOrWhiteSpace(baseUrl)
            ? Catalog.BaseUrlDefault(provider)
            : baseUrl).TrimEnd('/');
    }

    // ================================================================ 生成
    public async Task<GenResult> GenerateAsync(GenRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (req.Offline)
        {
            // 离线占位图：尊重批量张数（n），每张 step 不同 → 画面不同，便于验证多图流程
            await Task.Delay(400, req.Ct).ConfigureAwait(false);
            int count = Math.Max(1, req.N);
            var imgs = new List<(byte[], string)>(count);
            for (int i = 0; i < count; i++)
                imgs.Add((Placeholder.Png(req.Prompt, step: req.Step + i), "image/png"));
            return new GenResult(imgs);
        }

        // 参数前置校验（**在花钱之前**把不合法的组合挡掉 —— 文档明确会 400 的场景）
        // 注意顺序：先规范化参考图/蒙版（必要时压缩），再校验 —— 校验的是**实际发出**的字节。
        PrepareRefs(req);
        ValidateRequest(req);

        return _provider switch
        {
            ApiProvider.Apimart => await GenerateApimartAsync(req).ConfigureAwait(false),
            ApiProvider.OpenAi => await GenerateOpenAiAsync(req).ConfigureAwait(false),
            ApiProvider.DashScope => await GenerateDashScopeAsync(req).ConfigureAwait(false),
            _ => await GenerateOpenRouterAsync(req).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// 规范化参考图与蒙版：**只在必要时压缩**，并保证两者尺寸一致。
    ///
    /// ⚠️ 为什么必须在这里做（而不是 App 层各自压）：
    ///   文档要求「蒙版像素宽高必须与 image_urls[0] 完全一致」。
    ///   若 App 层把参考图压到 2048 而蒙版仍是原始尺寸，服务端就会认为尺寸不匹配
    ///   （APIMart 不做预校验，会静默产生错误的重绘区域）——
    ///   蒙版链路会"看起来发出了但完全没生效"。
    ///   所以：**压参考图时按同一比例压蒙版**，一致性由一个地方兜住。
    ///
    /// 压缩条件（文档对齐）：仅当超过单文件上限（20MB）或长边过大时才压，
    /// 否则**原样发送** —— 保住主体身份、文字、纹理等细节（这是"参考图生效"的关键）。
    /// </summary>
    private static void PrepareRefs(GenRequest req)
    {
        if (req.Refs is not { Count: > 0 })
        {
            // 没有参考图时蒙版无意义（文档：mask 仅在同时传 image_urls 时生效）。
            // ⚠️ 不能完全静默：用户若上传了蒙版却看到"没生效"，会以为功能坏了。
            if (req.Mask is { Length: > 0 })
            {
                AppLog.Warn("蒙版已忽略（未提供参考图）—— 文档要求 mask 必须与 image_urls 同时使用",
                            "ImageApi.PrepareRefs");
                req.Mask = null;
            }
            return;
        }

        var list = new List<(byte[] Data, string Media)>(req.Refs.Count);
        double firstScale = 1.0;
        for (int i = 0; i < req.Refs.Count; i++)
        {
            var (data, media) = req.Refs[i];
            var shrunk = ShrinkIfNeeded(data, out double scale);
            if (i == 0) firstScale = scale;
            // 压缩后统一按 PNG 传（无损，保真优先）；未压缩则保留原始 media
            list.Add(ReferenceEquals(shrunk, data)
                ? (data, media)
                : (shrunk, "image/png"));
        }
        req.Refs = list;

        if (req.Mask is { Length: > 0 })
        {
            // 参考图被压过 → 蒙版按同一比例压，否则尺寸必然对不上
            if (Math.Abs(firstScale - 1.0) > 1e-9)
            {
                var scaled = ScalePng(req.Mask, firstScale);
                if (scaled is not null) req.Mask = scaled;
                else
                    throw new ApiError(
                        "参考图已压缩，但蒙版缩放失败 —— 无法保证蒙版尺寸与参考图一致");
            }
        }
    }

    /// <summary>
    /// 仅在"超过单文件上限"或"长边超过 <see cref="Catalog.RefShrinkMaxSide"/>"时压缩。
    /// <paramref name="scale"/> 返回实际缩放比例（1.0 = 未压缩），供蒙版同步缩放。
    /// </summary>
    private static byte[] ShrinkIfNeeded(byte[] data, out double scale)
    {
        scale = 1.0;
        var size = ImageCodec.Size(data);
        if (size is not { } s) return data;    // 探测不到（非图片）→ 原样交给服务端报错

        long longSide = Math.Max(s.Width, s.Height);
        bool tooBig = data.LongLength > Catalog.MaxUpload;
        bool tooWide = longSide > Catalog.RefShrinkMaxSide;
        if (!tooBig && !tooWide) return data;   // ← 未超限：原样发送（保真）

        // 体积超标时按目标长边压；否则只压到刚好不超限
        int target = Catalog.RefShrinkMaxSide;
        var shrunk = ImageCodec.ShrinkForReference(data, target);
        scale = (double)Math.Max(1, (int)(longSide * Math.Min(1.0,
                    (double)target / longSide))) / Math.Max(1, longSide);
        // 用实际输出尺寸重算，避免估算误差导致蒙版与参考图差 1px
        if (ImageCodec.Size(shrunk) is { } ns && s.Width > 0)
            scale = (double)ns.Width / s.Width;
        return shrunk;
    }

    /// <summary>按比例缩放 PNG 蒙版（保持 Alpha 通道）。失败返回 null。</summary>
    private static byte[]? ScalePng(byte[] png, double scale)
    {
        if (scale <= 0 || Math.Abs(scale - 1.0) < 1e-9) return png;
        var size = ImageCodec.Size(png);
        if (size is not { } s) return null;
        int nw = Math.Max(1, (int)Math.Round(s.Width * scale));
        int nh = Math.Max(1, (int)Math.Round(s.Height * scale));
        return ImageCodec.ScaleKeepingAlpha(png, nw, nh);
    }

    /// <summary>
    /// 仅测试用：直接触发参考图/蒙版的规范化流程。
    /// 之所以暴露它，是因为"参考图有没有被压、压完蒙版还对不对得上"是本次修复的
    /// 核心不变量，而正常的 GenerateAsync 会真的发网络请求（测试无法离线断言）。
    /// </summary>
    internal static void PrepareRefsForTest(GenRequest req) => PrepareRefs(req);

    /// <summary>
    /// 提交前校验：只拦文档明确会报错的组合，避免"花了时间才发现参数不合法"。
    /// 不合法直接抛 <see cref="ApiError"/>（带可执行的修复建议）。
    /// </summary>
    private void ValidateRequest(GenRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt))
            throw new ApiError("提示词不能为空");

        var bgErr = Catalog.ValidateBackground(req.Background, req.OutputFormat);
        if (bgErr is not null) throw new ApiError(bgErr);

        // 精确像素尺寸的尺寸规则（按 provider 的文档各有一套，见 Catalog.ValidatePixelSizeFor）
        if (Catalog.ValidatePixelSizeFor(_provider, req.Model, req.Aspect) is { } pxErr)
            throw new ApiError(pxErr);

        int maxRefs = Catalog.MaxRefsFor(_provider);
        if (req.Refs is { Count: > 0 } && req.Refs.Count > maxRefs)
            throw new ApiError($"参考图最多 {maxRefs} 张（当前 {req.Refs.Count} 张）");

        // 蒙版三条硬要求（尺寸必须与第 1 张参考图一致；必须带 Alpha）
        if (req.Mask is { Length: > 0 })
        {
            var refSize = req.Refs is { Count: > 0 }
                ? ImageCodec.Size(req.Refs[0].Data) : null;
            if (Catalog.ValidateMask(req.Mask, ImageCodec.Size(req.Mask), refSize,
                                     req.Refs is { Count: > 0 }) is { } maskErr)
                throw new ApiError(maskErr);
            if (!Catalog.PngHasAlpha(req.Mask))
                throw new ApiError(
                    "蒙版必须是**带 Alpha 通道的 PNG**（alpha=0 的透明区域 = 要修改的地方）；"
                    + "普通黑白图不能直接当蒙版，服务端不会自动补 Alpha");
        }
    }

    // ---------------------------------------------------------------- 进度钩子（统一入口）

    /// <summary>
    /// 触发进度钩子（无任务号）。
    ///
    /// ⚠️ 为什么收拢成一个方法（v0.5.43）：
    ///   原来 18 处各写一遍 <c>try { OnSubmitProgress?.Invoke(...) } catch { }</c> ——
    ///   ① 样板重复，改动容易漏；② `catch { }` **完全静默**，
    ///   钩子里的 bug（上层落盘逻辑抛异常）永远查不到。
    ///   现在统一走这里：异常仍**不影响生成**（保持原语义），
    ///   但会记一条 **DEBUG 日志**（`IMGHUB_LOG_DEBUG=1` 时可见）。
    /// </summary>
    private void Notify(SubmitPhase phase, GenRequest req, bool ok)
        => Notify(phase, Array.Empty<string>(), req, ok);

    /// <summary>触发进度钩子（带任务号 / 结果图列表）。见 <see cref="Notify(SubmitPhase, GenRequest, bool)"/>。</summary>
    private void Notify(SubmitPhase phase, IReadOnlyList<string> images, GenRequest req, bool ok)
    {
        if (OnSubmitProgress is null) return;   // 没挂钩子 → 连 try 都省了
        try
        {
            OnSubmitProgress(phase, images, req.Prompt, req.Model, ok);
        }
        catch (Exception ex)
        {
            // 钩子异常**不影响生成**（原语义），但必须留痕：静默失败最难查。
            AppLog.Debug($"进度钩子抛异常，已忽略（phase={phase}, ok={ok}）",
                         ex, where: "ImageApi.Notify");
        }
    }

    // ---------------------------------------------------------------- OpenRouter（同步）
    private async Task<GenResult> GenerateOpenRouterAsync(GenRequest req)
    {
        // v5.26.0：同步接口无 task_id —— 发出前记一条"可能已扣费"痕迹，
        // 若进程在响应前被杀，重启时会如实提示用户去后台核对（不会假装能找回）。
        Notify(SubmitPhase.SyncSending, req, false);
        try
        {
            var result = req.Stream
                ? await GenerateOpenRouterStreamAsync(req).ConfigureAwait(false)
                : await GenerateOpenRouterCoreAsync(req).ConfigureAwait(false);
            Notify(SubmitPhase.Finished, req, true);
            return result;
        }
        catch
        {
            Notify(SubmitPhase.Finished, req, false);
            throw;
        }
    }

    /// <summary>
    /// 构造 OpenRouter <c>POST /images</c> 的请求体。
    ///
    /// 逐项对照 https://openrouter.ai/docs/guides/overview/multimodal/image-generation
    /// 的 "Request Parameters" 表：
    ///   model / prompt / n / resolution / aspect_ratio / size / quality /
    ///   output_format / background / output_compression / seed / stream /
    ///   input_references / user / provider.*
    ///
    /// ⚠️ 两处文档细节必须照做，否则会 400：
    ///   ① resolution 用档名（512/1K/2K/4K），不是 APIMart 的 1k/2k/4k；
    ///   ② 显式像素 size 是"权威"的，此时**不能再带** aspect_ratio / resolution，
    ///      否则文档说明会被拒绝（400）。
    /// </summary>
    internal static JsonObject BuildOpenRouterPayload(GenRequest req)
    {
        // ⚠️ 用 JsonObject（JSON DOM）而非 Dictionary：AOT 下反射序列化被禁用（CONSTRAINTS H1）
        // ⚠️ n 必须按**模型**钳制而非仅按 provider（v0.5.30 修两处真源）：
        //    gemini 图像系实测 supported_parameters.n.max = 1，若这里只按 provider 钳到 10，
        //    用户在多选/配置里留下的 n>1 会原样发出 → 服务端拒绝。
        //    UI 侧（MainViewModel.BatchNMax）已按模型收窄，这里必须用同一个真源。
        var payload = new JsonObject
        {
            ["model"] = req.Model,
            ["prompt"] = req.Prompt,
            ["quality"] = req.Quality,
            ["n"] = Math.Max(1, Math.Min(Catalog.MaxNFor(ApiProvider.OpenRouter, req.Model), req.N)),
        };

        bool explicitPixels = Catalog.ParsePixelSize(req.Aspect) is not null;
        if (explicitPixels)
        {
            // 显式像素是权威值：不能再带 aspect_ratio / resolution（文档：会被 400 拒绝）
            payload["size"] = req.Aspect.Trim();
        }
        else
        {
            payload["aspect_ratio"] = req.Aspect;
            // resolution：档名（512 / 1K / 2K / 4K）
            payload["resolution"] = Catalog.OpenRouterResolution(req.Resolution);
        }

        if (!string.IsNullOrWhiteSpace(req.OutputFormat))
            payload["output_format"] = req.OutputFormat.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(req.Background))
            payload["background"] = req.Background.Trim().ToLowerInvariant();
        if (req.OutputCompression > 0 && Catalog.CompressionApplies(req.OutputFormat))
            payload["output_compression"] = Math.Clamp(req.OutputCompression, 0, 100);
        if (int.TryParse((req.Seed ?? "").Trim(), out var seed))
            payload["seed"] = seed;
        if (req.Stream) payload["stream"] = true;

        if (req.Refs is { Count: > 0 })
        {
            var list = new JsonArray();
            foreach (var (data, media) in req.Refs.Take(Catalog.MaxRefs))
            {
                var b64 = Convert.ToBase64String(data);
                list.AddNode(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = $"data:{media};base64,{b64}",
                    },
                });
            }
            payload["input_references"] = list;

            // ⚠️ 蒙版**不**塞进 input_references（v5.27.0 决策）：
            //    OpenRouter 的 Image Generation 文档里**没有** mask 字段，
            //    input_references 的语义是"要参考/融合的图"。把一张黑底透明图当作
            //    参考图混进去，模型很可能把它当成"要融合的另一张素材"，
            //    反而破坏主体一致性 —— 属于"发明行为"，不是文档能力。
            //    所以局部重绘只在支持 mask_url 的 provider（APIMart）生效，
            //    OpenRouter 侧由上层改用「原图 + 标注合成图 + 提示词说明」表达区域
            //    （见 MainViewModel.EditWithRegionsAsync）。
        }

        if (req.Routing is { IsEmpty: false } r)
        {
            var provider = new JsonObject();
            AddStringArray(provider, "only", r.Only);
            AddStringArray(provider, "order", r.Order);
            AddStringArray(provider, "ignore", r.Ignore);
            if (!string.IsNullOrWhiteSpace(r.Sort)) provider["sort"] = r.Sort.Trim();
            if (r.AllowFallbacks is { } af) provider["allow_fallbacks"] = af;
            if (r.Options is { Count: > 0 } opts)
            {
                var options = new JsonObject();
                foreach (var (slug, kv) in opts)
                {
                    var one = new JsonObject();
                    foreach (var (k, v) in kv) one[k] = v;
                    options[slug] = one;
                }
                provider["options"] = options;
            }
            if (provider.Count > 0) payload["provider"] = provider;
        }

        return payload;
    }

    private static void AddStringArray(JsonObject target, string key, List<string>? values)
    {
        if (values is not { Count: > 0 }) return;
        var arr = new JsonArray();
        foreach (var v in values) arr.AddString(v);
        target[key] = arr;
    }

    private async Task<GenResult> GenerateOpenRouterCoreAsync(GenRequest req)
    {
        var payload = BuildOpenRouterPayload(req);
        using var doc = await _http.PostJsonAsync($"{_baseUrl}/images", payload, req.ApiKey,
                                                  ct: req.Ct).ConfigureAwait(false);
        return ParseOpenRouterResponse(doc.RootElement);
    }

    /// <summary>
    /// OpenRouter SSE 流式生图（文档 "Streaming Image Generation"）。
    /// 事件类型：image_generation.partial_image / image_generation.completed / error，
    /// 以 <c>data: [DONE]</c> 结束。部分图通过 <see cref="OnPartialImage"/> 回调（不落盘）。
    /// </summary>
    private async Task<GenResult> GenerateOpenRouterStreamAsync(GenRequest req)
    {
        var payload = BuildOpenRouterPayload(req);
        payload["stream"] = true;

        var images = new List<(byte[], string)>();
        double cost = 0;
        int tokens = 0;
        string? streamError = null;

        using var response = await _http.PostJsonStreamAsync($"{_baseUrl}/images", payload,
            req.ApiKey, req.Ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(req.Ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            req.Ct.ThrowIfCancellationRequested();
            // ⚠️ 不用 EndOfStream（异步方法里用它会同步阻塞，CA2024）：
            //    ReadLineAsync 返回 null 即流结束。
            var line = await reader.ReadLineAsync(req.Ct).ConfigureAwait(false);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            if (data == "[DONE]") break;

            JsonDocument evt;
            try { evt = JsonDocument.Parse(data); }
            catch (JsonException ex)
            {
                AppLog.Warn("SSE 事件不是合法 JSON（跳过）", "ImageApi.Stream", detail: data, ex: ex);
                continue;
            }
            using (evt)
            {
                var root = evt.RootElement;
                var type = root.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
                switch (type)
                {
                    case "image_generation.partial_image":
                        if (root.TryGetProperty("b64_json", out var pb) &&
                            pb.GetString() is { Length: > 0 } pb64)
                        {
                            int idx = root.TryGetProperty("partial_image_index", out var pidx) &&
                                      pidx.ValueKind == JsonValueKind.Number
                                ? pidx.GetInt32() : 0;
                            try { OnPartialImage?.Invoke(idx, Convert.FromBase64String(pb64)); }
                            catch (Exception ex)
                            {
                                AppLog.Warn("部分图回调失败（忽略）", "ImageApi.Stream", ex: ex);
                            }
                        }
                        break;

                    case "image_generation.completed":
                        if (root.TryGetProperty("b64_json", out var fb) &&
                            fb.GetString() is { Length: > 0 } fb64)
                        {
                            var media = root.TryGetProperty("media_type", out var fmt)
                                ? (fmt.GetString() ?? "image/png") : "image/png";
                            images.Add((Convert.FromBase64String(fb64), media));
                        }
                        ReadUsage(root, ref cost, ref tokens);
                        break;

                    case "error":
                        streamError = root.TryGetProperty("error", out var e) &&
                                      e.TryGetProperty("message", out var em)
                            ? em.GetString() : data;
                        break;
                }
            }
        }

        // 流式失败同样计入成本 0（文档：失败的生成不计费）
        if (images.Count == 0)
            throw new ApiError(streamError is { Length: > 0 }
                ? $"流式生成失败：{streamError}"
                : "流式响应结束但没有完整图像");
        return new GenResult(images, cost, tokens);
    }

    private static void ReadUsage(JsonElement root, ref double cost, ref int tokens)
    {
        if (!root.TryGetProperty("usage", out var usage) ||
            usage.ValueKind != JsonValueKind.Object) return;
        if (usage.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number)
            cost = c.GetDouble();
        if (usage.TryGetProperty("total_tokens", out var t) && t.ValueKind == JsonValueKind.Number)
            tokens = t.GetInt32();
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

    // ---------------------------------------------------------------- OpenAI 官方（同步 + multipart edits）
    /// <summary>
    /// OpenAI 官方 <c>api.openai.com</c> 直连。
    ///
    /// 两条子路径（docs/GPT Image generation.md "Generate Images" / "Edit Images"）：
    ///   · **无参考图** → <c>POST /images/generations</c>（**JSON**）
    ///   · **有参考图** → <c>POST /images/edits</c>（**multipart/form-data**，
    ///     文件字段 <c>image[]</c> 可多张、可选 <c>mask</c>、其余参数平铺为文本字段）
    ///
    /// ⚠️ 为什么必须分成两个端点（不能像 APIMart 那样只用一个）：
    ///   官方文档明确 "the Images API provides two endpoints"；
    ///   edits 走 multipart（curl 示例即 <c>-F "image[]=@a.png"</c>），
    ///   generations 只收 JSON。把参考图塞进 generations 会被直接拒。
    /// </summary>
    private async Task<GenResult> GenerateOpenAiAsync(GenRequest req)
    {
        return req.Refs is { Count: > 0 }
            ? await GenerateOpenAiEditAsync(req).ConfigureAwait(false)
            : await GenerateOpenAiGenerationAsync(req).ConfigureAwait(false);
    }

    /// <summary>OpenAI 文生图（POST /images/generations，JSON）。</summary>
    private async Task<GenResult> GenerateOpenAiGenerationAsync(GenRequest req)
    {
        Notify(SubmitPhase.SyncSending, req, false);

        try
        {
            // 流式（文档 "Streaming"）：仅 GPT Image 系支持；dall-e 系传 stream 会 400。
            bool streamable = req.Stream && !IsDallE3(req.Model);
            var result = streamable
                ? await GenerateOpenAiStreamAsync(req).ConfigureAwait(false)
                : await GenerateOpenAiCoreAsync(req).ConfigureAwait(false);
            Notify(SubmitPhase.Finished, req, true);
            return result;
        }
        catch
        {
            Notify(SubmitPhase.Finished, req, false);
            throw;
        }
    }

    private async Task<GenResult> GenerateOpenAiCoreAsync(GenRequest req)
    {
        var payload = BuildOpenAiGenerationPayload(req);
        using var doc = await _http.PostJsonAsync($"{_baseUrl}/images/generations",
            payload, req.ApiKey, ct: req.Ct).ConfigureAwait(false);
        return await ResolveImagesAsync(
            ParseOpenAiResponse(doc.RootElement, req.OutputFormat),
            req.ApiKey, req.OutputFormat, req.Ct).ConfigureAwait(false);
    }

    /// <summary>
    /// OpenAI SSE 流式生图（文档 "Streaming"）。
    ///
    /// 事件结构与 OpenRouter 的 SSE **同形**（都用 <c>image_generation.partial_image</c> /
    /// <c>image_generation.completed</c> + <c>b64_json</c> + <c>partial_image_index</c>），
    /// 因此复用同一套解析；差别只在请求参数叫 <c>partial_images</c>（0~3）
    /// 且响应体是 <c>{type, b64_json, ...}</c> 而不是 <c>data[]</c>。
    /// ⚠️ 流式**不做重试**：一旦开始推事件，重试会造成重复计费/重复部分图。
    /// </summary>
    private async Task<GenResult> GenerateOpenAiStreamAsync(GenRequest req)
    {
        var payload = BuildOpenAiGenerationPayload(req);
        payload["stream"] = true;
        payload["partial_images"] = Math.Clamp(req.PartialImages, 0, 3);

        var images = new List<(byte[], string)>();
        string? streamError = null;
        int tokens = 0;

        using var response = await _http.PostJsonStreamAsync($"{_baseUrl}/images/generations",
            payload, req.ApiKey, req.Ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(req.Ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            req.Ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(req.Ct).ConfigureAwait(false);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            if (data == "[DONE]") break;

            JsonDocument evt;
            try { evt = JsonDocument.Parse(data); }
            catch (JsonException ex)
            {
                AppLog.Warn("SSE 事件不是合法 JSON（跳过）", "ImageApi.OpenAiStream",
                            detail: data, ex: ex);
                continue;
            }
            using (evt)
            {
                var root = evt.RootElement;
                var type = root.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
                switch (type)
                {
                    case "image_generation.partial_image":
                        if (root.TryGetProperty("b64_json", out var pb) &&
                            pb.GetString() is { Length: > 0 } pb64)
                        {
                            int idx = root.TryGetProperty("partial_image_index", out var pidx) &&
                                      pidx.ValueKind == JsonValueKind.Number
                                ? pidx.GetInt32() : 0;
                            try { OnPartialImage?.Invoke(idx, Convert.FromBase64String(pb64)); }
                            catch (Exception ex)
                            {
                                AppLog.Warn("部分图回调失败（忽略）", "ImageApi.OpenAiStream", ex: ex);
                            }
                        }
                        break;

                    case "image_generation.completed":
                        if (root.TryGetProperty("b64_json", out var fb) &&
                            fb.GetString() is { Length: > 0 } fb64)
                            images.Add((Convert.FromBase64String(fb64),
                                        OutputFormatToMedia(req.OutputFormat)));
                        if (root.TryGetProperty("usage", out var usage) &&
                            usage.TryGetProperty("total_tokens", out var tk) &&
                            tk.ValueKind == JsonValueKind.Number)
                            tokens = tk.GetInt32();
                        break;

                    case "error":
                        streamError = root.TryGetProperty("error", out var e) &&
                                      e.TryGetProperty("message", out var em)
                            ? em.GetString() : data;
                        break;
                }
            }
        }

        if (images.Count == 0)
            throw new ApiError(streamError is { Length: > 0 }
                ? $"流式生成失败：{streamError}"
                : "流式响应结束但没有完整图像");
        return new GenResult(images, 0, tokens);
    }

    /// <summary>
    /// OpenAI 编辑/参考图（POST /images/edits，multipart/form-data）。
    /// 参考图按顺序作为 <c>image[]</c>（第 1 位 = 主体，与 APIMart 的 image_urls 同一约定），
    /// 蒙版作为 <c>mask</c>（文档：必须与第 1 张图同尺寸、含 Alpha 通道）。
    /// </summary>
    private async Task<GenResult> GenerateOpenAiEditAsync(GenRequest req)
    {
        Notify(SubmitPhase.SyncSending, req, false);

        var refs = req.Refs ?? new List<(byte[], string)>();
        var files = new List<(string Field, string FileName, string ContentType, byte[] Data)>();
        int idx = 0;
        foreach (var (data, media) in refs.Take(Catalog.MaxRefs))
        {
            idx++;
            req.Progress?.Report($"准备参考图 {idx}/{refs.Count}（{data.Length / 1024}KB）…");
            files.Add(("image[]", $"image{idx}.{ImageCodec.ExtFor(media)}", media, data));
        }
        if (req.Mask is { Length: > 0 })
        {
            req.Progress?.Report($"准备修改区域蒙版（{req.Mask.Length / 1024}KB）…");
            files.Add(("mask", "mask.png", "image/png", req.Mask));
        }

        var fields = BuildOpenAiEditFields(req);
        try
        {
            var result = req.Stream
                ? await GenerateOpenAiEditStreamAsync(req, files, fields).ConfigureAwait(false)
                : await GenerateOpenAiEditCoreAsync(req, files, fields).ConfigureAwait(false);
            Notify(SubmitPhase.Finished, req, true);
            return result;
        }
        catch
        {
            Notify(SubmitPhase.Finished, req, false);
            throw;
        }
    }

    private async Task<GenResult> GenerateOpenAiEditCoreAsync(
        GenRequest req,
        List<(string Field, string FileName, string ContentType, byte[] Data)> files,
        List<KeyValuePair<string, string>> fields)
    {
        using var doc = await _http.PostFormDataAsync($"{_baseUrl}/images/edits",
            fields, files, req.ApiKey, extraHeaders: null, ct: req.Ct).ConfigureAwait(false);
        return await ResolveImagesAsync(
            ParseOpenAiResponse(doc.RootElement, req.OutputFormat),
            req.ApiKey, req.OutputFormat, req.Ct).ConfigureAwait(false);
    }

    /// <summary>
    /// OpenAI <c>/images/edits</c> 的流式版本。
    /// ⚠️ edits 的事件类型是 <c>image_edit.partial_image</c> / <c>image_edit.completed</c>
    ///   （**不是** generations 的 <c>image_generation.*</c>）—— 混用会一个事件都收不到。
    /// </summary>
    private async Task<GenResult> GenerateOpenAiEditStreamAsync(
        GenRequest req,
        List<(string Field, string FileName, string ContentType, byte[] Data)> files,
        List<KeyValuePair<string, string>> fields)
    {
        var all = new List<KeyValuePair<string, string>>(fields)
        {
            new("stream", "true"),
            new("partial_images", Math.Clamp(req.PartialImages, 0, 3).ToString()),
        };

        var images = new List<(byte[], string)>();
        string? streamError = null;
        int tokens = 0;

        using var response = await _http.PostFormDataStreamAsync(
            $"{_baseUrl}/images/edits", all, files, req.ApiKey, req.Ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(req.Ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            req.Ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(req.Ct).ConfigureAwait(false);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            if (data == "[DONE]") break;

            JsonDocument evt;
            try { evt = JsonDocument.Parse(data); }
            catch (JsonException ex)
            {
                AppLog.Warn("SSE 事件不是合法 JSON（跳过）", "ImageApi.OpenAiEditStream",
                            detail: data, ex: ex);
                continue;
            }
            using (evt)
            {
                var root = evt.RootElement;
                var type = root.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
                switch (type)
                {
                    case "image_edit.partial_image":
                        if (root.TryGetProperty("b64_json", out var pb) &&
                            pb.GetString() is { Length: > 0 } pb64)
                        {
                            int idx = root.TryGetProperty("partial_image_index", out var pidx) &&
                                      pidx.ValueKind == JsonValueKind.Number
                                ? pidx.GetInt32() : 0;
                            try { OnPartialImage?.Invoke(idx, Convert.FromBase64String(pb64)); }
                            catch (Exception ex)
                            {
                                AppLog.Warn("部分图回调失败（忽略）", "ImageApi.OpenAiEditStream", ex: ex);
                            }
                        }
                        break;

                    case "image_edit.completed":
                        if (root.TryGetProperty("b64_json", out var fb) &&
                            fb.GetString() is { Length: > 0 } fb64)
                            images.Add((Convert.FromBase64String(fb64),
                                        OutputFormatToMedia(req.OutputFormat)));
                        if (root.TryGetProperty("usage", out var usage) &&
                            usage.TryGetProperty("total_tokens", out var tk) &&
                            tk.ValueKind == JsonValueKind.Number)
                            tokens = tk.GetInt32();
                        break;

                    case "error":
                        streamError = root.TryGetProperty("error", out var e) &&
                                      e.TryGetProperty("message", out var em)
                            ? em.GetString() : data;
                        break;
                }
            }
        }

        if (images.Count == 0)
            throw new ApiError(streamError is { Length: > 0 }
                ? $"流式编辑失败：{streamError}"
                : "流式响应结束但没有完整图像");
        return new GenResult(images, 0, tokens);
    }

    /// <summary>
    /// 构造 OpenAI <c>/images/generations</c> 的 JSON 请求体。
    ///
    /// 逐项对照 docs/GPT Image generation.md：
    ///   model / prompt / n / size / quality / background / output_format /
    ///   output_compression / moderation / response_format / style（仅 dall-e-3）
    ///
    /// ⚠️ 四条文档细节必须照做，否则 400 或参数被忽略：
    ///   ① <c>size</c> 就是 <c>WIDTHxHEIGHT</c> 或 <c>auto</c> —— **没有** aspect_ratio/resolution；
    ///   ② <c>response_format</c> 只对 dall-e-2/3 有意义（GPT Image 系恒返 b64，传了也只是被忽略）；
    ///   ③ GPT Image 2 / 2.5 **不接受</c> background=transparent**；
    ///   ④ <c>input_fidelity</c> 仅 gpt-image-1 支持，**gpt-image-2 必须省略</c>。
    /// </summary>
    internal static JsonObject BuildOpenAiGenerationPayload(GenRequest req)
    {
        var payload = new JsonObject
        {
            ["model"] = req.Model,
            ["prompt"] = req.Prompt,
            // n：文档 1~10，dall-e-3 仅 1（真源 = Catalog.MaxNFor）
            ["n"] = Math.Max(1, Math.Min(Catalog.MaxNFor(ApiProvider.OpenAi, req.Model), req.N)),
        };

        AddOpenAiCommonFields(payload, req);

        // dall-e-3 专有 style（vivid/natural）
        if (IsDallE3(req.Model) && !string.IsNullOrWhiteSpace(req.Style))
            payload["style"] = req.Style.Trim().ToLowerInvariant();

        return payload;
    }

    /// <summary>构造 OpenAI <c>/images/edits</c> 的 multipart 文本字段（与文件同级）。</summary>
    internal static List<KeyValuePair<string, string>> BuildOpenAiEditFields(GenRequest req)
    {
        var json = BuildOpenAiGenerationPayload(req);
        // edits 不支持 response_format / style 之外的差异由服务端忽略，
        // 但 generation 独有的字段照发也无害；这里原样平铺成文本。
        var fields = new List<KeyValuePair<string, string>>();
        foreach (var kv in json)
        {
            if (kv.Key is "model" or "prompt" or "n" or "size" or "quality")
            {
                fields.Add(new(kv.Key, kv.Value?.ToString() ?? ""));
            }
        }
        // edits 同样支持这些输出控制字段
        if (json["background"] is { } bg && !string.IsNullOrWhiteSpace(bg.ToString()))
            fields.Add(new("background", bg.ToString()));
        if (json["output_format"] is { } of && !string.IsNullOrWhiteSpace(of.ToString()))
            fields.Add(new("output_format", of.ToString()));
        if (json["output_compression"] is { } oc)
            fields.Add(new("output_compression", oc.ToString()));
        if (json["moderation"] is { } md && !string.IsNullOrWhiteSpace(md.ToString()))
            fields.Add(new("moderation", md.ToString()));
        if (json["input_fidelity"] is { } f && !string.IsNullOrWhiteSpace(f.ToString()))
            fields.Add(new("input_fidelity", f.ToString()));
        return fields;
    }

    /// <summary>OpenAI generations / edits 共有的参数（size / quality / 输出控制）。</summary>
    private static void AddOpenAiCommonFields(JsonObject payload, GenRequest req)
    {
        // ① size：像素串或 auto。
        //    ⚠️ 内部统一默认画幅是 "1:1"（比例名，给 OpenRouter/APIMart 用），
        //       OpenAI 的 size **不认识比例名** → "1:1" 与空值都必须转成 auto，
        //       否则会把 "1:1" 当尺寸发出去被 400 拒绝。
        var size = (req.Aspect ?? "").Trim();
        bool sizeIsRatioOrEmpty = size.Length == 0 ||
                                  size.Equals("1:1", StringComparison.OrdinalIgnoreCase);
        payload["size"] = sizeIsRatioOrEmpty ? "auto" : size;

        // ② quality：GPT Image 系 low/medium/high[/xhigh/max]/auto；dall-e-3 是 standard/hd。
        //    ⚠️ 必须按模型过滤：把 "auto" 发给 dall-e-3 会 400（它的合法值里没有 auto）。
        var quality = (req.Quality ?? "").Trim();
        var allowed = Catalog.QualityChoices(ApiProvider.OpenAi, req.Model);
        if (quality.Length > 0 && allowed.Contains(quality)) payload["quality"] = quality;
        else if (allowed.Length > 0) payload["quality"] = allowed[0];

        // ③ background：仅 GPT Image 系；2/2.5 不支持 transparent（文档明确报错）
        var bg = (req.Background ?? "").Trim().ToLowerInvariant();
        if (bg.Length > 0 && Catalog.SupportsTransparentBackground(ApiProvider.OpenAi, req.Model))
            payload["background"] = bg;

        // ④ output_format：仅 GPT Image 系（dall-e-3 没有这个参数）
        if (!IsDallE3(req.Model) && !string.IsNullOrWhiteSpace(req.OutputFormat))
        {
            var fmt = req.OutputFormat.Trim().ToLowerInvariant();
            if (fmt is "png" or "jpeg" or "webp") payload["output_format"] = fmt;
        }

        // ⑤ output_compression：仅 jpeg/webp 生效
        if (req.OutputCompression > 0 && Catalog.CompressionApplies(req.OutputFormat) &&
            !IsDallE3(req.Model))
            payload["output_compression"] = Math.Clamp(req.OutputCompression, 0, 100);

        // ⑥ moderation：auto / low（Gpt Image 系）
        if (!IsDallE3(req.Model) && !string.IsNullOrWhiteSpace(req.Moderation))
        {
            var mod = req.Moderation.Trim().ToLowerInvariant();
            if (mod is "auto" or "low") payload["moderation"] = mod;
        }

        // ⑦ input_fidelity：仅 gpt-image-1 系支持；gpt-image-2 **必须省略**（文档明确）
        var m = (req.Model ?? "").Trim().ToLowerInvariant();
        bool inputFidelityAllowed = m.StartsWith("gpt-image-1", StringComparison.Ordinal) &&
                                    !m.Contains("mini", StringComparison.Ordinal);
        if (inputFidelityAllowed && !string.IsNullOrWhiteSpace(req.InputFidelity))
        {
            var f = req.InputFidelity.Trim().ToLowerInvariant();
            if (f is "high" or "low") payload["input_fidelity"] = f;
        }
    }

    private static bool IsDallE3(string? model) =>
        string.Equals((model ?? "").Trim(), "dall-e-3", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 解析 OpenAI 的 <c>ImagesResponse</c>：<c>data[].b64_json</c>（GPT Image 系恒为 base64）
    /// 或 <c>data[].url</c>（dall-e 系按 response_format 可能是 URL）。
    /// <c>usage</c> 里有 input/output token（可反推成本）。
    /// </summary>
    internal static GenResult ParseOpenAiResponse(JsonElement root, string? outputFormat)
    {
        var images = new List<(byte[], string)>();
        var media = OutputFormatToMedia(outputFormat);

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("b64_json", out var b64El))
                {
                    var b64 = b64El.GetString();
                    if (!string.IsNullOrEmpty(b64))
                    {
                        images.Add((Convert.FromBase64String(b64), media));
                        continue;
                    }
                }
                // dall-e 系可能只给 URL（response_format=url）
                if (item.TryGetProperty("url", out var urlEl) &&
                    urlEl.GetString() is { Length: > 0 } url)
                {
                    // URL 需上层下载；这里用一个占位信号让调用方知道要下载
                    images.Add((Encoding.UTF8.GetBytes(url), UrlMediaMarker));
                }
            }
        }
        if (images.Count == 0)
            throw new ApiError($"响应里没有图像数据：{Trunc(root.ToString(), 200)}");

        double cost = 0;
        int tokens = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("total_tokens", out var t) && t.ValueKind == JsonValueKind.Number)
                tokens = t.GetInt32();
        }
        return new GenResult(images, cost, tokens);
    }

    /// <summary>URL 结果标记：表示"这一项是待下载的 URL"而不是图片字节。</summary>
    internal const string UrlMediaMarker = "url/marker";

    /// <summary>
    /// 把 <see cref="UrlMediaMarker"/> 的 URL 项下载成真实图片字节。
    ///
    /// ⚠️ 为什么在 Core 内下载而不是把 URL 抛给 UI：
    ///   · DashScope（千问）**只返回 URL**（文档：有效期 24 小时）；
    ///   · OpenAI 的 dall-e 系在 response_format=url 时也只给 URL。
    ///   上层（MainViewModel.LandResultsAsync）拿到的契约是「(字节, media)」并直接写盘，
    ///   若把 URL 原样传上去就会被当成图片字节写成损坏文件。
    /// </summary>
    private async Task<GenResult> ResolveImagesAsync(GenResult res, string? apiKey,
                                                     string outputFormat, CancellationToken ct)
    {
        var media = OutputFormatToMedia(outputFormat);
        var list = new List<(byte[], string)>(res.Images.Count);
        int failures = 0;
        foreach (var (data, m) in res.Images)
        {
            if (m != UrlMediaMarker) { list.Add((data, m)); continue; }
            var url = Encoding.UTF8.GetString(data);
            try
            {
                // OSS 预签名直链（千问）不需要鉴权；带上也无害。
                var bytes = await _http.GetBytesAsync(url, apiKey,
                    TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
                list.Add((bytes, media));
            }
            catch (Exception ex)
            {
                failures++;
                AppLog.Warn($"结果图下载失败：{Trunc(url, 120)}",
                            "ImageApi.ResolveImagesAsync", ex: ex);
            }
        }
        if (list.Count == 0 && failures > 0)
            throw new ApiError($"共 {failures} 张结果图下载失败（链接有效期 24 小时，请重试）");
        return new GenResult(list, res.Cost, res.Tokens, res.RawUsage);
    }

    private static string OutputFormatToMedia(string? outputFormat) =>
        (outputFormat ?? "").Trim().ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => "image/jpeg",
            "webp" => "image/webp",
            _ => "image/png",
        };

    // ---------------------------------------------------------------- 千问 DashScope（原生协议）
    /// <summary>
    /// 阿里云百炼 DashScope 原生协议（千问 Qwen-Image）。
    ///
    /// 两条子路径（docs/千问-*.md）：
    ///   · **3.0 系** → <c>POST /api/v1/services/aigc/multimodal-generation/generation</c>
    ///     （**同步**；input.messages[0].content = [{image}...{text}]，支持 1-3 张参考图，
    ///       n 1-6，支持 negative_prompt/prompt_extend/seed/watermark）
    ///   · **2.0 / max / plus / qwen-image 系** → 走 <c>text2image/image-synthesis</c>
    ///     （**异步**，需 <c>X-DashScope-Async: enable</c> 头 + 轮询 <c>/api/v1/tasks/{id}</c>）
    ///
    /// ⚠️ 为什么按模型分两条链路：官方文档明确 3.0 系的同步接口"功能最完整"，
    ///    而 text2image 的异步接口只受理 qwen-image / qwen-image-plus；
    ///    混用会让 2.0 系拿到「current user api does not support synchronous calls」。
    /// </summary>
    private async Task<GenResult> GenerateDashScopeAsync(GenRequest req)
    {
        var model = (req.Model ?? "").Trim();
        bool multimodal = model.StartsWith("qwen-image-3.0", StringComparison.OrdinalIgnoreCase);
        return multimodal
            ? await GenerateDashScopeMultimodalAsync(req).ConfigureAwait(false)
            : await GenerateDashScopeAsyncTaskAsync(req).ConfigureAwait(false);
    }

    /// <summary>千问 3.0 系：DashScope 同步 multimodal-generation。</summary>
    private async Task<GenResult> GenerateDashScopeMultimodalAsync(GenRequest req)
    {
        Notify(SubmitPhase.SyncSending, req, false);

        var payload = BuildDashScopeMultimodalPayload(req);
        try
        {
            using var doc = await _http.PostJsonAsync(
                $"{_baseUrl}/api/v1/services/aigc/multimodal-generation/generation",
                payload, req.ApiKey, ct: req.Ct).ConfigureAwait(false);
            var result = await ResolveImagesAsync(
                ParseDashScopeResponse(doc.RootElement),
                req.ApiKey, req.OutputFormat, req.Ct).ConfigureAwait(false);
            Notify(SubmitPhase.Finished, req, true);
            return result;
        }
        catch
        {
            Notify(SubmitPhase.Finished, req, false);
            throw;
        }
    }

    /// <summary>
    /// 千问 2.0 / max / plus / qwen-image：DashScope text2image 异步任务。
    /// 流程与 APIMart 同形（提交拿 task_id → 轮询 /api/v1/tasks/{id}）。
    /// </summary>
    private async Task<GenResult> GenerateDashScopeAsyncTaskAsync(GenRequest req)
    {
        var payload = BuildDashScopeText2ImagePayload(req);
        using var submitDoc = await _http.PostJsonAsync(
            $"{_baseUrl}/api/v1/services/aigc/text2image/image-synthesis",
            payload, req.ApiKey,
            extraHeaders: new[] { new KeyValuePair<string, string>("X-DashScope-Async", "enable") },
            ct: req.Ct).ConfigureAwait(false);

        var submit = submitDoc.RootElement;
        ThrowIfDashScopeError(submit);

        string? taskId = null;
        if (submit.TryGetProperty("output", out var outEl) &&
            outEl.TryGetProperty("task_id", out var tid))
            taskId = tid.GetString();
        if (string.IsNullOrEmpty(taskId))
            throw new ApiError($"提交成功但没有 task_id：{Trunc(submit.ToString(), 200)}");

        // 拿到 task_id 立即落盘（与 APIMart 同一时机、同一理由：崩溃后可找回，不重复扣费）
        Notify(SubmitPhase.AsyncSubmitted, new[] { taskId! }, req, false);

        try
        {
            var result = await PollDashScopeTaskAsync(taskId!, req.ApiKey, req.Ct)
                               .ConfigureAwait(false);
            Notify(SubmitPhase.Finished, new[] { taskId! }, req, true);
            return result;
        }
        catch
        {
            Notify(SubmitPhase.Finished, new[] { taskId! }, req, false);
            throw;
        }
    }

    /// <summary>
    /// 构造千问 3.0 的 DashScope 同步请求体。
    /// 逐项对照 docs/千问-图像生成与编辑3.0 API参考.md "DashScope同步调用"：
    ///   model / input.messages[0] = {role, content:[{image}..., {text}]} /
    ///   parameters.{n, size, negative_prompt, prompt_extend, prompt_extend_mode,
    ///               enable_thinking, seed, watermark}
    /// </summary>
    internal static JsonObject BuildDashScopeMultimodalPayload(GenRequest req)
    {
        // content 顺序：先参考图（按上传顺序，第 1 位 = 主体），再文本提示词。
        var content = new JsonArray();
        if (req.Refs is { Count: > 0 })
        {
            foreach (var (data, media) in req.Refs.Take(DashScopeMaxRefImages))
                content.Add(new JsonObject { ["image"] = DataUrl(data, media) });
        }
        content.Add(new JsonObject { ["text"] = req.Prompt });

        var message = new JsonObject
        {
            ["role"] = "user",
            ["content"] = content,
        };

        var parameters = new JsonObject();
        int n = Math.Max(1, Math.Min(Catalog.MaxNFor(ApiProvider.DashScope, req.Model), req.N));
        parameters["n"] = n;

        var size = (req.Aspect ?? "").Trim();
        if (size.Length > 0 && !size.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            !size.Equals("1:1", StringComparison.OrdinalIgnoreCase))
            parameters["size"] = size;    // 千问用 "宽*高" 与 "宽x高" 皆可，见下

        if (!string.IsNullOrWhiteSpace(req.NegativePrompt))
            parameters["negative_prompt"] = req.NegativePrompt.Trim();
        parameters["prompt_extend"] = req.PromptExtend;
        // prompt_extend_mode：agent 仅 T2I 支持，I2I 传它会 400 → 必须按有无参考图收窄
        var mode = (req.PromptExtendMode ?? "").Trim().ToLowerInvariant();
        if (mode is "direct" or "agent")
        {
            if (mode == "agent" && req.Refs is { Count: > 0 })
                mode = "direct";   // I2I 不支持 agent
            parameters["prompt_extend_mode"] = mode;
        }
        if (req.Watermark) parameters["watermark"] = true;
        if (int.TryParse((req.Seed ?? "").Trim(), out var seed) && seed >= 0)
            parameters["seed"] = seed;

        return new JsonObject
        {
            ["model"] = req.Model,
            ["input"] = new JsonObject { ["messages"] = new JsonArray { message } },
            ["parameters"] = parameters,
        };
    }

    /// <summary>
    /// 构造千问 2.0/max/plus/qwen-image 的 text2image 异步请求体。
    /// 对照 docs/千问-文生图API参考.md "异步接口"：model / input.prompt / input.negative_prompt /
    /// parameters.{size, n(固定1), prompt_extend}
    /// </summary>
    internal static JsonObject BuildDashScopeText2ImagePayload(GenRequest req)
    {
        var input = new JsonObject { ["prompt"] = req.Prompt };
        if (!string.IsNullOrWhiteSpace(req.NegativePrompt))
            input["negative_prompt"] = req.NegativePrompt.Trim();

        var parameters = new JsonObject();
        var size = (req.Aspect ?? "").Trim();
        // text2image 的 size 用 "宽*高"（星号），与 multimodal 的 "宽x高"不同 —— 见文档。
        if (size.Contains('x', StringComparison.OrdinalIgnoreCase))
            size = size.Replace('x', '*').Replace('X', '*');
        if (size.Length > 0 && !size.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            !size.Equals("1:1", StringComparison.OrdinalIgnoreCase))
            parameters["size"] = size;
        // 文档：n 当前固定为 1，设置其他值会报错 → 不传 n
        parameters["prompt_extend"] = req.PromptExtend;

        return new JsonObject
        {
            ["model"] = req.Model,
            ["input"] = input,
            ["parameters"] = parameters,
        };
    }

    /// <summary>千问 3.0 参考图上限（文档：I2I 支持传入 1-3 张图像）。</summary>
    internal const int DashScopeMaxRefImages = 3;

    /// <summary>把参考图字节转成 DashScope 接受的 <c>data:{mime};base64,{...}</c> 内联形式。</summary>
    private static string DataUrl(byte[] data, string media) =>
        $"data:{media};base64,{Convert.ToBase64String(data)}";

    /// <summary>解析千问 DashScope 响应（同步 multimodal 与异步任务查询同一结构）。</summary>
    internal static GenResult ParseDashScopeResponse(JsonElement root)
    {
        ThrowIfDashScopeError(root);

        var images = new List<(byte[], string)>();
        if (root.TryGetProperty("output", out var output) &&
            output.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("message", out var msg) ||
                    !msg.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.Array) continue;
                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("image", out var imgEl) &&
                        imgEl.GetString() is { Length: > 0 } url)
                    {
                        // DashScope 返回的是 URL（有效期 24 小时）→ 标记为待下载
                        images.Add((Encoding.UTF8.GetBytes(url), UrlMediaMarker));
                    }
                }
            }
        }
        if (images.Count == 0)
            throw new ApiError($"响应里没有图像数据：{Trunc(root.ToString(), 200)}");

        int tokens = 0;
        if (root.TryGetProperty("usage", out var usage) &&
            usage.TryGetProperty("image_count", out var ic) && ic.ValueKind == JsonValueKind.Number)
            tokens = 0;   // DashScope 的 usage 是"图片计量"而非 token，不当作 token 记
        return new GenResult(images, 0, tokens);
    }

    /// <summary>千问错误体（code/message 顶层，HTTP 200 也可能带 code）。</summary>
    private static void ThrowIfDashScopeError(JsonElement root)
    {
        if (!root.TryGetProperty("code", out var codeEl)) return;
        var code = codeEl.GetString();
        if (string.IsNullOrEmpty(code)) return;
        var msg = root.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : "";
        throw new ApiError($"{msg} (code {code})");
    }

    /// <summary>轮询千问异步任务直至 SUCCEEDED/FAILED/超时。</summary>
    private async Task<GenResult> PollDashScopeTaskAsync(string taskId, string apiKey,
                                                         CancellationToken ct,
                                                         double maxWait = 300,
                                                         double pollInterval = 3.0)
    {
        var url = $"{_baseUrl}/api/v1/tasks/{taskId}";
        double elapsed = 0;
        while (elapsed < maxWait)
        {
            ct.ThrowIfCancellationRequested();
            using var doc = await _http.GetJsonWithHeadersAsync(url, apiKey,
                timeout: TimeSpan.FromSeconds(30), ct: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            ThrowIfDashScopeError(root);

            string status = "";
            if (root.TryGetProperty("output", out var outEl) &&
                outEl.TryGetProperty("task_status", out var st))
                status = (st.GetString() ?? "").ToUpperInvariant();

            switch (status)
            {
                case "SUCCEEDED":
                    return await ResolveImagesAsync(ParseDashScopeResponse(root),
                                                    apiKey, "png", ct).ConfigureAwait(false);
                case "FAILED":
                case "CANCELED":
                case "UNKNOWN":
                {
                    var msg = outEl.TryGetProperty("message", out var mm) ? (mm.GetString() ?? "") : "";
                    throw new ApiError($"任务 {status}：{msg}");
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(pollInterval), ct).ConfigureAwait(false);
            elapsed += pollInterval;
        }
        throw new ApiError($"轮询超时（{maxWait:0}s）：任务 {taskId} 仍未完成");
    }

    // ================================================================ 即梦（火山引擎）
    /// <summary>
    /// 即梦（字节跳动 / 火山引擎视觉智能）链路。
    ///
    /// 与其它 provider 的**三个根本差异**（见 docs/即梦AI-*.md）：
    ///   ① **不是 REST 资源路径** —— 而是 RPC 风格：<c>Action</c> + <c>Version</c> 写在 query 上，
    ///      同一个 URL 靠 Action 区分"提交任务"与"查询结果"；
    ///   ② **鉴权是 AK/SK 签名**（<see cref="VolcSigner"/>），不是 Bearer token；
    ///      且 Region/Service 固定为 <c>cn-north-1</c> / <c>cv</c>；
    ///   ③ **两条业务链路共用一个协议**：生成（<c>jimeng_t2i_v40</c>）与
    ///      提取（两个 req_key）都走"提交 → 轮询"，差别只在 body 字段名。
    /// </summary>
    private async Task<GenResult> GenerateJimengAsync(GenRequest req)
    {
        var reqKey = (req.Model ?? "").Trim();
        bool isExtract = Catalog.IsJimengExtractReqKey(reqKey);

        // ① 提交任务
        var payload = isExtract
            ? BuildJimengExtractPayload(req, reqKey)
            : BuildJimengGenerationPayload(req, reqKey);

        if (isExtract)
        {
            // 提取：文档要求**必填 1 张图**（base64 或 URL 二选一）。
            // 这里用 base64 内联（与千问一致）：无需先上传换公网 URL，少一次往返。
            var refs = req.Refs ?? new List<(byte[], string)>();
            if (refs.Count == 0)
                throw new ApiError("即梦提取需要 1 张输入图 —— 请先在「参考图」里选一张"
                                 + "（或选中历史里的图后点「提取」）");
            if (refs.Count > 1)
                throw new ApiError($"即梦提取只接受 1 张输入图（当前 {refs.Count} 张）");
            var (data, media) = refs[0];
            payload["binary_data_base64"] = new JsonArray { Convert.ToBase64String(data) };

            // 文档：两份提取文档都**只**给了这 5 个参数，传多余的字段会被忽略；
            // 其中 width/height 默认 2048、范围 [1024, 4096] —— 由 BuildJimengExtractPayload 处理。
            _ = media;
        }
        else
        {
            // 生成：参考图 0~10 张。文档接受 image_urls（公网 URL）；
            // 但为免用户先去别处上传，这里用 **data URL 内联**（文档未禁止，
            // 且 OpenRouter/千问都这么做）。数量与体积上限由 ValidateRequest 前置把关。
            if (req.Refs is { Count: > 0 })
            {
                var arr = new JsonArray();
                foreach (var (data, media) in req.Refs.Take(JimengMaxRefImages))
                    arr.Add($"data:{media};base64,{Convert.ToBase64String(data)}");
                payload["image_urls"] = arr;
            }
        }

        using var submitDoc = await PostSignedJimengAsync(
            Catalog.JimengSubmitAction, payload, req, req.Ct).ConfigureAwait(false);
        var submit = submitDoc.RootElement;

        // ② 判 code：文档明确「code != 10000 时不会返回 task_id」
        ThrowIfJimengError(submit);

        string? taskId = null;
        if (submit.TryGetProperty("data", out var d) &&
            d.TryGetProperty("task_id", out var tid))
            taskId = tid.GetString();
        if (string.IsNullOrEmpty(taskId))
            throw new ApiError($"提交成功但没有 task_id：{Trunc(submit.ToString(), 200)}");

        // ③ 拿到 task_id 立即登记落盘（与 APIMart / 千问同一时机、同一理由：
        //    崩溃后只 GET 查询即可找回，不会重复扣费）
        Notify(SubmitPhase.AsyncSubmitted, new[] { taskId! }, req, false);

        try
        {
            var result = await PollJimengTaskAsync(taskId!, req, req.Ct).ConfigureAwait(false);
            Notify(SubmitPhase.Finished, new[] { taskId! }, req, true);
            return result;
        }
        catch
        {
            Notify(SubmitPhase.Finished, new[] { taskId! }, req, false);
            throw;
        }
    }

    /// <summary>即梦生成链路的参考图上限（文档：支持输入 0 至 10 张图）。</summary>
    internal const int JimengMaxRefImages = 10;

    /// <summary>
    /// 构造即梦**生成**请求体（docs/即梦AI-图片生成4.md 的 Body 参数表）。
    ///
    /// <c>req_key</c> / <c>prompt</c> / <c>size</c> / <c>width</c>+<c>height</c> /
    /// <c>scale</c> / <c>force_single</c>
    ///
    /// ⚠️ 两处文档细节：
    ///   ① **面积与宽高是"2 选 1"**：都传时以宽高为准；只传面积时模型自判比例。
    ///      代码里：画幅是像素串 → 传 width+height；是 auto → 传默认面积。
    ///   ② <c>width</c>/<c>height</c> **必须同时传**才生效（只传一个会被忽略）。
    /// </summary>
    internal static JsonObject BuildJimengGenerationPayload(GenRequest req, string reqKey)
    {
        var payload = new JsonObject
        {
            ["req_key"] = reqKey,
            ["prompt"] = req.Prompt,
        };

        if (Catalog.ParsePixelSize(req.Aspect) is { } px)
        {
            // 显式像素：宽高优先（文档："面积和宽高同时输入时，优先使用宽高"）
            payload["width"] = px.Width;
            payload["height"] = px.Height;
        }
        else
        {
            // "auto"（或空）→ 只传面积，由模型按 prompt 意图判断宽高比
            payload["size"] = Catalog.JimengDefaultPixels;
        }

        // scale：仅生成链路有；null = 不传（服务端默认 0.5）
        if (req.JimengScale is { } sc)
            payload["scale"] = Math.Round(Math.Clamp(sc, 0, 1), 2);

        // force_single：为 true 时才传（文档默认 false，省一个字段）
        if (req.JimengForceSingle) payload["force_single"] = true;

        return payload;
    }

    /// <summary>
    /// 构造即梦**提取**请求体（两份素材提取文档的 Body 参数表）。
    ///
    /// ⚠️ 两份文档的差异（这是最容易写错的地方）：
    ///   · 商品提取：req_key <c>jimeng_i2i_extract_tiled_images</c>，
    ///     指令字段叫 **<c>edit_prompt</c>**；
    ///   · 元素提取：req_key <c>i2i_material_extraction</c>，
    ///     指令字段叫 **<c>image_edit_prompt</c>**，且**多一个** <c>lora_weight</c>。
    ///   传错字段名 = "缺少必选参数"。
    /// </summary>
    internal static JsonObject BuildJimengExtractPayload(GenRequest req, string reqKey)
    {
        var kind = JimengExtract.KindOfReqKey(reqKey);
        var payload = new JsonObject
        {
            ["req_key"] = reqKey,
            // 指令：优先用 UI 传的预设文案；没传则退化为用户输入的 prompt
            //（保证"提取"在没选预设时也有一条合法指令，而不是空字段被拒）
            [JimengExtract.PromptField(kind)] = string.IsNullOrWhiteSpace(req.JimengExtractPrompt)
                ? req.Prompt
                : req.JimengExtractPrompt,
        };

        // 宽高：文档默认 2048、范围 [1024, 4096]；显式像素时按用户的来
        if (Catalog.ParsePixelSize(req.Aspect) is { } px)
        {
            payload["width"] = px.Width;
            payload["height"] = px.Height;
        }
        else
        {
            payload["width"] = 2048;
            payload["height"] = 2048;
        }

        // lora_weight：**仅元素提取**支持（商品提取文档里没有这个参数）
        if (kind == JimengExtract.JimengExtractKind.Element && req.JimengLoraWeight is { } lw)
            payload["lora_weight"] = Math.Round(Math.Clamp(lw, 0, 2), 3);

        return payload;
    }

    /// <summary>
    /// 构造即梦**查询结果**请求体。
    /// 文档：<c>req_key</c> + <c>task_id</c> 必填；<c>req_json</c> 可选（水印 / 是否返回链接）。
    /// </summary>
    internal static JsonObject BuildJimengQueryPayload(GenRequest req, string taskId)
    {
        var payload = new JsonObject
        {
            ["req_key"] = req.Model,
            ["task_id"] = taskId,
        };

        // req_json：文档要求"json 序列化后的**字符串**"（不是嵌套对象！）
        var reqJson = new JsonObject();
        if (req.JimengReturnUrl) reqJson["return_url"] = true;
        if (req.JimengLogo is { AddLogo: true } logo)
        {
            reqJson["logo_info"] = new JsonObject
            {
                ["add_logo"] = true,
                ["position"] = logo.Position,
                ["language"] = logo.Language,
                ["opacity"] = Math.Round(Math.Clamp(logo.Opacity, 0, 1), 2),
                ["logo_text_content"] = logo.TextContent,
            };
        }
        if (reqJson.Count > 0) payload["req_json"] = reqJson.ToJsonLine();

        return payload;
    }

    /// <summary>
    /// 发一个已签名的即梦请求（Action 走 query，Region/Service 固定）。
    /// </summary>
    private async Task<JsonDocument> PostSignedJimengAsync(string action, JsonObject payload,
                                                           GenRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ApiKey) || string.IsNullOrWhiteSpace(req.ApiSecret))
            throw new ApiError("即梦需要 **AccessKeyId + SecretAccessKey** 两把密钥 —— "
                             + "请在设置里分别填写（火山引擎控制台的「访问密钥」）");

        var body = payload.ToJsonLine();
        var uri = new Uri(_baseUrl);
        var query = new[]
        {
            new KeyValuePair<string, string>("Action", action),
            new KeyValuePair<string, string>("Version", Catalog.JimengVersion),
        };

        var signed = VolcSigner.Sign(
            req.ApiKey, req.ApiSecret,
            Catalog.JimengRegion, Catalog.JimengService, uri.Host,
            "POST", uri.AbsolutePath.Length == 0 ? "/" : uri.AbsolutePath,
            query, body, DateTimeOffset.UtcNow);

        var headers = new List<KeyValuePair<string, string>>
        {
            new("X-Date", signed.XDate),
            new("X-Content-Sha256", signed.ContentSha256),
            new("Authorization", signed.Authorization),
        };

        // ⚠️ query 拼进 URL：_http 的 PostJsonAsync 用整串作 URL，
        //    签名时的 CanonicalQuery 已按同样顺序编码，两者必须一致。
        var url = $"{_baseUrl}?Action={action}&Version={Catalog.JimengVersion}";
        return await _http.PostJsonAsync(url, payload, req.ApiKey,
                                         ct: ct, extraHeaders: headers).ConfigureAwait(false);
    }

    /// <summary>
    /// 轮询即梦任务直至 <c>done</c> 或超时。
    /// 文档状态机：in_queue → generating → done（done 也可能是失败，看外层 code）。
    /// </summary>
    private async Task<GenResult> PollJimengTaskAsync(string taskId, GenRequest req,
                                                      CancellationToken ct,
                                                      double maxWait = 300,
                                                      double pollInterval = 3.0)
    {
        double elapsed = 0;
        while (elapsed < maxWait)
        {
            ct.ThrowIfCancellationRequested();
            var payload = BuildJimengQueryPayload(req, taskId);
            using var doc = await PostSignedJimengAsync(
                Catalog.JimengGetResultAction, payload, req, ct).ConfigureAwait(false);
            var root = doc.RootElement;

            // 文档：**先判 code=10000，再判 data.status**，否则解析可能异常
            ThrowIfJimengError(root);

            string status = "";
            if (root.TryGetProperty("data", out var d) &&
                d.TryGetProperty("status", out var st))
                status = (st.GetString() ?? "").Trim().ToLowerInvariant();

            switch (status)
            {
                case "done":
                    return await ParseJimengResultAsync(d, req, ct).ConfigureAwait(false);
                case "not_found":
                    throw new ApiError("即梦任务未找到：可能无此任务，或任务已过期（12 小时）");
                case "expired":
                    throw new ApiError("即梦任务已过期，请重新提交");
                // in_queue / generating → 继续等
            }

            await Task.Delay(TimeSpan.FromSeconds(pollInterval), ct).ConfigureAwait(false);
            elapsed += pollInterval;
        }
        throw new ApiError($"轮询超时（{maxWait:0}s）：即梦任务 {taskId} 仍未完成");
    }

    /// <summary>
    /// 解析即梦结果：优先用 <c>binary_data_base64</c>（自包含、无过期问题），
    /// 其次用 <c>image_urls</c>（文档：有效期 24 小时，需再下载）。
    /// </summary>
    private async Task<GenResult> ParseJimengResultAsync(JsonElement data, GenRequest req,
                                                        CancellationToken ct)
    {
        var images = new List<(byte[], string)>();

        // ① base64（首选）
        if (data.TryGetProperty("binary_data_base64", out var b64) &&
            b64.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in b64.EnumerateArray())
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s))
                    images.Add((Convert.FromBase64String(s), "image/png"));
            }
        }

        // ② URL（base64 缺失时）
        if (images.Count == 0 &&
            data.TryGetProperty("image_urls", out var urls) &&
            urls.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in urls.EnumerateArray())
            {
                var u = item.GetString();
                if (!string.IsNullOrEmpty(u))
                    images.Add((Encoding.UTF8.GetBytes(u), UrlMediaMarker));
            }
        }

        if (images.Count == 0)
            throw new ApiError($"即梦返回里没有图像数据：{Trunc(data.ToString(), 200)}");

        return await ResolveImagesAsync(new GenResult(images), req.ApiKey,
                                        "png", ct).ConfigureAwait(false);
    }

    /// <summary>即梦错误体：<c>code</c> 非 10000 即失败（文档：此时不会返回 task_id）。</summary>
    private static void ThrowIfJimengError(JsonElement root)
    {
        if (!root.TryGetProperty("code", out var codeEl)) return;
        int code = codeEl.ValueKind == JsonValueKind.Number ? codeEl.GetInt32() : 10000;
        if (code == 10000) return;
        var msg = root.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : "";
        var rid = root.TryGetProperty("request_id", out var r) ? (r.GetString() ?? "") : "";
        throw new ApiError($"即梦 ${msg} (code {code}{(rid.Length > 0 ? $", request_id {rid}" : "")})");
    }

    // ---------------------------------------------------------------- APIMart（异步）
    /// <summary>
    /// 构造 APIMart <c>POST /images/generations</c> 的请求体。
    /// 逐项对照 docs.apimart.ai/cn/api-reference/images/gpt-image-2.5/generation 的
    /// "请求参数"表：model / prompt / size / resolution / quality / n /
    /// output_format / output_compression / background / moderation /
    /// image_urls / mask_url。
    /// </summary>
    /// <param name="req">统一请求参数。</param>
    /// <param name="imageUrls">已上传的参考图公网 URL（第 1 位 = 主体）。</param>
    /// <param name="maskUrl">已上传的蒙版 URL（可空）。</param>
    internal static JsonObject BuildApimartPayload(GenRequest req,
                                                   IReadOnlyList<string> imageUrls,
                                                   string? maskUrl)
    {
        var payload = new JsonObject
        {
            ["model"] = req.Model,
            ["prompt"] = req.Prompt,
            ["quality"] = req.Quality,
            // size 支持"比例名"或"精确像素"；文档建议图生图时不传 size，
            // 由 size=auto 让服务端按输入图比例 + resolution 计算。
            ["size"] = string.IsNullOrWhiteSpace(req.Aspect) ? "auto" : req.Aspect.Trim(),
            // n 走统一真源（Catalog.MaxNFor）—— APIMart 文档为 1~4
            ["n"] = Math.Max(1, Math.Min(Catalog.MaxNFor(ApiProvider.Apimart, req.Model), req.N)),
        };

        // 精确像素时 resolution 会被忽略（文档），但传了也无害；仍按原逻辑只在非 1k 时传
        if (Catalog.ParsePixelSize(req.Aspect) is null &&
            !string.IsNullOrEmpty(req.Resolution) && req.Resolution != "1k")
            payload["resolution"] = req.Resolution;

        if (!string.IsNullOrWhiteSpace(req.OutputFormat))
            payload["output_format"] = req.OutputFormat.Trim().ToLowerInvariant();
        if (req.OutputCompression > 0 && Catalog.CompressionApplies(req.OutputFormat))
            payload["output_compression"] = Math.Clamp(req.OutputCompression, 0, 100);
        if (!string.IsNullOrWhiteSpace(req.Background))
            payload["background"] = req.Background.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(req.Moderation))
            payload["moderation"] = req.Moderation.Trim().ToLowerInvariant();

        if (imageUrls.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var u in imageUrls.Take(Catalog.MaxRefs)) arr.AddString(u);
            payload["image_urls"] = arr;
        }
        // mask_url 仅在同时传 image_urls 时生效（文档），且只作用于第 1 张输入图
        if (!string.IsNullOrEmpty(maskUrl) && imageUrls.Count > 0)
            payload["mask_url"] = maskUrl;

        return payload;
    }

    private async Task<GenResult> GenerateApimartAsync(GenRequest req)
    {
        // ① 上传参考图 → 公网 URL（失败必须明确报错，绝不静默降级 —— CONSTRAINTS D6）
        var imageUrls = new List<string>();
        if (req.Refs is { Count: > 0 })
        {
            int idx = 0;
            foreach (var (data, media) in req.Refs.Take(Catalog.MaxRefs))
            {
                idx++;
                req.Progress?.Report($"上传参考图 {idx}/{req.Refs.Count}（{data.Length / 1024}KB）…");
                imageUrls.Add(await UploadImageAsync(data, media, req.ApiKey, req.Ct)
                                  .ConfigureAwait(false));
            }
            req.Progress?.Report($"参考图已上传 {imageUrls.Count} 张");
        }

        // ①b 上传蒙版 → 公网 URL（文档：mask_url 接受 HTTP(S) 直链或 PNG Data URL）。
        //     上传比内联 base64 更稳：蒙版可能到 MB 级，内联会撑大请求体。
        string? maskUrl = null;
        if (req.Mask is { Length: > 0 })
        {
            req.Progress?.Report($"上传修改区域蒙版（{req.Mask.Length / 1024}KB）…");
            maskUrl = await UploadImageAsync(req.Mask, "image/png", req.ApiKey, req.Ct)
                              .ConfigureAwait(false);
            req.Progress?.Report("蒙版已上传（仅作用于参考图第 1 张）");
        }

        // ② 提交任务（JsonObject：AOT 安全，见 CONSTRAINTS H1）
        var payload = BuildApimartPayload(req, imageUrls, maskUrl);

        using var submitDoc = await _http.PostJsonAsync($"{_baseUrl}/images/generations",
            payload, req.ApiKey, ct: req.Ct).ConfigureAwait(false);
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

        // ⚠️ v5.26.0 关键时机：**拿到 task_id 立即登记落盘**。
        //    此刻起即便程序崩溃/被杀，重启也能凭 task_id 查询取回图片
        //    （只 GET 查询，不重新提交 → 不会重复扣费）。
        try { OnSubmitProgress?.Invoke(SubmitPhase.AsyncSubmitted, tasks, req.Prompt, req.Model, false); }
        catch { /* 钩子异常不影响生成 */ }

        // ③ 并发轮询所有任务（n 张同时等，总时间 ≈ 最慢那张，而非串行累加）
        req.Progress?.Report($"提交 {tasks.Count} 个任务，开始并行轮询…");
        var pollTasks = tasks.Select(id => PollOneAsync(id, req.ApiKey, req.Ct)).ToArray();
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
            req.Progress?.Report($"⚠ 部分产出缺失{detail}{detail2}");
            if (failureReasons.Count > 0)
                req.Progress?.Report("失败原因：" + string.Join("；", failureReasons.Take(3)));
        }
        if (outImages.Count == 0)
        {
            var why = failureReasons.Count > 0
                ? string.Join("；", failureReasons.Take(2))
                : "无图像数据";
            Notify(SubmitPhase.Finished, tasks, req, false);
            throw new ApiError($"所有任务都没有产出图片（tasks={tasks.Count}，失败={failed}）：{why}");
        }
        Notify(SubmitPhase.Finished, tasks, req, true);
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
        // OpenAI 官方、千问 DashScope、即梦的模型清单是**静态维护**的（见 Catalog）：
        //   · OpenAI 的 GET /v1/models 返回的是全部模型（含 chat/embedding），
        //     过滤"哪些能生图"需要额外规则且官方未保证稳定；
        //   · DashScope 没有公开的"列出图像模型"端点（控制台里看）；
        //   · 即梦列的是 **req_key**（服务标识），根本没有"列模型"接口。
        // 因此这几家直接返回 Catalog 的清单，既准确又零往返。
        if (_provider is ApiProvider.OpenAi or ApiProvider.DashScope or ApiProvider.Jimeng)
            return Catalog.ModelChoices(_provider);

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
        // 新增 provider 的"校验 key"策略（都靠一次**免费只读**请求探活，不产生生图费用）：
        //   · OpenAI 官方：GET /v1/models（需要有效 key，返回模型清单长度）
        //   · 千问 DashScope：GET /api/v1/models（同上；个别地域不支持时退化为"不报错即通过"）
        if (_provider == ApiProvider.OpenAi)
        {
            using var doc = await _http.GetJsonAsync($"{_baseUrl}/models", apiKey,
                ct: ct).ConfigureAwait(false);
            int n = doc.RootElement.TryGetProperty("data", out var d) &&
                    d.ValueKind == JsonValueKind.Array ? d.GetArrayLength() : 0;
            return new KeyInfo(false, n, null, null);
        }
        if (_provider == ApiProvider.DashScope)
        {
            using var doc = await _http.GetJsonAsync($"{_baseUrl}/api/v1/models", apiKey,
                ct: ct).ConfigureAwait(false);
            int n = doc.RootElement.TryGetProperty("data", out var d) &&
                    d.ValueKind == JsonValueKind.Array ? d.GetArrayLength() : 0;
            return new KeyInfo(false, n, null, null);
        }
        if (_provider == ApiProvider.Jimeng)
        {
            // ⚠️ 即梦**没有**"免费只读"的探活接口（查询任务必须带有效 task_id）。
            //    所以这里**不做网络请求**，只做本地格式检查 —— 免得"校验"本身产生费用
            //    或制造垃圾任务。真正能不能用，第一次生成时就知道了（错误码会明确指出
            //    是签名问题还是配额问题，见 ThrowIfJimengError）。
            _ = apiKey;   // 用不到（签名要 AK+SK 两把，这里只拿到 AK）
            return new KeyInfo(false, Catalog.ModelChoicesJimeng.Length, null, null);
        }

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
