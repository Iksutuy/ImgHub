using System.Text.RegularExpressions;

namespace ImgHub.Integration.Tests;

/// <summary>
/// 按钮图标字体（Material Symbols 静态子集）的**契约测试**（v0.5.37）。
///
/// 为什么需要它：新增图标时如果忘了把码位加进 <c>tools/make-icon-font.py</c> 并重跑脚本，
///   字形就不在子集字体里 → **界面上显示空白**（不报错、不崩溃，只静默少个图标）。
///   这类问题测试不主动查就查不出来。本文件把"XAML 用到的码位 ⊆ 字体含有的码位"钉成不变量。
/// </summary>
public class IconFontContractTests
{
    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "ImgHub.slnx"))
                || Directory.Exists(Path.Combine(dir, "src", "ImgHub.App")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("找不到仓库根目录");
    }

    /// <summary>收集 XAML 里所有 <c>Classes="icon..."</c> 的图标码位。</summary>
    private static HashSet<int> CodepointsUsedInXaml()
    {
        var used = new HashSet<int>();
        var views = Path.Combine(Root(), "src", "ImgHub.App", "Views");
        foreach (var file in Directory.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            // 两种书写顺序都要覆盖（属性顺序不固定）
            foreach (Match m in Regex.Matches(text,
                @"Classes=""icon[^""]*""\s+Text=""&#x([0-9A-Fa-f]+);"""))
                used.Add(Convert.ToInt32(m.Groups[1].Value, 16));
            foreach (Match m in Regex.Matches(text,
                @"Text=""&#x([0-9A-Fa-f]+);""\s+Classes=""icon"))
                used.Add(Convert.ToInt32(m.Groups[1].Value, 16));
        }
        return used;
    }

    /// <summary>读子集字体的 cmap（用 fontTools 生成时已保证是静态子集）。</summary>
    private static byte[] FontBytes()
    {
        var p = Path.Combine(Root(), "src", "ImgHub.App", "Assets", "Fonts", "MaterialSymbols.ttf");
        if (!File.Exists(p)) throw new FileNotFoundException($"缺少图标字体：{p}");
        return File.ReadAllBytes(p);
    }

    [Fact]
    public void IconFont_IsPresent_AndSmallEnoughToShip()
    {
        var bytes = FontBytes();
        Assert.True(bytes.Length > 1000, "字体文件异常小，可能没生成成功");
        // 子集应当很小（原可变字体 10.2 MB）；给个宽松上界防止误把源文件提交进来
        Assert.True(bytes.Length < 200 * 1024,
            $"图标字体应远小于源文件（实际 {bytes.Length / 1024} KB）—— 可能提交了未子集化的源字体");
    }

    [Fact]
    public void IconFont_IsStatic_NotVariable()
    {
        // 与中文字体同一条硬约束（CONSTRAINTS D6）：
        // 可变字体在 Avalonia 下会被渲染成默认轴实例 → 图标发虚。
        // fvar 表存在即表示仍是可变字体。
        var bytes = FontBytes();
        var fvar = System.Text.Encoding.ASCII.GetBytes("fvar");
        Assert.False(Contains(bytes, fvar),
            "图标字体仍含 fvar 表（可变字体）—— 必须先用 tools/make-icon-font.py 实例化为静态");
    }

    [Fact]
    public void EveryIconUsedInXaml_ExistsInFont()
    {
        // ⭐ 核心不变量：XAML 用到 ≠ 字体含有 → 界面上会显示空白。
        var used = CodepointsUsedInXaml();
        Assert.True(used.Count > 0, "应在 XAML 中检测到图标用法（否则本测试失去意义）");

        // 从字体的 cmap 里解析出所有可映射码位。
        // 用一个最小的 TTF cmap 解析：这里只需 format 4/12 的码位集合。
        var available = ReadCmapCodepoints(FontBytes());

        var missing = used.Where(cp => !available.Contains(cp)).OrderBy(cp => cp).ToList();
        Assert.True(missing.Count == 0,
            "以下图标码位在 XAML 里被使用，但**不在子集字体中**（界面会显示空白）："
            + string.Join(", ", missing.Select(c => $"U+{c:X4}"))
            + "。请把它们加进 tools/make-icon-font.py 的 ICON_CODEPOINTS 并重跑该脚本。");
    }

    // ---------------------------------------------------------------- 最小 cmap 解析

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            if (ok) return true;
        }
        return false;
    }

    private static int Be16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
    private static long Be32(byte[] b, int o)
        => ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3];

    /// <summary>
    /// 解析 TTF 的 cmap，返回所有可映射码位。
    /// 只实现 format 4（BMP）与 format 12（含扩展平面）—— 图标都在 BMP（U+E000 区）。
    /// </summary>
    private static HashSet<int> ReadCmapCodepoints(byte[] b)
    {
        var set = new HashSet<int>();
        int numTables = Be16(b, 4);
        int cmapOff = -1;
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            var tag = System.Text.Encoding.ASCII.GetString(b, rec, 4);
            if (tag == "cmap") { cmapOff = (int)Be32(b, rec + 8); break; }
        }
        if (cmapOff < 0) throw new InvalidOperationException("字体缺少 cmap 表");

        int nSub = Be16(b, cmapOff + 2);
        for (int i = 0; i < nSub; i++)
        {
            int sub = cmapOff + 4 + i * 8;
            int off = cmapOff + (int)Be32(b, sub + 4);
            int fmt = Be16(b, off);
            if (fmt == 4)
            {
                int segX2 = Be16(b, off + 6);
                int seg = segX2 / 2;
                int endO = off + 14;
                int startO = endO + segX2 + 2;
                for (int s = 0; s < seg; s++)
                {
                    int end = Be16(b, endO + s * 2);
                    int start = Be16(b, startO + s * 2);
                    if (start == 0xFFFF) continue;
                    for (int c = start; c <= end && c != 0x10000; c++) set.Add(c);
                }
            }
            else if (fmt == 12)
            {
                int nGroups = (int)Be32(b, off + 12);
                for (int g = 0; g < nGroups; g++)
                {
                    int go = off + 16 + g * 12;
                    int sc = (int)Be32(b, go);
                    int ec = (int)Be32(b, go + 4);
                    // 图标都在 BMP，避免展开超大区间
                    if (sc > 0xFFFF) continue;
                    if (ec > 0xFFFF) ec = 0xFFFF;
                    for (int c = sc; c <= ec; c++) set.Add(c);
                }
            }
        }
        return set;
    }
}
