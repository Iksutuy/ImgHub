using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using ImgHub.App.ViewModels;
using ImgHub.Core.Imaging;
using ImgHub.Core.Models;
using ImgHub.Core.Storage;

namespace ImgHub.Integration.Tests;

/// <summary>
/// 位图生命周期回归（P0-2）。
///
/// 背景：改动前全 App 层 `.Dispose()` 命中数为 **0** ——
///   · 每次 <c>RefreshHistory</c> 重建最多 200 个 <see cref="HistoryRow"/>，
///     每行一个 <c>DecodeToWidth</c> 的非托管位图，从不释放；
///   · 每次切图替换 <see cref="MainViewModel.PreviewImage"/>，旧位图也不释放。
/// 只能等 GC finalizer → 内存峰值与句柄耗尽风险。
///
/// 判据说明：<c>Bitmap</c> 释放后**再次使用**才抛 <c>ObjectDisposedException</c>，
/// 所以「Dispose 真生效」的可观测信号是：<b>该实例已不可用</b>。
/// 见 <see cref="Thumb_AfterDispose_IsUnusable"/>。
/// </summary>
public class BitmapLifetimeTests : IDisposable
{
    private readonly string _home;

    public BitmapLifetimeTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "imghub-bmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    private void WritePng(string name, int size = 64)
        => File.WriteAllBytes(Path.Combine(_home, name), Placeholder.Png("t", size: size));

    // ================================================================ 判据自检（重要）

    [Fact]
    public void Thumb_AfterDispose_IsUnusable()
    {
        // 这条**先证明判据有效**：Avalonia 的 Bitmap.Dispose() 之后实例确实不可用。
        // 若这个前提不成立，后面所有"已验证释放"的断言都是空的。
        WritePng("probe.png");
        using var fs = File.OpenRead(Path.Combine(_home, "probe.png"));
        var bmp = Bitmap.DecodeToWidth(fs, 80);
        Assert.NotNull(bmp);

        bmp.Dispose();

        // 释放后取像素/尺寸应抛 —— 这就是"真的释放了"的证据
        var ex = Record.Exception(() => _ = bmp.PixelSize);
        Assert.NotNull(ex);
    }

    // ================================================================ HistoryRow

    [Fact]
    public void HistoryRow_Dispose_ReleasesThumb()
    {
        WritePng("a.png");
        var row = new HistoryRow(new Item { File = "a.png", Prompt = "p" }, _home);

        var thumb = row.Thumb;
        Assert.NotNull(thumb);            // 前置：确实解码出来了

        row.Dispose();

        // 释放后原实例不可用（若实现是空操作，这个实例仍能正常访问像素）
        Assert.NotNull(Record.Exception(() => _ = thumb.PixelSize));
    }

    [Fact]
    public void HistoryRow_Dispose_IsIdempotent()
    {
        WritePng("a.png");
        var row = new HistoryRow(new Item { File = "a.png" }, _home);
        _ = row.Thumb;

        row.Dispose();
        Assert.Null(Record.Exception(row.Dispose));   // 二次 Dispose 不得抛
    }

    [Fact]
    public void HistoryRow_DisposeWithoutDecode_DoesNotThrow()
    {
        // 没访问过 Thumb（从未解码）时 Dispose 也要安全
        var row = new HistoryRow(new Item { File = "missing.png" }, _home);
        Assert.Null(Record.Exception(row.Dispose));
    }

    [Fact]
    public void HistoryRow_ResolveAfterDispose_ReturnsNullOrFresh_NotDisposedInstance()
    {
        // 列表被重建后若还有旧引用被渲染，不该拿到已释放的位图
        WritePng("a.png");
        var row = new HistoryRow(new Item { File = "a.png" }, _home);
        var first = row.Thumb;
        row.Dispose();

        var second = row.Thumb;
        Assert.NotSame(first, second);
    }

    // ================================================================ PreviewThumb

    [Fact]
    public void PreviewThumb_Dispose_ReleasesThumb()
    {
        WritePng("b.png");
        var t = new PreviewThumb(new Item { File = "b.png", Prompt = "p" }) { HomePath = _home };

        var thumb = t.Thumb;
        Assert.NotNull(thumb);

        t.Dispose();
        Assert.NotNull(Record.Exception(() => _ = thumb.PixelSize));
    }

    [Fact]
    public void PreviewThumb_Dispose_IsIdempotent_AndSafeWithoutDecode()
    {
        var never = new PreviewThumb(new Item { File = "nope.png" }) { HomePath = _home };
        Assert.Null(Record.Exception(never.Dispose));

        WritePng("b.png");
        var t = new PreviewThumb(new Item { File = "b.png" }) { HomePath = _home };
        _ = t.Thumb;
        t.Dispose();
        Assert.Null(Record.Exception(t.Dispose));
    }

