using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace ImgHub.App.Controls;

/// <summary>
/// 悬停说明（ToolTip）统一入口 —— 附加属性方案。
///
/// 为什么用附加属性而不是在每个控件上写 `ToolTip.Tip`（v5.24.0 用户要求）：
///   1. **一处控制全局开关**：设置里的「悬停说明」开关只需改
///      <see cref="TipEnabled"/>，所有用 <c>Help.Tip</c> 的控件立即跟随，
///      不必给每个按钮加 `IsVisible`/绑定。
///   2. **文案集中**：按钮的功能说明写在 XAML 一处，读代码时立刻看到"点了会怎样"。
///
/// 用法：
/// <code>
/// &lt;Button Content="生成" Help.Tip="用当前参数出图（会真实计费）" /&gt;
/// </code>
/// </summary>
public static class Help
{
    private static bool _tipEnabled = true;

    /// <summary>
    /// 全局开关：false 时所有 <see cref="TipProperty"/> 都**不显示**。
    /// 由设置界面切换（MainViewModel.EnableToolTips）。
    /// 切换后调用 <see cref="RefreshTree"/> 可让已挂载的控件立即跟随。
    /// </summary>
    public static bool TipEnabled
    {
        get => _tipEnabled;
        set => _tipEnabled = value;
    }

    /// <summary>悬停说明文本。空/null 表示不显示。</summary>
    public static readonly AttachedProperty<string?> TipProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Tip", typeof(Help));

    public static string? GetTip(Control c) => c.GetValue(TipProperty);
    public static void SetTip(Control c, string? v) => c.SetValue(TipProperty, v);

    static Help()
    {
        // 值变化时同步到 Avalonia 原生 ToolTip.Tip（并考虑全局开关）
        TipProperty.Changed.AddClassHandler<Control>((control, e) =>
            Apply(control, e.GetNewValue<string?>()));
    }

    /// <summary>把附加属性的值同步到控件的 ToolTip。</summary>
    private static void Apply(Control control, string? text)
    {
        var effective = _tipEnabled ? text : null;
        if (string.IsNullOrEmpty(effective))
        {
            Avalonia.Controls.ToolTip.SetTip(control, null);
            return;
        }
        Avalonia.Controls.ToolTip.SetTip(control, effective);
        // 悬停时才弹出：显式设置 ShowDelay，避免"闪一下就出来"
        Avalonia.Controls.ToolTip.SetShowDelay(control, 420);
        Avalonia.Controls.ToolTip.SetBetweenShowDelay(control, 120);
    }

    /// <summary>全局开关切换后，强制刷新一棵树里所有 Help.Tip 控件。</summary>
    public static void RefreshTree(Visual? root)
    {
        if (root is null) return;
        if (root is Control c && c.GetValue(TipProperty) is { } text)
            Apply(c, text);
        foreach (var child in root.GetVisualDescendants())
            if (child is Control cc && cc.GetValue(TipProperty) is { } t)
                Apply(cc, t);
    }
}
