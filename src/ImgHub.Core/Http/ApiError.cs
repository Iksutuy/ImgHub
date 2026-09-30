namespace ImgHub.Core.Http;

/// <summary>带 HTTP 状态码的错误。对应 Python 的 <c>httpclient.ApiError</c>。</summary>
public sealed class ApiError : Exception
{
    public int? Status { get; }

    /// <summary>
    /// 是否值得重试。
    /// **默认 false**（业务错误/配置错误，重试无意义且浪费流量 —— 见 CONSTRAINTS C2）；
    /// 网络类错误（连接失败/超时/限流/5xx）由构造方显式置 true。
    /// </summary>
    public bool Retryable { get; init; }

    public ApiError(string message, int? status = null) : base(message)
    {
        Status = status;
    }
}

/// <summary>从错误响应体里提取 message 与错误码。</summary>
public static class ErrorBody
{
    /// <summary>返回 (message, code)。code 统一成字符串（有的错误体里是数字）。</summary>
    public static (string Message, string? Code) Parse(string raw)
    {
        System.Text.Json.JsonDocument doc;
        try
        {
            doc = System.Text.Json.JsonDocument.Parse(raw);
        }
        catch
        {
            var t = raw.Length > 300 ? raw[..300] : raw;
            return (t, null);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object &&
                root.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    string? code = null;
                    if (err.TryGetProperty("code", out var c) && c.ValueKind != System.Text.Json.JsonValueKind.Null)
                        code = c.ToString();
                    else if (err.TryGetProperty("type", out var ty) && ty.ValueKind != System.Text.Json.JsonValueKind.Null)
                        code = ty.ToString();

                    var msg = err.TryGetProperty("message", out var m) ? m.ToString() : err.ToString();
                    if (msg.Length > 400) msg = msg[..400];
                    return (msg, string.IsNullOrEmpty(code) ? null : code);
                }
                var em = err.ToString();
                return (em.Length > 400 ? em[..400] : em, null);
            }
            var s = root.ToString();
            return (s.Length > 300 ? s[..300] : s, null);
        }
    }
}
