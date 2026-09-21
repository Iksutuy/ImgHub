using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Imgagent.App.Views;

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
