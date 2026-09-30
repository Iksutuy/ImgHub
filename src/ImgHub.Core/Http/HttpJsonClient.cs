using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImgHub.Core.Diagnostics;

namespace ImgHub.Core.Http;

/// <summary>
/// HTTP 客户端封装：JSON 请求 + 网络类错误自动重试 + 可读错误。
/// 对应 Python 的 <c>httpclient.post_json / get_json</c>。
///
/// 重试规则：429 / 5xx / **连接类错误（DNS·拒绝·超时）** 重试；
/// 业务类 4xx 直接抛（不浪费流量、不掩盖问题）。
///
/// ⚠️ **AOT 硬约束**（见 docs/CONSTRAINTS.md H1）：
/// 请求体必须用 <see cref="JsonNode"/> 或 source-gen 的 <c>JsonTypeInfo</c>，
/// **不能**用 `JsonSerializer.Serialize(object)` 的反射重载 ——
/// AOT 下会抛「Reflection-based serialization has been disabled」。
/// </summary>
public sealed class HttpJsonClient
{
    private readonly HttpClient _http;
    private readonly int _maxAttempts;
    private readonly double _backoffBase;

    public HttpJsonClient(HttpClient? http = null,
                          int maxAttempts = Catalog.MaxAttempts,
                          double backoffBase = Catalog.BackoffBase,
                          TimeSpan? timeout = null)
    {
        if (http is null)
        {
            // 连接超时与整请求超时分开：避免 DNS/连不通时"卡满 300 秒"
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(20),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };
            _http = new HttpClient(handler);
        }
        else _http = http;

        if (timeout is { } t) _http.Timeout = t;
        else if (_http.Timeout == TimeSpan.FromSeconds(100)) // 默认值 → 覆盖为 300s
            _http.Timeout = TimeSpan.FromSeconds(Catalog.Timeout);

