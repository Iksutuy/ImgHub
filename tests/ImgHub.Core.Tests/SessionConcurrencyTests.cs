using System.Collections.Concurrent;
using ImgHub.Core.Models;
using ImgHub.Core.Storage;

namespace ImgHub.Core.Tests;

/// <summary>
/// <see cref="Session.Items"/> 的线程安全回归（P0-4）。
///
/// 背景：`Items` 原为裸 <c>List&lt;Item&gt;</c>，但被**跨线程**访问 ——
///   · <c>MainViewModel.ResumePendingAsync</c> 全程 <c>ConfigureAwait(false)</c>，
///     在**线程池线程**调 <c>Session.Push</c> / <c>Session.Log</c>；
///   · 同时 UI 线程可能在 <c>RefreshHistoryCore</c> 遍历同一个 List。
/// 并发「枚举 + 插入」→ <c>InvalidOperationException</c>（集合被修改），
/// 或 <c>SaveState</c> 枚举时丢行。
/// </summary>
public class SessionConcurrencyTests : IDisposable
{
    private readonly string _home;

    public SessionConcurrencyTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "imghub-conc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    private const int Iterations = 3000;
    private const int MaxItems = 64;

    /// <summary>并发「插入 / 删除」与「枚举 / 求和」——旧实现必抛集合被修改。</summary>
    [Fact]
    public void ConcurrentMutateAndEnumerate_DoesNotThrow()
    {
        var s = new Session(_home);
        var errors = new ConcurrentQueue<Exception>();
        using var start = new ManualResetEventSlim(false);

        var mutators = Enumerable.Range(0, 3).Select(w => Task.Run(() =>
        {
            start.Wait();
            try
            {
                for (int i = 0; i < Iterations; i++)
                {
                    s.Items.Insert(0, new Item
                    {
                        File = $"w{w}-{i}.png", Prompt = $"p{w}-{i}", Cost = 0.001,
                    });
                    // 保持规模恒定 → 高频修改版本号，最大化暴露枚举竞态。
                    // 用原子裁剪（TrimTail），而不是「读 Count 再 RemoveAt」那种 TOCTOU 写法。
                    s.Items.TrimTail(MaxItems);
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        })).ToArray();

        var readers = Enumerable.Range(0, 3).Select(reader => Task.Run(() =>
        {
            start.Wait();
            try
            {
                for (int i = 0; i < Iterations; i++)
                {
                    double sum = 0;
                    foreach (var it in s.Items) sum += it.Cost;   // 旧实现：这里抛
                    _ = s.Items.Count;
                    _ = s.TotalCost;
                    _ = s.CostForUnknownProvider();
                    _ = s.CostByProvider();
                    _ = s.Current;
                    GC.KeepAlive(sum);
                    GC.KeepAlive(reader);
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        })).ToArray();

        start.Set();
        Task.WaitAll(mutators.Concat(readers).ToArray());

        Assert.Empty(errors);
    }

    /// <summary>并发 Push（会连锁 SaveState → 枚举 Items 落盘）不得抛、不得丢行。</summary>
    [Fact]
    public void ConcurrentPush_AndSaveState_DoesNotThrowNorLoseRows()
    {
        var s = new Session(_home);
        var errors = new ConcurrentQueue<Exception>();
        using var start = new ManualResetEventSlim(false);

        // Push 会 SaveState（枚举 Items 序列化），与别的 Push/Log 并发
        var pushers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            start.Wait();
            try
            {
                for (int i = 0; i < 60; i++)
                    s.Push(new Item
                    {
                        File = $"p{w}-{i}.png", Prompt = $"p{w}-{i}",
                        Kind = "gen", Cost = 0.01,
                    });
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        })).ToArray();

        var loggers = Enumerable.Range(0, 2).Select(w => Task.Run(() =>
        {
            start.Wait();
            try
            {
                for (int i = 0; i < 200; i++)
                    s.Log(new Item { File = $"l{w}-{i}.png", Prompt = "p", Kind = "gen" });
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        })).ToArray();

        start.Set();
        Task.WaitAll(pushers.Concat(loggers).ToArray());

        Assert.Empty(errors);
        Assert.Equal(4 * 60, s.Items.Count);   // 每次 Push 恰好加一行，一行不丢
    }

    /// <summary>枚举期间不得因并发修改而中断（快照语义）。</summary>
    [Fact]
    public void Enumeration_SeesConsistentSnapshot()
    {
        var s = new Session(_home);
        for (int i = 0; i < 10; i++)
            s.Items.Add(new Item { File = $"{i}.png", Cost = 0.5 });

        var errors = new ConcurrentQueue<Exception>();
        using var start = new ManualResetEventSlim(false);

        var mutator = Task.Run(() =>
        {
            start.Wait();
            try
            {
                for (int i = 0; i < Iterations; i++)
                {
                    s.Items.Insert(0, new Item { File = $"x{i}.png", Cost = 0.5 });
                    s.Items.TrimTail(20);
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        });

        var reader = Task.Run(() =>
        {
            start.Wait();
            try
            {
                for (int i = 0; i < Iterations; i++)
                {
                    int n = 0;
                    foreach (var _ in s.Items) n++;
                    // 快照的项数必须自洽（>=0 且不超过当时的规模上限）
                    if (n < 0 || n > 100) throw new InvalidOperationException($"快照项数异常：{n}");
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        });

        start.Set();
        Task.WaitAll(mutator, reader);

        Assert.Empty(errors);
    }
}
