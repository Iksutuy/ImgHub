using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using System.Text.RegularExpressions;

namespace ImgHub.App.Controls;

/// <summary>
/// 极简 Markdown 渲染器：把指南文本转成 Avalonia 控件（**不是**完整 Markdown 实现）。
///
/// 支持（够用即可，刻意不引入第三方库）：
///   · <c>**粗体**</c>、<c>`行内代码`</c>、<c>*斜体*</c>
///   · 行首 <c>·</c> 或 <c>-</c> / <c>*</c> 无序项 → 带项目符号的段落
///   · 行首 <c>1.</c> 有序项 → 带数字的段落
///   · 空行分段；单换行按软换行处理（保持同一段）
///
/// ⚠️ 为什么自己写而不引库（v0.5.31）：
///   ① Avalonia 生态里的 Markdown 控件多为第三方包，而本项目走 **Native AOT** ——
///      新依赖要重新验证 AOT 兼容性（反射绑定/AOT 裁剪风险，见 CONSTRAINTS H1）；
///   ② 指南里只用到最朴素的几种标记，自己实现的收益（可控、零依赖、AOT 安全）大于成本。
///   ③ 用 <see cref="TextBlock.Inlines"/> 拼 <see cref="Run"/>/<see cref="Bold"/>，
///      不用正则替换成 HTML —— Avalonia 没有 HTML 渲染，那条路走不通。
///
/// ⚠️⚠️ 颜色**一律用 <c>DynamicResource</c> 绑定，绝不一次性查资源**（v0.5.31 血泪）：
///   本项目的画刷定义在 <c>App.axaml</c> 的 <c>ResourceDictionary.ThemeDictionaries</c>
///   （Light / Dark 两套）。而本渲染器在 <c>DataContextChanged</c> 阶段被调用 ——
///   那时控件**还没挂到窗口的可视树上**，<c>TryFindResource</c> 找不到主题字典里的键，
///   返回 null。于是 <c>TextBlock.Foreground = null</c> →
///   **Avalonia 不绘制该文本**，表现为「渲染后几乎不显示内容」
///   （只有行内代码可见，因为它当时用的是硬编码颜色）。
///   <c>DynamicResource</c> 是延迟求值 + 跟随主题切换，天然避开这个时序问题。
/// </summary>
public static class MarkdownText
{
    /// <summary>行内代码的字体（等宽，便于与正文区分）。</summary>
    private static readonly FontFamily CodeFont =
        new("Consolas, Menlo, DejaVu Sans Mono, monospace");

