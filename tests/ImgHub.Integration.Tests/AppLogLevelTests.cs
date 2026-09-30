using ImgHub.Core.Diagnostics;

namespace ImgHub.Integration.Tests;

/// <summary>
/// 日志测试的**串行集合**。
///
/// `AppLog` 的内存缓冲与级别开关都是**进程级静态状态**，而 xunit 默认并行跑
/// 不同测试类 → 别的类同时在写日志时，这里的 `AppLog.Recent()` 会拿到多余条目。
/// 实测（CI run 36725366650，Windows job）：
///   `Debug_WithException_KeepsStackTrace` 报
///   `InvalidOperationException: Sequence contains more than one element`
///   —— 同一提交重跑就绿了，属典型的**测试互相干扰**，不是产品缺陷。
///
/// 同一集合内的类会串行执行，从而消除这类污染。
/// ⚠️ 新增任何碰 <c>AppLog.MinLevel</c> / <c>ClearMemory</c> / <c>Recent</c> /
///   <c>DebugEnabled</c> 的测试类，都必须加 <c>[Collection(AppLogTests.Name)]</c>。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppLogTests
{
    public const string Name = "AppLog (shared process-wide log buffer)";
}

/// <summary>
/// 日志分级（v0.5.43 新增 DEBUG）的契约。
///
/// 用户要求："尽可能捕获所有状态和操作并记录在日志中（DEBUG level）"。
/// 但 Debug **默认不落盘**（量大、日常无价值），需 `IMGHUB_LOG_DEBUG=1` 打开。
/// 这几条钉住：级别可切换、Debug 被过滤时不污染内存、被吞的异常有处可查。
/// </summary>
[Collection(AppLogTests.Name)]
public class AppLogLevelTests
{
    [Fact]
    public void Debug_IsSuppressedByDefault()
    {
        var old = AppLog.MinLevel;
        try
        {
            AppLog.ClearMemory();
            AppLog.MinLevel = LogLevel.Info;          // 默认
            AppLog.Debug("这条不该出现", where: "test");
            // Debug 被过滤 → 内存里不应有这一条。
            // 不断言 Empty：共享缓冲可能残留其它来源的条目（集合已串行化，
            // 这里是第二道保险，避免将来又因环境敏感而假失败）。
            Assert.DoesNotContain(AppLog.Recent(), x => x.Message.Contains("这条不该出现"));
        }
        finally { AppLog.MinLevel = old; }
    }

    [Fact]
    public void Debug_IsRecorded_WhenEnabled()
    {
        var old = AppLog.MinLevel;
        try
        {
            AppLog.ClearMemory();
            AppLog.DebugEnabled = true;
            AppLog.Debug("这条应该出现", where: "test");
            // 同样按内容挑，避免共享缓冲里有别的条目时假失败。
            var e = AppLog.Recent().Single(x => x.Message.Contains("这条应该出现"));
            Assert.Equal(LogLevel.Debug, e.Level);
            Assert.Equal("DEBUG", e.LevelText);
        }
        finally { AppLog.MinLevel = old; AppLog.ClearMemory(); }
    }

    [Fact]
    public void Debug_WithException_KeepsStackTrace()
    {
        // 被吞掉的异常（原本 `catch { }` 的那种）必须能通过 Debug 留痕
        var old = AppLog.MinLevel;
        try
        {
            AppLog.ClearMemory();
            AppLog.DebugEnabled = true;
            try { throw new InvalidOperationException("模拟钩子异常"); }
            catch (Exception ex) { AppLog.Debug("钩子抛异常，已忽略", ex, where: "test"); }

            // 按内容挑，不用 Single()：即便有其它来源的条目混进来也不至于假失败。
            // （共享缓冲已由上面的集合串行化，这里是第二道保险。）
            var e = AppLog.Recent().Single(x => x.Message.Contains("钩子抛异常"));
            Assert.Contains("模拟钩子异常", e.Detail);
            Assert.Contains("InvalidOperationException", e.Detail);   // 含类型/堆栈
        }
        finally { AppLog.MinLevel = old; AppLog.ClearMemory(); }
    }

    [Fact]
    public void LevelOrdering_IsCorrect()
    {
        // 数值顺序决定过滤：Debug < Info < Warn < Error
        Assert.True(LogLevel.Debug < LogLevel.Info);
        Assert.True(LogLevel.Info < LogLevel.Warn);
        Assert.True(LogLevel.Warn < LogLevel.Error);
    }
}
