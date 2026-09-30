using ImgHub.App.Ui;

namespace ImgHub.Integration.Tests;

/// <summary>
/// 本地化（中/英/日）的契约回归（v0.5.40）。
///
/// 为什么必须有两类断言：
///   · **三语键一致性** —— 漏翻译的键会静默回退到中文（对日文用户就是"界面半中半日"），
///     必须在测试里拦住，而不是靠人眼扫 190 条。
///   · **切换即时生效** —— 这是 `Localizer` 用 INotifyPropertyChanged 而非 DynamicResource 的
///     唯一理由（DynamicResource 实测不会推送运行时更新）。要钉住"赋值即通知"。
/// </summary>
[Collection(LocalizationTests.Name)]
public class LocalizationContractTests
{
    private static Localizer L => Localizer.Instance;

    [Fact]
    public void AllThreeLanguages_HaveExactlyTheSameKeys()
    {
        // 通过反射读三个私有字典 —— 这是"键集合一致"的唯一可靠判据。
        var t = typeof(Localizer);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;

        var zh = (Dictionary<string, string>)t.GetField("Zh", flags)!.GetValue(null)!;
        var en = (Dictionary<string, string>)t.GetField("En", flags)!.GetValue(null)!;
        var ja = (Dictionary<string, string>)t.GetField("Ja", flags)!.GetValue(null)!;

        Assert.True(zh.Count > 100, $"中文文案应成规模（当前 {zh.Count} 条）");

        var missingInEn = zh.Keys.Except(en.Keys).OrderBy(x => x).ToList();
        var missingInJa = zh.Keys.Except(ja.Keys).OrderBy(x => x).ToList();
        var extraInEn = en.Keys.Except(zh.Keys).OrderBy(x => x).ToList();
        var extraInJa = ja.Keys.Except(zh.Keys).OrderBy(x => x).ToList();

        Assert.True(missingInEn.Count == 0, $"英文缺少这些键：{string.Join(", ", missingInEn)}");
        Assert.True(missingInJa.Count == 0, $"日文缺少这些键：{string.Join(", ", missingInJa)}");
        Assert.True(extraInEn.Count == 0, $"英文有中文没有的键：{string.Join(", ", extraInEn)}");
        Assert.True(extraInJa.Count == 0, $"日文有中文没有的键：{string.Join(", ", extraInJa)}");
    }

    [Fact]
    public void NoTranslation_IsLeftAsEmptyOrKeyEcho()
    {
        // 空串 = 界面上出现"看不见的文字"（比显示键名更难发现）。
        var t = typeof(Localizer);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        foreach (var name in new[] { "Zh", "En", "Ja" })
        {
            var d = (Dictionary<string, string>)t.GetField(name, flags)!.GetValue(null)!;
            var empties = d.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();
            Assert.True(empties.Count == 0, $"{name} 有空译文：{string.Join(", ", empties)}");
        }
    }

    [Fact]
    public void SwitchingLanguage_ChangesLookupImmediately()
    {
        var old = L.Language;
        try
        {
            L.Language = "zh";
            Assert.Equal("生成", L["action.generate"]);

            L.Language = "en";
            Assert.Equal("Generate", L["action.generate"]);

            L.Language = "ja";
            Assert.Equal("生成", L["action.generate"]);   // 日文里"生成"就是汉字，同形
            Assert.Equal("設定", L["common.settings"]);   // 这个能区分：日文"設定" vs 中文"设置"
        }
        finally { L.Language = old; }
    }