    /// <summary>把 Markdown 文本渲染成一个可放进面板的控件。</summary>
    /// <param name="markdown">Markdown 源文本。</param>
    /// <param name="foregroundKey">
    /// 正文前景色**资源键**（如 <c>AppTextBrush</c>）。传 null 则沿用全局 TextBlock 样式。
    /// ⚠️ 是"键"不是"画刷实例" —— 见类注释，实例在此时查不到。
    /// </param>
    public static Control Render(string? markdown, double fontSize = 13,
                                 string? foregroundKey = null, double lineHeight = 21)
    {
        var panel = new StackPanel { Spacing = 6 };
        if (string.IsNullOrWhiteSpace(markdown)) return panel;

        // 按空行切段落（Markdown 的段落规则）；段内单换行视为软换行
        var paragraphs = Regex.Split(markdown.Replace("\r\n", "\n").Trim(), @"\n\s*\n");
        foreach (var para in paragraphs)
        {
            if (string.IsNullOrWhiteSpace(para)) continue;
            var lines = para.Split('\n').Select(l => l.Trim())
                            .Where(l => l.Length > 0).ToList();
            if (lines.Count == 0) continue;

            // 列表项：每行单独成段（带前缀符号）
            if (lines.All(IsListItem))
            {
                foreach (var line in lines)
                    panel.Children.Add(ListItemBlock(line, fontSize, foregroundKey, lineHeight));
                continue;
            }

            // 普通段落：多行用软换行拼在一起（Avalonia 的 Run 之间可插 LineBreak）
            var tb = NewTextBlock(fontSize, foregroundKey, lineHeight);
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0) tb.Inlines!.Add(new LineBreak());
                AppendInline(tb, lines[i], fontSize);
            }
            panel.Children.Add(tb);
        }
        return panel;
    }

    private static bool IsListItem(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("·", StringComparison.Ordinal)
            || t.StartsWith("- ", StringComparison.Ordinal)
            || t.StartsWith("* ", StringComparison.Ordinal)
            || Regex.IsMatch(t, @"^\d+[.)]\s");
    }

    private static Control ListItemBlock(string line, double fontSize,
                                         string? foregroundKey, double lineHeight)
    {
        var t = line.TrimStart();
        // 项目符号：把 Markdown 的 "- "/"* " 统一成实心点；有序项保留其编号
        string bullet;
        var m = Regex.Match(t, @"^(\d+)[.)]\s+(.*)$");
        if (m.Success) { bullet = m.Groups[1].Value + "."; t = m.Groups[2].Value; }
        else
        {
            bullet = "·";
            t = Regex.Replace(t, @"^[·\-\*]\s*", "");
        }

        // 用两列 Grid 做悬挂缩进：符号列固定宽，内容列自动换行且左对齐
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*") };

        var bulletTb = new TextBlock
        {
            Text = bullet, FontSize = fontSize,
            VerticalAlignment = VerticalAlignment.Top,
        };
        BindForeground(bulletTb, foregroundKey);

        var body = NewTextBlock(fontSize, foregroundKey, lineHeight);
        AppendInline(body, t, fontSize);

        Grid.SetColumn(bulletTb, 0);
        Grid.SetColumn(body, 1);
        grid.Children.Add(bulletTb);
        grid.Children.Add(body);
        return grid;
    }

    private static TextBlock NewTextBlock(double fontSize, string? foregroundKey, double lineHeight)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            LineHeight = lineHeight,
        };
        BindForeground(tb, foregroundKey);
        tb.Inlines = new InlineCollection();
        return tb;
    }

    /// <summary>
    /// 把前景色绑到主题资源键。<paramref name="key"/> 为 null 时**不设置** ——
    /// 让 App.axaml 里全局的 <c>Style Selector="TextBlock"</c> 生效（默认前景色）。
    /// </summary>
    private static void BindForeground(Control c, string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        c.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(key));
    }

    /// <summary>解析一行里的 <c>**粗体**</c> / <c>`代码`</c> / <c>*斜体*</c> 并追加到 TextBlock。</summary>
    private static void AppendInline(TextBlock tb, string text, double fontSize)
    {
        // 依次匹配：**粗体** | `代码` | *斜体*
        var pattern = @"(\*\*(?<b>.+?)\*\*)|(`(?<c>[^`]+)`)|(\*(?<i>[^*]+)\*)";
        int pos = 0;
        foreach (Match m in Regex.Matches(text, pattern))
        {
            if (m.Index > pos) tb.Inlines!.Add(new Run(text[pos..m.Index]));
            if (m.Groups["b"].Success)
            {
                tb.Inlines!.Add(new Bold { Inlines = { new Run(m.Groups["b"].Value) } });
            }
            else if (m.Groups["c"].Success)
            {
                // 行内代码：等宽字体 + 主题色（同样用 DynamicResource，别硬编码颜色）
                var code = new Run(m.Groups["c"].Value) { FontFamily = CodeFont };
                code.Bind(TextElement.ForegroundProperty,
                          new DynamicResourceExtension("AppCodeBrush"));
                tb.Inlines!.Add(code);
            }
            else if (m.Groups["i"].Success)
            {
                tb.Inlines!.Add(new Italic { Inlines = { new Run(m.Groups["i"].Value) } });
            }
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) tb.Inlines!.Add(new Run(text[pos..]));
    }
}
