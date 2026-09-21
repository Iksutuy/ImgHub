using Imgagent.App.Services;

namespace Imgagent.App.Services;

/// <summary>平台能力抽象：各 head（Desktop / Android）注入不同实现。</summary>
public interface IPlatformStorage
{
    Task<string?> SaveToGalleryAsync(byte[] data, string fileName, string mediaType);
    Task OpenInExternalViewerAsync(string path);
    Task RevealInFileManagerAsync(string path);
    Task<string?> GetGalleryDirAsync();
}

/// <summary>平台信息（用于环境面板 / 诊断）。</summary>
public interface IPlatformInfo
{
    string PlatformName { get; }
    string Version { get; }
}

/// <summary>应用服务容器（组合根产物，注入 ViewModel）。</summary>
public sealed class AppServices
{
    public required Core.Storage.Session Session { get; init; }
    public required Core.Http.HttpJsonClient Http { get; init; }
    public required Core.Services.ImageApi ImageApi { get; set; }
    public required Core.Services.IPolishService Polish { get; init; }
    public required IPlatformStorage Storage { get; init; }
    public required IPlatformInfo Platform { get; init; }
}

/// <summary>数据目录解析：各平台给标准位置。</summary>
public static class AppPaths
{
    public static string ResolveHome()
    {
        var env = Environment.GetEnvironmentVariable("IMGAGENT_HOME");
        if (!string.IsNullOrWhiteSpace(env))
        {
            Directory.CreateDirectory(env);
            return env;
        }
        string dir;
        if (OperatingSystem.IsWindows())
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "imgagent");
        else
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                               ".imgagent");
        Directory.CreateDirectory(dir);
        return dir;
    }
}