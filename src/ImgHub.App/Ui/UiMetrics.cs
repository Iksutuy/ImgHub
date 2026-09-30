namespace ImgHub.App.Ui;

/// <summary>
/// UI 度量常量（**唯一真源**，Plan C / v0.5.28）。
///
/// ⚠️ 为什么需要它：同一语义的高度此前散落在多个 `.axaml` 里用字面量写死
///    （宽屏 34 / 窄屏 38），改一处容易漏改另一处 ——
///    v0.5.28 用户反馈的「下拉框太高把生成按钮挤出视口」正是这类不同步的表现：
///    改了 App.axaml 的全局 40→34，但窄屏布局里另有硬编码的 38，两套不一致。
///    集中到常量后：① 改一处全局生效；② 「宽/窄尺寸本就不同」这个意图由名字自解释。
///
/// ⚠️ 只放「同语义、多文件重复」的高度值。**不要**把逐控件调出来的
///    Padding / Margin 也搬进来 —— 那些值的不对称是**刻意**的
///    （例如 ComboBox 的 `11,0,6,2` 是为补偿 CJK 字形下缘偏低），
///    强行"统一间距"会抹掉差异。依据见 docs/plan-c-ui-metrics.md §1.2。
///
/// 用法（XAML）：
/// <code>
/// &lt;UserControl xmlns:ui="using:ImgHub.App.Ui" ...&gt;
///   &lt;ComboBox MinHeight="{x:Static ui:UiMetrics.FieldHeightWide}" /&gt;
/// </code>
/// 注：`x:Static` 要求 `const`（不能是 `static readonly`）；已实测编译与运行时均生效。
/// </summary>
public static class UiMetrics
{
    // ================================================================ 字段高度
    /// <summary>
    /// 宽屏字段高度（ComboBox / TextBox / NumericUpDown / 主操作按钮）。
    /// 34 是权衡值：再小则 CJK 字底被裁，再大则左栏放不下「生成」按钮行。
    /// </summary>
    public const double FieldHeightWide = 34;

    /// <summary>
    /// 窄屏（手机竖屏）字段高度。触屏需要更大的点击区域，故比宽屏高 4px。
    /// ⚠️ 不要与 <see cref="FieldHeightWide"/> 合并 —— 两者**刻意不同**。
    /// </summary>
    public const double FieldHeightNarrow = 38;

    /// <summary>顶栏小控件高度（Provider 下拉、标注工具下拉等）。</summary>
    public const double FieldHeightCompact = 32;

    // ================================================================ 固定区块高度
    /// <summary>
    /// 标注工具条高度（宽屏，三行按钮 + 换行余量）。
    /// 刻意固定：否则点开标注模式时预览图会上下跳动。
    ///
    /// ⚠️ v0.5.36：92 → 118。原因：行 1 加了「撤销/重做」、行 2 加了「图片居中」，
    ///   每行都可能换行到第 3 行；而高度固定时**溢出部分会被裁掉**（历史上踩过
    ///   「蒙版说明被遮挡」）。这里按"最多 3 行 × 34px + 行距"给足余量。
    ///
    /// ⚠️ v0.5.39：118 → 160（用户要求改成**明确三行**）。
    ///   原因：切到马克笔时出现「粗细」控件 → 两行布局的 WrapPanel 会横向溢出并换行，
    ///   与下一行挤成一团（用户报"图标显示越界，而且挤成一团"）。
    ///   现在固定三行：`3 × 34px + 2 × 4px 行距 + 上下 padding` ≈ 160。
    ///
    /// ⚠️ **不要与 <see cref="PromptBoxHeightWide"/> 混用**：两者语义不同（
    ///   一个是"工具条要装下几行按钮"，一个是"提示词输入框要多高"）。
    ///   此前共用一个常量 → 改工具条高度会连带把左栏的提示词框拉长。
    /// </summary>
    public const double AnnotationToolbarHeightWide = 160;

    /// <summary>提示词输入框高度（宽屏，左栏）。与工具条**无关**（见上方说明）。</summary>
    public const double PromptBoxHeightWide = 96;

    /// <summary>标注工具条高度（窄屏，三行按钮 + 换行余量）。</summary>
    public const double AnnotationToolbarHeightNarrow = 132;

    /// <summary>窄屏历史列表固定高度。</summary>
    public const double HistoryListHeightNarrow = 240;

    /// <summary>窄屏消息区固定高度。</summary>
    public const double MessageAreaHeightNarrow = 180;
}
