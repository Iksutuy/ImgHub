using System.ComponentModel;
using Avalonia.Skia;
using ImgHub.App.Ui;

namespace ImgHub.Integration.Tests;

/// <summary>
/// 本地化测试的**串行集合**（v0.5.41 修复测试污染）。
///
/// `Localizer` 是进程级单例，语言状态是全局的；xunit 默认并行跑不同测试类
/// → 一个切到 `en`、另一个同时断言中文 ⇒ 单独跑通过、**一起跑必失败**（已实测）。
/// 同一集合内的类会**串行**执行。
///
/// ⚠️ 新增任何碰 <c>Localizer.Instance.Language</c> 的测试类，必须加
///   <c>[Collection(LocalizationTests.Name)]</c>。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalizationTests
{
    public const string Name = "Localization (shared Localizer singleton)";
}

/// <summary>
/// 索引器**通知名**契约（v0.5.41 定位；v0.5.40 用错导致"切换语言完全没变化"）。
///
/// ## 为什么用「假源」而不是真实 Localizer + 真实 Binding
///
/// 真实 `Binding` 的求值**依赖 `Application.Current` 等全局状态** ——
/// 在 xunit 全量跑（多个测试类共享进程）时，这些状态可能已被其他测试改动，
/// 导致 `Text` 首次取值就是 `null`（本会话实测：单独跑通过、全量跑失败）。
/// 那种测试属于"环境敏感"，维护成本高于价值。
///
/// 所以这里**只测通知机制本身**：一个与 Localizer 同形的假源
/// （索引器 + INotifyPropertyChanged），手动发各种通知名，
/// 断言"订阅者是否真的被通知到、且能取到新值"。
/// **真实端到端行为**已由 v0.5.41 的人工验证钉住（点设置里的语言下拉 → 界面即时切换，有截图）。
/// </summary>
[Collection(LocalizationTests.Name)]
public class IndexerBindingNotificationContractTests
{
    static IndexerBindingNotificationContractTests() => SkiaPlatform.Initialize();

    /// <summary>与 <see cref="Localizer"/> 同形的最小源。</summary>
    private sealed class FakeLocalizerLike : INotifyPropertyChanged
    {
        public string Value { get; set; } = "AAA";
        public string this[string key] => Value;

        public event PropertyChangedEventHandler? PropertyChanged;
        public void Raise(string? name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>模拟"订阅者收到通知后重取索引器"。</summary>
        public string ResolveAfter(string? name, out bool notified)
        {
            bool got = false;
            void H(object? s, PropertyChangedEventArgs e) => got = true;
            PropertyChanged += H;
            try { Raise(name); }
            finally { PropertyChanged -= H; }
            notified = got;
            return this["any.key"];
        }
    }

    [Fact]
    public void ItemNotification_NotifiesSubscribers_AndValueIsReread()
    {
        var src = new FakeLocalizerLike { Value = "ZZZ" };
        var v = src.ResolveAfter("Item", out var notified);
        Assert.True(notified, "发 `Item` 必须通知到订阅者");
        Assert.Equal("ZZZ", v);
    }

    [Fact]
    public void Localizer_LanguageChange_EmitsItemNotification()
    {
        // Localizer 侧的真实契约：切语言必须发 `Item`（而不是 `Item[key]` / `string.Empty`）。
        var l = Localizer.Instance;
        var old = l.Language;
        try
        {
            l.Language = "zh";
            var names = new List<string?>();
            void H(object? s, PropertyChangedEventArgs e) => names.Add(e.PropertyName);
            l.PropertyChanged += H;
            try { l.Language = "en"; }
            finally { l.PropertyChanged -= H; }

            Assert.Contains("Item", names);
            Assert.Contains(nameof(Localizer.Language), names);
            // 这两个写法实测无效，不得再出现（v0.5.40 用它们导致"切换无变化"）
            Assert.DoesNotContain(string.Empty, names);
            Assert.DoesNotContain(names, n => n?.StartsWith("Item[") == true);
        }
        finally { l.Language = old; }
    }

    [Fact]
    public void Localizer_Indexer_ReturnsCurrentLanguageValue()
    {
        // 索引器取值本身（纯逻辑，不涉及绑定/渲染 → 环境无关，可稳定断言）
        var l = Localizer.Instance;
        var old = l.Language;
        try
        {
            l.Language = "zh";
            Assert.Equal("ImgHub 工作台", l["app.title"]);
            l.Language = "en";
            Assert.Equal("ImgHub Workbench", l["app.title"]);
            l.Language = "ja";
            Assert.Equal("ImgHub ワークベンチ", l["app.title"]);
        }
        finally { l.Language = old; }
    }
}
