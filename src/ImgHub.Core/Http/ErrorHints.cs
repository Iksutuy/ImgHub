using System.Text.RegularExpressions;

namespace ImgHub.Core.Http;

/// <summary>
/// 把 API 错误翻译成"你该怎么办"。
/// 对应 Python 的 <c>api.error_hint()</c> / <c>api.explain_error()</c>。
///
/// ⚠️ 铁律（CONSTRAINTS E2/E3，来自真机 bug）：APIMart 对"余额不足"用**两种状态码**
/// （402 和 403），**403 ≠ 权限问题**。所以判断顺序必须是
/// **余额 → 地区 → 权限**，否则 403 会被误诊为 "key 无效"。
/// </summary>
public static class ErrorHints
{
    /// <summary>TUI 单行提示。空串表示没有额外建议。</summary>
    public static string Hint(Exception e)
    {
        var status = e is ApiError ae ? ae.Status : null;
        var msg = e.Message;
        var low = msg.ToLowerInvariant();

        // —— 余额判断必须先于权限（APIMart 余额不足返回 403）——
        if (status == 402 || low.Contains("insufficient") || low.Contains("quota")
            || (low.Contains("balance") && low.Contains("insufficient"))
            || low.Contains("payment") || low.Contains("credit"))
        {
            var bits = new List<string>();
            var cur = Regex.Match(msg, @"(?:current|balance)\s*[:=]?\s*(\d+\.?\d*)");
            var req = Regex.Match(msg, @"required\s*[:=]?\s*(\d+\.?\d*)");
            if (cur.Success) bits.Add($"当前 ${cur.Groups[1].Value}");
            if (req.Success) bits.Add($"需要 ${req.Groups[1].Value}");
            var detail = bits.Count > 0 ? $"（{string.Join("，", bits)}）" : "";
            return $"账户余额不足{detail} —— 去 APIMart 充值；编辑比文生图贵（要传参考图）";
        }
        if (status is 401 or 403) return "key 无效或没权限（去设置里换 key）";
        if (status == 429) return "触发限流，等几秒再试";
        if (status is 500 or 502 or 503 or 504) return "服务端错误，稍后重试";
        if (low.Contains("timed out") || msg.Contains("超时")) return "超时了，可以降到 low 档或换 1k 分辨率";
        if (low.Contains("certificate") || low.Contains("ssl")) return "TLS 证书问题，换个网络试试";
        if (low.Contains("region")) return "地区限制，换网络或换模型";
        return "";
    }

    /// <summary>多行可操作建议（CLI/日志用）。</summary>
    public static IReadOnlyList<string> Explain(Exception e)
    {
        var status = e is ApiError ae ? ae.Status : null;
        var msg = e.Message;
        var low = msg.ToLowerInvariant();
        var lines = new List<string> { $"[X] 失败：{msg}" };

        // ⚠️ 顺序：先判余额，再判权限。
        var noMoney = status is 402 or 403
                      || low.Contains("insufficient") || low.Contains("quota")
                      || low.Contains("balance") || low.Contains("credit")
                      || low.Contains("payment");

        if (noMoney && (low.Contains("insufficient") || low.Contains("quota")
                        || low.Contains("balance") || status == 402
                        || low.Contains("credit") || low.Contains("payment")))
        {
            lines.Add("    -> **账户余额不足**（不是权限问题）。");
            var cur = Regex.Match(msg, @"(?:current|balance)\s*[:=]?\s*(\d+\.?\d*)");
            var req = Regex.Match(msg, @"required\s*[:=]?\s*(\d+\.?\d*)");
            if (cur.Success || req.Success)
            {
                var parts = new List<string>();
                if (cur.Success) parts.Add($"当前 ${cur.Groups[1].Value}");
                if (req.Success) parts.Add($"本次需要 ${req.Groups[1].Value}");
                lines.Add($"       {string.Join("，", parts)}");
            }
            lines.Add("       · 充值后重试");
            lines.Add("       · 或先降成本：换 low、用 1k、设 1 张");
            lines.Add("       · 编辑比文生图贵（要传参考图，输入 token 更多）");
        }
        else if (low.Contains("not available in your region"))
        {
            lines.Add("    -> 这是**地区限制**（不是你的代码问题）。可以：");
            lines.Add("       · 换网络（Wi-Fi <-> 流量）");
            lines.Add("       · 换模型");
            lines.Add("       · 白名单内备选：google/gemini-2.5-flash-image、openai/gpt-image-1-mini");
            lines.Add("       · 想先跑通流程：切到离线模式");
        }
        else if (low.Contains("no allowed providers"))
        {
            lines.Add("    -> 账号设了 provider 白名单，这个模型不在里面。");
            lines.Add("       换模型，或去 https://openrouter.ai/settings/privacy 放开");
        }
        else if (status is 401 or 403 || low.Contains("invalid_api_key") || low.Contains("no auth"))
        {
            lines.Add("    -> API key 无效或没权限。去设置里换 key。");
        }
        else if (status == 429)
        {
            lines.Add("    -> 触发限流（已自动重试 3 次），等几秒再试");
        }
        else if (low.Contains("certificate") || low.Contains("ssl"))
        {
            lines.Add("    -> TLS 证书问题：换个网络");
        }
        else if (low.Contains("timed out") || msg.Contains("超时"))
        {
            lines.Add("    -> 超时。high 档大画幅可能要 20s+；也可以先降到 low");
        }
        else
        {
            lines.Add("    -> 可先切到离线模式，确认是本机流程问题还是服务端问题");
        }
        return lines;
    }
}
