using System.Runtime.CompilerServices;
using Avalonia.Skia;

namespace ImgHub.Integration.Tests;

/// <summary>
/// 测试进程级的 Avalonia 渲染后端初始化（P0-2 需要）。
///
/// 为什么必须做：Avalonia 的 <c>Bitmap</c> 解码/释放都走
/// <c>Avalonia.Platform.IPlatformRenderInterface</c>，而 xunit 进程没有窗口，
/// 默认找不到该服务 → <c>Bitmap.DecodeToWidth</c> 抛
/// <c>InvalidOperationException: Unable to locate 'Avalonia.Platform.IPlatformRenderInterface'</c>。
///
/// <c>SkiaPlatform.Initialize()</c> 只注册渲染后端，**不开窗口、不需要消息泵**，
/// 因此适合单测环境（实测可用；比引入 Avalonia.Headless 更轻）。
/// </summary>
internal static class SkiaTestSetup
{
    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            SkiaPlatform.Initialize();
        }
        catch
        {
            // 已经初始化过（例如同一进程内多次调用）→ 忽略。
            // 真正不可用的情形会在测试里以明确异常暴露，而不是在这里静默。
        }
    }
}
