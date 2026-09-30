using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ImgHub.Core.Http;

/// <summary>
/// 火山引擎（ByteDance Cloud）**签名 V4** 计算器。
///
/// 为什么需要它：即梦（visual.volcengineapi.com）**不用 Bearer token**，
/// 而是要求请求头带 <c>Authorization: HMAC-SHA256 Credential=..., SignedHeaders=..., Signature=...</c>，
/// 由 <c>AccessKeyId</c>（AK）+ <c>SecretAccessKey</c>（SK）派生。
/// 没有正确签名 → 服务端直接 403，且不会告诉你"签名哪一步错了"。
///
/// 实现依据（火山引擎"公共参数 - 签名参数 - 在 Header 中的场景"的标准流程）：
///   ① 拼 CanonicalRequest（固定顺序：Method / CanonicalURI / CanonicalQuery /
///      CanonicalHeaders / SignedHeaders / HashedPayload）；
///   ② 拼 StringToSign（Algorithm / RequestDate / CredentialScope / hash(①)）；
///   ③ 逐级派生签名密钥：kDate → kRegion → kService → kSigning；
///   ④ Signature = hex(HMAC-SHA256(kSigning, ②))。
///
/// ⚠️ 三处最容易写错、且错了只报"签名不匹配"的地方：
///   ① **CanonicalQuery 必须按参数名排序并做 RFC3986 编码**（Action/Version 的字母序）；
///   ② **HashedPayload 是请求体的 SHA256**（空体也要算，不是省略）；
///   ③ **时间戳必须用 UTC**，且 <c>X-Date</c> 与 Credential 里的日期部分要一致。
///
/// 本类**不碰网络**，纯函数 —— 便于单测直接对着已知向量断言。
/// </summary>
public static class VolcSigner
{
    /// <summary>签名算法标识（固定）。</summary>
    public const string Algorithm = "HMAC-SHA256";

    /// <summary>签名结果：可直接放进请求头的 Authorization 值 + 用到的 X-Date。</summary>
    public sealed record Signed(string Authorization, string XDate, string ContentSha256);

    /// <summary>
    /// 计算签名。
    /// </summary>
    /// <param name="accessKeyId">AccessKeyId（AK）。</param>
    /// <param name="secretAccessKey">SecretAccessKey（SK）。</param>
    /// <param name="region">区域，即梦固定 <c>cn-north-1</c>。</param>
    /// <param name="service">服务名，即梦固定 <c>cv</c>。</param>
    /// <param name="host">主机名（不含协议），如 <c>visual.volcengineapi.com</c>。</param>
    /// <param name="method">HTTP 方法（GET / POST）。</param>
    /// <param name="path">路径（如 <c>/</c>）。</param>
    /// <param name="query">查询参数（会被排序 + 编码），如 Action/Version。</param>
    /// <param name="body">请求体原文（空体传 ""）。</param>
    /// <param name="now">当前时间（测试可传固定值；**内部按 UTC 处理**）。</param>
    public static Signed Sign(string accessKeyId, string secretAccessKey,
                              string region, string service, string host,
                              string method, string path,
                              IEnumerable<KeyValuePair<string, string>> query,
                              string body, DateTimeOffset now)
    {
        // ① 时间戳：火山引擎要求 X-Date 形如 20240924T073000Z（UTC）
        var xDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var shortDate = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        // ② 请求体哈希
        var payloadHash = Sha256Hex(body ?? "");

        // ③ CanonicalQuery：**按参数名排序** + RFC3986 编码
        var canonicalQuery = string.Join("&", query
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{UriEncode(kv.Key)}={UriEncode(kv.Value)}"));

        // ④ CanonicalHeaders：参与签名的头，按名排序，格式 "name:value\n"
        //    这里只签 host + x-date + x-content-sha256（火山引擎允许自定义参与集合，
        //    但**必须**与 SignedHeaders 一致，否则签名不匹配）。
        var canonicalHeaders =
            $"host:{host}\n" +
            $"x-content-sha256:{payloadHash}\n" +
            $"x-date:{xDate}\n";
        const string signedHeaders = "host;x-content-sha256;x-date";

        // ⑤ CanonicalRequest
        var canonicalRequest = string.Join("\n",
            method.ToUpperInvariant(),
            path,
            canonicalQuery,
            canonicalHeaders,
            signedHeaders,
            payloadHash);

        // ⑥ StringToSign
        var credentialScope = $"{shortDate}/{region}/{service}/request";
        var stringToSign = string.Join("\n",
            Algorithm,
            xDate,
            credentialScope,
            Sha256Hex(canonicalRequest));

        // ⑦ 派生密钥（四级 HMAC，**每一级都以上一级的字节为 key**）
        var kDate = Hmac(Encoding.UTF8.GetBytes(secretAccessKey), shortDate);
        var kRegion = Hmac(kDate, region);
        var kService = Hmac(kRegion, service);
        var kSigning = Hmac(kService, "request");

        // ⑧ 最终签名
        var signature = Convert.ToHexString(Hmac(kSigning, stringToSign)).ToLowerInvariant();

        var authorization =
            $"{Algorithm} Credential={accessKeyId}/{credentialScope}, "
            + $"SignedHeaders={signedHeaders}, Signature={signature}";

        return new Signed(authorization, xDate, payloadHash);
    }

    /// <summary>SHA256 → 小写十六进制。</summary>
    private static string Sha256Hex(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static byte[] Hmac(byte[] key, string data) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    /// <summary>
    /// RFC3986 百分号编码。
    ///
    /// ⚠️ 必须与 .NET 自带的 <c>Uri.EscapeDataString</c> 区分：
    ///   后者**不编码**某些字符（如 <c>!</c> <c>*</c> <c>'</c> <c>(</c> <c>)</c>），
    ///   而 RFC3986 要求 unreserved 只有 <c>A-Za-z0-9-_.~</c> —— 差一个字符签名就不匹配。
    /// </summary>
    private static string UriEncode(string s)
    {
        var sb = new StringBuilder(s.Length * 2);
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            if ((b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') ||
                (b >= '0' && b <= '9') ||
                b == '-' || b == '_' || b == '.' || b == '~')
                sb.Append((char)b);
            else
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
