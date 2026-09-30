using ImgHub.Core.Imaging;

namespace ImgHub.Core.Tests;

/// <summary>
/// 图像层测试：占位图确定性、编解码、参考图缩小。
/// 对应 Python 的 test_impydroid 里的 pngcodec / placeholder 用例。
/// </summary>
public class ImagingTests
{
    [Fact]
    public void Placeholder_IsDeterministic()
    {
        // 回归：同 prompt + step 必须永远同一张图（便于复现与对拍）
        var a = Placeholder.Png("一只猫", step: 0, size: 32);
        var b = Placeholder.Png("一只猫", step: 0, size: 32);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Placeholder_DifferentPrompt_DifferentImage()
    {
        var a = Placeholder.Png("猫", size: 32);
        var b = Placeholder.Png("狗", size: 32);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Placeholder_DifferentStep_DifferentImage()
    {
        var a = Placeholder.Png("猫", step: 0, size: 32);
        var b = Placeholder.Png("猫", step: 1, size: 32);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Placeholder_ProducesValidPng()
    {
        var png = Placeholder.Png("测试", size: 64);
        Assert.Equal("image/png", ImageCodec.SniffMediaType(png));
        var size = ImageCodec.Size(png);
        Assert.NotNull(size);
        Assert.Equal(64, size!.Value.Width);
        Assert.Equal(64, size.Value.Height);
    }

    [Fact]
    public void Crc32_MatchesKnownValue()
    {
        // 与 Python zlib.crc32(b"123456789") 一致 = 0xCBF43926
        Assert.Equal(0xCBF43926u, Placeholder.Crc32("123456789"));
    }

    [Fact]
    public void Sniff_DetectsFormats()
    {
        Assert.Equal("image/png", ImageCodec.SniffMediaType(Placeholder.Png("x", size: 8)));
        Assert.Equal("image/jpeg", ImageCodec.SniffMediaType(new byte[] { 0xFF, 0xD8, 0xFF }));
        Assert.Null(ImageCodec.SniffMediaType(new byte[] { 0, 1, 2, 3 }));
    }

    [Fact]
    public void ShrinkForReference_ShrinksLargeImage()
    {
        var big = Placeholder.Png("big", size: 512);
        var shrunk = ImageCodec.ShrinkForReference(big, maxSide: 128);
        var size = ImageCodec.Size(shrunk);
        Assert.NotNull(size);
        Assert.True(Math.Max(size!.Value.Width, size.Value.Height) <= 128);
    }

    [Fact]
    public void ShrinkForReference_LeavesSmallImageUnchanged()
    {
        var small = Placeholder.Png("small", size: 64);
        var same = ImageCodec.ShrinkForReference(small, maxSide: 1024);
        Assert.Equal(small, same);
    }

    [Fact]
    public void ShrinkForReference_NonImage_ReturnsInput()
    {
        var junk = new byte[] { 1, 2, 3, 4, 5 };
        Assert.Equal(junk, ImageCodec.ShrinkForReference(junk));
    }

    [Fact]
    public void ExtFor_MapsMediaTypes()
    {
        Assert.Equal("jpg", ImageCodec.ExtFor("image/jpeg"));
        Assert.Equal("webp", ImageCodec.ExtFor("image/webp"));
        Assert.Equal("png", ImageCodec.ExtFor("image/png"));
        Assert.Equal("png", ImageCodec.ExtFor("application/unknown"));
    }
}
