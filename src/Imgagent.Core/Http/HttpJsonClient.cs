using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Imgagent.Core.Http;

/// <summary>
/// HTTP 客户端封装：JSON 请求 + 网络类错误自动重试 + 可读错误。
/// 对应 Python 的 <c>httpclient.post_json / get_json</c>。
///
/// 重试规则与原版一致：仅 429 和 5xx 重试；4xx 直接抛（不浪费流量、不掩盖问题）。
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
        _http = http ?? new HttpClient();
        if (timeout is { } t) _http.Timeout = t;
        else if (_http.Timeout == TimeSpan.FromSeconds(100)) // 默认值 → 覆盖为 300s
            _http.Timeout = TimeSpan.FromSeconds(Catalog.Timeout);
        _maxAttempts = maxAttempts;
        _backoffBase = backoffBase;
    }

    /// <summary>POST JSON。网络类错误自动重试；4xx 直接抛 ApiError。</summary>
    public async Task<JsonDocument> PostJsonAsync(string url, object payload,
                                                  string apiKey,
                                                  TimeSpan? timeout = null,
                                                  CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(payload);
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
                req.Headers.TryAddWithoutValidation("X-Title", "imgagent");

                using var cts = LinkTimeout(ct, timeout);
                using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    var status = (int)resp.StatusCode;
                    var (msg, code) = ErrorBody.Parse(text);
                    var tail = string.IsNullOrEmpty(code) ? "" : $"/{code}";
                    last = new ApiError($"{msg} (HTTP {status}{tail})", status);
                    bool retryable = status == 429 || (status >= 500 && status < 600);
                    if (!retryable || attempt == _maxAttempts) throw last;
                }
                else
                {
                    // 2xx 但 body 非法 JSON → 这是协议/服务端问题，重试无意义，
                    // 直接抛非重试类 ApiError（旧实现会掉进 catch(Exception) 被重试 3 次）
                    try
                    {
                        return JsonDocument.Parse(text);
                    }
                    catch (JsonException jex)
                    {
                        throw new ApiError(
                            $"响应不是合法 JSON：{Trunc(text, 200)} ({jex.Message})", (int)resp.StatusCode);
                    }
                }
            }
            catch (ApiError) when (attempt == _maxAttempts)
            {
                throw;
            }
            catch (ApiError) { /* 可重试，继续 */ }
            catch (TaskCanceledException)
            {
                last = new ApiError($"超时（>{(_http.Timeout.TotalSeconds):0}s）");
            }
            catch (HttpRequestException ex)
            {
                last = new ApiError($"网络错误：{ex.Message}");
            }
            catch (Exception ex)
            {
                last = new ApiError($"{ex.GetType().Name}: {ex.Message}");
            }

            if (attempt < _maxAttempts)
            {
                var delay = Math.Min(8.0, Math.Pow(_backoffBase, attempt))
                            + Random.Shared.NextDouble() * 0.4;
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
                throw new ApiError($"{msg} (HTTP {status}{tail})", status);
            }
            return JsonDocument.Parse(text);
        }
        catch (ApiError) { throw; }
        catch (TaskCanceledException)
        {
            throw new ApiError($"超时（>{(_http.Timeout.TotalSeconds):0}s）");
        }
        catch (Exception ex)
        {
            throw new ApiError($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>下载二进制（图片）。</summary>
    public async Task<byte[]> GetBytesAsync(string url, string? apiKey = null,
                                            TimeSpan? timeout = null,
                                            CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(apiKey))
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            req.Headers.TryAddWithoutValidation("Accept", "image/*");

            using var cts = LinkTimeout(ct, timeout);
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new ApiError($"下载图片失败 (HTTP {(int)resp.StatusCode})", (int)resp.StatusCode);
            return await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        }
        catch (ApiError) { throw; }
        catch (Exception ex)
        {
            throw new ApiError($"下载图片失败：{ex.Message}");
        }
    }

    /// <summary>POST multipart/form-data（上传参考图）。响应可能被 data 包裹，也可能扁平。</summary>
    public async Task<JsonDocument> PostMultipartAsync(string url, byte[] fileData,
                                                       string fileName, string contentType,
                                                       string apiKey,
                                                       TimeSpan? timeout = null,
                                                       CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileData);
        fileContent.Headers.TryAddWithoutValidation("Content-Type", contentType);
        content.Add(fileContent, "file", fileName);

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var cts = LinkTimeout(ct, timeout ?? TimeSpan.FromSeconds(120));
        using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var status = (int)resp.StatusCode;
            var (msg, _) = ErrorBody.Parse(text);
            throw new ApiError($"参考图上传失败（HTTP {status}）：{msg}", status);
        }
        return JsonDocument.Parse(text);
    }

    private static string Trunc(string s, int n) => s.Length > n ? s[..n] : s;

    private static CancellationTokenSource LinkTimeout(CancellationToken ct, TimeSpan? timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);
        return cts;
    }
}
