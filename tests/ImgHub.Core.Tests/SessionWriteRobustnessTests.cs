using System.Text.Json;
using ImgHub.Core;
using ImgHub.Core.Diagnostics;
using ImgHub.Core.Models;
using ImgHub.Core.Storage;

namespace ImgHub.Core.Tests;

/// <summary>
/// 存储写路径的健壮性回归（P0-1 / P0-3 / P0-4）。
///
/// 背景：这两条问题是**同一类错误** —— 写操作"看起来"有保护，实际没有：
///   · P0-1：序列化在 <c>AtomicWrite</c> 的 try **之外**求值，异常绕过日志直接消失；
///   · P0-3：写锁只覆盖 <c>AtomicWrite</c>，其余 5 个写盘点裸奔 → 并发下丢流水/截断。
/// </summary>
public class SessionWriteRobustnessTests : IDisposable
{
    private readonly string _home;

    public SessionWriteRobustnessTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "imghub-wr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        AppLog.Init(_home);
        AppLog.ClearMemory();
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    // ================================================================ P0-1

    [Fact]
    public void SaveConfig_SerializationFailure_IsLoggedAndDoesNotThrow()
    {
        // P0-1 核心：序列化失败必须**留下日志**。
        // 注入点用的是真实用户可触发的值：JimengScale 是 double，NaN 会被
        // JsonSerializer 拒绝（不允许 named floating point literals）。
        var s = new Session(_home);
        s.Config.Provider = "jimeng";
        s.Config.JimengScale = double.NaN;

        var ex = Record.Exception(() => s.SaveConfig());
        Assert.Null(ex);   // 写盘失败是"静默降级"，不得把异常抛到 UI

        var logged = AppLog.Recent(200).Any(e => e.Message.Contains("写入失败"));
        Assert.True(logged, "序列化/写盘失败必须写入日志，否则用户只看到「参数不保存」而查不到原因");

        // 不得留下半截文件（AtomicWrite 的临时文件应被清掉或未产生目标文件）
        Assert.False(File.Exists(s.ConfigFile), "序列化失败的配置不应落盘");
    }

    [Fact]
    public void SaveConfig_Successful_StillWritesFile()
    {
        // 对照：修好之后正常路径必须照旧工作（防"为了记日志把功能改坏"）
        var s = new Session(_home);
        s.Config.Provider = "jimeng";
        s.Config.JimengScale = 0.5;

        s.SaveConfig();

        Assert.True(File.Exists(s.ConfigFile));
        var reloaded = new Session(_home);
        Assert.Equal(0.5, reloaded.Config.JimengScale, 6);
    }

    [Fact]
    public void SaveState_SerializationFailure_IsLoggedAndDoesNotThrow()
    {
        // SaveState 走同一个 AtomicWrite → 同样必须留痕
        var s = new Session(_home);
        s.Items.Add(new Item { File = "a.png", Prompt = "p", Cost = double.NaN });

        var ex = Record.Exception(() => s.SaveState());
        Assert.Null(ex);

        Assert.True(AppLog.Recent(200).Any(e => e.Message.Contains("写入失败")),
                    "state.json 写失败同样必须留痕");
    }

    // ================================================================ P0-3

    [Fact]
    public void ConcurrentHistoryAppendAndTrim_ProducesIntactFile()
    {
        // P0-3 压力测试：生成线程 Append 流水，同时 UI 线程 TrimLog 全量重写同一文件。
        // 旧实现两者都不持锁 → 可能丢行 / 截断（写到一半被覆盖）。
        var s = new Session(_home);
        const int n = 300;
        using var start = new ManualResetEventSlim(false);

        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < n; i++)
                s.Log(new Item { File = $"w{w}-{i}.png", Prompt = $"p{w}-{i}", Kind = "gen" });
        })).ToArray();

        // 裁剪必须**与写入同时持续进行**，否则裁剪跑在文件还没写满时会早早退出（无效压力）
        var trimming = true;
        var trimmer = Task.Run(() =>
        {
            start.Wait();
            while (Volatile.Read(ref trimming)) s.TrimLog();
        });

        start.Set();
        Task.WaitAll(writers);
        Volatile.Write(ref trimming, false);
        trimmer.Wait();
        s.TrimLog();   // 最后再裁一次，把残余压到上限内

        // 断言：文件每一行都是完整 JSON（无截断），且行数不超过上限
        var lines = File.ReadAllLines(s.HistoryLog).Where(l => l.Trim().Length > 0).ToArray();
        Assert.True(lines.Length <= Catalog.HistoryMax,
                    $"裁剪后不得超过 {Catalog.HistoryMax} 行，实际 {lines.Length}");

        foreach (var line in lines)
        {
            // 只要有一行解析失败 = 发生了"写到一半被重写"的截断
            using var doc = JsonDocument.Parse(line);
            Assert.True(doc.RootElement.TryGetProperty("file", out _), "每条流水都应有 file 字段");
        }
    }

    [Fact]
    public void ConcurrentPromptHistoryAppendAndRemove_ProducesIntactFile()
    {
        // P0-3：PushPromptHistory（Append）与 RemovePromptHistory（全量重写）同样无锁
        var s = new Session(_home);
        using var start = new ManualResetEventSlim(false);

        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < 80; i++) s.PushPromptHistory($"p{w}-{i}");
        })).ToArray();

        var remover = Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < 20; i++) s.RemovePromptHistory("p0-0");
        });

        start.Set();
        Task.WaitAll(writers.Append(remover).ToArray());

        var lines = File.ReadAllLines(s.PromptHistoryFile).Where(l => l.Trim().Length > 0).ToArray();
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);   // 截断即抛
            Assert.True(doc.RootElement.TryGetProperty("prompt", out _));
        }
    }

    [Fact]
    public void ConcurrentKeyWriteAndRead_NeverObservesPartialFile()
    {
        // P0-3：key 文件 WriteAllText + File.Move 未走锁 → 与读取并发可能读到半个文件
        var s = new Session(_home);
        var path = s.KeyFile(ApiProvider.OpenRouter);
        const string full = "sk-or-v1-0123456789abcdefghijklmnop";

        using var start = new ManualResetEventSlim(false);
        var writer = Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < 200; i++) s.SaveKey(full, ApiProvider.OpenRouter);
        });
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < 200; i++)
            {
                if (!File.Exists(path)) continue;
                var read = Session.ReadKeyFileCompat(path, path + ".legacy-missing");
                // 要么读不到（文件正被替换），要么读到完整内容 —— 绝不能是半截
                if (read.Length > 0)
                    Assert.Equal(full, read);
            }
        })).ToArray();

        start.Set();
        Task.WaitAll(readers.Append(writer).ToArray());
    }
}
