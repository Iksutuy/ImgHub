namespace Imgagent.Core.Models;

/// <summary>一张图（AI 生成 / 修改 / 导入）。对应 Python 的 <c>store.Item</c>。</summary>
public sealed class Item
{
    /// <summary>相对 HOME 的文件名。</summary>
    public string File { get; set; } = "";

    public string Prompt { get; set; } = "";

    /// <summary>gen | edit | import | offline</summary>
    public string Kind { get; set; } = "gen";

    public double Ts { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public string Model { get; set; } = "";
    public string Quality { get; set; } = "";
    public double Cost { get; set; }
    public int Tokens { get; set; }
    public string Note { get; set; } = "";

    /// <summary>生成该图的 provider（"openrouter" / "apimart"；旧数据为空）。</summary>
    public string Provider { get; set; } = "";

    /// <summary>该图在数据目录中的绝对路径。</summary>
    public string Path(string home) => System.IO.Path.Combine(home, File);

    public string Title(int i)
    {
        var mark = i == 0 ? ">" : " ";
        var p = Prompt.Replace("\n", " ");
        if (p.Length > 38) p = p[..38];
        return $"{mark}[{i}] {Kind,-7} {p}";
    }
}