        _maxAttempts = Math.Max(1, maxAttempts);
        _backoffBase = backoffBase;
    }

    /// <summary>POST JSON。payload 用 <see cref="JsonObject"/>（AOT 安全）或任意 JSON 节点。</summary>
    public async Task<JsonDocument> PostJsonAsync(string url, JsonNode payload,
                                                  string apiKey,
                                                  TimeSpan? timeout = null,
                                                  CancellationToken ct = default,
                                                  IEnumerable<KeyValuePair<string, string>>? extraHeaders = null)
    {
        var (text, status) = await PostRawAsync(url, payload, apiKey, timeout, ct, extraHeaders)
                                   .ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException jex)
        {
            // 2xx 但 body 非法 JSON → 协议/服务端问题，重试无意义
            AppLog.Error("响应不是合法 JSON（协议错误，不重试）", "HttpJsonClient.PostJsonAsync",
                         detail: Trunc(text, 200), ex: jex);
            throw new ApiError($"响应不是合法 JSON：{Trunc(text, 200)} ({jex.Message})", status);
        }
    }

    /// <summary>
    /// POST JSON 并**保留响应流**（不读 body）—— 供 OpenRouter SSE 流式生图使用。
    /// 调用方负责 Dispose 返回的响应。
    ///
    /// ⚠️ 流式请求**不做重试**：SSE 一旦开始推事件，重试会导致重复计费/重复部分图。
    /// </summary>
    public async Task<HttpResponseMessage> PostJsonStreamAsync(string url, JsonNode payload,
                                                               string apiKey,
                                                               CancellationToken ct = default)
    {
        const string where = "HttpJsonClient.PostJsonStreamAsync";
        var body = payload.ToJsonLine();
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
            req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://openrouter.ai/");
            req.Headers.TryAddWithoutValidation("X-Title", "imghub");

            AppLog.Info("POST（流式 SSE）", where);
            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                                    .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var status = (int)resp.StatusCode;
                var (msg, code) = ErrorBody.Parse(text);
                resp.Dispose();
                var tail = string.IsNullOrEmpty(code) ? "" : $"/{code}";
                throw new ApiError($"{msg} (HTTP {status}{tail})", status);
            }
            return resp;
        }
        catch (ApiError) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new ApiError("已取消");
        }
        catch (Exception ex)
        {
            var kind = NetworkDiagnostics.Classify(ex);
            var msg = NetworkDiagnostics.Describe(kind, ex, "流式请求失败");
            AppLog.Warn($"流式请求失败（{kind}）", where, detail: msg, ex: ex);
            throw new ApiError(msg);
        }
    }

    /// <summary>POST JSON 的底层实现（返回原文，供 JSON 与非 JSON 响应复用）。</summary>
    private async Task<(string Text, int Status)> PostRawAsync(
        string url, JsonNode payload,
        string apiKey,
        TimeSpan? timeout,
        CancellationToken ct,
        IEnumerable<KeyValuePair<string, string>>? extraHeaders = null)
    {
        // ⚠️ 不再用 JsonSerializer.Serialize(object)（AOT 下崩）
        // 单行 + 中文不转义：请求体不需要缩进（省流量），但要可读、便于排查
        var body = payload.ToJsonLine();
        const string where = "HttpJsonClient.PostRawAsync";
        Exception? last = null;

        for (int attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://openrouter.ai/");
                req.Headers.TryAddWithoutValidation("X-Title", "imghub");
                if (extraHeaders is not null)
                {
                    foreach (var h in extraHeaders)
                        req.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }

                AppLog.Info($"POST {Redact(url)}（第 {attempt}/{_maxAttempts} 次）", where);

                using var cts = LinkTimeout(ct, timeout);
                using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    var status = (int)resp.StatusCode;
                    var (msg, code) = ErrorBody.Parse(text);
                    var tail = string.IsNullOrEmpty(code) ? "" : $"/{code}";
                    bool retryable = status == 429 || (status >= 500 && status < 600);
                    var err = new ApiError($"{msg} (HTTP {status}{tail})", status)
                    { Retryable = retryable };
                    AppLog.Warn($"HTTP {status}：{msg}", where,
                                detail: retryable ? "可重试" : "业务错误，不重试");
                    // 业务类 4xx（401/402/403/404/422…）立刻抛出，不消耗重试次数
                    if (!retryable || attempt == _maxAttempts) throw err;
                    last = err;
                }
                else
                {
                    AppLog.Info($"HTTP {(int)resp.StatusCode} 成功（{text.Length} 字节）", where);
                    return (text, (int)resp.StatusCode);
                }
            }
            // ⚠️ 重试判定统一看 ApiError.Retryable（默认 false）：
            //    4xx 业务错误直接冒泡；只有网络类/限流/5xx 才继续循环。
            catch (ApiError ae) when (!ae.Retryable || attempt == _maxAttempts)
            {
                throw;
            }
            catch (ApiError) { /* 可重试，继续 */ }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户主动取消：不当作网络错误
                AppLog.Info("请求已被用户取消", where);
                throw new ApiError("已取消");
            }
            catch (OperationCanceledException oce)
            {
                // 超时（连接超时/读超时都会被包装成 OperationCanceledException 派生类）
                var kind = NetworkDiagnostics.Classify(oce);
                last = new ApiError(NetworkDiagnostics.Describe(kind, oce,
                    $"超时（>{_http.Timeout.TotalSeconds:0}s）"), null) { Retryable = true };
                AppLog.Warn("请求超时", where, detail: NetworkDiagnostics.Describe(kind, oce), ex: oce);
            }
            catch (HttpRequestException hex)
            {
                var kind = NetworkDiagnostics.Classify(hex);
                var msg = NetworkDiagnostics.Describe(kind, hex);
                // 连接类错误（DNS/拒绝/不可达）值得重试；TLS/代理类不重试（换网络才有用）
                last = new ApiError(msg, null)
                {
                    Retryable = kind is NetworkKind.NoNetwork or NetworkKind.DnsFailure
                               or NetworkKind.ConnectionRefused or NetworkKind.ConnectionReset
                               or NetworkKind.HostUnreachable or NetworkKind.Timeout,
                };
                AppLog.Warn($"网络错误（{kind}）", where, detail: msg, ex: hex);
            }
            catch (Exception ex)
            {
                last = new ApiError($"{ex.GetType().Name}: {ex.Message}", null);
                AppLog.Error("未预期的请求异常", where, ex: ex);
            }

            if (attempt < _maxAttempts)
            {
                // 连接类错误退避更长（网络抖动需要更多时间恢复）
                var isConn = last is ApiError ae && NetworkDiagnostics.IsConnectionProblem(ae.Message);
                var baseDelay = Math.Min(8.0, Math.Pow(_backoffBase, attempt));
                if (isConn) baseDelay *= 1.5;
                var delay = baseDelay + Random.Shared.NextDouble() * 0.4;
                AppLog.Info($"{delay:0.0}s 后重试", where);
                await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
            }
        }
        throw last ?? new ApiError("未知错误");
    }

    /// <summary>GET JSON。</summary>
    public async Task<JsonDocument> GetJsonAsync(string url, string? apiKey = null,
                                                 TimeSpan? timeout = null,
                                                 CancellationToken ct = default)
    {
        const string where = "HttpJsonClient.GetJsonAsync";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(apiKey))
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var cts = LinkTimeout(ct, timeout);
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var status = (int)resp.StatusCode;
                var (msg, code) = ErrorBody.Parse(text);
                var tail = string.IsNullOrEmpty(code) ? "" : $"/{code}";
                AppLog.Warn($"GET HTTP {status}：{msg}", where);
                throw new ApiError($"{msg} (HTTP {status}{tail})", status);
            }
            return JsonDocument.Parse(text);
        }
        catch (ApiError) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new ApiError("已取消");
        }
        catch (Exception ex)
        {
            var kind = NetworkDiagnostics.Classify(ex);
            var msg = NetworkDiagnostics.Describe(kind, ex);
            AppLog.Warn($"GET 失败（{kind}）", where, detail: msg, ex: ex);
            throw new ApiError(msg);
        }
    }

    /// <summary>下载二进制（图片）。</summary>
    public async Task<byte[]> GetBytesAsync(string url, string? apiKey = null,
                                            TimeSpan? timeout = null,
                                            CancellationToken ct = default)
    {
        const string where = "HttpJsonClient.GetBytesAsync";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(apiKey))
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            req.Headers.TryAddWithoutValidation("Accept", "image/*");

            using var cts = LinkTimeout(ct, timeout);
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                AppLog.Warn($"下载图片 HTTP {(int)resp.StatusCode}", where, detail: Redact(url));
                throw new ApiError($"下载图片失败 (HTTP {(int)resp.StatusCode})",
                                   (int)resp.StatusCode);
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                AppLog.Warn("下载到的图片为空（0 字节）", where, detail: Redact(url));
                throw new ApiError("下载到的图片为空（0 字节）");
            }
            return bytes;
        }
        catch (ApiError) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new ApiError("已取消");
        }
        catch (Exception ex)
        {
            var kind = NetworkDiagnostics.Classify(ex);
            var msg = NetworkDiagnostics.Describe(kind, ex, "图片下载失败");
            AppLog.Warn($"图片下载失败（{kind}）", where, detail: msg, ex: ex);
            throw new ApiError(msg);
        }
    }

    /// <summary>POST multipart/form-data（上传参考图）。响应可能被 data 包裹，也可能扁平。</summary>
    public async Task<JsonDocument> PostMultipartAsync(string url, byte[] fileData,
                                                       string fileName, string contentType,
                                                       string apiKey,
                                                       TimeSpan? timeout = null,
                                                       CancellationToken ct = default)
        => await PostFormDataAsync(url,
            fields: null,
            files: new[]
            {
                (Field: "file", FileName: fileName, ContentType: contentType, Data: fileData),
            },
            apiKey: apiKey, extraHeaders: null, timeout: timeout, ct: ct).ConfigureAwait(false);

    /// <summary>
    /// 通用 multipart/form-data POST。
    ///
    /// ⚠️ 为什么需要独立的通用版（v0.5.31，OpenAI 官方 <c>/images/edits</c>）：
    ///   上面的 <see cref="PostMultipartAsync"/> 只能传**一个**名为 "file" 的文件，
    ///   而 OpenAI 的 edits 接口要求：
    ///     · 多个同名文件字段 <c>image[]</c>（最多 16 张参考图）；
    ///     · 可选的 <c>mask</c> 文件字段；
    ///     · 与文件**同级**的普通文本字段 model / prompt / size / quality …
    ///   硬塞进旧签名会让 OpenAI 链路无法实现（缺 mask、缺多图、缺文本字段）。
    /// 参考图字段名用带 <c>[]</c> 的写法：官方 curl 示例就是 <c>-F "image[]=@a.png"</c>。
    /// </summary>
    public async Task<JsonDocument> PostFormDataAsync(
        string url,
        IEnumerable<KeyValuePair<string, string>>? fields,
        IEnumerable<(string Field, string FileName, string ContentType, byte[] Data)> files,
        string apiKey,
        IEnumerable<KeyValuePair<string, string>>? extraHeaders = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        const string where = "HttpJsonClient.PostFormDataAsync";
        try
        {
            using var content = new MultipartFormDataContent();
            if (fields is not null)
            {
                foreach (var f in fields)
                    content.Add(new StringContent(f.Value ?? "", Encoding.UTF8), f.Key);
            }
            int fileCount = 0;
            long totalBytes = 0;
            foreach (var f in files)
            {
                var fc = new ByteArrayContent(f.Data);
                fc.Headers.TryAddWithoutValidation("Content-Type", f.ContentType);
                content.Add(fc, f.Field, f.FileName);
                fileCount++;
                totalBytes += f.Data.Length;
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (extraHeaders is not null)
            {
                foreach (var h in extraHeaders)
                {
                    // 少数 header（如 DashScope 的 X-DashScope-Async）必须落在请求头，
                    // 且大小写由服务端判定 → 用 TryAddWithoutValidation 原样送出。
                    if (!req.Headers.TryAddWithoutValidation(h.Key, h.Value))
                        content.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }

            AppLog.Info($"POST multipart（{fileCount} 文件 / {totalBytes / 1024} KB）", where);

            using var cts = LinkTimeout(ct, timeout ?? TimeSpan.FromSeconds(Catalog.Timeout));
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var status = (int)resp.StatusCode;
                var (msg, code) = ErrorBody.Parse(text);
                var tail = string.IsNullOrEmpty(code) ? "" : $"/{code}";
                bool retryable = status == 429 || (status >= 500 && status < 600);
                AppLog.Warn($"HTTP {status}：{msg}", where,
                            detail: retryable ? "可重试" : "业务错误，不重试");
                throw new ApiError($"{msg} (HTTP {status}{tail})", status) { Retryable = retryable };
            }
            try
            {
                return JsonDocument.Parse(text);
            }
            catch (JsonException jex)
            {
                AppLog.Error("响应不是合法 JSON（协议错误，不重试）", where,
                             detail: Trunc(text, 200), ex: jex);
                throw new ApiError($"响应不是合法 JSON：{Trunc(text, 200)} ({jex.Message})",
                                   (int)resp.StatusCode);
            }
        }
        catch (ApiError) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new ApiError("已取消");
        }
        catch (Exception ex)
        {
            var kind = NetworkDiagnostics.Classify(ex);
            var msg = NetworkDiagnostics.Describe(kind, ex, "请求失败");
            AppLog.Warn($"multipart 请求失败（{kind}）", where, detail: msg, ex: ex);
            throw new ApiError(msg);
        }
    }

    /// <summary>
    /// multipart/form-data POST 并**保留响应流**（不读 body）—— 供 OpenAI
    /// <c>/images/edits</c> 的 SSE 流式编辑使用（事件类型是 <c>image_edit.*</c>）。
    /// 调用方负责 Dispose 返回的响应。**不做重试**（同 <see cref="PostJsonStreamAsync"/>）。
    /// </summary>
    public async Task<HttpResponseMessage> PostFormDataStreamAsync(
        string url,
        IEnumerable<KeyValuePair<string, string>>? fields,
        IEnumerable<(string Field, string FileName, string ContentType, byte[] Data)> files,
        string apiKey,
        CancellationToken ct = default)
    {
        const string where = "HttpJsonClient.PostFormDataStreamAsync";
        try
        {
            var content = new MultipartFormDataContent();
            if (fields is not null)
            {
                foreach (var f in fields)
                    content.Add(new StringContent(f.Value ?? "", Encoding.UTF8), f.Key);
            }
            foreach (var f in files)
            {
                var fc = new ByteArrayContent(f.Data);
                fc.Headers.TryAddWithoutValidation("Content-Type", f.ContentType);
                content.Add(fc, f.Field, f.FileName);
            }

            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

            AppLog.Info("POST multipart（流式 SSE）", where);
            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                                    .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var status = (int)resp.StatusCode;
                var (msg, code) = ErrorBody.Parse(text);
                var tail = string.IsNullOrEmpty(code) ? "" : $"/{code}";
                resp.Dispose();
                req.Dispose();
                content.Dispose();
                throw new ApiError($"{msg} (HTTP {status}{tail})", status);
            }
            // 响应交由调用方 Dispose；request/content 的生存期随响应结束（流读完才释放）
            return resp;
        }
        catch (ApiError) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new ApiError("已取消");
        }
        catch (Exception ex)
        {
            var kind = NetworkDiagnostics.Classify(ex);
            var msg = NetworkDiagnostics.Describe(kind, ex, "流式请求失败");
            AppLog.Warn($"multipart 流式请求失败（{kind}）", where, detail: msg, ex: ex);
            throw new ApiError(msg);
        }
    }

    /// <summary>GET 一个 JSON 端点并附带自定义 header（DashScope 任务查询用）。</summary>
    public async Task<JsonDocument> GetJsonWithHeadersAsync(
        string url, string? apiKey,
        IEnumerable<KeyValuePair<string, string>>? extraHeaders = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        const string where = "HttpJsonClient.GetJsonWithHeadersAsync";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(apiKey))
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (extraHeaders is not null)
            {
                foreach (var h in extraHeaders)
                    req.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }

            using var cts = LinkTimeout(ct, timeout ?? TimeSpan.FromSeconds(60));
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var status = (int)resp.StatusCode;
                var (msg, code) = ErrorBody.Parse(text);
                var tail = string.IsNullOrEmpty(code) ? "" : $"/{code}";
                throw new ApiError($"{msg} (HTTP {status}{tail})", status)
                { Retryable = status == 429 || (status >= 500 && status < 600) };
            }
            try { return JsonDocument.Parse(text); }
            catch (JsonException jex)
            {
                AppLog.Error("响应不是合法 JSON（协议错误，不重试）", where,
                             detail: Trunc(text, 200), ex: jex);
                throw new ApiError($"响应不是合法 JSON：{Trunc(text, 200)} ({jex.Message})",
                                   (int)resp.StatusCode);
            }
        }
        catch (ApiError) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new ApiError("已取消");
        }
        catch (Exception ex)
        {
            var kind = NetworkDiagnostics.Classify(ex);
            var msg = NetworkDiagnostics.Describe(kind, ex, "请求失败");
            AppLog.Warn($"GET 失败（{kind}）", where, detail: msg, ex: ex);
            throw new ApiError(msg);
        }
    }

    private static string Trunc(string s, int n) => s.Length > n ? s[..n] : s;

    /// <summary>URL 脱敏（去掉可能含凭据的查询串）。</summary>
    private static string Redact(string url)
    {
        var q = url.IndexOf('?');
        return q > 0 ? url[..q] + "?…" : url;
    }

    private static CancellationTokenSource LinkTimeout(CancellationToken ct, TimeSpan? timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);
        return cts;
    }
}
