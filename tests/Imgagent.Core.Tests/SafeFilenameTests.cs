using System.Text.RegularExpressions;
using Imgagent.Core.Storage;

namespace Imgagent.Core.Tests;

/// <summary>
/// 文件名安全的回归测试 —— 对应 Python 的 store.safe_filename 测试。
/// 三道防线：白名单字符 / basename 防穿越 / 剥首尾点。
/// </summary>
public class SafeFilenameTests
{
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("a/b/c")]
    [InlineData("....")]
    [InlineData(".hidden")]
    [InlineData("")]
    public void SafeFilename_NeverEscapesOrEmpty(string evil)
    {
        var name = Session.SafeFilename("0917_120000_001", evil, ".png");
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain("\\", name);
        Assert.False(name.StartsWith("."));
        Assert.NotEqual("", name);
        Assert.True(name.Length <= 95);
    }

    [Fact]
    public void SafeFilename_KeepsChinese()
    {
        var name = Session.SafeFilename("0919_120000_001", "一只猫在窗台上", ".png", limit: 24);
        Assert.Contains("一只猫", name);
        Assert.EndsWith(".png", name);
    }

    [Fact]
    public void SafeFilename_SanitizesSuffix()
    {
        var name = Session.SafeFilename("p", "stem", ".p/ng;rm");
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain(";", name);
    }

    [Fact]
    public void SafeFilename_EmptyStemFallsBackToImage()
    {
        var name = Session.SafeFilename("pre", "!!!", ".png");
        Assert.Contains("image", name);
    }

    [Fact]
    public void SafeFilename_ManyEvilCombos_AllSafe()
    {
        string[] evil = { "../../etc/passwd", "a\\b", "..", ".", "   ", "\0name", "con:*?\"<>|" };
        string[] sufs = { ".png", ".JPG", "", ".tar.gz" };
        foreach (var e in evil)
            foreach (var s in sufs)
            {
                var n = Session.SafeFilename("0917_120000_001", e, s);
                Assert.DoesNotContain("/", n);
                Assert.DoesNotContain("\\", n);
                Assert.False(n.StartsWith("."));
                Assert.False(n.Contains('\0'));
            }
    }
}
