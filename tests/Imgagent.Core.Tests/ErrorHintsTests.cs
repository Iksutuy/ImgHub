using Imgagent.Core.Http;

namespace Imgagent.Core.Tests;

/// <summary>
/// 错误翻译的回归测试 —— 逐条对应 Python 的 test_http_error_translation。
/// 铁律（CONSTRAINTS E2/E3）：APIMart 余额不足返回 403，**403 不是权限问题**，
/// 所以「余额」判断必须**先于**「权限」。
/// </summary>
public class ErrorHintsTests
{
    [Fact]
    public void Hint_403InsufficientQuota_IsBalanceNotPermission()
    {
        // 这正是真机踩过的坑：403 + quota_not_enough 被误诊为 "key 无效"
        var e = new ApiError(
            "insufficient balance: insufficient quota: balance=38650, required=42383 (HTTP 403/quota_not_enough)",
            403);
        var hint = ErrorHints.Hint(e);
        Assert.Contains("余额不足", hint);
        Assert.DoesNotContain("key 无效", hint);
    }

    [Fact]
    public void Hint_402InsufficientBalance_IsBalance()
    {
        var e = new ApiError("insufficient balance (current: 0.02 USD, required: 0.05 USD) (HTTP 402)", 402);
        Assert.Contains("余额不足", ErrorHints.Hint(e));
    }

    [Fact]
    public void Hint_401_IsKeyInvalid()
    {
        var e = new ApiError("invalid_api_key (HTTP 401)", 401);
        Assert.Contains("key 无效", ErrorHints.Hint(e));
    }

    [Fact]
    public void Hint_429_IsRateLimit()
    {
        Assert.Contains("限流", ErrorHints.Hint(new ApiError("too many requests", 429)));
    }

    [Fact]
    public void Explain_403Quota_PutsBalanceFirst()
    {
        var e = new ApiError(
            "insufficient balance: insufficient quota: balance=38650, required=42383",
            403);
        var lines = ErrorHints.Explain(e);
        var joined = string.Join("\n", lines);
        Assert.Contains("账户余额不足", joined);
        // 必须提取出数字给用户看
        Assert.Contains("38650", joined);
        Assert.Contains("42383", joined);
    }

    [Fact]
    public void ErrorBody_ParsesCodeFromBody()
    {
        var raw = """
            {"error":{"message":"insufficient balance: ...","type":"quota_not_enough"}}
            """;
        var (msg, code) = ErrorBody.Parse(raw);
        Assert.Contains("insufficient balance", msg);
        Assert.Equal("quota_not_enough", code);
    }

    [Fact]
    public void ErrorBody_NumericCode_BecomesString()
    {
        var raw = """{"error":{"message":"bad","code":403}}""";
        var (msg, code) = ErrorBody.Parse(raw);
        Assert.Equal("bad", msg);
        Assert.Equal("403", code);   // 数字也要统一成字符串
    }

    [Fact]
    public void ErrorBody_NonJson_ReturnsRawTruncated()
    {
        var (msg, code) = ErrorBody.Parse("plain text error");
        Assert.Equal("plain text error", msg);
        Assert.Null(code);
    }
}
