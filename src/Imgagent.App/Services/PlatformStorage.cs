using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Imgagent.App.Services;

namespace Imgagent.App.Services;

/// <summary>用 Avalonia StorageProvider 实现的平台存储（Windows/Android 通用）。</summary>
public sealed class PlatformStorage : IPlatformStorage
{
    private static TopLevel? TopLevel =>
        (Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow
        ?? (Application.Current?.ApplicationLifetime
            as ISingleViewApplicationLifetime)?.MainView as TopLevel;

    public async Task<string?> GetGalleryDirAsync()
    {
        var top = TopLevel;
        if (top is null) return null;
        try
        {
            var pics = await top.StorageProvider
                .TryGetWellKnownFolderAsync(WellKnownFolder.Pictures);
            return pics?.Path.LocalPath;
        }
        catch { return null; }
    }

    public async Task<string?> SaveToGalleryAsync(byte[] data, string fileName, string mediaType)
    {
        var top = TopLevel;
        if (top is null) return null;
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
            if (file is null) return null;
            await using var stream = await file.OpenWriteAsync().ConfigureAwait(false);
            await stream.WriteAsync(data).ConfigureAwait(false);
            return file.Path.LocalPath;
        }
        catch { return null; }
    }

    public Task OpenInExternalViewerAsync(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path, UseShellExecute = true,
                });
            }
            catch { }
        }
        return Task.CompletedTask;
    }

    public Task RevealInFileManagerAsync(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            catch { }
        }
        return Task.CompletedTask;
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