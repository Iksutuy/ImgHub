using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ImgHub.App.Views;

/// <summary>
/// 主窗口：自绘标题栏（无原生边框），仿 Reasonix 风格。
///
/// 这些是**桌面专属**操作：
///   · Android 走 ISingleViewApplicationLifetime，不使用 MainWindow；
///   · 仍做防御性处理，避免将来多平台场景出错。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SetupTitleBarDrag();
        SetupResizeEdges();
    }

    /// <summary>
    /// 边缘/四角拖拽缩放（v0.5.39 用户要求 #5）。
    ///
    /// 为什么必须手工做：窗口用 `SystemDecorations="None"` 自绘标题栏 →
    /// **系统边框的 resize 热区不存在**，对角拖拽完全没反应。
    /// Avalonia 为此提供 <see cref="Window.BeginResizeDrag"/>，把 XAML 里那 8 个
    /// 透明 Border 接到它即可（真正的缩放交给系统，所以拖动跟手、无抖动）。
    ///
    /// 注：最大化的窗口不允许 resize（系统语义如此），这里与标题栏拖拽保持一致的处理。
    /// </summary>
    private void SetupResizeEdges()
    {
        void Wire(Control c, WindowEdge edge)
        {
            c.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                if (WindowState == WindowState.Maximized) return;   // 最大化时不可 resize
                BeginResizeDrag(edge, e);
                e.Handled = true;
            };
        }

        Wire(ResizeN,  WindowEdge.North);
        Wire(ResizeS,  WindowEdge.South);
        Wire(ResizeW,  WindowEdge.West);
        Wire(ResizeE,  WindowEdge.East);
        Wire(ResizeNW, WindowEdge.NorthWest);
        Wire(ResizeNE, WindowEdge.NorthEast);
        Wire(ResizeSW, WindowEdge.SouthWest);
        Wire(ResizeSE, WindowEdge.SouthEast);
    }

    /// <summary>标题栏拖拽移动窗口（原生边框已关闭，需手动实现）。</summary>
    private void SetupTitleBarDrag()
    {
        void Press(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            // 双击 = 最大化 / 还原
            if (e.ClickCount == 2) { ToggleMaximize(); e.Handled = true; return; }
            if (WindowState == WindowState.Maximized)
            {
                // 最大化状态拖动：先还原（简化处理，不追踪鼠标相对位置）
                WindowState = WindowState.Normal;
            }
            BeginMoveDrag(e);
            e.Handled = true;
        }

        TitleBarDragArea.PointerPressed += Press;
        TitleBar.PointerPressed += Press;
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
