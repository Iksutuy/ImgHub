namespace ImgHub.Core.Models;

/// <summary>生成结果。对应 Python 的 <c>api.GenResult</c>。
/// 关键不变量：<see cref="Images"/> 是 (字节, media_type) 列表，两个 provider 统一。</summary>
public sealed class GenResult
{
    public List<(byte[] Data, string Media)> Images { get; init; } = new();
    public double Cost { get; init; }
    public int Tokens { get; init; }
    public Dictionary<string, object?> RawUsage { get; init; } = new();

    public GenResult() { }

    public GenResult(List<(byte[] Data, string Media)> images,
                     double cost = 0, int tokens = 0,
                     Dictionary<string, object?>? rawUsage = null)
    {
        Images = images;
        Cost = cost;
        Tokens = tokens;
        RawUsage = rawUsage ?? new();
    }
}