    // ================================================================ VM：刷新历史批量释放

    [Fact]
    public void RefreshHistory_DisposesSupersededRows()
    {
        // P0-2 核心场景：反复刷新历史时，被替换掉的 HistoryRow 必须释放其位图。
        for (int i = 0; i < 5; i++) WritePng($"{i}.png");

        var (vm, session) = MakeVm();
        for (int i = 0; i < 5; i++)
            session.Items.Add(new Item { File = $"{i}.png", Prompt = $"p{i}" });

        vm.RefreshHistoryCommand.Execute(null);
        Assert.Equal(5, vm.History.Count);

        // 解码一遍（模拟 UI 真的渲染过缩略图），捕获这一代的位图
        var firstGen = vm.History.Select(r => r.Thumb).ToArray();
        Assert.All(firstGen, Assert.NotNull);

        vm.RefreshHistoryCommand.Execute(null);   // 重建 → 上一代必须被释放

        foreach (var old in firstGen)
            Assert.NotNull(Record.Exception(() => _ = old!.PixelSize));   // 已释放
    }

    [Fact]
    public void RefreshHistory_RepeatedRefreshes_ReleaseOldRows()
    {
        // 用弱引用确认「上一批 HistoryRow 不再被 VM 持有」——
        // 仅 Dispose 位图还不够，VM 若继续引用旧行，行与位图会一起存活。
        for (int i = 0; i < 3; i++) WritePng($"{i}.png");

        var (vm, session) = MakeVm();
        for (int i = 0; i < 3; i++) session.Items.Add(new Item { File = $"{i}.png" });

        // ⚠️ 必须借 NoInlining 辅助方法拿弱引用：否则 Debug 下局部变量仍是栈根，
        //    GC 收不掉（测试会假失败），而这与产品行为无关。
        var weak = BuildAndDecodeHistory(vm);

        for (int i = 0; i < 5; i++)
        {
            vm.RefreshHistoryCommand.Execute(null);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.True(weak.All(w => !w.IsAlive),
            "刷新后旧 HistoryRow 必须可回收（否则它们的非托管位图会长期占用内存）");
        Assert.Equal(3, vm.History.Count);
    }

    /// <summary>
    /// 刷新一次历史、解码全部缩略图，只返回弱引用。
    /// <see cref="MethodImplOptions.NoInlining"/> 保证本方法的栈帧（含局部 HistoryRow）
    /// 在返回后不再构成 GC 根。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] BuildAndDecodeHistory(MainViewModel vm)
    {
        vm.RefreshHistoryCommand.Execute(null);
        foreach (var r in vm.History) _ = r.Thumb;
        return vm.History.Select(r => new WeakReference(r)).ToArray();
    }

    // ================================================================ 辅助

    private (MainViewModel Vm, Session Session) MakeVm()
    {
        var session = new Session(_home);
        session.Config.Offline = true;
        session.Config.Provider = "openrouter";
        session.Config.Model = ImgHub.Core.Catalog.DefaultModelOpenRouter;
        session.Config.Quality = "low";
        session.Sanitize();

        var svc = new ImgHub.App.Services.AppServices
        {
            Session = session,
            Http = new ImgHub.Core.Http.HttpJsonClient(),
            ImageApi = new ImgHub.Core.Services.ImageApi(
                session.Provider, new ImgHub.Core.Http.HttpJsonClient()),
            Polish = new ImgHub.Core.Services.PolishService(new ImgHub.Core.Http.HttpJsonClient()),
            Storage = new FakeStorage(),
            Platform = new FakePlatform(),
            ModelStats = new ImgHub.Core.Services.ModelStatsService(_home),
            Pending = new PendingTaskStore(_home),
        };
        var vm = new MainViewModel(svc) { Offline = true };
        return (vm, session);
    }

    private sealed class FakeStorage : ImgHub.App.Services.IPlatformStorage
    {
        public Task<ImgHub.App.Services.PlatformOpResult> SaveToGalleryAsync(
            byte[] data, string fileName, string mediaType)
            => Task.FromResult(ImgHub.App.Services.PlatformOpResult.Ok(fileName));
        public Task<ImgHub.App.Services.PlatformOpResult> OpenInExternalViewerAsync(string path)
            => Task.FromResult(ImgHub.App.Services.PlatformOpResult.Ok(path));
        public Task<ImgHub.App.Services.PlatformOpResult> RevealInFileManagerAsync(string path)
            => Task.FromResult(ImgHub.App.Services.PlatformOpResult.Ok(path));
        public Task<string?> GetGalleryDirAsync() => Task.FromResult<string?>(null);
    }

    private sealed class FakePlatform : ImgHub.App.Services.IPlatformInfo
    {
        public string PlatformName => "Test";
        public string Version => "1.0";
    }
}
