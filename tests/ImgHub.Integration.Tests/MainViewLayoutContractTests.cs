using System.Text.RegularExpressions;
using System.IO;

namespace ImgHub.Integration.Tests;

/// <summary>
/// 标注工具栏与预览层的 **XAML 静态契约**回归。
///
/// 为什么用静态断言（而不是渲染后量 Bounds）：
///   `MainView.axaml` 用了 `Cursor`，构造它需要 `Avalonia.Platform.ICursorFactory`，
///   而测试环境只初始化了 Skia 渲染后端（`SkiaPlatform.Initialize()`）→ 拿不到该服务。
///   所以这里改为对 XAML 源码做**结构断言**，覆盖那些"改错了也不报错、只在肉眼下暴露"的契约。
/// </summary>
public class MainViewLayoutContractTests
{
    private static string Xaml => ReadSource("src/ImgHub.App/Views/MainView.axaml");

    /// <summary>仓库根（向上找直到看到 src/ImgHub.App）。</summary>
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "src", "ImgHub.App"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("找不到仓库根目录");
    }

    private static string ReadSource(string relative)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException($"找不到源文件：{relative}");
    }

    // ================================================================ 预览层对齐（v0.5.33 回归）

    [Fact]
    public void PreviewLayers_MustUseStretch_NeverCenter()
    {
        // ⚠️ **关键回归防护**：曾把 Image/RegionCanvas 改成 `HorizontalAlignment="Center"`，
        //    而 Center 下**未设尺寸**的控件 DesiredSize 为 0 → 画布 0×0 →
        //    工具画不上、滚轮也不响应（用户报"编辑不了图片"）。
        //    必须用 Stretch：对齐逻辑没跑时至少铺满容器，仍可交互。
        foreach (var name in new[] { "PreviewLayer", "PreviewLayerNarrow", "RegionLayer", "RegionLayerNarrow" })
        {
            var idx = Xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
            Assert.True(idx >= 0, $"XAML 里找不到 {name}");

            // 取该元素声明所在的片段（到下一个 "/>" 或 ">"）
            var end = Xaml.IndexOf('>', idx);
            var seg = Xaml[idx..Math.Min(end + 1, Xaml.Length)];

            Assert.DoesNotContain("HorizontalAlignment=\"Center\"", seg);
            Assert.DoesNotContain("VerticalAlignment=\"Center\"", seg);
        }
    }

    [Fact]
    public void PreviewLayers_UseStretch_Alignment()
    {
        foreach (var name in new[] { "PreviewLayer", "PreviewLayerNarrow" })
        {
            var idx = Xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
            Assert.True(idx >= 0);
            var end = Xaml.IndexOf('>', idx);
            var seg = Xaml[idx..Math.Min(end + 1, Xaml.Length)];

            Assert.Contains("HorizontalAlignment=\"Stretch\"", seg);
            Assert.Contains("VerticalAlignment=\"Stretch\"", seg);
            Assert.Contains("Stretch=\"Uniform\"", seg);   // 图片必须保持 Uniform（等比）
        }
    }

    // ================================================================ 标注工具栏（需求③ / 遮挡）

    [Fact]
    public void MaskHelpButton_ExistsOnce_InBottomBar()
    {
        // 需求③：蒙版说明入口。⚠️ 必须放**底栏**：
        //   标注工具条是固定高度，在中栏一行放不下 → 换行到第 3 行 → 被裁（用户报"被遮挡"）。
        // ⚠️ v0.5.37：图标化后按钮无 Content 属性（文字在 TextBlock 里）。
        // ⚠️ v0.5.40：文案改走 Localizer 索引器绑定，锚点改为**键名**。
        Assert.Equal(1, CountOccurrences(Xaml, "{CompiledBinding ShowMaskHelpCommand}"));

        var barIdx = Xaml.IndexOf("===== 底栏", StringComparison.Ordinal);
        var helpIdx = Xaml.IndexOf("[bottom.maskHelp]", StringComparison.Ordinal);
        Assert.True(barIdx >= 0 && helpIdx > barIdx,
            "「蒙版说明」必须在底栏内（中栏工具条放不下会被裁）");
    }

    [Fact]
    public void ViewerAndGalleryButtons_AreInBottomBar_NotMiddleColumn()
    {
        // 用户要求（v0.5.33）：把「系统看图器 / 保存到相册」移到**底栏左侧**。
        // 原因：它们原在中栏标注工具条下方，工具条换行后与它们互相遮挡。
        // ⚠️ v0.5.37：锚点从 Content 改为文字 TextBlock。
        // ⚠️ v0.5.40：改走 Localizer 键名锚点。
        var barIdx = Xaml.IndexOf("===== 底栏", StringComparison.Ordinal);
        Assert.True(barIdx >= 0, "应存在启用后的底栏");

        foreach (var key in new[] { "bottom.viewer", "bottom.saveGallery" })
        {
            var marker = $"[{key}]";
            Assert.Equal(1, CountOccurrences(Xaml, marker));

            var idx = Xaml.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(idx > barIdx,
                $"「{key}」必须位于底栏内（中栏那份应已移除）");
        }
    }

    [Fact]
    public void EveryButton_HasAnIcon()
    {
        // v0.5.37 用户要求：**所有按钮**都要有统一风格的图标。
        // 例外（有意不加）：
        //   · 调色板色块（内容由 Background 表达）
        //   · 缩略图项（内容是图片 + 文字，非工具按钮）
        //   这两类在 XAML 里没有 Content 也没有文字，无法用图标表达语义。
        var xamlDir = Path.Combine(RepoRoot(), "src", "ImgHub.App", "Views");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(xamlDir, "*.axaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            // ⚠️ 同时查 Button **与 ToggleButton**（v0.5.38：用户报"深色按钮没有图标"，
            //   它其实是 ToggleButton —— 只查 Button 会漏）。
            foreach (Match m in Regex.Matches(text,
                @"<(Button|ToggleButton)([^>]*?)(?:/>|>(.*?)</\1>)",
                RegexOptions.Singleline))
            {
                var seg = m.Groups[2].Value;
                var body = m.Groups[3].Value;
                if (body.Contains("Classes=\"icon")) continue;          // 已有图标
                if (seg.Contains("Background=\"{Binding}\"")) continue; // 色块按钮
                if (seg.Contains("Width=\"44\"") || seg.Contains("Width=\" 44\"")) continue;
                if (seg.Contains("x:Name=\"ThumbButton\"") || body.Contains("CostText")) continue;
                // 状态徽标（自检结论）：内容是"圆点 + 文字"，本身是状态指示而非动作按钮
                if (seg.Contains("ShowSelfCheckCommand")) continue;

                var content = Regex.Match(seg, @"Content=""([^""]*)""");
                var ln = text[..m.Index].Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetFileName(file)}:{ln} Content={content.Groups[1].Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "以下按钮还没有图标（用户要求所有按钮统一加图标）：\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void NoHiddenBottomBar_WithUnreachableButtons()
    {
        // 约束 D4b/D4c：按钮必须可达 —— 不允许再出现 IsVisible="False" 的整条底栏。
        // （旧底栏 `IsVisible="False"` 里的按钮全部不可达，是历史事故。）
        Assert.DoesNotContain("底栏暂隐藏", Xaml);
    }

    [Fact]
    public void EditImageButton_EnabledByCanAnnotate_NotIsConfigured()
    {
        // ⚠️ 用户报"切换到未就绪端点就自动取消编辑"：
        //    按钮 IsEnabled 原绑 `IsConfigured` → 切到没配 key 的端点时变灰，
        //    用户既进不去编辑、也退不出（看着像"被自动取消"）。
        //    标注是纯本地操作，不该依赖 API key —— 必须绑 `CanAnnotate`。
        var count = CountOccurrences(Xaml, "IsEnabled=\"{CompiledBinding CanAnnotate}\"");
        Assert.True(count >= 2,
            $"「编辑图片」按钮（两套布局）都应绑 CanAnnotate，实际 {count} 次");

        // 反证：不应再有「编辑图片」按钮绑 IsConfigured
        var editIdx = Xaml.IndexOf("Content=\"编辑图片\"", StringComparison.Ordinal);
        while (editIdx >= 0)
        {
            var end = Xaml.IndexOf("/>", editIdx, StringComparison.Ordinal);
            var seg = Xaml[editIdx..Math.Min(end + 2, Xaml.Length)];
            Assert.DoesNotContain("IsConfigured", seg);
            editIdx = Xaml.IndexOf("Content=\"编辑图片\"", editIdx + 1, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ToolbarRows_DeclareThreeRows()
    {
        // 工具条是**固定高度**（UiMetrics.AnnotationToolbarHeightWide）：只能容纳声明过的行数。
        // 若有人往某行塞太多控件导致换行，就会重演"被遮挡/挤成一团"。
        // ⚠️ v0.5.39 用户要求：由 2 行改为 **3 行**（切马克笔时出现「粗细」控件 →
        //   2 行布局会横向溢出并换行，与下一行挤在一起）。
        var regionModeStart = Xaml.IndexOf("IsVisible=\"{CompiledBinding RegionMode}\"", StringComparison.Ordinal);
        Assert.True(regionModeStart >= 0);

        var seg = Xaml[regionModeStart..Math.Min(regionModeStart + 8000, Xaml.Length)];
        Assert.Contains("RowDefinitions=\"Auto,Auto,Auto\"", seg);
    }

    [Fact]
    public void ToolRow_ComboHasExplicitHeight_ForBaselineAlignment()
    {
        // 用户报"工具和下拉框之间没对齐"：下拉原来 Height=34 + 文字默认基线。
        // 现在统一：控件垂直居中 + 下拉用统一度量常量（避免两处硬编码漂移）。
        var toolIdx = Xaml.IndexOf("ItemsSource=\"{CompiledBinding ToolNames}\"", StringComparison.Ordinal);
        Assert.True(toolIdx >= 0);

        var end = Xaml.IndexOf("/>", toolIdx, StringComparison.Ordinal);
        var seg = Xaml[toolIdx..Math.Min(end + 2, Xaml.Length)];

        Assert.Contains("VerticalAlignment=\"Center\"", seg);
        Assert.Contains("FieldHeightCompact", seg);   // 走 UiMetrics 单一真源
    }

    // ================================================================ v0.5.38 三项修复

    [Fact]
    public void DarkThemeToggle_HasIcon()
    {
        // 用户报"深色按钮没有图标"。它是 ToggleButton（不是 Button），
        // 而 EveryButton_HasAnIcon 起初只匹配 <Button> → 漏检。这里钉住它。
        var idx = Xaml.IndexOf("{CompiledBinding DarkTheme}", StringComparison.Ordinal);
        Assert.True(idx >= 0, "应存在深色主题切换按钮");

        var end = Xaml.IndexOf("</ToggleButton>", idx, StringComparison.Ordinal);
        Assert.True(end > idx, "深色按钮应有子元素（图标 + 文字），而不是自闭合的 Content 写法");

        var body = Xaml[idx..end];
        Assert.Contains("Classes=\"icon", body);      // 有图标
        Assert.Contains("&#xE51C;", body);            // 月亮（浅色主题时显示）
        Assert.Contains("&#xE518;", body);            // 太阳（深色主题时显示）
        Assert.Contains("Classes=\"iconText\"", body); // 文字用同款对齐类
    }

    [Fact]
    public void AdvancedParamsSectionHeader_HasIcon()
    {
        // 「高级参数」折叠标题是自绘的 ToggleButton（当 Expander 用），也要有图标。
        // ⚠️ 它在 Sections/AdvancedParamsPanel.axaml（共用 Section），不在 MainView.axaml。
        var xaml = ReadSource("src/ImgHub.App/Views/Sections/AdvancedParamsPanel.axaml");
        var idx = xaml.IndexOf("AdvancedOpen", StringComparison.Ordinal);
        Assert.True(idx >= 0, "应存在高级参数折叠标题（绑 AdvancedOpen）");
        var end = xaml.IndexOf("</ToggleButton>", idx, StringComparison.Ordinal);
        Assert.True(end > idx, "折叠标题应有子元素（图标 + 文字），而不是自闭合的 Content 写法");
        var body = xaml[idx..end];
        Assert.Contains("Classes=\"icon", body);
        // v0.5.40：文案改走 Localizer，锚点用键名
        Assert.Contains("[adv.title]", body);
    }

    [Fact]
    public void IconAndTextNeverSetLineHeight()
    {
        // ⚠️ 这条是本轮最重要的坑（用户报"图标和文字不在一条水平线上"）。
        //   给 `.icon` / `.iconText` 设 LineHeight 会让图标偏上：
        //     LineHeight(17) < 图标自然行盒(20.40px) → baseline 被压到底 → 字形中心上移。
        //   实测差值：设 LineHeight = 2.25px 错位；不设 = 0.63px（可接受）。
        //   因此**这两条样式里绝不能再出现 LineHeight setter**。
        var app = ReadSource("src/ImgHub.App/App.axaml");
        foreach (var sel in new[] { "TextBlock.icon", "TextBlock.iconText" })
        {
            var i = app.IndexOf($"Selector=\"{sel}\"", StringComparison.Ordinal);
            Assert.True(i >= 0, $"App.axaml 应有 {sel} 样式");
            var end = app.IndexOf("</Style>", i, StringComparison.Ordinal);
            var body = app[i..end];
            Assert.DoesNotContain("LineHeight", body);
            Assert.Contains("VerticalAlignment", body);   // 靠 Center 对齐，而不是行高
        }
    }

    [Fact]
    public void RevealInFileManager_HasAReachableEntry()
    {
        // review 债务 #2：`PlatformStorage.RevealInFileManagerAsync` 曾"有实现无入口"。
        // v0.5.38 补了底栏按钮 —— 这里钉住"命令有绑定，且落在底栏（可见容器）内"。
        Assert.Contains("{CompiledBinding RevealInFileManagerCommand}", Xaml);

        var barIdx = Xaml.IndexOf("===== 底栏", StringComparison.Ordinal);
        var cmdIdx = Xaml.IndexOf("{CompiledBinding RevealInFileManagerCommand}", StringComparison.Ordinal);
        Assert.True(barIdx >= 0 && cmdIdx > barIdx,
            "「定位文件」按钮必须在底栏内（底栏可见 → 命令可达）");
    }

    [Fact]
    public void MainWindow_HasResizeHotspots_ForAllEdgesAndCorners()
    {
        // ⚠️ v0.5.39 用户要求 #5："工作台窗口不能在对角拖拽缩放"。
        //   根因：`SystemDecorations="None"` 自绘标题栏 → 系统边框的 resize 热区不存在。
        //   修法：XAML 里叠 8 个透明 Border（四边 + 四角）→ code-behind 接 BeginResizeDrag。
        //   这里钉住"8 个热区都在"，防止后来重构时被顺手删掉（删了就又不能缩放了，且不报错）。
        var win = ReadSource("src/ImgHub.App/Views/MainWindow.axaml");
        foreach (var n in new[]
                 {
                     "ResizeN", "ResizeS", "ResizeW", "ResizeE",
                     "ResizeNW", "ResizeNE", "ResizeSW", "ResizeSE",
                 })
        {
            Assert.Contains($"Name=\"{n}\"", win);
        }

        // 四角必须有对角光标（这是"能对角拖拽"的可见标志）
        foreach (var cur in new[]
                 {
                     "TopLeftCorner", "TopRightCorner",
                     "BottomLeftCorner", "BottomRightCorner",
                 })
        {
            Assert.Contains($"Cursor=\"{cur}\"", win);
        }

        // 热区层不能设 Background（否则会命中整块区域、吃掉所有内容点击）
        var layerIdx = win.IndexOf("Name=\"ResizeLayer\"", StringComparison.Ordinal);
        Assert.True(layerIdx >= 0);
        var layerEnd = win.IndexOf('>', layerIdx);
        Assert.DoesNotContain("Background", win[layerIdx..layerEnd]);

        // code-behind 必须真的把 8 个热区接到 BeginResizeDrag
        var cs = ReadSource("src/ImgHub.App/Views/MainWindow.axaml.cs");
        Assert.Contains("BeginResizeDrag", cs);
        Assert.Equal(8, CountOccurrences(cs, "WindowEdge."));
    }

    [Fact]
    public void DraggingStroke_IsInsidePushOpacity_NotBypassingAlpha()
    {
        // ⚠️ v0.5.39 用户反馈 #1："拖动绘制蒙版时变成不透明了，松手又变成半透明"。
        //   根因：v0.5.38 给离屏层加缓存时，把 `_current`（拖动中那条）挪到了
        //        `PushOpacity(alpha)` 的 **using 块外面** → 拖动时不带 alpha。
        //   为什么用结构断言而不是渲染测试（我试过、已放弃）：
        //     离屏 RenderTargetBitmap 里的 PushOpacity **不产生可测量的差异**
        //     （实测：注入旧 bug 后"拖动 vs 已提交"的像素差仍很小 → 渲染测试会假通过）。
        //     所以改为直接断言源码结构：`_current?.DrawPreview` 必须出现在 PushOpacity 的块内。
        var cs = ReadSource("src/ImgHub.App/Controls/RegionCanvas.cs");

        var pushIdx = cs.IndexOf("using (ctx.PushOpacity(alpha))", StringComparison.Ordinal);
        Assert.True(pushIdx >= 0, "应存在 PushOpacity(alpha) 的合成块");

        // 该 using 块的范围：从 pushIdx 到它的闭合大括号。
        // 判据（不依赖注释/缩进）：在 pushIdx 与 _current 的绘制点之间，
        //   `{` 与 `}` 必须保持平衡（净增 ≥1，说明还在块内）。
        var drawIdx = cs.IndexOf("_current?.DrawPreview(ctx, cw, ch, minSide);", pushIdx, StringComparison.Ordinal);
        Assert.True(drawIdx > pushIdx, "_current 的绘制必须出现在 PushOpacity 之后");

        var between = cs[pushIdx..drawIdx];
        var opens = between.Count(ch => ch == '{');
        var closes = between.Count(ch => ch == '}');
        Assert.True(opens - closes >= 1,
            $"`_current` 必须在 PushOpacity 的 using 块**内部**。" +
            $"实测块内大括号净增 {opens - closes}（≥1 = 还在块内；≤0 = 已被挪到块外 → 拖动时不带 alpha）");

        // 顺序也应是：先贴离屏层，再画拖动中那条
        Assert.Contains("DrawImage(flat", between);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            n++; i += needle.Length;
        }
        return n;
    }
}
