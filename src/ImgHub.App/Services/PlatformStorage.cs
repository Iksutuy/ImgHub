using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using ImgHub.App.Services;

namespace ImgHub.App.Services;

/// <summary>
/// TopLevel 来源。抽成接口的原因：<c>Application.Current.ApplicationLifetime</c> 是全局静态状态，
/// 在单测里没有真实窗口 → 无法构造 <see cref="PlatformStorage"/> 来断言失败路径。
/// 注入后即可测试「拿不到 TopLevel 时必须报 Failed，而不是伪装成 Cancelled」。
/// </summary>
public interface ITopLevelSource
{
    TopLevel? Get();
}

/// <summary>
/// 从 Avalonia 生命周期解析 TopLevel（桌面 + Android 单视图两条路）。
///
/// ⚠️ **v0.5.32 修复 P0-8**：旧写法是
/// <c>(ISingleViewApplicationLifetime)?.MainView as TopLevel</c> ——
/// 但 Android 的 <c>MainView</c> 是 <c>UserControl</c>（**不是** TopLevel），
/// `as TopLevel` 恒为 null → Android 上「保存相册」直接 return，静默失效。
/// 正确做法：对非 TopLevel 的控件用 <see cref="TopLevel.GetTopLevel(Visual)"/> 反查。
/// </summary>
public sealed class LifetimeTopLevelSource : ITopLevelSource
{
    public TopLevel? Get()
    {
        var lifetime = Application.Current?.ApplicationLifetime;

        if (lifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;

        if (lifetime is ISingleViewApplicationLifetime singleView)
        {
            var main = singleView.MainView;
            if (main is TopLevel top) return top;          // 已经是 TopLevel
            if (main is Visual visual) return TopLevel.GetTopLevel(visual);
        }

        return null;
    }
}

/// <summary>
/// 用 Avalonia StorageProvider / Launcher 实现的平台存储（Windows + Android 通用）。
///
/// ⚠️ **v0.5.32 修复 P0-5 / P0-8**：
///   · 失败不再返回 <c>null</c> 让调用方误判成「用户取消」，而是返回带状态的
///     <see cref="PlatformOpResult"/>；
///   · 「打开外部程序」改用跨平台的 <see cref="ILauncher"/>，不再只有 Windows 分支；
///   · 「在文件管理器中显示」Windows 走 explorer，其它平台显式返回
///     <see cref="PlatformOpStatus.Unsupported"/>（而不是静默什么都不做）。
/// </summary>
public sealed class PlatformStorage : IPlatformStorage
{
    private readonly ITopLevelSource _topLevel;

    public PlatformStorage() : this(new LifetimeTopLevelSource()) { }

    public PlatformStorage(ITopLevelSource topLevel) => _topLevel = topLevel;

    public async Task<string?> GetGalleryDirAsync()
    {
        var top = _topLevel.Get();
        if (top is null) return null;
        try
        {
            var pics = await top.StorageProvider
                .TryGetWellKnownFolderAsync(WellKnownFolder.Pictures).ConfigureAwait(false);
            return pics?.Path.LocalPath;
        }
        catch (Exception ex)
        {
            Core.Diagnostics.AppLog.Warn("查询相册目录失败", "PlatformStorage.GetGalleryDirAsync", ex: ex);
            return null;
        }
    }

    public async Task<PlatformOpResult> SaveToGalleryAsync(
        byte[] data, string fileName, string mediaType)
    {
        var top = _topLevel.Get();
        if (top is null)
            return PlatformOpResult.Failed("拿不到窗口句柄（StorageProvider 不可用）");

        try
        {
            var pics = await top.StorageProvider
                .TryGetWellKnownFolderAsync(WellKnownFolder.Pictures).ConfigureAwait(false);

            var file = await top.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "保存生成的图片",
                    SuggestedFileName = fileName,
                    SuggestedStartLocation = pics,
                    DefaultExtension = Path.GetExtension(fileName).TrimStart('.'),
                }).ConfigureAwait(false);

            // 用户关掉选择框 = 明确取消（**不是失败**）
            if (file is null) return PlatformOpResult.Cancelled();

            await using (var stream = await file.OpenWriteAsync().ConfigureAwait(false))
                await stream.WriteAsync(data).ConfigureAwait(false);

            return PlatformOpResult.Ok(file.Path.LocalPath);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.AppLog.Error("保存到相册失败", "PlatformStorage.SaveToGalleryAsync", ex: ex);
            return PlatformOpResult.Failed(ex.Message);
        }
    }

    public async Task<PlatformOpResult> OpenInExternalViewerAsync(string path)
    {
        var top = _topLevel.Get();

        // 首选：Avalonia 的跨平台 Launcher（Windows / Android 都支持）
        if (top?.Launcher is { } launcher && top.StorageProvider is { } sp)
        {
            try
            {
                var file = await sp.TryGetFileFromPathAsync(path).ConfigureAwait(false);
                if (file is not null && await launcher.LaunchFileAsync(file).ConfigureAwait(false))
                    return PlatformOpResult.Ok(path);
            }
            catch (Exception ex)
            {
                Core.Diagnostics.AppLog.Warn(
                    "Launcher 打开文件失败，回退到 Shell", "PlatformStorage.OpenInExternalViewerAsync", ex: ex);
            }
        }

        // 回退：Windows Shell（Launcher 不可用或返回 false 时）
        if (OperatingSystem.IsWindows())
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path, UseShellExecute = true,
                });
                return PlatformOpResult.Ok(path);
            }
            catch (Exception ex)
            {
                Core.Diagnostics.AppLog.Error(
                    "系统看图器打开失败", "PlatformStorage.OpenInExternalViewerAsync", ex: ex);
                return PlatformOpResult.Failed(ex.Message);
            }
        }

        return PlatformOpResult.Unsupported("当前平台不支持直接用外部程序打开文件");
    }

    public Task<PlatformOpResult> RevealInFileManagerAsync(string path)
    {
        // 「在文件管理器中定位文件」是 Windows 特有的概念；
        // 其它平台**显式**返回 Unsupported，而不是静默什么都不做（旧实现的坑）。
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(
                PlatformOpResult.Unsupported("当前平台不支持在文件管理器中定位文件"));

        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            return Task.FromResult(PlatformOpResult.Ok(path));
        }
        catch (Exception ex)
        {
            Core.Diagnostics.AppLog.Error(
                "在文件管理器中显示失败", "PlatformStorage.RevealInFileManagerAsync", ex: ex);
            return Task.FromResult(PlatformOpResult.Failed(ex.Message));
        }
    }
}

/// <summary>默认平台信息。</summary>
public sealed class PlatformInfo : IPlatformInfo
{
    public string PlatformName =>
        OperatingSystem.IsWindows() ? "Windows" :
        OperatingSystem.IsAndroid() ? "Android" :
        OperatingSystem.IsLinux() ? "Linux" :
        OperatingSystem.IsMacOS() ? "macOS" : "Unknown";

    public string Version => Environment.OSVersion.VersionString;
}