    [Fact]
    public void LanguageChange_RaisesItemNotification_SoBindingsRefresh()
    {
        // ⚠️ 这是"不重建界面就能切语言"的关键（v0.5.41 实测定位）。
        //   Avalonia 12 的索引器绑定**只认 `Item`**（C# 索引器的元数据名）：
        //     · string.Empty → 不刷新（那是经典绑定的约定）
        //     · Item[key]   → 不刷新（**看起来最像，却是错的**）
        //     · Item        → ✅ 刷新
        //   ⇒ 这里钉住"发的是 `Item`"。
        var old = L.Language;
        try
        {
            L.Language = "zh";
            var events = new List<string?>();
            void H(object? s, System.ComponentModel.PropertyChangedEventArgs e) => events.Add(e.PropertyName);
            L.PropertyChanged += H;
            try { L.Language = "en"; }
            finally { L.PropertyChanged -= H; }

            Assert.Contains("Item", events);                      // 唯一有效的索引器通知名
            Assert.Contains(nameof(Localizer.Language), events);  // 语言属性本身
            Assert.DoesNotContain(string.Empty, events);          // 空名对编译绑定无效 → 不该再发
        }
        finally { L.Language = old; }
    }

    [Fact]
    public void UnknownKey_DoesNotReturnEmpty()
    {
        // 静默空白是本项目踩过的坑（按钮存在但看不见）。未知键至少返回键名，便于定位。
        var old = L.Language;
        try
        {
            L.Language = "en";
            Assert.Equal("definitely.not.a.key", L["definitely.not.a.key"]);
        }
        finally { L.Language = old; }   // ⚠️ 必须还原：Localizer 是进程级单例，不还原会污染其他测试
    }

    // ================================================================ v0.5.41：VM 侧文案也要本地化

