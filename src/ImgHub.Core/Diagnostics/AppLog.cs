using System.Text;

namespace ImgHub.Core.Diagnostics;

/// <summary>日志级别（用户要求：分级 info / warn / error；v0.5.43 增加 Debug）。</summary>
public enum LogLevel
{
    /// <summary>
    /// 诊断细节（v0.5.43）：状态流转、边界判定、被吞掉的非致命异常……
    ///
    /// ⚠️ **默认不记录** —— 这类日志量大且日常无价值，
    ///   只在排查问题时用环境变量 `IMGHUB_LOG_DEBUG=1` 打开（见 <see cref="AppLog.MinLevel"/>）。
    /// </summary>
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>一条日志（供 UI 诊断面板展示）。</summary>
public sealed record LogEntry(DateTime At, LogLevel Level, string Message, string? Where, string? Detail)
{
    public string TimeText => At.ToString("HH:mm:ss");
    public string LevelText => Level switch
    {
        LogLevel.Error => "ERROR",
        LogLevel.Warn => "WARN",
        LogLevel.Debug => "DEBUG",
        _ => "INFO",
    };
    public override string ToString()
        => $"{At:HH:mm:ss.fff} [{LevelText,-5}] {(Where is null ? "" : $"({Where}) ")}{Message}"
           + (Detail is null ? "" : $" | {Detail}");
}

/// <summary>
/// 应用日志（分级 + 落盘）。
///
/// 用户要求（v5.24.0）：
///   · **分级**：info / warn / error；
///   · 日志保存到数据目录下的 **log/** 文件夹，供事后诊断；
///   · 每条尽量写清**原因与位置**（`where` = 文件:方法:行号），方便排查。
///
/// 设计要点：
///   · 线程安全（lock）；写失败**绝不抛**（降级为仅内存）；
///   · 按天分文件 `imghub-yyyy-MM-dd.log`，追加写；
///   · 单文件超上限自动滚动（保留最近 N 个）；
///   · **API key 自动脱敏**（绝不把凭据写进日志）；
///   · 内存保留最近若干条，供 UI「诊断」查看（即使磁盘不可写也有记录）。
/// </summary>
public static class AppLog
{
    private const long MaxFileBytes = 5L * 1024 * 1024;   // 单文件 5 MB
    private const int MaxFiles = 10;                      // 保留最近 10 个
    private const int MemoryCapacity = 500;               // 内存环形缓冲

    private static readonly object Gate = new();
    private static readonly LinkedList<LogEntry> Memory = new();
    private static string? _logDir;

    /// <summary>
    /// 最低记录级别。
    ///
    /// 默认 <see cref="LogLevel.Info"/>（Debug 不记）；设环境变量 `IMGHUB_LOG_DEBUG=1`
    /// 则降到 <see cref="LogLevel.Debug"/>（见 <see cref="Init"/>）。
    /// 也可在设置里临时打开（`EnableDebug`）。
    /// </summary>
    private static LogLevel _minLevel = LogLevel.Info;

    /// <summary>诊断细节开关（v0.5.43）。设了 `IMGHUB_LOG_DEBUG` 或调用它即可打开。</summary>
    public static bool DebugEnabled
    {
        get => _minLevel <= LogLevel.Debug;
        set => _minLevel = value ? LogLevel.Debug : LogLevel.Info;
    }

    /// <summary>日志目录（未初始化时为 null，日志只进内存）。</summary>
    public static string? LogDirectory => _logDir;

    /// <summary>最低记录级别（默认 Info = 不记 Debug）。</summary>
    public static LogLevel MinLevel
    {
        get => _minLevel;
        set => _minLevel = value;
    }

    /// <summary>内存里的最近日志（倒序，最新在前）。</summary>
    public static IReadOnlyList<LogEntry> Recent(int take = 200)
    {
        lock (Gate) return Memory.TakeLast(take).Reverse().ToList();
    }

    public static void ClearMemory()
    {
        lock (Gate) Memory.Clear();
    }

    /// <summary>
    /// 初始化日志目录（在组合根调用一次）。目录建不出也不会抛 —— 只退化为内存日志。
    ///
    /// 同时识别 `IMGHUB_LOG_DEBUG`（v0.5.43）：设了就把最低级别降到 Debug，
    /// 从而记录状态流转/边界判定等诊断细节。
    ///
    /// ⚠️ 兼容：改名前的 `IMGAGENT_LOG_DEBUG` 也认（与其它 IMGAGENT_* 变量同一套策略）。
    /// </summary>
    public static void Init(string homePath)
    {
        try
        {
            var dbg = Environment.GetEnvironmentVariable("IMGHUB_LOG_DEBUG")
                   ?? Environment.GetEnvironmentVariable("IMGAGENT_LOG_DEBUG");
            if (!string.IsNullOrWhiteSpace(dbg) && dbg != "0" && !dbg.Equals("false", StringComparison.OrdinalIgnoreCase))
                _minLevel = LogLevel.Debug;

            var dir = Path.Combine(homePath, "log");
            Directory.CreateDirectory(dir);
            lock (Gate) _logDir = dir;
            Info("日志已启用", where: "AppLog.Init",
                 detail: $"目录：{dir}；最低级别：{_minLevel}");
            Debug("调试日志已开启（IMGHUB_LOG_DEBUG）", where: "AppLog.Init");
        }
        catch (Exception ex)
        {
            lock (Gate) _logDir = null;
            // 目录不可用 → 至少内存里留一条（此时还写不到盘）
            Warn("日志目录不可用，仅记录到内存", where: "AppLog.Init", ex: ex);
        }
    }

