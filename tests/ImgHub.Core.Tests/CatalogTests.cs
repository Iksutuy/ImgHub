using ImgHub.Core;
using ImgHub.Core.Models;

namespace ImgHub.Core.Tests;

/// <summary>
/// 常量目录的回归测试 —— 逐条对应 Python 的 test_impydroid.py 里的
/// 质量档位 / 成本估算 / 模型匹配契约（CONSTRAINTS D4/D8）。
/// </summary>
public class CatalogTests
{
    [Fact]
    public void Quality_RoutesPerProvider_OpenRouterHasNoXhigh()
    {
        // 回归：OpenRouter 传 xhigh/max 直接 400，菜单里不能给这些选项
        var orq = Catalog.QualityChoices(ApiProvider.OpenRouter, "openai/gpt-image-2.5-flare");
        Assert.DoesNotContain("xhigh", orq);
        Assert.DoesNotContain("max", orq);
        Assert.Contains("low", orq);
        Assert.Contains("high", orq);
        Assert.False(Catalog.QualitySupported(ApiProvider.OpenRouter, "xhigh", "x"));
    }

    [Fact]
    public void Quality_Apimart25_SupportsAllSix()
    {
        var q = Catalog.QualityChoices(ApiProvider.Apimart, "gpt-image-2.5-flare");
        Assert.Contains("xhigh", q);
        Assert.Contains("max", q);
        Assert.Equal(6, q.Length);
    }

    [Fact]
    public void Quality_ApimartNon25_DropsXhighMax()
    {
        // 回归：xhigh/max 传给 gpt-image-2 会 400（文档明确不自动降级）
        var q = Catalog.QualityChoices(ApiProvider.Apimart, "gpt-image-2");
        Assert.DoesNotContain("xhigh", q);
        Assert.DoesNotContain("max", q);
        Assert.False(Catalog.QualitySupported(ApiProvider.Apimart, "xhigh", "gpt-image-2"));
    }

    [Fact]
    public void Cost_NoTenfoldDeviation_VsOfficial()
    {
        // 回归：旧版 xhigh/max 退化到默认 0.02，比实际低约 10 倍
        var official = new Dictionary<string, double>
        {
            ["low"] = 0.00588, ["medium"] = 0.01317, ["high"] = 0.05268,
            ["xhigh"] = 0.09366, ["max"] = 0.21072,
        };
        foreach (var (q, off) in official)
        {
            var mine = Catalog.EstimateCost(q, "1k");
            var ratio = mine / off;
            Assert.InRange(ratio, 0.6, 1.2);
        }
        Assert.True(Catalog.EstimateCost("xhigh", "1k") > 0.05);
        Assert.True(Catalog.EstimateCost("max", "1k") > 0.15);
    }

    [Fact]
    public void Cost_2k_IsNotQuadruple()
    {
        // 回归：查表失败再乘倍数 → 2k 被算成 1k 的 4 倍
        var c1 = Catalog.EstimateCost("low", "1k");
        var c2 = Catalog.EstimateCost("low", "2k");
        var c4 = Catalog.EstimateCost("low", "4k");
        Assert.Equal(2.0, c2 / c1, 3);
        Assert.Equal(4.0, c4 / c1, 3);
    }

    [Fact]
    public void Cost_AutoFallsBackToLow()
    {
        Assert.Equal(Catalog.CostAutoFallback, Catalog.EstimateCost("auto", "1k"), 6);
    }

    [Fact]
    public void ModelMatchesProvider_NamingConvention()
    {
        Assert.True(Catalog.ModelMatchesProvider("openai/gpt-image-2", ApiProvider.OpenRouter));
        Assert.False(Catalog.ModelMatchesProvider("gpt-image-2", ApiProvider.OpenRouter));
        Assert.True(Catalog.ModelMatchesProvider("gpt-image-2", ApiProvider.Apimart));
        Assert.False(Catalog.ModelMatchesProvider("openai/gpt-image-2", ApiProvider.Apimart));
    }

    [Fact]
    public void EstimateDetail_TokenTableMatchesOfficial()
    {
        var (_, tLow) = Catalog.EstimateDetail("low", "1k", 1);
        var (_, tMed) = Catalog.EstimateDetail("medium", "1k", 1);
        Assert.Equal(196, tLow);
        Assert.Equal(439, tMed);
    }
}
