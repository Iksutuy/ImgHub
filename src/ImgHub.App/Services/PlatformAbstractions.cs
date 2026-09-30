using ImgHub.App.Services;
using ImgHub.Core.Diagnostics;
using ImgHub.Core.Models;

namespace ImgHub.App.Services;

/// <summary>
/// 平台操作结果状态。
/// ⚠️ **必须能区分「用户取消」与「操作失败」** —— v0.5.32 修复 P0-5：
/// 旧实现在失败时也返回 <c>null</c>，调用方一律解读为「用户取消」，
/// 于是 Android / 权限错误 / 磁盘满 全都静默伪装成「用户没保存」。
/// </summary>
public enum PlatformOpStatus
{
    /// <summary>成功。</summary>
    Ok,

    /// <summary>用户主动取消（**不是错误**，UI 不该报错，也不该提示成功）。</summary>
    Cancelled,

    /// <summary>当前平台不支持该操作（例如 Android 没有「文件管理器中显示」）。</summary>
    Unsupported,

    /// <summary>真失败（异常 / 无权限 / IO 错误），应报错并允许重试。</summary>
    Failed,
}

/// <summary>平台操作结果：状态 + 可选路径 + 可选原因。</summary>
public readonly record struct PlatformOpResult(
    PlatformOpStatus Status, string? Path = null, string? Error = null)
{
    public static PlatformOpResult Ok(string? path = null) => new(PlatformOpStatus.Ok, path);
    public static PlatformOpResult Cancelled() => new(PlatformOpStatus.Cancelled);
    public static PlatformOpResult Unsupported(string what) => new(PlatformOpStatus.Unsupported, Error: what);
    public static PlatformOpResult Failed(string error) => new(PlatformOpStatus.Failed, Error: error);

    public bool IsOk => Status == PlatformOpStatus.Ok;
}

/// <summary>平台能力抽象：各 head（Desktop / Android）注入不同实现。</summary>
public interface IPlatformStorage
{
    Task<PlatformOpResult> SaveToGalleryAsync(byte[] data, string fileName, string mediaType);
    Task<PlatformOpResult> OpenInExternalViewerAsync(string path);
    Task<PlatformOpResult> RevealInFileManagerAsync(string path);

    /// <summary>查询相册目录（仅查询，找不到返回 null 是合理的）。</summary>
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

    /// <summary>
    /// 生图 API（**接口类型**，方便注入假实现做离线测试）。
    /// ⚠️ 若声明成具体类 <c>ImageApi</c>，测试就无法断言"实际发出的请求内容"，
    ///    而"参考图/蒙版有没有真的发出去"恰恰只能在这一层验证。
    /// </summary>
    public required Core.Services.IImageApi ImageApi { get; set; }

    /// <summary>
    /// 生图 API 的**工厂**（切换 provider 时重建实例用）。
    /// 默认 = 真实实现；测试可注入返回记录型假实现的工厂，
    /// 从而验证"切换 provider 后请求内容仍正确"。
    ///
    /// ⚠️ 第三个参数是 baseUrl：新增的 OpenAI / 千问 provider 的端点无法预置
    ///   （千问是 <c>{WorkspaceId}.cn-beijing.maas.aliyuncs.com</c> 这种业务空间专属域名），
    ///   必须由调用方按 provider 传入（见 <c>Session.BaseUrlFor</c>）。
    /// </summary>
    public Func<ApiProvider, Core.Http.HttpJsonClient, string?, Core.Services.IImageApi>
        ImageApiFactory { get; init; } =
            (p, http, baseUrl) => new Core.Services.ImageApi(p, http, baseUrl);

    public required Core.Services.IPolishService Polish { get; init; }
    public required IPlatformStorage Storage { get; init; }
    public required IPlatformInfo Platform { get; init; }

    /// <summary>模型花费统计（价格预估用，持久化 model_stats.json）。</summary>
    public required Core.Services.ModelStatsService ModelStats { get; init; }

    /// <summary>
    /// 未完成生成任务（v5.26.0）：提交即落盘，程序意外退出后可凭 task_id 找回图片。
    /// </summary>
    public required Core.Storage.PendingTaskStore Pending { get; init; }
}

/// <summary>数据目录解析：各平台给标准位置。</summary>
public static class AppPaths
{
    /// <summary>当前数据目录名（Windows: %LOCALAPPDATA% 下；其它: ~/ 下）。</summary>
    public const string DirNameWindows = "imghub";
    public const string DirNameUnix = ".imghub";

    /// <summary>
    /// 改名前的目录名（v5.28.0 由 imgagent 改为 ImgHub）。
    /// 仅用于**兼容读取既有用户数据**（历史图片 / config.json / state.json / key）——
    /// 改名不应让用户的图库与配置凭空消失。
    /// </summary>
    public const string LegacyDirNameWindows = "imgagent";
    public const string LegacyDirNameUnix = ".imgagent";

    public static string ResolveHome()
    {
        var env = Environment.GetEnvironmentVariable("IMGHUB_HOME");
        if (!string.IsNullOrWhiteSpace(env))
        {
            Directory.CreateDirectory(env);
            return env;
        }

        // 兼容：改名前的 IMGAGENT_HOME 仍可识别（老用户的环境变量不必改）
        var legacyEnv = Environment.GetEnvironmentVariable("IMGAGENT_HOME");
        if (!string.IsNullOrWhiteSpace(legacyEnv))
        {
            Directory.CreateDirectory(legacyEnv);
            return legacyEnv;
        }

        bool win = OperatingSystem.IsWindows();
        var parent = win
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var newDir = Path.Combine(parent, win ? DirNameWindows : DirNameUnix);
        var oldDir = Path.Combine(parent, win ? LegacyDirNameWindows : LegacyDirNameUnix);

        // 新目录已存在（含本次新建）→ 直接用
        if (Directory.Exists(newDir)) { Directory.CreateDirectory(newDir); return newDir; }

        // 新目录不存在但旧目录存在 → **沿用旧目录**，用户数据无需搬迁
        if (Directory.Exists(oldDir))
        {
            AppLog.Info($"检测到改名前的数据目录，继续沿用：{oldDir}",
                        "AppPaths.ResolveHome");
            return oldDir;
        }

        Directory.CreateDirectory(newDir);
        return newDir;
    }
}