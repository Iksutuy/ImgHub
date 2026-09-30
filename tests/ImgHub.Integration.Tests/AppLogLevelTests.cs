using ImgHub.Core.Diagnostics;

namespace ImgHub.Integration.Tests;

/// <summary>
/// 日志分级（v0.5.43 新增 DEBUG）的契约。
///
/// 用户要求："尽可能捕获所有状态和操作并记录在日志中（DEBUG level）"。
/// 但 Debug **默认不落盘**（量大、日常无价值），需 `IMGHUB_LOG_DEBUG=1` 打开。
/// 这几条钉住：级别可切换、Debug 被过滤时不污染内存、被吞的异常有处可查。
/// </summary>
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
            Assert.Empty(AppLog.Recent());            // Debug 被过滤 → 内存里没有
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
            var recent = AppLog.Recent();
            Assert.Single(recent);
            Assert.Equal(LogLevel.Debug, recent[0].Level);
            Assert.Equal("DEBUG", recent[0].LevelText);
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

            var e = AppLog.Recent().Single();
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
