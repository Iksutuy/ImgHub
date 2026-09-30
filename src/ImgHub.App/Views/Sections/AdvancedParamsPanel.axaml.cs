using Avalonia.Controls;

namespace ImgHub.App.Views.Sections;

/// <summary>
/// 高级参数面板（Plan A / v0.5.28；v0.5.29 改布局修问题 3）。
///
/// 纯展示 Section：只有 {Binding}，无事件、无 code-behind 逻辑 ——
/// 因此不需要事件转发，也不会与主 View 的命名作用域冲突。
///
/// ⚠️ v0.5.29 不再用 `Expander` 承载内容：其模板内边距会把字段压窄（详见 axaml 注释）。
///    改用 ToggleButton + IsVisible 自绘折叠，布局完全可控。
/// </summary>
public partial class AdvancedParamsPanel : UserControl
{
    public AdvancedParamsPanel() => InitializeComponent();
}
