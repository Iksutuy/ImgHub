using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace Imgagent.Android;

/// <summary>
/// Android 主 Activity —— 承载共享的 MainView。
///
/// ⚠️ Avalonia 12 的写法（与 v11 不同）：
///   · App 类型由 `AndroidApp : AvaloniaAndroidApplication&lt;TApp&gt;` 指定；
///   · Activity 只继承非泛型的 `AvaloniaMainActivity`，无需覆写建造器。
///
/// 图标走 Resources/mipmap-*（多密度 + 自适应），见 tools/make-android-icons.ps1。
/// 这里**不设 Icon**，由 AndroidManifest 统一指定（避免 @drawable/icon 引用失效）。
/// </summary>
[Activity(
    Label = "imgagent 工作台",
    Theme = "@style/MyTheme.NoActionBar",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize
                           | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
}