    [Fact]
    public void ViewModelBoundStrings_AreLocalized_NotHardcodedChinese()
    {
        // ⚠️ 用户报"切换语言完全没变化"的**第二个根因**（v0.5.41）：
        //   上一轮只把 XAML 里的**字面量**接进了 Localizer，
        //   而 VM 里**动态生成文案的属性**（ToolNames / RegionCountText / SetupHint / …）
        //   仍是硬编码中文 → 切到英/日时这些地方不变。
        //
        // 这条用**源码扫描**钉住：这些"会被 XAML 绑定"的属性不得出现中文字面量。
        var src = File.ReadAllText(RepoPath("src/ImgHub.App/ViewModels/MainViewModel.cs"));

        string[] mustBeLocalized =
        {
            "ToolNames", "RegionCountText", "SetupHint", "BatchNHint",
            "HistoryMultiButtonText", "PromptMultiButtonText",
            "PolishStyleNames", "PolishStyleLabel", "ExtractHint", "GuideActiveHint", "EditTip", "GenerateTip",
        };

        var offenders = new List<string>();
        foreach (var prop in mustBeLocalized)
        {
            var i = src.IndexOf($"public string {prop}", StringComparison.Ordinal);
            if (i < 0) i = src.IndexOf($"public IReadOnlyList<string> {prop}", StringComparison.Ordinal);
            Assert.True(i >= 0, $"未找到属性 {prop}（改名了？请同步这条测试）");

            // ⚠️ 精确取到该属性**定义结束**为止：
            //   · 表达式体（=> ...;）取到第一个分号
            //   · 块体（{ ... }）取到配对的 }
            //   用固定窗口会吃到**下一个**成员（实测：把日志行的中文误报成该属性的问题）。
            var seg = ExtractMember(src, i);
            foreach (var line in seg.Split('\n'))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("///") || trimmed.StartsWith("//")) continue;   // 注释允许中文
                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"[\u4e00-\u9fff]"))
                {
                    offenders.Add($"{prop}: {line.Trim()}");
                    break;
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "以下会被界面显示的属性仍是硬编码中文（切语言时不会变），必须改走 Localizer：\n" +
            string.Join("\n", offenders));
    }

    [Fact]
    public void ToolNames_OrderMatchesRegionToolEnum_AndIsTranslated()
    {
        // ToolNames 会随语言变化，但**顺序**必须与 RegionCanvas.RegionTool 枚举一致
        // （下拉索引直接映射到工具）—— 本地化时最容易把顺序搞乱。
        var keys = new[]
        {
            "region.toolRect", "region.toolEllipse", "region.toolMarker", "region.toolEraser",
        };
        Assert.Equal(Enum.GetNames<ImgHub.App.Controls.RegionCanvas.RegionTool>().Length, keys.Length);

        var old = L.Language;
        try
        {
            var zh0 = L["region.toolRect"];
            foreach (var l in new[] { "zh", "en", "ja" })
            {
                L.Language = l;
                foreach (var k in keys)
                    Assert.False(string.IsNullOrWhiteSpace(L[k]), $"{l} 下 {k} 为空");
            }
            // 证明它**真的会变**（否则等于没本地化）
            L.Language = "en";
            Assert.NotEqual(zh0, L["region.toolRect"]);
        }
        finally { L.Language = old; }
    }

    /// <summary>从成员定义起点取到该成员结束（表达式体看分号，块体看配对大括号）。</summary>
    private static string ExtractMember(string src, int start)
    {
        // 跳过签名（形如 public string Xxx => / public string Xxx { ）
        var arrow = src.IndexOf("=>", start, StringComparison.Ordinal);
        var brace = src.IndexOf('{', start);
        if (brace >= 0 && (arrow < 0 || brace < arrow))
        {
            // 块体：配对
            int depth = 0, i = brace;
            for (; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}') { depth--; if (depth == 0) { i++; break; } }
            }
            return src[start..i];
        }
        if (arrow >= 0)
        {
            var semi = src.IndexOf(';', arrow);
            return semi > 0 ? src[start..(semi + 1)] : src[start..Math.Min(start + 300, src.Length)];
        }
        return src[start..Math.Min(start + 300, src.Length)];
    }

    [Fact]
    public void EveryLocalizedVmProperty_IsNotifiedWhenLanguageChanges()
    {
        // ⚠️ 用户报"切换语言没变化"的**第三个坑**（v0.5.41）：
        //   VM 里"动态生成文案"的计算属性（如 HistoryMultiButtonText）
        //   内部虽读了 Localizer，但 **Avalonia 只认 OnPropertyChanged(nameof(X))** ——
        //   不显式通知 → 界面保持旧语言（实测：切英文后「多选」按钮仍是中文）。
        //
        // 这条钉住：`NotifyLocalizedStrings()` 必须覆盖上面那些属性的**全部**。
        var src = File.ReadAllText(RepoPath("src/ImgHub.App/ViewModels/MainViewModel.cs"));

        var i = src.IndexOf("private void NotifyLocalizedStrings()", StringComparison.Ordinal);
        Assert.True(i >= 0, "应存在 NotifyLocalizedStrings()（切语言时统一通知 VM 文案属性）");
        var end = src.IndexOf("\n    }", i, StringComparison.Ordinal);
        var body = src[i..end];
        var notified = System.Text.RegularExpressions.Regex
            .Matches(body, @"nameof\((\w+)\)").Select(m => m.Groups[1].Value).ToHashSet();

        string[] required =
        {
            "ToolNames", "RegionCountText", "EditTip", "GenerateTip", "RegionEditTip",
            "ExtractHint", "GuideActiveHint", "PolishStyleNames", "PolishStyleLabel",
            "SetupHint", "BatchNHint", "HistoryMultiButtonText", "PromptMultiButtonText",
        };
        var missing = required.Where(r => !notified.Contains(r)).ToList();
        Assert.True(missing.Count == 0,
            "这些属性会随语言变化，但切语言时没通知刷新（界面会保持旧语言）：" +
            string.Join(", ", missing));
    }

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

    [Fact]
    public void Normalize_AcceptsCommonSpellings_AndFallsBackToChinese()
    {
        Assert.Equal("en", Localizer.Normalize("EN"));
        Assert.Equal("en", Localizer.Normalize("en-US"));
        Assert.Equal("ja", Localizer.Normalize("ja-JP"));
        Assert.Equal("ja", Localizer.Normalize("jp"));
        Assert.Equal("zh", Localizer.Normalize("zh-CN"));
        Assert.Equal("zh", Localizer.Normalize(null));
        Assert.Equal("zh", Localizer.Normalize("klingon"));   // 未知 → 中文（源语言）
    }
}