    /// <summary>
    /// 诊断细节（v0.5.43）：默认**不落盘**，需 `IMGHUB_LOG_DEBUG=1` 或 `DebugEnabled = true`。
    ///
    /// 用在哪（不是随便加）：
    ///   · 状态流转（标注进入/退出、生成阶段切换、选中项变化）；
    ///   · **被吞掉的非致命异常**（回调钩子、清理动作）—— 这类最值得记，因为静默失败难查；
    ///   · 边界判定（钳制、回退、空值短路）。
    ///
    /// ⚠️ 别把 Debug 当 Info 用：高频循环里逐帧/逐像素打日志会拖慢界面。
    /// </summary>
    public static void Debug(string message, string? where = null, string? detail = null)
        => Write(LogLevel.Debug, message, where, detail, ex: null);

    /// <summary>
    /// 诊断细节 + 异常（v0.5.43）。
    /// 专给"catch 了但业务上可忽略"的场景用 —— 把异常记下来但仍不中断主流程。
    /// </summary>
    public static void Debug(string message, Exception? ex, string? where = null, string? detail = null)
        => Write(LogLevel.Debug, message, where, detail, ex);

    public static void Info(string message, string? where = null, string? detail = null)
        => Write(LogLevel.Info, message, where, detail, ex: null);

    public static void Warn(string message, string? where = null, string? detail = null,
                            Exception? ex = null)
        => Write(LogLevel.Warn, message, where, detail, ex);

    public static void Error(string message, string? where = null, string? detail = null,
                             Exception? ex = null)
        => Write(LogLevel.Error, message, where, detail, ex);

    /// <summary>
    /// 统一写入口。**任何异常都在此吞掉** —— 日志系统本身绝不能影响主流程。
    /// </summary>
    private static void Write(LogLevel level, string message, string? where,
                              string? detail, Exception? ex)
    {
        if (level < _minLevel) return;

        var entry = new LogEntry(
            DateTime.Now, level,
            Mask(message),
            where,
            detail is null && ex is null
                ? null
                : string.Join(" | ", new[] { Mask(detail ?? ""), DescribeException(ex) }
                                        .Where(s => !string.IsNullOrEmpty(s))));

        lock (Gate)
        {
            Memory.AddLast(entry);
            while (Memory.Count > MemoryCapacity) Memory.RemoveFirst();

            if (_logDir is null) return;
            try
            {
                var path = Path.Combine(_logDir, $"imghub-{DateTime.Now:yyyy-MM-dd}.log");
                RollIfNeeded(path);
                File.AppendAllText(path, entry + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 写盘失败：保留内存记录，标记一次（避免递归调用）
                _logDir = null;
            }
        }
    }

    /// <summary>超过上限则改名归档（`*.1.log`），并清理超量归档。</summary>
    private static void RollIfNeeded(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length < MaxFileBytes) return;
            var rolled = Path.Combine(
                Path.GetDirectoryName(path)!,
                $"{Path.GetFileNameWithoutExtension(path)}.{DateTime.Now:HHmmss}.log");
            File.Move(path, rolled, overwrite: true);

            var dir = Path.GetDirectoryName(path)!;
            var olds = Directory.GetFiles(dir, "imghub-*.log")
                                .OrderByDescending(File.GetLastWriteTimeUtc)
                                .Skip(MaxFiles).ToList();
            // ⚠️ 这里**必须保持静默**：日志系统自己不能反过来记日志（会递归/放大故障）。
            //   删除归档失败只影响"历史日志留存数"，不影响当前写入。
            foreach (var f in olds) { try { File.Delete(f); } catch { } }
        }
        catch { /* 滚动失败不影响写日志（同上：日志系统不自我记录） */ }
    }

    /// <summary>异常详情（含 InnerException 链 —— AOT/网络错误常包在内层）。</summary>
    private static string DescribeException(Exception? ex)
    {
        if (ex is null) return "";
        var sb = new StringBuilder();
        var cur = ex;
        int depth = 0;
        while (cur is not null && depth < 5)
        {
            if (depth > 0) sb.Append("  ← ");
            sb.Append(cur.GetType().Name).Append(": ").Append(Mask(cur.Message));
            cur = cur.InnerException;
            depth++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 凭据脱敏：保留前 12 个字符（`sk-or-v1-abc`），其余替换为 `***`。**key 绝不落盘**。
    /// 注意要按"整段 key 字符"匹配（含 `-` 与 `_`），避免只吃掉一段就停下。
    /// </summary>
    public static string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(sk-[A-Za-z0-9_\-]{9})[A-Za-z0-9_\-]+",
            "$1***");
    }
}
