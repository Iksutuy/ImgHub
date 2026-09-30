using Avalonia.Controls;

namespace ImgHub.App.Views.Sections;

/// <summary>
/// 消息面板（Plan A / v0.5.28）。
///
/// 纯展示 Section：只通过 {Binding} 读 ViewModel，无事件、无 code-behind 逻辑。
/// 这样就不需要「事件转发」样板，也不会与主 View 的命名作用域冲突。
/// </summary>
public partial class MessagePanel : UserControl
{
    public MessagePanel() => InitializeComponent();
}
