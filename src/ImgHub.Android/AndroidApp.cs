using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace ImgHub.Android;

/// <summary>
/// Android Application —— 指定 Avalonia App 类型。
/// Avalonia 12 的 AvaloniaAndroidApplication 是**泛型**类（&lt;TApp&gt;），
/// 由它把共享的 App 接到 Android 生命周期上。
/// </summary>
[Application]
public class AndroidApp : AvaloniaAndroidApplication<ImgHub.App.App>
{
    public AndroidApp(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership) { }
}
