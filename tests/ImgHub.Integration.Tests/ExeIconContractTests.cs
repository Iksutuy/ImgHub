namespace ImgHub.Integration.Tests;

/// <summary>
/// exe 图标的格式契约（v0.5.42）。
///
/// ## 用户报的问题
/// 「**二进制文件没有图标**」—— 资源管理器里显示通用程序图标。
///
/// ## 两个独立根因（都是"不报错"型）
/// 1. **PNG-in-ICO**：`.ico` 内部的帧是 PNG 数据。Explorer / `Shell32.ExtractIcon`
///    不渲染它 → 显示通用图标（Avalonia 窗口图标那边则会直接抛异常崩掉）。
/// 2. **目录表 offset 基准错**：ICO 布局是 `[ICONDIR(6)][目录表(16×N)][帧数据]`，
///    每项的 `dwImageOffset` 是**相对文件开头的绝对偏移**，基线必须是 `6+16×N`。
///    写成从 0 累加的话，文件字节数看着正常，但**任何工具都读不出图标**。
///
/// ## 为什么必须有这条测试
/// 生成脚本 `make-icon.ps1` 曾用 `New-Object System.Drawing.Icon(...)` 自检，
/// 但那一步抛异常被 PowerShell 的**非终止错误**吞掉 → 脚本照常打印 `DONE`，
/// 于是损坏的 `.ico` 一直没被发现。⇒ 用**独立于生成脚本**的检查兜底。
/// </summary>
public class ExeIconContractTests
{
    private static string RepoPath(string rel)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            var p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(p)) return p;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("找不到 " + rel);
    }

    private static byte[] DesktopIco => File.ReadAllBytes(RepoPath("src/ImgHub.Desktop/app.ico"));

    [Fact]
    public void Ico_IsAValidIconDirectory()
    {
        var b = DesktopIco;
        Assert.True(b.Length > 1024, "ico 不应为空/过小");

        Assert.Equal(0, BitConverter.ToUInt16(b, 0));   // reserved
        Assert.Equal(1, BitConverter.ToUInt16(b, 2));   // type = icon

        var count = BitConverter.ToUInt16(b, 4);
        Assert.True(count >= 4, $"应包含多个尺寸（便于 Explorer 按需取），实际 {count}");
    }

    [Fact]
    public void Ico_FramesAreBmp_NotPng()
    {
        // ⚠️ 核心：帧数据必须是 BMP（BITMAPINFOHEADER 以 0x28 开头），
        //   不能是 PNG（0x89 'PNG'）。PNG-in-ICO → Explorer 不渲染 → exe 无图标。
        var b = DesktopIco;
        var count = BitConverter.ToUInt16(b, 4);

        for (int i = 0; i < count; i++)
        {
            var o = 6 + (i * 16);
            var off = (int)BitConverter.ToUInt32(b, o + 12);

            Assert.True(off + 4 <= b.Length, $"帧 #{i} 的 offset {off} 越界");
            var isPng = b[off] == 0x89 && b[off + 1] == 0x50 && b[off + 2] == 0x4E && b[off + 3] == 0x47;
            Assert.False(isPng,
                $"帧 #{i} 是 PNG 数据（PNG-in-ICO）—— Explorer 不渲染它，exe 会显示通用图标。" +
                "生成脚本必须写 BMP/DIB 帧。");

            var biSize = BitConverter.ToUInt32(b, off);
            Assert.True(biSize == 40, $"帧 #{i} 应始于 BITMAPINFOHEADER(40)，实际 biSize={biSize}");
        }
    }

    [Fact]
    public void Ico_DirectoryOffsetsStartAfterDirectoryTable()
    {
        // ⚠️ 第二个坑：offset 基线必须是 6+16×N。
        //   旧版从 0 累加 → 全部条目指向文件头 → 整个 ico 不可解析。
        var b = DesktopIco;
        var count = BitConverter.ToUInt16(b, 4);
        var dirLen = 6 + (16 * count);

        for (int i = 0; i < count; i++)
        {
            var o = 6 + (i * 16);
            var off = (int)BitConverter.ToUInt32(b, o + 12);
            var len = (int)BitConverter.ToUInt32(b, o + 8);

            Assert.True(off >= dirLen,
                $"帧 #{i} 的 offset={off} 小于目录表长度 {dirLen} —— 会读到文件头，ico 不可用");
            Assert.True(off + len <= b.Length,
                $"帧 #{i} 的 offset+size={off + len} 超出文件长度 {b.Length}");
        }

        // 第一帧应恰好紧跟目录表
        Assert.Equal(dirLen, (int)BitConverter.ToUInt32(b, 6 + 12));
    }

    [Fact]
    public void DesktopCsproj_ReferencesTheIcon()
    {
        // 光有正确的 ico 还不够 —— csproj 必须把它声明为 ApplicationIcon。
        var csproj = File.ReadAllText(RepoPath("src/ImgHub.Desktop/ImgHub.Desktop.csproj"));
        Assert.Contains("<ApplicationIcon>app.ico</ApplicationIcon>", csproj);
    }
}
