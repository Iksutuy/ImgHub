using System.Net;
using System.Net.Sockets;

namespace ImgHub.Core.Http;

/// <summary>网络故障分类（用于给出「该怎么修」的具体提示，而非笼统报错）。</summary>
public enum NetworkKind
{
    Unknown,
    NoNetwork,        // 完全没有网络 / 网卡断开
    DnsFailure,       // 域名解析失败
    ConnectionRefused,// 目标拒绝连接
    ConnectionReset,  // 连接被重置（含中间设备阻断）
    HostUnreachable,  // 路由不可达
    Timeout,          // 连接/读取超时
    TlsError,         // TLS/证书问题（含代理嗅探）
    ProxyError,       // 代理配置问题
    Cancelled,        // 用户取消
}

/// <summary>
/// 网络异常诊断：把 <see cref="Exception"/> 归类成 <see cref="NetworkKind"/>，
/// 并生成**可操作**的中文说明。
///
/// 为什么要单独一层：`HttpRequestException` 会把真正原因藏在
/// `InnerException`（常是 `SocketException`）里，只看外层消息会得到
/// 「网络错误：An error occurred…」这类无用提示，用户无从下手。
/// </summary>
public static class NetworkDiagnostics
{
    /// <summary>解包异常链，归类网络故障。</summary>
    public static NetworkKind Classify(Exception? ex)
    {
        var cur = ex;
        int guard = 0;
        while (cur is not null && guard++ < 6)
        {
            switch (cur)
            {
                case SocketException se:
                    return se.SocketErrorCode switch
                    {
                        SocketError.HostNotFound or SocketError.NoData => NetworkKind.DnsFailure,
                        SocketError.TryAgain => NetworkKind.DnsFailure,
                        SocketError.ConnectionRefused => NetworkKind.ConnectionRefused,
                        SocketError.ConnectionReset or SocketError.ConnectionAborted
                            => NetworkKind.ConnectionReset,
                        SocketError.NetworkDown or SocketError.NetworkUnreachable
                            => NetworkKind.NoNetwork,
                        SocketError.HostUnreachable => NetworkKind.HostUnreachable,
                        SocketError.TimedOut => NetworkKind.Timeout,
                        SocketError.AccessDenied => NetworkKind.ProxyError,
                        _ => NetworkKind.Unknown,
                    };

                case OperationCanceledException:
                    // 注意：调用方需先判「是不是用户取消」，这里只表示"超时类"
                    return NetworkKind.Timeout;

                case System.Security.Authentication.AuthenticationException:
                    return NetworkKind.TlsError;
            }

            var msg = cur.Message.ToLowerInvariant();
            if (msg.Contains("no such host") || msg.Contains("name or service not known")
                || msg.Contains("nodename nor servname") || msg.Contains("dns"))
                return NetworkKind.DnsFailure;
            if (msg.Contains("certificate") || msg.Contains("ssl") || msg.Contains("tls"))
                return NetworkKind.TlsError;
            if (msg.Contains("proxy")) return NetworkKind.ProxyError;
            if (msg.Contains("network is unreachable") || msg.Contains("network down"))
                return NetworkKind.NoNetwork;
            if (msg.Contains("actively refused") || msg.Contains("connection refused"))
                return NetworkKind.ConnectionRefused;
            if (msg.Contains("timed out") || msg.Contains("timeout") || msg.Contains("超时"))
                return NetworkKind.Timeout;
            if (msg.Contains("forcibly closed") || msg.Contains("connection reset"))
                return NetworkKind.ConnectionReset;

            cur = cur.InnerException;
        }
        return NetworkKind.Unknown;
    }

    /// <summary>该故障是否属于「连接层面」（值得重试，且退避应更长）。</summary>
    public static bool IsConnectionProblem(string message)
    {
        var m = message ?? "";
        return m.Contains("网络") || m.Contains("解析") || m.Contains("连接")
            || m.Contains("超时") || m.Contains("代理") || m.Contains("TLS");
    }

    /// <summary>生成可读说明（含原因 + 该怎么办）。</summary>
    public static string Describe(NetworkKind kind, Exception? ex = null, string? prefix = null)
    {
        var head = string.IsNullOrEmpty(prefix) ? "网络失败" : prefix;
        var detail = RootMessage(ex);
        var tail = detail.Length > 0 ? $"（{detail}）" : "";

        return kind switch
        {
            NetworkKind.NoNetwork =>
                $"{head}：设备似乎没有网络{tail} —— 检查 Wi-Fi/流量是否开启，或切到离线模式",
            NetworkKind.DnsFailure =>
                $"{head}：域名解析失败{tail} —— 检查网络、DNS 或被代理拦截（可换 Wi-Fi/流量）",
            NetworkKind.ConnectionRefused =>
                $"{head}：服务端拒绝连接{tail} —— 可能是服务暂时不可用，稍后重试",
            NetworkKind.ConnectionReset =>
                $"{head}：连接被重置{tail} —— 常见于网络中间设备拦截，换个网络再试",
            NetworkKind.HostUnreachable =>
                $"{head}：目标主机不可达{tail} —— 检查网络或代理设置",
            NetworkKind.Timeout =>
                $"{head}：连接超时{tail} —— 网络慢或服务繁忙；可降低画质/分辨率后重试",
            NetworkKind.TlsError =>
                $"{head}：TLS/证书校验失败{tail} —— 常见于代理或公司网络，换个网络再试",
            NetworkKind.ProxyError =>
                $"{head}：代理配置有问题{tail} —— 检查系统代理设置",
            NetworkKind.Cancelled => $"{head}：已取消",
            _ => $"{head}{tail} —— 可先切到离线模式，确认是本机流程问题还是服务端问题",
        };
    }

    /// <summary>取最内层的可读消息（外层 HttpRequestException 的消息通常无用）。</summary>
    public static string RootMessage(Exception? ex)
    {
        var cur = ex;
        string last = "";
        int guard = 0;
        while (cur is not null && guard++ < 6)
        {
            if (!string.IsNullOrWhiteSpace(cur.Message)) last = cur.Message.Trim();
            cur = cur.InnerException;
        }
        // SocketException 的消息已足够信息量；HttpRequestException 的外层话术则省略
        if (ex is HttpRequestException && last == ex.Message)
        {
            var inner = ex.InnerException;
            if (inner is not null && !string.IsNullOrWhiteSpace(inner.Message))
                last = inner.Message.Trim();
        }
        if (last.Length > 160) last = last[..160] + "…";
        return last;
    }

    /// <summary>当前是否有网络（粗略判断，供生成前预检）。不发起请求。</summary>
    public static bool HasNetwork()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
        }
        catch
        {
            return true;   // 判断不了就放行，交给实际请求去失败
        }
    }
}
