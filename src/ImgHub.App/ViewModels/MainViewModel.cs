using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImgHub.App.Services;
using ImgHub.App.Ui;
using ImgHub.Core;
using ImgHub.Core.Diagnostics;
using ImgHub.Core.Http;
using ImgHub.Core.Imaging;
using ImgHub.Core.Models;
using ImgHub.Core.Services;
using ImgHub.Core.Storage;

namespace ImgHub.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _svc;
    private readonly Session _sess;

    public MainViewModel(AppServices services)
    {
        _svc = services;
        _sess = services.Session;

        _provider = _sess.Provider;
        _polishEnabled = _sess.Config.PolishEnabled;
        var m0 = _sess.Config.Model;
        _model = (!string.IsNullOrWhiteSpace(m0) && Catalog.ModelMatchesProvider(m0, _provider))
            ? m0! : Catalog.DefaultModel(_provider);
        _quality = _sess.Config.Quality;
        _aspect = _sess.Config.Aspect;
        _resolution = _sess.Config.Resolution;
        _outputFormat = _sess.Config.OutputFormat;
        _batchN = _sess.Config.BatchN;
        // v5.27.0 文档对齐参数（从配置恢复）
        _background = _sess.Config.Background;
        _outputCompression = _sess.Config.OutputCompression;
        _moderation = _sess.Config.Moderation;
        _seed = _sess.Config.Seed;
        _stream = _sess.Config.Stream;
        // v0.5.31 新增参数（从配置恢复，否则重启悄悄回默认）
        _partialImages = _sess.Config.PartialImages;
        _inputFidelity = _sess.Config.InputFidelity;
        _style = _sess.Config.Style;
        _negativePrompt = _sess.Config.NegativePrompt;
        _promptExtend = _sess.Config.PromptExtend;
        _promptExtendMode = _sess.Config.PromptExtendMode;
        _watermark = _sess.Config.Watermark;
        // v0.5.31：即梦参数（从配置恢复）
        _jimengScale = _sess.Config.JimengScale;
        _jimengForceSingle = _sess.Config.JimengForceSingle;
        _jimengLoraWeight = _sess.Config.JimengLoraWeight;
        _jimengReturnUrl = _sess.Config.JimengReturnUrl;
        _jimengLogoEnabled = _sess.Config.JimengLogoEnabled;
        _jimengLogoPosition = _sess.Config.JimengLogoPosition;
        _jimengLogoLanguage = _sess.Config.JimengLogoLanguage;
        _jimengLogoOpacity = _sess.Config.JimengLogoOpacity;
        _jimengLogoText = _sess.Config.JimengLogoText;
        // v0.5.31：润色风格索引（与服务端 Style 对齐）+ 记住初始模型名（供切模型时存旧快照）
        _polishStyleIndex = Math.Max(0, Array.IndexOf(PolishStyleKeys,
            (string.IsNullOrWhiteSpace(_sess.Config.PolishStyle) ? "auto"
                : _sess.Config.PolishStyle.Trim().ToLowerInvariant())));
        _presetModelBeforeSwitch = _model;
        _presetProviderBeforeSwitch = _provider;
        _svc.Polish.Style = PolishStyleKeys[_polishStyleIndex];
        _svc.Polish.ActiveStyle = _provider;
        _providerOnly = _sess.Config.ProviderOnly;
        _providerIgnore = _sess.Config.ProviderIgnore;
        _providerOrder = _sess.Config.ProviderOrder;
        _providerSort = _sess.Config.ProviderSort;
        _allowFallbacks = _sess.Config.AllowFallbacks;
        _offline = _sess.Config.Offline;
        UseIcons = _sess.Config.UseIcons;    // v0.5.37：透传（已无 UI 开关，仅保持 config 契约）
        _enableHoverAnimation = _sess.Config.UseHoverAnimation;
        EnableToolTips = _sess.Config.UseToolTips;   // 走 setter → 同步 Help 全局开关
        ApplyLanguage(_sess.Config.Language);        // v0.5.40：启动即应用持久化语言

        foreach (var p in Catalog.Providers)
            Providers.Add(new ProviderOption(p.Provider, p.Label));
        RebuildModelChoices();
        OnPropertyChanged(nameof(SelectedProvider));
        UpdateEstimate();
        RefreshConfigured();

        Log($"ImgHub 工作台已启动 · {_svc.Platform.PlatformName}");
        Log($"数据目录 {_sess.HomePath}");
        RefreshHistory();
        RefreshPromptHistory();
        RunSelfCheck(logToMessages: true);   // P5：就绪 = 启动自检结论，不再无条件写「就绪」

        // v5.26.0：接线「未完成任务」钩子 + 启动恢复（不阻塞 UI，见 ResumePendingAsync）
        WireSubmitProgress();
        WirePartialImage();
        RefreshPendingCount();
        _ = ResumePendingAsync();

        if (_sess.Current is { } first) _ = ShowPreviewAsync(first);
    }

    /// <summary>
    /// 当前**预览中**的图（编辑/标注的目标）。
    ///
    /// ⚠️ 为什么需要它（v5.27.0 修复的真实缺陷）：
    ///   旧实现的编辑目标取自 <c>Session.Current</c>，而它的定义是
    ///   <c>Items[0]</c> —— 也就是**历史列表最新的一张**。
    ///   于是在历史里点选一张旧图只改变了预览，编辑却仍然拿最新那张去改，
    ///   用户看到的现象就是"我明明选了这张图，出来的图却完全不相关"。
    ///   现在改为显式跟踪"预览中的图"，点选哪张就编辑哪张。
    /// </summary>
    private Item? _currentItem;

    /// <summary>当前编辑/标注目标（无显式选择时回退到最新一张）。</summary>
    public Item? CurrentItem => _currentItem ?? _sess.Current;

    /// <summary>当前目标的显示名（UI 提示用）。</summary>
    public string CurrentItemName => CurrentItem?.File ?? "（无）";

    /// <summary>
    /// 「编辑」按钮的悬停说明 —— **必须动态显示目标图**。
    ///
    /// ⚠️ 为什么值得做成动态文案（v5.27.0）：
    ///   用户报告的困惑是「编辑历史图片出来的图不相关」。根因之一是编辑目标
    ///   曾恒为最新一张，而 UI 上完全没有提示"这次要改哪张"。
    ///   把目标文件名直接写在提示里，用户一眼就能确认是否符合预期，
    ///   而不是等出图后才发现改错了对象。
    /// </summary>
    public string EditTip => CurrentItem is null
        ? Localizer.Instance["action.editTipNoItem"]
        : Localizer.Instance.T("action.editTipWithItem",
            CurrentItemName, RefImages.Count, Model);

    /// <summary>「生成」按钮的悬停说明（动态显示本次会带上的参考图数量）。</summary>
    public string GenerateTip => RefImages.Count == 0
        ? Localizer.Instance.T("action.generateTipNoRef", Model, Quality)
        : Localizer.Instance.T("action.generateTipWithRef", RefImages.Count, Model, Quality);

    /// <summary>
    /// 「用标注编辑」按钮的悬停说明 —— 如实说明走的是哪条链路。
    ///
    /// ⚠️ 为什么必须区分（v5.27.0）：两条链路的"区域约束强度"完全不同，
    ///   用户必须知道自己在用哪一条，否则会误以为"圈了就一定会严格只改圈内"：
    ///     · APIMart + 带 Alpha 蒙版 → mask_url 局部重绘，约束最强（文档保证语义）
    ///     · 其他情况 → 靠"标注合成图 + 提示词"表达区域，模型只是**尽量**遵守
    ///       （文档明确：蒙版也只引导编辑，不保证边界外像素完全不变）
    /// </summary>
    public string RegionEditTip
    {
        get
        {
            var L = Localizer.Instance;
            var head = L.T("region.editTipHead", RegionCount);
            // ⚠️ 真源统一在 Catalog.SupportsMaskChannel（与 ImageApi 的实际实现对齐）：
            //   之前这里各写各的 provider 判断，导致"提示说有蒙版、实际却丢弃"的不一致。
            if (_provider == ApiProvider.Apimart)
                return head + L.T("region.editTipMaskUrl", CurrentItemName);
            if (_provider == ApiProvider.OpenAi)
                return head + L.T("region.editTipMaskField", CurrentItemName);
            if (Catalog.SupportsMaskChannel(_provider))
                return head + L.T("region.editTipMaskGeneric", CurrentItemName);
            return head + L.T("region.editTipNoMask", Provider.Label(), CurrentItemName);
        }
    }

    // ================================================================ 可绑定
    [ObservableProperty] private string _prompt;
    [ObservableProperty] private string _quality;
    [ObservableProperty] private string _aspect;
    [ObservableProperty] private string _resolution;
    [ObservableProperty] private string _outputFormat;

    // ---------------------------------------------------------------- 文档对齐参数（v5.27.0）
    /// <summary>背景模式：auto / transparent / opaque（transparent 仅 png/webp）。</summary>
    [ObservableProperty] private string _background = "";
    /// <summary>输出压缩强度 0~100（仅 jpeg/webp 生效；0 = 不传）。</summary>
    [ObservableProperty] private int _outputCompression;
    /// <summary>内容审核强度 auto/low（仅 APIMart GPT-Image-2.5）。</summary>
    [ObservableProperty] private string _moderation = "";
    /// <summary>确定性种子（OpenRouter，部分模型支持）。</summary>
    [ObservableProperty] private string _seed = "";
    /// <summary>流式部分图（OpenRouter SSE，仅 supports_streaming 的模型）。</summary>
    [ObservableProperty] private bool _stream;

    // ---------------------------------------------------------------- v0.5.31 新增（OpenAI 官方 / 千问）
    /// <summary>OpenAI 流式部分图张数（partial_images，0~3）。</summary>
    [ObservableProperty] private int _partialImages = 2;
    /// <summary>OpenAI input_fidelity（high/low，"空" = 不传）。仅 gpt-image-1 系。</summary>
    [ObservableProperty] private string _inputFidelity = "";
    /// <summary>dall-e-3 的 style（vivid/natural，"空" = 不传）。</summary>
    [ObservableProperty] private string _style = "";
    /// <summary>千问 negative_prompt（反向提示词）。</summary>
    [ObservableProperty] private string _negativePrompt = "";
    /// <summary>千问 prompt_extend（提示词智能改写，文档默认 true）。</summary>
    [ObservableProperty] private bool _promptExtend = true;
    /// <summary>千问 prompt_extend_mode（""/direct/agent；agent 仅 T2I）。</summary>
    [ObservableProperty] private string _promptExtendMode = "";
    /// <summary>千问 watermark（是否加水印，文档默认 false）。</summary>
    [ObservableProperty] private bool _watermark;
    /// <summary>当前 provider 的生图端点（可覆盖，落盘到 config.base_urls）。</summary>
    [ObservableProperty] private string _baseUrlInput = "";

    // ---------------------------------------------------------------- 即梦（火山引擎）
    /// <summary>即梦 SecretAccessKey 输入框（设置浮层；与 AK 分开保存）。</summary>
    [ObservableProperty] private string _secretKeyInput = "";
    /// <summary>即梦 <c>scale</c>：文本描述影响程度（0~1，默认 0.5）。</summary>
    [ObservableProperty] private double _jimengScale = 0.5;
    /// <summary>即梦 <c>force_single</c>：强制只出 1 张（省时省钱）。</summary>
    [ObservableProperty] private bool _jimengForceSingle;
    /// <summary>即梦提取指令（当前选中的预设原文）。</summary>
    [ObservableProperty] private string _jimengExtractPrompt = "";
    /// <summary>即梦元素提取的 <c>lora_weight</c>（默认 1.0）。</summary>
    [ObservableProperty] private double _jimengLoraWeight = 1.0;
    /// <summary>即梦查询是否同时返回图片链接（<c>req_json.return_url</c>）。</summary>
    [ObservableProperty] private bool _jimengReturnUrl = true;
    /// <summary>即梦是否加明水印（<c>req_json.logo_info.add_logo</c>）。</summary>
    [ObservableProperty] private bool _jimengLogoEnabled;
    /// <summary>即梦水印位置。</summary>
    [ObservableProperty] private int _jimengLogoPosition;
    /// <summary>即梦水印语言。</summary>
    [ObservableProperty] private int _jimengLogoLanguage;
    /// <summary>即梦水印透明度（0~1）。</summary>
    [ObservableProperty] private double _jimengLogoOpacity = 1;
    /// <summary>即梦水印文字内容。</summary>
    [ObservableProperty] private string _jimengLogoText = "";

    // ---- 提取浮窗（v0.5.31）----
    /// <summary>提取浮窗是否打开。</summary>
    [ObservableProperty] private bool _extractOpen;
    /// <summary>提取浮窗的标签页（0 = 商品提取，1 = 元素提取）。</summary>
    [ObservableProperty] private int _extractTab;
    /// <summary>当前标签页的预设列表（供 UI 绑定）。</summary>
    public IReadOnlyList<JimengExtract.Preset> ExtractPresets =>
        JimengExtract.PresetsOf(_extractTab == 1
            ? JimengExtract.JimengExtractKind.Element
            : JimengExtract.JimengExtractKind.Product);
    /// <summary>提取浮窗里选中的预设索引。</summary>
    [ObservableProperty] private int _extractPresetIndex;
    /// <summary>可替换的物件名（仅"提取饰品"这类预设开放，见 JimengExtract.Customize）。</summary>
    [ObservableProperty] private string _extractItemName = "";
    /// <summary>当前选中的预设是否允许替换物件名。</summary>
    public bool ExtractAllowsItemName =>
        ExtractPresets.Count > 0 &&
        ExtractPresetIndex >= 0 && ExtractPresetIndex < ExtractPresets.Count &&
        ExtractPresets[ExtractPresetIndex].Customizable;
    /// <summary>提取浮窗的说明文案（随标签页变化）。</summary>
    public string ExtractHint => _extractTab == 1
        ? Localizer.Instance["extract.hintElement"]
        : Localizer.Instance["extract.hintProduct"];

    partial void OnExtractTabChanged(int value)
    {
        ExtractPresetIndex = 0;
        OnPropertyChanged(nameof(ExtractPresets));
        OnPropertyChanged(nameof(ExtractAllowsItemName));
        OnPropertyChanged(nameof(ExtractHint));
    }

    partial void OnExtractPresetIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ExtractAllowsItemName));
        // 选中预设 → 立即把官方文案写进请求字段（用户点「开始提取」时直接可用）
        if (ExtractPresets.Count > 0 && value >= 0 && value < ExtractPresets.Count)
        {
            var p = ExtractPresets[value];
            JimengExtractPrompt = JimengExtract.Customize(p, _extractItemName);
        }
    }

    partial void OnExtractItemNameChanged(string value)
        => OnExtractPresetIndexChanged(_extractPresetIndex);

    /// <summary>OpenRouter provider 路由：只允许这些 slug（逗号分隔）。</summary>
    [ObservableProperty] private string _providerOnly = "";
    /// <summary>OpenRouter provider 路由：排除这些 slug。</summary>
    [ObservableProperty] private string _providerIgnore = "";
    /// <summary>OpenRouter provider 路由：优先顺序。</summary>
    [ObservableProperty] private string _providerOrder = "";
    /// <summary>OpenRouter provider 路由排序：price / throughput / latency。</summary>
    [ObservableProperty] private string _providerSort = "";
    /// <summary>主 provider 失败后是否允许回退（null = 不传，用服务端默认）。</summary>
    [ObservableProperty] private bool? _allowFallbacks;
    /// <summary>provider 路由区是否展开（默认收起，避免干扰常用流程）。</summary>
    [ObservableProperty] private bool _advancedOpen;
    /// <summary>最近一次流式部分图预览（OpenRouter SSE）。</summary>
    [ObservableProperty] private Bitmap? _partialImage;

    /// <summary>是否显示 APIMart 专属参数（审核强度）。</summary>
    public bool IsApimartProvider => Provider == ApiProvider.Apimart;
    /// <summary>是否显示 OpenRouter 专属参数（provider 路由 / seed / 流式）。</summary>
    public bool IsOpenRouterProvider => Provider == ApiProvider.OpenRouter;
    /// <summary>OpenAI 官方：显示 moderation / input_fidelity / style（dall-e-3）/ partial_images。</summary>
    public bool IsOpenAiProvider => Provider == ApiProvider.OpenAi;
    /// <summary>千问 DashScope：显示反向提示词 / prompt_extend / watermark。</summary>
    public bool IsDashScopeProvider => Provider == ApiProvider.DashScope;
    /// <summary>即梦（火山引擎）：需要 AK/SK 两把密钥；有提取链路。</summary>
    public bool IsJimengProvider => Provider == ApiProvider.Jimeng;
    /// <summary>即梦是否需要**第二把密钥**（AK/SK 签名）→ 设置浮层多一个输入框。</summary>
    public bool NeedsSecretKey => Provider.NeedsAccessKeyPair();
    /// <summary>当前是否处于即梦的"提取"链路（选中的 req_key 是提取类）。</summary>
    public bool IsJimengExtractMode =>
        IsJimengProvider && Catalog.IsJimengExtractReqKey(_model);
    /// <summary>是否有独立的"分辨率档"概念（OpenAI 官方与千问直接用像素 size，没有档位）。</summary>
    public bool HasResolutionTier =>
        Provider is not (ApiProvider.OpenAi or ApiProvider.DashScope or ApiProvider.Jimeng);
    /// <summary>当前模型是否支持 OpenAI 的 input_fidelity（仅 gpt-image-1 系，不含 2/2.5/mini）。</summary>
    public bool SupportsInputFidelity =>
        IsOpenAiProvider && (Model ?? "").Trim().ToLowerInvariant() is "gpt-image-1" or "gpt-image-1.5";
    /// <summary>当前是否 dall-e-3（它有一套独立参数：style，以及 standard/hd 质量）。</summary>
    public bool IsDallE3Model =>
        IsOpenAiProvider && string.Equals((Model ?? "").Trim(), "dall-e-3",
                                          StringComparison.OrdinalIgnoreCase);
    /// <summary>参考图张数上限（随 provider 变化：千问 3 张，其余 16 张）。</summary>
    public int MaxRefs => Catalog.MaxRefsFor(Provider);

    public string[] ModerationOptions => Catalog.ModerationChoices;
    /// <summary>
    /// 背景选项**按模型收窄**：GPT Image 2 / 2.5 不支持 transparent（文档明确会报错）。
    /// ⚠️ 曾经这里只有恒定的 <c>BackgroundOptions</c>，用户能在 2/2.5 上选到 transparent，
    ///    请求会被服务端拒绝且看不出原因 —— 现在下拉与发送逻辑共用这个真源。
    /// </summary>
    public string[] BackgroundOptionsForModel =>
        Catalog.SupportsTransparentBackground(Provider, Model)
            ? Catalog.BackgroundChoices
            : new[] { "auto", "opaque" };
    /// <summary>input_fidelity 选项（仅 gpt-image-1 系显示）。</summary>
    public string[] InputFidelityOptions { get; } = { "", "high", "low" };
    /// <summary>dall-e-3 的 style 选项。</summary>
    public string[] StyleOptions { get; } = { "", "vivid", "natural" };
    /// <summary>千问 prompt_extend_mode 选项（"" = 不传，用服务端默认 direct）。</summary>
    public string[] PromptExtendModeOptions { get; } = { "", "direct", "agent" };
    /// <summary>OpenAI 流式部分图张数（文档 0~3）。</summary>
    public int[] PartialImageCounts { get; } = { 0, 1, 2, 3 };

    public bool HasPartialImage => PartialImage is not null;
    [ObservableProperty] private int _batchN;
    [ObservableProperty] private bool _offline;
    [ObservableProperty] private string _model;
    [ObservableProperty] private ApiProvider _provider;
    [ObservableProperty] private bool _polishEnabled;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _status = "就绪";
    [ObservableProperty] private double _totalCost;
    [ObservableProperty] private double _providerCost;

    /// <summary>无 provider 标记的旧数据累计（>0 时 UI 提示"未归类"）。</summary>
    [ObservableProperty] private double _unknownCost;
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private string? _previewPath;
    [ObservableProperty] private string _estimatedCostText = "";
    [ObservableProperty] private bool _darkTheme;
    [ObservableProperty] private bool _settingsOpen;
    [ObservableProperty] private bool _keyChecking;
    [ObservableProperty] private string _keyStatusText = "未配置";
    [ObservableProperty] private string _keyCheckStatus = "";
    [ObservableProperty] private string _apiKeyInput = "";
    [ObservableProperty] private string _polishBaseInput = "";
    [ObservableProperty] private string _polishModelInput = "";
    [ObservableProperty] private string _polishKeyInput = "";
    [ObservableProperty] private bool _polishOpen;
    [ObservableProperty] private int _polishIndex;
    [ObservableProperty] private string _polishCurrent = "";
    [ObservableProperty] private int _regionCount;

    // ---------------------------------------------------------------- 提示词指南浮窗（v0.5.31）
    /// <summary>提示词工程指南浮窗是否打开。</summary>
    [ObservableProperty] private bool _guideOpen;
    /// <summary>当前标签页索引（对应 <see cref="PromptGuide.All"/> 的顺序）。</summary>
    [ObservableProperty] private int _guideTab;
    /// <summary>供 UI 绑定：全部指南（标签页内容）。</summary>
    public IReadOnlyList<PromptGuide.Guide> Guides => PromptGuide.All;
    /// <summary>当前标签页的指南。</summary>
    public PromptGuide.Guide CurrentGuide =>
        PromptGuide.All.Count == 0
            ? PromptGuide.For(_provider)
            : PromptGuide.All[Math.Clamp(_guideTab, 0, PromptGuide.All.Count - 1)];
    /// <summary>浮窗顶部的"当前生效"说明（让用户知道正在用哪套润色提示词）。</summary>
    public string GuideActiveHint =>
        Localizer.Instance.T("guide.active",
            PromptGuide.For(_svc.Polish.EffectiveStyle == "qwen"
                ? ApiProvider.DashScope : ApiProvider.OpenAi).Title,
            PolishStyleLabel);

    // ---------------------------------------------------------------- 润色风格选择（v0.5.31）
    /// <summary>润色提示词风格的**显示名**列表（顺序与 <see cref="PolishStyleKeys"/> 对齐）。</summary>
    public IReadOnlyList<string> PolishStyleNames => new[]
    {
        Localizer.Instance["polish.styleAuto"],
        Localizer.Instance["polish.styleOpenAi"],
        Localizer.Instance["polish.styleQwen"],
    };
    /// <summary>风格选择项（空 = auto；与 <see cref="AppConfig.PolishStyle"/> 的值对齐）。</summary>
    public static readonly string[] PolishStyleKeys = { "auto", "openai", "qwen" };

    [ObservableProperty] private int _polishStyleIndex;
    /// <summary>当前风格的说明（浮窗里显示）。v0.5.41：走 Localizer（切语言会变）。</summary>
    public string PolishStyleLabel =>
        Localizer.Instance[(_svc.Polish.EffectiveStyle == "qwen")
            ? "polish.styleQwen" : "polish.styleOpenAi"];
    /// <summary>把索引写回服务（UI 用 ComboBox 绑索引，避免中英混写）。</summary>
    partial void OnPolishStyleIndexChanged(int value)
    {
        var i = Math.Clamp(value, 0, PolishStyleKeys.Length - 1);
        _svc.Polish.Style = PolishStyleKeys[i];
        _sess.Config.PolishStyle = _svc.Polish.Style;
        OnPropertyChanged(nameof(PolishStyleLabel));
        OnPropertyChanged(nameof(GuideActiveHint));
        PersistConfig();
        Log($"润色提示词风格 → {PolishStyleNames[i]}", MessageLevel.Info);
    }

    /// <summary>圈画数量变化 → 刷新「用标注编辑」的悬停说明（提示里带数量）。</summary>
    partial void OnRegionCountChanged(int value)
    {
        OnPropertyChanged(nameof(RegionEditTip));
        OnPropertyChanged(nameof(CanEditWithRegions));
        OnPropertyChanged(nameof(RegionCountText));
        OnPropertyChanged(nameof(HasRegions));
    }

    /// <summary>是否已圈画过区域（≥1 处）。</summary>
    public bool HasRegions => RegionCount > 0;

    /// <summary>标注数量文案（UI 常驻显示，避免用户不知道已画了几处）。</summary>
    public string RegionCountText => RegionCount == 0
        ? Localizer.Instance["region.none"]
        : Localizer.Instance.T("region.count", RegionCount);

    /// <summary>
    /// 「用标注编辑」是否可用 —— **必须同时满足**：能运行 + 已圈画 +（画布已有标注导出）。
    ///
    /// ⚠️ 为什么加 RegionCount 条件（v5.28.0 待办 2）：
    ///   原来只绑 CanRun，用户没圈画时按钮也可点，只能在点击后弹警告。
    ///   从源头禁用对用户更清楚 —— 一眼看出"还差什么"。
    /// </summary>
    public bool CanEditWithRegions => CanRun && RegionCount > 0;
    [ObservableProperty] private bool _regionMode;
    [ObservableProperty] private string _brushColor = "#FF3B30";
    /// <summary>笔刷粗细（px）。v0.5 按用户要求默认 3px（细线更精确）。</summary>
    [ObservableProperty] private double _brushSize = 3;

    /// <summary>可用粗细档（像素）。UI 用 Slider 或下拉都能绑。</summary>
    public double BrushSizeMin => 2;
    public double BrushSizeMax => 80;

    /// <summary>粗细显示文本（供 UI 展示当前值）。</summary>
    public string BrushSizeText => $"{BrushSize:0}px";

    /// <summary>
    /// 当前工具是否**使用**粗细设置（v0.5.36 需求⑥）。
    ///
    /// 方框 / 圆圈是**区域**语义（填充整块，粗细无意义）→ 隐藏粗细条，避免用户调了没反应。
    /// 马克笔（笔迹宽度）/ 橡皮（擦除半径）才受粗细影响。
    /// </summary>
    public bool ToolUsesBrushSize =>
        CanvasTool is Controls.RegionCanvas.RegionTool.Marker
                  or Controls.RegionCanvas.RegionTool.Eraser;
    [ObservableProperty] private string _annotatedPath = "";

    /// <summary>
    /// 标注按「底图路径」缓存 —— 实现「标注跟随图片」：
    ///   · 切换历史图片时，当前图的标注被保存，目标图的标注被恢复；
    ///   · 同一张图来回切换，标注不会丢。
    /// 值 = 该图的标注快照（RegionCanvas.RegionSnapshot）。
    /// </summary>
    private readonly Dictionary<string, Controls.RegionCanvas.RegionSnapshot> _regionCache = new();

    /// <summary>保存当前画布的标注到缓存（由视图在切换底图前调用）。</summary>
    public void SaveRegionsFor(string? imagePath, Controls.RegionCanvas.RegionSnapshot snap)
    {
        if (string.IsNullOrEmpty(imagePath)) return;
        if (snap.Strokes.Count == 0) _regionCache.Remove(imagePath);
        else _regionCache[imagePath] = snap;
    }

    /// <summary>取出某张图已缓存的标注（可能为 null）。</summary>
    public Controls.RegionCanvas.RegionSnapshot? GetRegionsFor(string? imagePath)
    {
        if (string.IsNullOrEmpty(imagePath)) return null;
        return _regionCache.TryGetValue(imagePath, out var s) ? s : null;
    }

    /// <summary>清空所有缓存（内存释放）。</summary>
    public void ClearRegionCache() => _regionCache.Clear();
    [ObservableProperty] private int _toolIndex;
    [ObservableProperty] private bool _paletteOpen;

    // ================================================================ 位图生命周期（P0-2）

    /// <summary>
    /// 批量释放并清空一个持有非托管位图的列表（P0-2）。
    /// ⚠️ **必须先 Dispose 再 Clear** —— 清空后旧对象若已被 GC 回收，
    /// 就再也拿不到它们的位图来释放了（等 finalizer 会让内存峰值失控）。
    /// </summary>
    private static void ClearAndDispose<T>(ObservableCollection<T> list) where T : IDisposable
    {
        foreach (var item in list) item.Dispose();
        list.Clear();
    }

    /// <summary>
    /// 替换非托管位图属性：**先释放旧值**（P0-2）。
    /// 旧实现直接赋值 → 每切一次图就泄漏一个位图，只能等 GC finalizer。
    /// </summary>
    private static void ReplaceBitmap(
        Bitmap? old, Bitmap? @new, Action<Bitmap?> assign)
    {
        if (!ReferenceEquals(old, @new)) old?.Dispose();
        assign(@new);
    }

    public ObservableCollection<HistoryRow> History { get; } = new();
    public ObservableCollection<Message> Messages { get; } = new();
    public ObservableCollection<string> PromptHistory { get; } = new();
    public ObservableCollection<string> ModelChoices { get; } = new();
    public ObservableCollection<ProviderOption> Providers { get; } = new();
    public ObservableCollection<PreviewThumb> BatchResults { get; } = new();
    public ObservableCollection<PreviewThumb> RefImages { get; } = new();
    public ObservableCollection<string> PolishCandidates { get; } = new();

    public string ProviderLabel => _provider.Label();
    public string KeyHint => Catalog.KeyHint(_provider);
    public string PlatformName => _svc.Platform.PlatformName;
    public string Version => _svc.Platform.Version;
    /// <summary>P13：底栏显示的版本号（取自 assembly 版本，来源 = Directory.Build.props 的 Version）。</summary>
    public string VersionText
    {
        get
        {
            try
            {
                var info = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info))
                {
                    // 去掉 "+<git-sha>" 后缀（SourceLink 会附加）
                    var plus = info.IndexOf('+');
                    var v = plus > 0 ? info[..plus] : info;
                    return $"ImgHub v{v}";
                }
            }
            catch { /* 读取失败不阻断 UI */ }
            return "ImgHub";
        }
    }
    public string HomePath => _sess.HomePath;
    public string[] QualityOptions => Catalog.QualityChoices(_provider, _model);
    /// <summary>画幅选项随 provider **与模型**变化（见 Catalog.AspectChoicesFor）。</summary>
    public string[] AspectOptions => Catalog.AspectChoicesFor(_provider, _model);
    /// <summary>分辨率选项随 provider 与模型变化（部分模型仅 1K）。</summary>
    public string[] ResolutionOptions => Catalog.ResolutionChoicesFor(_provider, _model);
    /// <summary>输出格式选项随模型变化（dall-e-3 只认 png；千问固定 png）。</summary>
    public string[] OutputFormatOptions => Catalog.OutputFormatChoices(_provider, _model);

    // ---------------------------------------------------------------- 下拉的索引绑定（v0.5.31）
    /// <summary>
    /// ⚠️ 为什么下拉一律绑 <c>SelectedIndex</c> 而不是 <c>SelectedItem</c>（v0.5.31 真实回归）：
    ///   ComboBox 在 <c>ItemsSource</c> 被替换（切 provider/模型 → 选项集合变化）时，
    ///   会**先**把 SelectedItem 置为 null 并**回写绑定源**。于是：
    ///     ① 字符串型 VM 属性被写成空串 → <c>SaveParamPreset</c> 把 <c>aspect=''</c> 存进快照
    ///        （用户在 config.json 里就能看到空值，切回来参数"没保存住"）；
    ///     ② 空值再被收窄回退，值虽然对了，但下拉的选中态已经丢了（画面里"画幅"空白）。
    ///   索引绑定的 setter 收到 <c>-1</c>（不匹配）时可以**安全忽略**，
    ///   既不会写出空值，也让"值合法就在下拉里显示选中"这一不变量由 getter 保证。
    /// </summary>
    private static int IndexOf(string[] options, string? value) =>
        string.IsNullOrEmpty(value)
            ? -1
            : Array.FindIndex(options, o => string.Equals(o, value, StringComparison.Ordinal));

    public int QualityIndex
    {
        get => IndexOf(QualityOptions, _quality);
        set => SetFromIndex(QualityOptions, value, v => Quality = v);
    }

    public int AspectIndex
    {
        get => IndexOf(AspectOptions, _aspect);
        set => SetFromIndex(AspectOptions, value, v => Aspect = v);
    }

    public int ResolutionIndex
    {
        get => IndexOf(ResolutionOptions, _resolution);
        set => SetFromIndex(ResolutionOptions, value, v => Resolution = v);
    }

    public int OutputFormatIndex
    {
        get => IndexOf(OutputFormatOptions, _outputFormat);
        set => SetFromIndex(OutputFormatOptions, value, v => OutputFormat = v);
    }

    /// <summary>把下拉索引写回值；<c>-1</c>（集合里没有当前值）一律忽略，绝不写空。</summary>
    private static void SetFromIndex(string[] options, int index, Action<string> apply)
    {
        if (index < 0 || index >= options.Length) return;
        apply(options[index]);
    }

    /// <summary>刷新全部下拉的选中态（选项集合或当前值变化后调用）。</summary>
    private void NotifyDropdownIndices()
    {
        OnPropertyChanged(nameof(QualityIndex));
        OnPropertyChanged(nameof(AspectIndex));
        OnPropertyChanged(nameof(ResolutionIndex));
        OnPropertyChanged(nameof(OutputFormatIndex));
    }

    /// <summary>
    /// 批量张数的最大值 —— **随模型变化**。
    /// 文档/实测：gemini 图像系只支持 n=1；OpenRouter 其它模型 10；APIMart 4。
    /// 用动态上限而不是固定 4，避免用户拉到服务端会拒绝的值。
    /// </summary>
    public int BatchNMax => Catalog.MaxNFor(_provider, _model);

    /// <summary>批量张数是否受限（用于 UI 提示"该模型仅支持 1 张"）。</summary>
    public bool BatchNLimited => BatchNMax < 4;
    public string BatchNHint => BatchNLimited
        ? Localizer.Instance.T("batch.limited", BatchNMax)
        : Localizer.Instance.T("batch.max", BatchNMax);
    /// <summary>与 RegionCanvas.RegionTool 枚举顺序**严格对应**（v0.5.36 起去掉了「画笔」）：
    /// 0=方框 1=圆圈 2=马克笔 3=橡皮。
    ///
    /// ⚠️ v0.5.41：改为**从 Localizer 取**（此前硬编码中文 → 切到英/日时下拉仍是中文）。
    ///   顺序必须与枚举一致，有测试钉住（见 i18n 相关契约测试）。</summary>
    public IReadOnlyList<string> ToolNames => new[]
    {
        Localizer.Instance["region.toolRect"],
        Localizer.Instance["region.toolEllipse"],
        Localizer.Instance["region.toolMarker"],
        Localizer.Instance["region.toolEraser"],
    };
    public IReadOnlyList<string> PaletteColors { get; } = new[]
    {
        "#FF3B30", "#FF9500", "#FFD60A", "#32D74B", "#00C7BE", "#0A84FF",
        "#5E5CE6", "#BF5AF2", "#FF2D55", "#A2845E", "#FFFFFF", "#C7C7CC",
        "#8E8E93", "#636366", "#48484A", "#1C1C1E",
    };
    /// <summary>
    /// 当前工具。
    ///
    /// ⚠️ v0.5.36：枚举去掉了「画笔」，索引整体左移（旧 4=橡皮 → 新 3=橡皮）。
    ///   对旧配置里的 `tool_index` 做一次**兼容映射**：>=4 的一律落到橡皮（3），
    ///   否则用户升级后会发现"选的工具变了"。
    /// </summary>
    public Controls.RegionCanvas.RegionTool CanvasTool
    {
        get
        {
            var idx = Math.Clamp(_toolIndex, 0, ToolNames.Count - 1);
            return (Controls.RegionCanvas.RegionTool)idx;
        }
    }
    public string PolishCounterText =>
        PolishCandidates.Count == 0 ? "" : $"{PolishIndex + 1}/{PolishCandidates.Count}";

    // ================================================================ 状态
    public bool IsConfigured => !string.IsNullOrEmpty(LoadApiKey()) || Offline;
    public bool CanRun => IsConfigured;
    public bool NeedsSetup => !IsConfigured;

    /// <summary>
    /// 「编辑图片」（进入/退出标注模式）是否可用。
    ///
    /// ⚠️ **刻意不依赖 <see cref="IsConfigured"/>**（v0.5.33 修）：
    ///   标注/圈画是**纯本地操作**，不需要 API key。此前绑 `IsConfigured` →
    ///   用户切到一个还没配 key 的端点时，按钮**直接变灰** →
    ///   表现为"切换到未就绪端点就自动取消了编辑"（既进不去、也退不出）。
    ///   真正需要 key 的是「用标注编辑」（那才调 API，见 <see cref="CanEditWithRegions"/>）。
    /// </summary>
    public bool CanAnnotate => CurrentItem is not null;

    // ================================================================ 启动自检（P5）
    /// <summary>
    /// 逐项自检的结论（用户要求：**全功能可用、端点全部配置**才写「就绪」；
    /// 否则写「未就绪：<原因>」，而不是无条件显示「就绪」）。
    /// </summary>
    [ObservableProperty] private string _selfCheckText = "自检中…";
    /// <summary>自检是否全部通过。</summary>
    [ObservableProperty] private bool _selfCheckOk;
    /// <summary>自检详情（逐项一行，供 tooltip 与消息面板）。</summary>
    public ObservableCollection<string> SelfCheckDetails { get; } = new();

    // ================================================================ 未完成任务恢复（v5.26.0）
    /// <summary>当前未完成的生图任务数（UI 可提示）。</summary>
    [ObservableProperty] private int _pendingCount;

    /// <summary>是否有未完成任务（设置/顶栏可显示入口）。</summary>
    public bool HasPending => PendingCount > 0;

    partial void OnPendingCountChanged(int value) => OnPropertyChanged(nameof(HasPending));

    /// <summary>
    /// 把 <see cref="ImageApi"/> 的进度钩子接到 <see cref="PendingTaskStore"/>。
    ///
    /// 三个时机（见 SubmitPhase）：
    ///   · SyncSending     —— 同步接口发出前记痕迹（无 task_id，只能事后告知）
    ///   · AsyncSubmitted  —— **拿到 task_id 立即落盘**（退出也能找回的关键）
    ///   · Finished        —— 正常结束 → 清理痕迹
    /// </summary>
    private void WireSubmitProgress()
    {
        _svc.ImageApi.OnSubmitProgress = (phase, taskIds, prompt, model, success) =>
        {
            try
            {
                switch (phase)
                {
                    case SubmitPhase.AsyncSubmitted:
                        _svc.Pending.Add(taskIds, _provider.Key(), model, prompt,
                                         Math.Max(1, _batchN), _quality, _aspect,
                                         _resolution, _outputFormat);
                        _syncSentAt = null;      // 异步路径无 sent 记录
                        break;

                    case SubmitPhase.SyncSending:
                        _syncSentAt = _svc.Pending.AddSyncSent(
                            _provider.Key(), model, prompt,
                            _quality, _aspect, _resolution, _outputFormat);
                        break;

                    case SubmitPhase.Finished:
                        if (taskIds.Count > 0)
                        {
                            // 成功 → 标记 done（Compact 会清掉）；失败 → 保留并写明
                            foreach (var id in taskIds)
                                _svc.Pending.Update(id, success ? "done" : "failed",
                                    success ? "" : "轮询/下载失败，见日志");
                        }
                        else if (_syncSentAt is { } at)
                        {
                            _svc.Pending.ClearSyncSent(at);   // 同步请求已正常收尾
                            _syncSentAt = null;
                        }
                        break;
                }
                RefreshPendingCount();
            }
            catch (Exception ex)
            {
                // 记录失败绝不能影响生成
                AppLog.Warn("未完成任务登记失败", "MainViewModel.WireSubmitProgress", ex: ex);
            }
        };
    }

    /// <summary>同步接口本次请求的 sent 记录时间戳（用于正常收尾时清理）。</summary>
    private double? _syncSentAt;

    /// <summary>
    /// 接线 OpenRouter SSE 的「部分图」回调（文档 Streaming Image Generation）：
    /// 生成过程中把部分图贴到预览区，让用户知道"确实在出图"而不是卡住。
    /// 部分图**不落盘**（文档：部分图不产生部分计费，只用于预览）。
    /// </summary>
    private void WirePartialImage()
    {
        _svc.ImageApi.OnPartialImage = (index, bytes) =>
        {
            try
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        // P0-2：替换前释放旧的部分图（否则每次流式回调泄漏一个位图）
                        ReplaceBitmap(PartialImage, new Bitmap(new MemoryStream(bytes)),
                                      b => PartialImage = b);
                        OnPropertyChanged(nameof(HasPartialImage));
                    }
                    catch { /* 部分图渲染失败不影响主流程 */ }
                });
            }
            catch (Exception ex)
            {
                AppLog.Warn("部分图更新失败", "MainViewModel.WirePartialImage", ex: ex);
            }
        };
    }

    private void RefreshPendingCount()
    {
        try
        {
            PendingCount = _svc.Pending.LoadOpen().Count;
            OnPropertyChanged(nameof(HasPending));
        }
        catch (Exception ex)
        {
            AppLog.Warn("读取未完成任务失败", "MainViewModel.RefreshPendingCount", ex: ex);
        }
    }

    /// <summary>
    /// 启动时恢复未完成任务（v5.26.0 用户要求：提交后意外退出也能找回图片）。
    ///
    /// ⚠️ **只查询、不重新提交** → **不会重复扣费**。
    /// 不阻塞 UI（fire-and-forget），完成后 Dispatcher.Post 更新界面。
    /// </summary>
    public async Task ResumePendingAsync(string? apiKeyOverride = null)
    {
        const string where = "MainViewModel.ResumePendingAsync";
        List<PendingTask> open;
        try { open = _svc.Pending.LoadOpen(); }
        catch (Exception ex)
        {
            AppLog.Error("读取未完成任务失败", where, ex: ex);
            return;
        }
        if (open.Count == 0) { RefreshPendingCount(); return; }

        AppLog.Info($"发现 {open.Count} 个未完成生成任务，尝试恢复（只查询，不重复扣费）", where);

        foreach (var t in open)
        {
            // sent：同步接口已发出但没收到响应 → 无法自动找回，如实告知
            if (t.Status == "sent")
            {
                Log($"⚠ 上次有一个同步请求（{t.Model}）未收到响应就退出了：" +
                    $"图片可能已在服务端生成并扣费，请到 {t.Provider} 后台核对。" +
                    $"（同步接口无任务号，本工具无法自动找回）",
                    MessageLevel.Warn, where);
                _svc.Pending.Update(t.TaskId, "failed", "同步请求未收尾（无法自动找回）");
                continue;
            }

            if (string.IsNullOrEmpty(t.TaskId)) continue;

            // 用**该任务所属 provider** 的 key（不是当前选中的）
            var prov = ApiProviderExtensions.Parse(t.Provider) ?? _provider;
            var key = apiKeyOverride ?? LoadKeyFor(prov);
            if (string.IsNullOrEmpty(key))
            {
                Log($"⚠ 任务 {t.TaskId} 需要 {t.Provider} 的 API key 才能取回 —— " +
                    $"请到设置里填写后重启，或稍后重试", MessageLevel.Warn, where);
                continue;
            }

            try
            {
                Log($"尝试取回任务 {t.TaskId}（仅查询，不重新提交）…", MessageLevel.Info, where);
                var api = new ImageApi(prov, _svc.Http, null);
                var td = await api.PollTaskAsync(t.TaskId, key, CancellationToken.None,
                                                 maxWait: 90, pollInterval: 3.0)
                                .ConfigureAwait(false);

                var items = await DownloadCompletedAsync(td, t, key, where).ConfigureAwait(false);
                if (items.Count == 0)
                {
                    _svc.Pending.Update(t.TaskId, "failed", "任务已完成但图片下载失败");
                    Log($"任务 {t.TaskId} 已完成，但图片下载失败（可稍后重试）",
                        MessageLevel.Warn, where);
                    continue;
                }

                LandResumed(t, items);
                _svc.Pending.Update(t.TaskId, "done");
                Log($"✅ 已找回任务 {t.TaskId} 的 {items.Count} 张图", MessageLevel.Ok, where);
            }
            catch (ApiError ex) when (ex.Status is 401 or 403)
            {
                _svc.Pending.Update(t.TaskId, "failed", "key 无效/无权限");
                Log($"任务 {t.TaskId} 取回失败：key 无效（换回正确 key 后仍可重试）",
                    MessageLevel.Err, where);
            }
            catch (Exception ex)
            {
                // 超时/网络问题 → 保留 pending，下次启动再试
                AppLog.Warn($"任务 {t.TaskId} 暂未取回（保留待下次）", where, ex: ex);
                Log($"任务 {t.TaskId} 暂未取回：{ex.Message}（已保留，下次启动自动重试）",
                    MessageLevel.Warn, where);
            }
        }

        try { _svc.Pending.Compact(); }
        catch (Exception ex) { AppLog.Debug("清理未完成任务记录失败（不影响使用）", ex, where: "MainViewModel.CleanPending"); }
        Dispatcher.UIThread.Post(() =>
        {
            RefreshPendingCount();
            RefreshHistory();
        });
    }

    /// <summary>读指定 provider 的 key（环境变量优先，其次 key 文件）。</summary>
    private string LoadKeyFor(ApiProvider p)
    {
        try
        {
            // 兼容：改名前的 IMGAGENT_* 环境变量仍可识别
            var env = p == ApiProvider.Apimart
                ? Environment.GetEnvironmentVariable("IMGHUB_APIMART_API_KEY")
                  ?? Environment.GetEnvironmentVariable("IMGAGENT_APIMART_API_KEY")
                : Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
                  ?? Environment.GetEnvironmentVariable("IMGAGENT_API_KEY");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            // 新名优先，旧名回退（改名前的 key 仍可用）
            return Session.ReadKeyFileCompat(_sess.KeyFile(p), _sess.LegacyKeyFile(p));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取 {p} 的 key 失败", "MainViewModel.LoadKeyFor", ex: ex);
            return "";
        }
    }

    /// <summary>从已完成的任务响应里下载图片（复用轮询结果结构）。</summary>
    private async Task<List<(byte[] Data, string Media)>> DownloadCompletedAsync(
        System.Text.Json.JsonElement td, PendingTask t, string key, string where)
    {
        var list = new List<(byte[], string)>();
        try
        {
            if (!td.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("images", out var imgs) ||
                imgs.ValueKind != System.Text.Json.JsonValueKind.Array)
                return list;

            foreach (var entry in imgs.EnumerateArray())
            {
                if (!entry.TryGetProperty("url", out var urlEl)) continue;
                var url = urlEl.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? urlEl.EnumerateArray().Select(x => x.GetString())
                           .FirstOrDefault(s => !string.IsNullOrEmpty(s))
                    : urlEl.GetString();
                if (string.IsNullOrEmpty(url)) continue;
                try
                {
                    var bytes = await _svc.Http.GetBytesAsync(url, key, ct: CancellationToken.None)
                                               .ConfigureAwait(false);
                    if (bytes.Length > 0)
                        list.Add((bytes, Core.Imaging.ImageCodec.SniffMediaType(bytes) ?? "image/png"));
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"恢复下载图片失败：{url}", where, ex: ex);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("解析已完成任务失败", where, ex: ex);
        }
        return list;
    }

    /// <summary>把找回的图片落盘 + 入历史（复用既有落盘路径）。</summary>
    private void LandResumed(PendingTask t, List<(byte[] Data, string Media)> items)
    {
        try
        {
            int seq = 1;
            foreach (var (data, media) in items)
            {
                var path = SaveImageToHome(data, media, t.Prompt, _sess.Counter + seq);
                if (path is null) continue;
                var item = new Item
                {
                    File = Path.GetFileName(path),
                    Prompt = t.Prompt,
                    Kind = "gen",
                    Model = t.Model,
                    Quality = t.Quality,
                    Cost = 0,        // 恢复路径不重复计费（费用已在提交时产生）
                    Provider = t.Provider,
                    Note = $"recovered:{t.TaskId}",
                };
                _sess.Push(item);
                _sess.Log(item);
                seq++;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("找回图片落盘失败", "MainViewModel.LandResumed", ex: ex);
        }
    }

    /// <summary>手动重试取回未完成任务（设置里可点）。</summary>
    [RelayCommand]
    private async Task RetryPendingAsync() => await ResumePendingAsync();

    /// <summary>
    /// 自检状态指示点画刷（v5.25.0 美化）：通过=绿、未通过=橙。
    /// 用 VM 暴露画刷而非自定义 XAML Converter（少一个类型，AOT 也更稳）。
    /// </summary>
    public Avalonia.Media.IBrush SelfCheckBrush => SelfCheckOk
        ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#23C343"))
        : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF9A2E"));

    partial void OnSelfCheckOkChanged(bool value)
        => OnPropertyChanged(nameof(SelfCheckBrush));

    /// <summary>
    /// 启动自检：检查「本机可判断」的条件。**不阻塞网络探测**（离线/无网也应可用本地功能）。
    /// 返回是否全部通过。
    /// </summary>
    public bool RunSelfCheck(bool logToMessages = false)
    {
        var details = new List<string>();
        var problems = new List<string>();

        // ① 生图 provider + key
        var hasKey = !string.IsNullOrEmpty(LoadApiKey());
        if (hasKey)
            details.Add($"✓ 生图 API key 已配置（{ProviderLabel}）");
        else
        {
            details.Add("✗ 生图 API key 未配置");
            problems.Add("未配置 API key");
        }

        // ② 模型合法性（必须属于当前 provider 的清单）
        var model = Model ?? "";
        if (!string.IsNullOrWhiteSpace(model) && Catalog.ModelMatchesProvider(model, _provider))
            details.Add($"✓ 模型合法（{model}）");
        else
        {
            details.Add("✗ 模型与 provider 不匹配");
            problems.Add("模型无效");
        }

        // ③ 数据目录可写
        try
        {
            Directory.CreateDirectory(_sess.HomePath);
            var probe = Path.Combine(_sess.HomePath, ".selfcheck.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            details.Add($"✓ 数据目录可写（{_sess.HomePath}）");
        }
        catch (Exception ex)
        {
            details.Add($"✗ 数据目录不可写：{ex.Message}");
            problems.Add("数据目录不可写");
        }

        // ④ 润色配置（可选，但按用户口径「全功能可用」也算未就绪）
        if (_sess.Config.PolishEnabled)
        {
            if (!string.IsNullOrWhiteSpace(_sess.Config.PolishBaseUrl) &&
                !string.IsNullOrWhiteSpace(_sess.Config.PolishModel) &&
                !string.IsNullOrWhiteSpace(_sess.PolishKey))
                details.Add("✓ 润色已配置");
            else
            {
                details.Add("✗ 润色未配置（可选功能不可用）");
                problems.Add("润色未配置（可选）");
            }
        }
        else details.Add("· 润色已关闭（视为通过）");

        SelfCheckDetails.Clear();
        foreach (var d in details) SelfCheckDetails.Add(d);

        SelfCheckOk = problems.Count == 0;
        SelfCheckText = SelfCheckOk ? "就绪" : "未就绪：" + problems[0];
        Status = SelfCheckText;

        if (logToMessages)
        {
            Log(SelfCheckOk ? "启动自检：全部通过 → 就绪" : "启动自检：存在未就绪项", 
                SelfCheckOk ? MessageLevel.Ok : MessageLevel.Warn);
            foreach (var d in details) Log("  " + d, MessageLevel.Dim);
        }
        return SelfCheckOk;
    }

    /// <summary>顶栏徽标点击 → 打开设置修复未就绪项。</summary>
    [RelayCommand]
    private void ShowSelfCheck()
    {
        RunSelfCheck(logToMessages: true);
        if (!SelfCheckOk) OpenSettingsCore();
    }

    // ================================================================ 响应式布局
    /// <summary>
    /// 宽屏（≥900px）三栏并排；窄屏（<900px，手机竖屏）改纵向堆叠。
    /// 由视图在 SizeChanged 时设置（Avalonia 无内置媒体查询）。
    /// </summary>
    [ObservableProperty] private bool _isWideLayout = true;

    /// <summary>
    /// 调试选项是否可见（#3：离线模式归入 debug 功能）。
    /// 仅当环境变量 IMGHUB_DEBUG=1 时为 true。
    /// </summary>
    public bool ShowDebugOptions =>
        string.Equals(Environment.GetEnvironmentVariable("IMGHUB_DEBUG"), "1",
                      StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 是否显示按钮图标。
    ///
    /// ⚠️ v0.5.37：**已不再是 UI 开关** —— 图标改为内嵌字体（跨平台安全），
    ///   设置里的复选框已删除。字段保留是因为 `AppConfig.UseIcons` 是
    ///   config.json 与 legacy Python 的 1:1 契约（删了会破坏互通，见 NOTICE.md 的兼容策略）。
    ///   现在它只作为"读取旧配置 + 原样写回"的透传字段，不再影响界面。
    /// </summary>
    public bool UseIcons { get; set; } = true;

    /// <summary>
    /// 按钮是否启用**鼠标悬停动画**（v5.24.0 用户要求，设置里可关）。
    /// 由 XAML 的 `Classes.hoverfx="{Binding EnableHoverAnimation}"` 控制样式类。
    /// </summary>
    [ObservableProperty] private bool _enableHoverAnimation = true;

    private bool _enableToolTips = true;
    /// <summary>
    /// 是否显示**悬停说明（ToolTip）**（v5.24.0 用户要求，设置里可关）。
    /// 关闭时把全局开关传给 <c>Imagent.App.Controls.Help.Tip</c> 附加属性，
    /// 由它把 ToolTip 置空（附加属性方案：一次生效，无需逐个按钮改绑定）。
    /// </summary>
    public bool EnableToolTips
    {
        get => _enableToolTips;
        set
        {
            if (!SetProperty(ref _enableToolTips, value)) return;
            Controls.Help.TipEnabled = value;   // 全局生效（已挂 Help.Tip 的按钮立即跟随）
        }
    }


    /// <summary>参考图是否保留（默认 false = 一次性，用完即清，避免污染后续任务）。</summary>
    [ObservableProperty] private bool _keepRefImages;
    // v0.5.36：未配置提示指向**右上角**（设置按钮在顶栏右侧；底栏的设置按钮已按用户要求去掉）
    public string SetupHint => Localizer.Instance["setup.hint"];
    public bool HasBatchResults => BatchResults.Count > 1;

    private void RefreshConfigured()
    {
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(NeedsSetup));
        OnPropertyChanged(nameof(CanRun));
        // CanRun 变化会连带影响依赖它的命令可用性
        //（否则刚配好 key，按钮仍显示禁用）
        OnPropertyChanged(nameof(CanEditWithRegions));
        // P5：配置变化（保存 key / 切 provider）后重算自检结论，保持徽标诚实
        RunSelfCheck();
    }

    public ProviderOption? SelectedProvider
    {
        get => Providers.FirstOrDefault(p => p.Provider == _provider);
        set
        {
            if (value is not null && value.Provider != _provider)
                Provider = value.Provider;
        }
    }

    private bool _switchingProvider;
    /// <summary>切换前记下的 provider / model（用于把"上一个端点"的参数存进**它自己**的槽位）。</summary>
    private ApiProvider? _presetProviderBeforeSwitch;
    partial void OnProviderChanged(ApiProvider value)
    {
        if (_switchingProvider) return;
        _switchingProvider = true;
        try
        {
            OnPropertyChanged(nameof(ProviderLabel));
            OnPropertyChanged(nameof(SelectedProvider));
            OnPropertyChanged(nameof(KeyHint));
            OnPropertyChanged(nameof(IsApimartProvider));
            OnPropertyChanged(nameof(IsOpenRouterProvider));
            OnPropertyChanged(nameof(IsOpenAiProvider));
            OnPropertyChanged(nameof(IsDashScopeProvider));
            OnPropertyChanged(nameof(IsJimengProvider));
            OnPropertyChanged(nameof(NeedsSecretKey));
            OnPropertyChanged(nameof(IsJimengExtractMode));
            OnPropertyChanged(nameof(HasResolutionTier));
            OnPropertyChanged(nameof(SupportsInputFidelity));
            OnPropertyChanged(nameof(IsDallE3Model));
            OnPropertyChanged(nameof(MaxRefs));
            OnPropertyChanged(nameof(BackgroundOptionsForModel));
            OnPropertyChanged(nameof(AspectOptions));
            OnPropertyChanged(nameof(ResolutionOptions));
            OnPropertyChanged(nameof(QualityOptions));
            OnPropertyChanged(nameof(OutputFormatOptions));
            OnPropertyChanged(nameof(RegionEditTip));
            OnPropertyChanged(nameof(GenerateTip));
            OnPropertyChanged(nameof(EditTip));
            _sess.Config.Provider = value.Key();
            // ① 先把"离开的那个 provider+模型"的参数存进**它自己**的槽位。
            //    ⚠️ 必须用 _presetProviderBeforeSwitch / _presetModelBeforeSwitch：
            //       进到这个回调时 _provider 与 _model 都**已经是新值**了
            //       （ObservableProperty 的 setter 先赋值再回调），
            //       直接调 SaveParamPreset() 会把旧参数写进新端点的槽位 → 覆盖掉要复原的值。
            SaveParamPresetFor(_presetProviderBeforeSwitch ?? value, _presetModelBeforeSwitch);
            var nextModel = Catalog.ModelMatchesProvider(Model, value)
                ? Model : Catalog.DefaultModel(value);
            if (string.IsNullOrEmpty(nextModel)) nextModel = Catalog.DefaultModel(value);
            // 赋值会触发 OnModelChanged；那里看到 _switchingProvider 就只更新跟踪变量，
            // 复原/收窄统一在本方法末尾做（避免两处重复执行）。
            Model = nextModel;
            // ② 复原"要进入的 provider+模型"上次用的参数（没有快照则保持现值）
            bool restored = RestoreParamPreset(value, Model);
            if (!Catalog.QualitySupported(value, Quality, Model))
                Quality = Catalog.QualityChoices(value, Model)[0];
            // ③ **最后**统一收窄到该模型真正支持的取值（顺序不能颠倒，见 RestoreParamPreset 注释）
            ClampParamsToCapabilities(value, Model);
            RebuildModelChoices();
            RebuildQualityOptions();
            UpdateEstimate();
            _presetProviderBeforeSwitch = value;
            _presetModelBeforeSwitch = Model;
            // ④ 把新端点的当前参数也存一份，这样下次切回来能复原
            SaveParamPreset();
            if (restored) Log($"已复原 {value.Label()} · {Model} 的上次参数", MessageLevel.Info);
            // 润色的自动风格跟随生图 provider（用户手动指定过则不覆盖）
            _svc.Polish.ActiveStyle = value;
            // 走工厂（不是直接 new）：测试可注入假实现来验证切换后的请求内容
            // ⚠️ baseUrl 必须按**新 provider** 取：千问的业务空间专属域名与 OpenAI 官方
            //    端点完全不同，沿用上一个 provider 的端点会打到错误的服务上。
            _svc.ImageApi = _svc.ImageApiFactory(value, _svc.Http,
                                                 _sess.BaseUrlFor(value));
            BaseUrlInput = _sess.BaseUrlFor(value);
            WireSubmitProgress();
            WirePartialImage();
            PersistConfig();
            ProviderCost = _sess.CostForProvider(value);
            // 需求④：切换端点后**重新进行就绪检查** —— 每个端点有各自的 key 文件，
            // 切过去可能立刻变成"未配置"，徽标/按钮可用性必须跟着变（否则会显示
            // 「就绪」却点不动生成按钮，或反过来）。
            RefreshConfigured();
            Log($"Provider → {value.Label()} · 模型 → {Model}", MessageLevel.Ok);
        }
        finally { _switchingProvider = false; }
    }

    partial void OnQualityChanged(string value) { NotifyDropdownIndices(); UpdateEstimate(); PersistConfig(); }
    partial void OnResolutionChanged(string value) { NotifyDropdownIndices(); UpdateEstimate(); PersistConfig(); }
    partial void OnBatchNChanged(int value) { UpdateEstimate(); PersistConfig(); }
    partial void OnAspectChanged(string value) { NotifyDropdownIndices(); PersistConfig(); }
    partial void OnOutputFormatChanged(string value) { NotifyDropdownIndices(); PersistConfig(); }
    partial void OnBackgroundChanged(string value) { ValidateBackgroundChoice(); PersistConfig(); }
    partial void OnOutputCompressionChanged(int value) => PersistConfig();
    partial void OnModerationChanged(string value) => PersistConfig();
    partial void OnSeedChanged(string value) => PersistConfig();
    // v0.5.31 新增参数：变化即落盘
    partial void OnPartialImagesChanged(int value) => PersistConfig();
    partial void OnInputFidelityChanged(string value) => PersistConfig();
    partial void OnStyleChanged(string value) => PersistConfig();
    partial void OnNegativePromptChanged(string value) => PersistConfig();
    partial void OnPromptExtendChanged(bool value) => PersistConfig();
    partial void OnPromptExtendModeChanged(string value) => PersistConfig();
    partial void OnWatermarkChanged(bool value) => PersistConfig();

    /// <summary>
    /// 把「画幅 / 分辨率 / 格式 / 批量张数」收窄到当前 provider + 模型真正支持的范围。
    ///
    /// ⚠️ 为什么需要（v5.28.0 待办 1/2）：
    ///   这些参数的可用集合**随模型不同**（实测 `GET /api/v1/images/models`）：
    ///     · gemini 图像系：n 上限 1、分辨率含 512、比例含 1:4/8:1
    ///     · gpt-image 系：n 上限 10、比例仅 9 种、无 512
    ///     · gemini-3.1-flash-lite-image：分辨率**仅 1K**
    ///   若只按 provider 给固定选项，用户选了服务端不支持的值会在提交时被拒，
    ///   而错误来自服务端，用户看不出是哪一项不合规。
    ///   这里在**切换 provider / 切换模型**时统一收窄：不匹配就回退到安全值并提示。
    /// </summary>
    private void ClampParamsToCapabilities(ApiProvider provider, string? model)
    {
        // ⚠️ 画幅判据必须用「**能否在下拉里选中**」（纯集合判定），不能用 `AspectOrSizeSupported`。
        //    后者对 APIMart / OpenRouter 会放行任意像素串（协议确实支持），
        //    但这两个 provider 的下拉**只列比例名** → 值"合法"却选不中 →
        //    ComboBox 的 SelectedIndex 变成 -1 → **画幅空白**（v0.5.31 实测回归，
        //    从 OpenAI 的 1024x1024 切到 APIMart 时必现）。
        //    收窄的目标就是"让下拉有选中值"，所以判据必须与下拉同一真源。
        var aspectChoices = Catalog.AspectChoicesFor(provider, model);
        if (!aspectChoices.Contains(Aspect))
        {
            // ⚠️ 回退目标取**该 provider 自己的**合法集合首项，不能硬编码 "1:1"：
            //    OpenAI 官方 / 千问的画幅是像素串（如 "1024x1024"），"1:1" 对它们非法。
            var fallback = aspectChoices.Length > 0 ? aspectChoices[0] : "1:1";
            Log($"模型不支持画幅 {Aspect} → 已回退 {fallback}", MessageLevel.Warn);
            Aspect = fallback;
        }

        var resChoices = Catalog.ResolutionChoicesFor(provider, model);
        if (!resChoices.Contains(Resolution))
        {
            // 需求：不一致时**默认选第一个元素**（不再优先 "1k"）
            var fallback = resChoices.Length > 0 ? resChoices[0] : "1k";
            Log($"模型不支持分辨率 {Resolution} → 已回退 {fallback}", MessageLevel.Warn);
            Resolution = fallback;
        }

        // 输出格式：不一致时回退到该模型的**第一个**合法格式
        var fmtChoices = Catalog.OutputFormatChoices(provider, model);
        if (!fmtChoices.Contains(OutputFormat))
            OutputFormat = fmtChoices.Length > 0 ? fmtChoices[0] : "png";

        // 质量档随模型变化（dall-e-3 是 standard/hd，与 GPT Image 系完全不同）→ 收窄
        if (!Catalog.QualitySupported(provider, Quality, model))
        {
            var qFallback = Catalog.QualityChoices(provider, model)[0];
            Log($"模型不支持质量档 {Quality} → 已回退 {qFallback}", MessageLevel.Warn);
            Quality = qFallback;
        }

        // GPT Image 2/2.5 不支持 background=transparent（文档明确会报错）→ 立刻回退
        if (!Catalog.SupportsTransparentBackground(provider, model) &&
            string.Equals(Background, "transparent", StringComparison.OrdinalIgnoreCase))
        {
            Log("该模型不支持 background=transparent → 已回退 auto", MessageLevel.Warn);
            Background = "auto";
        }
        OnPropertyChanged(nameof(BackgroundOptionsForModel));

        int maxN = Catalog.MaxNFor(provider, model);
        if (BatchN > maxN)
        {
            Log($"模型单次最多 {maxN} 张 → 批量已从 {BatchN} 收窄为 {maxN}", MessageLevel.Warn);
            BatchN = maxN;
        }

        OnPropertyChanged(nameof(BatchNMax));
        OnPropertyChanged(nameof(BatchNLimited));
        OnPropertyChanged(nameof(BatchNHint));
        OnPropertyChanged(nameof(QualityOptions));
        OnPropertyChanged(nameof(AspectOptions));
        OnPropertyChanged(nameof(ResolutionOptions));
        OnPropertyChanged(nameof(OutputFormatOptions));
        OnPropertyChanged(nameof(SupportsInputFidelity));
        OnPropertyChanged(nameof(IsDallE3Model));
        OnPropertyChanged(nameof(MaxRefs));
        // ⚠️ 选项集合变了 → 必须刷新各下拉的选中索引，否则 ComboBox 会保留旧的
        //    SelectedIndex 指向已不存在的项（表现为"画幅"空白，见 NotifyDropdownIndices 说明）
        NotifyDropdownIndices();
    }

    /// <summary>
    /// 校验 background 与 output_format 的组合（文档：transparent 只支持 png/webp）。
    /// 不合法时**立即回退**并在消息面板说明原因 —— 否则要等提交后才 400。
    /// </summary>
    private void ValidateBackgroundChoice()
    {
        if (Catalog.ValidateBackground(_background, _outputFormat) is not { } why) return;
        Log(why + " —— 已把背景回退为 auto", MessageLevel.Warn);
        Background = "auto";
    }
    partial void OnOfflineChanged(bool value)
    {
        _sess.Config.Offline = value;
        PersistConfig();
        RefreshConfigured();
    }
    partial void OnPolishEnabledChanged(bool value)
    {
        _sess.Config.PolishEnabled = value;
        _svc.Polish.SwitchOn = value;
        PersistConfig();
    }
    private string? _presetModelBeforeSwitch;
    partial void OnModelChanged(string value)
    {
        _sess.Config.Model = value;
        // ⚠️ provider 切换会顺带改 Model（走同一个 setter）→ 此时这里**只更新跟踪变量**，
        //    保存旧快照 / 复原新快照 / 收窄 全部交给 OnProviderChanged 统一做。
        //    否则两处都会执行：① 重复收窄；② 用已经变过的 _provider 存快照 → 存错槽位。
        if (_switchingProvider)
        {
            _presetModelBeforeSwitch = value;
            return;
        }

        // ① 切模型前把"上一个模型"的参数存进它自己的槽位。
        //    ⚠️ 必须用 _presetModelBeforeSwitch（上一次的模型名）：进到这个回调时
        //       value 已经是新模型，用它去存会把旧参数写到新模型的槽位上。
        if (!string.IsNullOrWhiteSpace(_presetModelBeforeSwitch) &&
            !string.Equals(_presetModelBeforeSwitch, value, StringComparison.OrdinalIgnoreCase))
            SaveParamPresetFor(_provider, _presetModelBeforeSwitch!);

        // ② 复原「新模型」上次用的参数（没有快照则保持现值）
        bool restored = RestoreParamPreset(_provider, value);

        if (!Catalog.QualitySupported(_provider, _quality, value))
        {
            var old = _quality;
            Quality = Catalog.QualityChoices(_provider, value)[0];
            Log($"模型不支持 {old}，质量已回退 {Quality}", MessageLevel.Warn);
        }
        // 模型变了 → 画幅/分辨率/张数上限也可能变（如切到 gemini 图像系：n 只能 1）
        // ⚠️ 顺序：先复原快照再收窄（见 RestoreParamPreset 注释）
        ClampParamsToCapabilities(_provider, value);
        RebuildQualityOptions();
        UpdateEstimate();     // 模型变了 → 预估按新模型统计重算（可能变'暂无'）
        _presetModelBeforeSwitch = value;
        // ③ 把**新模型**的当前参数也存一份，这样下次切回来能复原
        SaveParamPreset();
        if (restored) Log($"已复原 {Model} 的上次参数", MessageLevel.Info);
        PersistConfig();
    }
    partial void OnToolIndexChanged(int value)
    {
        AppLog.Debug($"标注工具切换 → index={value}（{CanvasTool}）", where: "MainViewModel.OnToolIndexChanged");
        OnPropertyChanged(nameof(CanvasTool));
        OnPropertyChanged(nameof(ToolUsesBrushSize));   // 需求⑥：方框/圆圈隐藏粗细条
    }

    partial void OnBrushSizeChanged(double value) => OnPropertyChanged(nameof(BrushSizeText));
    partial void OnPolishIndexChanged(int value) => OnPropertyChanged(nameof(PolishCounterText));

    // ================================================================ 按端点+模型记参数（v0.5.31）
    /// <summary>
    /// 把当前参数存进「该 provider + 模型」的快照槽位。
    ///
    /// ⚠️ 为什么键要带模型：同一 provider 下不同模型的合法值差别很大
    ///   （OpenAI 的 dall-e-3 只有 standard/hd、固定 3 个尺寸），
    ///   只按 provider 记会让"切模型"时复原出该模型不支持的值。
    /// ⚠️ 只存"随模型变化的参数"：prompt / 参考图 / 标注属一次性内容，不进快照。
    /// </summary>
    private void SaveParamPreset() => SaveParamPresetFor(_provider, _model);

    /// <summary>把当前参数存进指定 provider+模型的槽位（切模型时需要存"上一个"模型）。</summary>
    private void SaveParamPresetFor(ApiProvider provider, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return;
        var key = AppConfig.PresetKey(provider, model);
        _sess.Config.ParamPresets[key] = new AppConfig.ParamPreset
        {
            Quality = _quality,
            Aspect = _aspect,
            Resolution = _resolution,
            OutputFormat = _outputFormat,
            N = _batchN,
            Background = _background,
            OutputCompression = _outputCompression,
            Moderation = _moderation,
            Seed = _seed,
            Stream = _stream,
            PartialImages = _partialImages,
            InputFidelity = _inputFidelity,
            Style = _style,
            NegativePrompt = _negativePrompt,
            PromptExtend = _promptExtend,
            PromptExtendMode = _promptExtendMode,
            Watermark = _watermark,
        };
    }

    /// <summary>
    /// 复原「该 provider + 模型」上次用的参数。
    ///
    /// ⚠️ 顺序很关键：**先**用快照覆盖，**再**由 <see cref="ClampParamsToCapabilities"/>
    ///   收窄到该模型真正支持的取值。反过来（先收窄再覆盖）会把快照里的值绕过校验塞回去，
    ///   导致服务端收下不支持的值 → 400。
    /// ⚠️ 没有快照时保持现值不动（首次用该模型 → 由调用方的默认/收窄决定），
    ///   绝不能"没有就清空"，否则用户切回来会发现参数被重置。
    /// </summary>
    /// <returns>是否真的复原了快照（用于 UI 提示）。</returns>
    private bool RestoreParamPreset(ApiProvider provider, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var key = AppConfig.PresetKey(provider, model);
        if (!_sess.Config.ParamPresets.TryGetValue(key, out var p) || p is null) return false;

        if (!string.IsNullOrWhiteSpace(p.Quality)) Quality = p.Quality;
        if (!string.IsNullOrWhiteSpace(p.Aspect)) Aspect = p.Aspect;
        if (!string.IsNullOrWhiteSpace(p.Resolution)) Resolution = p.Resolution;
        if (!string.IsNullOrWhiteSpace(p.OutputFormat)) OutputFormat = p.OutputFormat;
        if (p.N > 0) BatchN = p.N;
        Background = p.Background;
        OutputCompression = p.OutputCompression;
        Moderation = p.Moderation;
        Seed = p.Seed;
        Stream = p.Stream;
        if (p.PartialImages > 0) PartialImages = p.PartialImages;
        InputFidelity = p.InputFidelity;
        Style = p.Style;
        NegativePrompt = p.NegativePrompt;
        if (p.PromptExtend is { } pe) PromptExtend = pe;
        PromptExtendMode = p.PromptExtendMode;
        Watermark = p.Watermark;
        return true;
    }

    // ================================================================ 节流持久化
    private void PersistConfig()
    {
        _sess.Config.Model = _model;
        _sess.Config.Quality = _quality;
        _sess.Config.Aspect = _aspect;
        _sess.Config.Resolution = _resolution;
        _sess.Config.OutputFormat = _outputFormat;
        _sess.Config.BatchN = _batchN;
        _sess.Config.Offline = _offline;
        _sess.Config.PolishEnabled = _polishEnabled;
        _sess.Config.Provider = _provider.Key();
        // v5.27.0：文档对齐参数一并持久化（否则重启后悄悄回到默认值）
        _sess.Config.Background = _background;
        _sess.Config.OutputCompression = _outputCompression;
        _sess.Config.Moderation = _moderation;
        _sess.Config.Seed = _seed;
        _sess.Config.Stream = _stream;
        _sess.Config.ProviderOnly = _providerOnly;
        _sess.Config.ProviderIgnore = _providerIgnore;
        _sess.Config.ProviderOrder = _providerOrder;
        _sess.Config.ProviderSort = _providerSort;
        _sess.Config.AllowFallbacks = _allowFallbacks;
        // v0.5.31 新增参数同样落盘（OpenAI 官方 / 千问）
        _sess.Config.PartialImages = _partialImages;
        _sess.Config.InputFidelity = _inputFidelity;
        _sess.Config.Style = _style;
        _sess.Config.NegativePrompt = _negativePrompt;
        _sess.Config.PromptExtend = _promptExtend;
        _sess.Config.PromptExtendMode = _promptExtendMode;
        _sess.Config.Watermark = _watermark;
        // v0.5.31：即梦参数
        _sess.Config.JimengScale = _jimengScale;
        _sess.Config.JimengForceSingle = _jimengForceSingle;
        _sess.Config.JimengLoraWeight = _jimengLoraWeight;
        _sess.Config.JimengReturnUrl = _jimengReturnUrl;
        _sess.Config.JimengLogoEnabled = _jimengLogoEnabled;
        _sess.Config.JimengLogoPosition = _jimengLogoPosition;
        _sess.Config.JimengLogoLanguage = _jimengLogoLanguage;
        _sess.Config.JimengLogoOpacity = _jimengLogoOpacity;
        _sess.Config.JimengLogoText = _jimengLogoText;
        lock (_persistLock)
        {
            _persistPending = true;
            _persistTimer ??= new Timer(_ =>
            {
                lock (_persistLock)
                {
                    if (!_persistPending) return;
                    _persistPending = false;
                }
                try { _sess.SaveConfig(); }
                catch (Exception ex) { AppLog.Debug("延迟保存配置失败（不影响使用）", ex, where: "MainViewModel.SaveConfigDebounced"); }
            });
            _persistTimer.Change(120, Timeout.Infinite);
        }
    }
    private readonly object _persistLock = new();
    private Timer? _persistTimer;
    private bool _persistPending;

    public void FlushConfig()
    {
        lock (_persistLock)
        {
            _persistPending = false;
            _persistTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        try { _sess.SaveConfig(); }
        catch (Exception ex) { AppLog.Debug("保存配置失败（不影响使用）", ex, where: "MainViewModel.FlushConfig"); }
    }

    /// <summary>
    /// 价格预估（#10）：
    ///   · 该 provider+模型 **用过** → 按**历史平均单次花费** × 批量数；
    ///   · **没用过** → 不显示预估（避免按错表误导）。
    /// </summary>
    private void UpdateEstimate()
    {
        var model = Model ?? "";
        var provider = Provider.Key();

        if (string.IsNullOrWhiteSpace(model))
        {
            EstimatedCostText = "";
            return;
        }

        var avg = _svc.ModelStats.GetAvgCost(provider, model);
        if (avg is null)
        {
            // 没用过该模型 → 不显示（避免误导）
            EstimatedCostText = "该模型暂无历史花费记录，生成一次后显示预估";
            return;
        }

        var cost = avg.Value * Math.Max(1, BatchN);
        // v5.28.0：文案精简（原版含模型名 + "历史平均 X/次"，在左栏会被裁切成半截）
        //         模型名在左栏下拉框已可见，此处不必重复。
        EstimatedCostText = $"预估 ≈ ${cost:0.0000}（{_batchN} 张 · 均价 ${avg:0.0000}/次）";
    }

    // ================================================================ 生成
    [RelayCommand] private async Task GenerateAsync() => await RunGenerationAsync(false);
    [RelayCommand] private async Task EditAsync() => await RunGenerationAsync(true);

    private async Task RunGenerationAsync(bool editMode)
    {
        if (Busy) return;
        if (!CanRun) { Log("尚未配置 API key", MessageLevel.Warn); return; }
        var prompt = (Prompt ?? "").Trim();

        if (editMode)
        {
            if (CurrentItem is null) { Log("还没有可编辑的图", MessageLevel.Warn); return; }
            if (prompt.Length == 0) { Log("请输入编辑要求", MessageLevel.Warn); return; }
        }
        else if (prompt.Length == 0) { Log("请输入提示词", MessageLevel.Warn); return; }

        _sess.PushPromptHistory(prompt, editMode ? "edit" : "gen");
        RefreshPromptHistory();

        // ⚠️ v5.27.0 修复①：参考图在**生成**时此前被整个丢弃（refs 恒为 null）
        //    → 用户上传的参考图完全没进请求，出图自然"毫不相关"。
        //    现在两种模式都带上参考图，且**编辑时把编辑目标放在第 1 位**
        //    （文档：模型按位置理解参考图，第 1 位 = 主体，放错主体就变）。
        var refs = await CollectRefsAsync(editMode);
        if (refs.Count == 0 && editMode)
        {
            Log("找不到要编辑的图片文件", MessageLevel.Err);
            return;
        }

        // 蒙版：仅在用户圈画过区域且编辑目标存在时附带
        var mask = await LoadMaskAsync();

        Busy = true;
        Status = editMode ? "编辑中…" : "生成中…";
        ClearAndDispose(BatchResults);   // P0-2：先释放旧缩略图（本次会重填）
        OnPropertyChanged(nameof(HasBatchResults));
        FlushConfig();
        var t0 = DateTime.UtcNow;
        try
        {
            var progress = new Progress<string>(msg => Log(msg, MessageLevel.Info));
            var key = LoadApiKey();
            var req = BuildRequest(prompt, key, refs, mask, progress);
            var res = await _svc.ImageApi.GenerateAsync(req);
            await LandResultsAsync(res, prompt, editMode, t0);
            // 参考图默认「一次性」：用完即清，避免污染后续无关任务
            if (refs.Count > 0 && !KeepRefImages)
            {
                ClearAndDispose(RefImages);   // P0-2：释放参考图缩略图
                OnPropertyChanged(nameof(GenerateTip));
                OnPropertyChanged(nameof(EditTip));
                Log("参考图已用完并清空（如需复用请重新添加）", MessageLevel.Info);
            }
        }
        catch (Exception ex)
        {
            // 写清位置与完整异常链（AOT 反射错误、网络错误都能在这里看到根因）
            AppLog.Error(ex.Message,
                         where: $"MainViewModel.RunGenerationAsync(edit={editMode})", ex: ex);
            Log($"生成失败：{ex.Message}", MessageLevel.Err,
                where: "MainViewModel.RunGenerationAsync");
            foreach (var line in ErrorHints.Explain(ex).Skip(1)) Log(line.Trim(), MessageLevel.Dim);
            Prompt = prompt;
        }
        finally
        {
            Busy = false;
            Status = Offline ? "离线" : SelfCheckText;   // P5 就绪 = 自检结论
        }
    }

    /// <summary>
    /// 组装统一的请求参数（**唯一入口** —— 生成/编辑/标注编辑/重试都走它，
    /// 保证两 provider 的参数集合完全一致，不会出现"某个入口漏传参数"）。
    /// </summary>
    private GenRequest BuildRequest(string prompt, string key,
                                    List<(byte[], string)> refs, byte[]? mask,
                                    IProgress<string>? progress = null,
                                    CancellationToken ct = default)
        => new()
        {
            Prompt = prompt,
            ApiKey = key,
            Model = _model,
            Quality = _quality,
            Aspect = _aspect,
            N = Math.Max(1, _batchN),
            Refs = refs.Count > 0 ? refs : null,
            Mask = mask,
            Offline = Offline,
            Step = _sess.Counter,
            Resolution = _resolution,
            OutputFormat = _outputFormat,
            Background = _background,
            OutputCompression = _outputCompression,
            Moderation = _moderation,
            Seed = _seed,
            Stream = _stream,
            // v0.5.31 新增（OpenAI 官方 / 千问）
            PartialImages = Math.Clamp(_partialImages, 0, 3),
            InputFidelity = _inputFidelity,
            Style = _style,
            NegativePrompt = _negativePrompt,
            PromptExtend = _promptExtend,
            PromptExtendMode = _promptExtendMode,
            Watermark = _watermark,
            // 即梦（火山引擎）
            ApiSecret = Provider == ApiProvider.Jimeng ? LoadSecret() : "",
            JimengScale = IsJimengProvider ? _jimengScale : null,
            JimengForceSingle = IsJimengProvider && _jimengForceSingle,
            JimengExtractPrompt = IsJimengProvider ? _jimengExtractPrompt : "",
            JimengLoraWeight = IsJimengProvider ? _jimengLoraWeight : null,
            JimengReturnUrl = _jimengReturnUrl,
            JimengLogo = _jimengLogoEnabled
                ? new JimengLogoInfo
                {
                    AddLogo = true, Position = _jimengLogoPosition,
                    Language = _jimengLogoLanguage, Opacity = _jimengLogoOpacity,
                    TextContent = _jimengLogoText,
                }
                : null,
            Routing = BuildRouting(),
            Progress = progress,
            Ct = ct,
        };

    /// <summary>把 UI 上的 provider 路由开关转成请求参数（仅 OpenRouter 有意义）。</summary>
    private ProviderRouting? BuildRouting()
    {
        var only = SplitList(_providerOnly);
        var ignore = SplitList(_providerIgnore);
        var order = SplitList(_providerOrder);
        var r = new ProviderRouting
        {
            Only = only, Ignore = ignore, Order = order,
            Sort = string.IsNullOrWhiteSpace(_providerSort) ? null : _providerSort.Trim(),
            AllowFallbacks = _allowFallbacks,
        };
        return r.IsEmpty ? null : r;
    }

    private static List<string>? SplitList(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        return parts.Count > 0 ? parts : null;
    }

    /// <summary>
    /// 收集参考图：**编辑时把「当前预览的图」放在第 1 位**（文档：第 1 位是主体），
    /// 其余用户添加的参考图依次跟随。生成时只用用户添加的参考图。
    /// </summary>
    private async Task<List<(byte[], string)>> CollectRefsAsync(bool editMode)
    {
        var refs = new List<(byte[], string)>();
        if (editMode && CurrentItem is { } cur)
        {
            var bp = cur.Path(_sess.HomePath);
            if (File.Exists(bp))
            {
                var raw = await File.ReadAllBytesAsync(bp);
                refs.Add((raw, ImageCodec.SniffMediaType(raw) ?? "image/png"));
            }
        }
        foreach (var rf in RefImages)
        {
            if (refs.Count >= MaxRefs) break;
            var rp = rf.Item.Path(_sess.HomePath);
            if (!File.Exists(rp)) continue;
            var rb = await File.ReadAllBytesAsync(rp);
            refs.Add((rb, ImageCodec.SniffMediaType(rb) ?? "image/png"));
        }
        // ⚠️ 这里**不再**无条件 ShrinkForReference：压缩交给 Core 的 PrepareRefs
        //    按"仅在超过上传上限/长边时"决定，并与蒙版尺寸保持一致
        //    （旧版无条件压到 1024，会明显削弱主体与文字细节的保留）。
        if (refs.Count == 0 && !editMode && RefImages.Count == 0 && _sess.Items.Count == 0)
            Log("（无参考图：纯文生图）", MessageLevel.Dim);
        return refs;
    }

    /// <summary>
    /// 读取当前标注导出的蒙版（若有）。
    /// 会校验三件事，任一不满足就**明确告知并降级**（而不是发出一个静默错配的蒙版）：
    ///   ① 蒙版文件存在且非空；
    ///   ② 是**带 Alpha 通道的 PNG**（文档要求；黑白图不能用）；
    ///   ③ 仍属于当前编辑目标那张图（尺寸必须与参考图第 1 位一致）。
    /// </summary>
    private async Task<byte[]?> LoadMaskAsync()
    {
        if (string.IsNullOrEmpty(AnnotatedMaskPath) || !File.Exists(AnnotatedMaskPath))
            return null;
        try
        {
            // ③ 归属校验：蒙版对应的是另一张图 → 不能直接用（尺寸会不匹配）
            var target = CurrentItem?.Path(_sess.HomePath);
            if (_annotatedMaskFor is { Length: > 0 } owner &&
                !string.Equals(owner, target, StringComparison.OrdinalIgnoreCase))
            {
                Log("标注蒙版属于另一张图（已切换预览）→ 本次不使用蒙版通道；"
                    + "如需蒙版编辑请重新圈画", MessageLevel.Warn,
                    where: "MainViewModel.LoadMaskAsync");
                return null;
            }

            var mask = await File.ReadAllBytesAsync(AnnotatedMaskPath);
            if (mask.Length == 0) return null;
            // ② 文档：普通黑白图不能当蒙版 —— 提前挡住，避免"发出去了但没生效"
            if (!Catalog.PngHasAlpha(mask))
            {
                Log("蒙版缺少 Alpha 通道 → 已跳过（蒙版必须是透明 PNG）",
                    MessageLevel.Warn, where: "MainViewModel.LoadMaskAsync");
                return null;
            }
            // ①b 与目标图尺寸核对（服务端不预校验，必须由调用方保证）
            var maskSize = ImageCodec.Size(mask);
            var refSize = target is not null && File.Exists(target)
                ? ImageCodec.Size(await File.ReadAllBytesAsync(target)) : null;
            if (Catalog.ValidateMask(mask, maskSize, refSize, refSize is not null)
                is { } why)
            {
                Log($"蒙版校验未通过 → 本次不使用蒙版通道：{why}", MessageLevel.Warn,
                    where: "MainViewModel.LoadMaskAsync");
                return null;
            }
            return mask;
        }
        catch (Exception ex)
        {
            AppLog.Warn("读取蒙版失败", "MainViewModel.LoadMaskAsync", ex: ex);
            return null;
        }
    }

    private async Task LandResultsAsync(GenResult res, string prompt, bool editMode, DateTime t0)
    {
        int done = 0;
        foreach (var (data, media) in res.Images)
        {
            var path = SaveImageToHome(data, media, prompt, _sess.Counter + 1 + done);
            if (path is null) continue;
            var item = new Item
            {
                File = Path.GetFileName(path), Prompt = prompt,
                Kind = editMode ? "edit" : (Offline ? "offline" : "gen"),
                Model = _model, Quality = _quality,
                Cost = res.Cost / Math.Max(1, res.Images.Count),
                Tokens = res.Tokens, Provider = _provider.Key(),
            };
            _sess.Push(item); _sess.Log(item); done++;
            Log($"已保存 {item.File}", MessageLevel.Ok);
        }
        // TotalCost 是计算属性（从 Items 求和），此处无需手动累加
        TotalCost = _sess.TotalCost;
        ProviderCost = _sess.CostForProvider(_provider);
        UnknownCost = _sess.CostForUnknownProvider();
        _sess.SaveState();
        if (done > 0)
        {
            var secs = (DateTime.UtcNow - t0).TotalSeconds;
            var costText = Offline ? "" : $"，花费 ${res.Cost:0.0000}";
            // #10 记录该模型的历史花费（供价格预估）
            try
            {
                _svc.ModelStats.Record(Provider.Key(), _model, res.Cost, res.Images.Count);
            }
            catch { /* 统计失败不影响主流程 */ }
            Log($"完成 {done} 张，用时 {secs:0.0}s{costText}", MessageLevel.Ok);
            RefreshHistory();
            var batch = _sess.Items.Snapshot().Take(done).Reverse().ToList();   // P0-4：快照
            PublishBatchResults(batch);
            await ShowPreviewAsync(_sess.Current);
            Prompt = "";
        }
        else
        {
            Log("生成失败：图片未能保存", MessageLevel.Err);
            ClearAndDispose(BatchResults);   // P0-2：失败清空缩略图（先释放旧的）
            OnPropertyChanged(nameof(HasBatchResults));
        }
    }

    private string? SaveImageToHome(byte[] data, string media, string prompt, int seq)
    {
        try
        {
            var fmt = OutputFormat.ToLowerInvariant();
            var ext = "." + (fmt == "jpeg" ? "jpg" : fmt);
            var name = Session.SafeFilename($"{DateTime.Now:MMdd_HHmmss}_{seq:000}", prompt, ext, limit: 24);
            var path = Path.Combine(_sess.HomePath, name);
            var sniffed = ImageCodec.SniffMediaType(data);
            var wantMime = ImageCodec.MimeForExt(ext);
            if (!string.Equals(sniffed, wantMime, StringComparison.OrdinalIgnoreCase))
            {
                using var bmp = ImageCodec.Decode(data);
                if (bmp is not null) data = ImageCodec.Encode(bmp, wantMime);
            }
            File.WriteAllBytes(path, data);
            return path;
        }
        catch (Exception ex)
        {
                        Log($"落盘失败：{ex.Message}", MessageLevel.Err, where: "MainViewModel.SaveImageToHome", ex: ex);
            return null;
        }
    }

    // ================================================================ 导入
    public async Task ImportImageAsync(byte[] data, string media, string name)
    {
        var path = SaveImageToHome(data, media, name, _sess.Counter + 1);
        if (path is null) return;
        var item = new Item { File = Path.GetFileName(path), Prompt = name, Kind = "import" };
        _sess.Push(item);
        Log($"已导入 {item.File}", MessageLevel.Ok);
        RefreshHistory();
        await ShowPreviewAsync(item);
    }

    public async Task ImportPickedFileAsync(byte[] data, string media, string name)
        => await ImportImageAsync(data, media, name);

    public async Task AddReferenceImageAsync(byte[] data, string media, string name)
    {
        try
        {
            // ⚠️ v5.27.0：参考图必须**按原始格式原样落盘**，不能走 SaveImageToHome
            //    （后者会按 output_format 转码）。否则当输出格式是 jpeg 时，
            //    一张带透明背景的 PNG 商品图会被转成 JPEG —— Alpha 通道丢失，
            //    模型看到的主体边缘就变了，"保留主体"的效果直接打折。
            var path = SaveRefImageToHome(data, media, name);
            if (path is null) return;
            var item = new Item { File = Path.GetFileName(path), Prompt = "参考图：" + name, Kind = "import" };
            RefImages.Add(new PreviewThumb(item) { HomePath = _sess.HomePath });
            OnPropertyChanged(nameof(GenerateTip));
            OnPropertyChanged(nameof(EditTip));
            Log($"参考图已加入（{RefImages.Count} 张 · {data.Length / 1024}KB）", MessageLevel.Ok);
        }
        catch (Exception ex)
        {
                        Log($"参考图加入失败：{ex.Message}", MessageLevel.Err, where: "MainViewModel.AddReferenceImageAsync", ex: ex);
        }
    }

    /// <summary>参考图落盘：保留原始字节与格式（不做格式转码）。</summary>
    private string? SaveRefImageToHome(byte[] data, string media, string name)
    {
        try
        {
            var ext = "." + ImageCodec.ExtFor(media);
            var fileName = Session.SafeFilename(
                $"ref_{DateTime.Now:MMdd_HHmmss}", name, ext, limit: 24);
            var path = Path.Combine(_sess.HomePath, fileName);
            File.WriteAllBytes(path, data);
            return path;
        }
        catch (Exception ex)
        {
                        Log($"参考图落盘失败：{ex.Message}", MessageLevel.Err, where: "MainViewModel.SaveRefImageToHome", ex: ex);
            return null;
        }
    }

    [RelayCommand]
    private void RemoveRefImage(PreviewThumb? thumb)
    {
        if (thumb is null) return;
        RefImages.Remove(thumb);
        thumb.Dispose();   // P0-2：离开列表即释放非托管缩略图
        OnPropertyChanged(nameof(GenerateTip));
        OnPropertyChanged(nameof(EditTip));
        Log($"已移除参考图 {thumb.File}", MessageLevel.Info);
    }

    // ================================================================ 撤回
    [RelayCommand]
    private void Undo() => Safe(UndoCore, "撤回");
    /// <summary>
    /// 删除一条历史（用户需求 #11）：
    ///   · 从历史列表移除；
    ///   · **同时删除磁盘上的图片文件**（右键删除的语义 = 彻底删除）；
    ///   · 从预览/缩略图中清理。
    /// </summary>
    [RelayCommand]
    private void DeleteHistoryItem(ImgHub.Core.Models.Item item)
    {
        Safe(() =>
        {
            if (item is null) return;
            if (_sess.RemoveItem(item))
            {
                var path = item.Path(_sess.HomePath);
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) { AppLog.Debug($"删除历史图片文件失败：{path}", ex, where: "MainViewModel.RemoveHistoryItem"); }
                TotalCost = _sess.TotalCost;
                ProviderCost = _sess.CostForProvider(_provider);
                _sess.SaveState();
                Log($"已删除 {item.File}（含文件）", MessageLevel.Ok);
                RefreshHistory();
            }
        }, "删除历史");
    }

    /// <summary>仅从历史列表移除（保留磁盘文件）。轻量删除。</summary>
    [RelayCommand]
    private void DeleteListItem(ImgHub.Core.Models.Item item)
    {
        Safe(() =>
        {
            if (item is null) return;
            if (_sess.RemoveItem(item))
            {
                TotalCost = _sess.TotalCost;
                ProviderCost = _sess.CostForProvider(_provider);
                _sess.SaveState();
                Log($"已从列表移除 {item.File}（文件保留）", MessageLevel.Ok);
                RefreshHistory();
            }
        }, "移除历史项");
    }

    /// <summary>删除一条提示词历史（用户需求 #11）。</summary>
    [RelayCommand]
    private void DeletePromptHistory(string prompt)
    {
        Safe(() =>
        {
            _sess.RemovePromptHistory(prompt);
            RefreshPromptHistory();
            Log("已删除该提示词记录", MessageLevel.Ok);
        }, "删除提示词历史");
    }

    // ================================================================ 确认浮层（P11）
    /// <summary>破坏性操作前的确认浮层（删除文件不可恢复）。</summary>
    [ObservableProperty] private bool _confirmOpen;
    [ObservableProperty] private string _confirmTitle = "";
    [ObservableProperty] private string _confirmMessage = "";
    [ObservableProperty] private string _confirmOkText = "确认删除";
    [ObservableProperty] private bool _confirmDanger = true;

    /// <summary>浮层确认后要执行的动作（null = 无待执行动作）。</summary>
    private Action? _confirmAction;

    /// <summary>打开确认浮层；<paramref name="onOk"/> 在用户点「确认」时执行。</summary>
    public void AskConfirm(string title, string message, Action onOk,
                           string okText = "确认删除")
    {
        ConfirmTitle = title;
        ConfirmMessage = message;
        ConfirmOkText = okText;
        _confirmAction = onOk;
        ConfirmOpen = true;
    }

    [RelayCommand]
    private void ConfirmOk()
    {
        ConfirmOpen = false;
        var act = _confirmAction;
        _confirmAction = null;
        Safe(() => act?.Invoke(), "确认操作");
    }

    [RelayCommand]
    private void ConfirmCancel()
    {
        ConfirmOpen = false;
        _confirmAction = null;
        Log("已取消", MessageLevel.Info);
    }

    // ================================================================ 多选批量（P12）
    /// <summary>历史列表多选模式（默认关，防误触）。</summary>
    [ObservableProperty] private bool _historyMultiSelect;
    /// <summary>提示词历史多选模式。</summary>
    [ObservableProperty] private bool _promptMultiSelect;

    public string HistoryMultiButtonText => HistoryMultiSelect
        ? Localizer.Instance["common.multiDone"] : Localizer.Instance["common.multi"];
    public string PromptMultiButtonText => PromptMultiSelect
        ? Localizer.Instance["common.multiDone"] : Localizer.Instance["common.multi"];

    /// <summary>P12：多选模式下 ListBox 用 Multiple，否则保持单选（点击即切换预览）。</summary>
    public Avalonia.Controls.SelectionMode HistorySelectionMode =>
        HistoryMultiSelect ? Avalonia.Controls.SelectionMode.Multiple
                           : Avalonia.Controls.SelectionMode.Single;
    public Avalonia.Controls.SelectionMode PromptSelectionMode =>
        PromptMultiSelect ? Avalonia.Controls.SelectionMode.Multiple
                          : Avalonia.Controls.SelectionMode.Single;

    partial void OnHistoryMultiSelectChanged(bool value)
    {
        OnPropertyChanged(nameof(HistoryMultiButtonText));
        OnPropertyChanged(nameof(HistorySelectionMode));
        Log(value ? "历史：多选模式已开启" : "历史：多选模式已关闭", MessageLevel.Info);
    }

    partial void OnPromptMultiSelectChanged(bool value)
    {
        OnPropertyChanged(nameof(PromptMultiButtonText));
        OnPropertyChanged(nameof(PromptSelectionMode));
        Log(value ? "提示词历史：多选模式已开启" : "提示词历史：多选模式已关闭", MessageLevel.Info);
    }

    /// <summary>批量「仅从列表移除」（保留文件）。</summary>
    public void BatchRemoveFromList(IReadOnlyList<HistoryRow> rows)
    {
        if (rows.Count == 0) { Log("未选中任何项", MessageLevel.Warn); return; }
        Safe(() =>
        {
            int n = 0;
            foreach (var row in rows)
                if (_sess.RemoveItem(row.Item)) n++;
            _sess.SaveState();
            RefreshHistory();
            TotalCost = _sess.TotalCost;
            ProviderCost = _sess.CostForProvider(_provider);
            Log($"已从列表移除 {n} 项（文件保留）", MessageLevel.Ok);
        }, "批量移除");
    }

    /// <summary>批量「删除（含文件）」—— 调用前必须先经确认浮层。</summary>
    public void BatchDeleteWithFiles(IReadOnlyList<HistoryRow> rows)
    {
        if (rows.Count == 0) { Log("未选中任何项", MessageLevel.Warn); return; }

        // 破坏性操作：先弹确认浮层（P11）
        var files = rows.Select(r => r.File).ToList();
        AskConfirm(
            "确认完全删除",
            $"将完全删除 {rows.Count} 个图片文件（不可恢复）：\n" +
            string.Join("\n", files.Take(8)) + (files.Count > 8 ? "\n…" : ""),
            () => Safe(() =>
            {
                int n = 0;
                foreach (var row in rows)
                {
                    if (!_sess.RemoveItem(row.Item)) continue;
                    n++;
                    try
                    {
                        var p = row.Item.Path(_sess.HomePath);
                        if (File.Exists(p)) File.Delete(p);
                    }
                    catch { /* 单文件删除失败不阻断其余 */ }
                }
                _sess.SaveState();
                RefreshHistory();
                TotalCost = _sess.TotalCost;
                ProviderCost = _sess.CostForProvider(_provider);
                ClearRegionCache();
                Log($"已完全删除 {n} 项（含文件）", MessageLevel.Ok);
            }, "批量删除"),
            okText: $"完全删除 {rows.Count} 项");
    }

    /// <summary>批量删除提示词历史 —— 调用前必须先经确认浮层。</summary>
    public void BatchDeletePromptHistory(IReadOnlyList<string> prompts)
    {
        if (prompts.Count == 0) { Log("未选中任何项", MessageLevel.Warn); return; }
        AskConfirm(
            "确认删除提示词记录",
            $"将删除 {prompts.Count} 条提示词历史（不可恢复）：\n" +
            string.Join("\n", prompts.Take(8).Select(p => "· " + Truncate(p, 40))) +
            (prompts.Count > 8 ? "\n…" : ""),
            () => Safe(() =>
            {
                int n = 0;
                foreach (var p in prompts)
                {
                    _sess.RemovePromptHistory(p);
                    n++;
                }
                RefreshPromptHistory();
                Log($"已删除 {n} 条提示词记录", MessageLevel.Ok);
            }, "批量删除提示词"),
            okText: $"删除 {prompts.Count} 条");
    }

    /// <summary>单条删除（含文件）—— 先弹确认浮层（P11）。由右键菜单调用。</summary>
    public void RequestDeleteHistory(HistoryRow row)
        => AskConfirm(
            "确认完全删除",
            $"将完全删除该图片文件（不可恢复）：\n{row.File}",
            () => Safe(() =>
            {
                if (_sess.RemoveItem(row.Item))
                {
                    try
                    {
                        var p = row.Item.Path(_sess.HomePath);
                        if (File.Exists(p)) File.Delete(p);
                    }
                    catch (Exception ex) { AppLog.Debug("清空批量结果时删除文件失败", ex, where: "MainViewModel.ClearBatchFiles"); }
                    _sess.SaveState();
                    RefreshHistory();
                    TotalCost = _sess.TotalCost;
                    ProviderCost = _sess.CostForProvider(_provider);
                    ClearRegionCache();
                    Log($"已删除 {row.File}（含文件）", MessageLevel.Ok);
                }
            }, "删除历史"),
            okText: "完全删除");

    /// <summary>单条提示词删除 —— 先弹确认浮层。由右键菜单调用。</summary>
    public void RequestDeletePrompt(string prompt)
        => AskConfirm(
            "确认删除提示词记录",
            $"将删除该条提示词历史（不可恢复）：\n{Truncate(prompt, 60)}",
            () => Safe(() =>
            {
                _sess.RemovePromptHistory(prompt);
                RefreshPromptHistory();
                Log("已删除该提示词记录", MessageLevel.Ok);
            }, "删除提示词历史"),
            okText: "删除");

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private void UndoCore()
    {
        // P0-4：用原子的 TryTakeFirst —— 「判空 → 取 [0] → RemoveAt(0)」三步之间没有锁，
        // 并发写入（ResumePending 落盘）时可能取到别人刚插入的项或越界。
        var removed = _sess.Items.TryTakeFirst();
        if (removed is null) { Log("没有可撤回的图", MessageLevel.Warn); return; }
        // TotalCost 从 Items 求和，移除 item 后自动变小，无需手动扣减
        TotalCost = _sess.TotalCost;
        ProviderCost = _sess.CostForProvider(_provider);
        _sess.SaveState();
        Log($"已撤回 {removed.File}", MessageLevel.Ok);
        RefreshHistory();
    }

    // ================================================================ 预览
    [RelayCommand]
    private async Task ShowPreviewAsync(Item? item)
    {
        item ??= _sess.Current;
        if (item is null) { Log("还没有图", MessageLevel.Warn); return; }
        var path = item.Path(_sess.HomePath);
        if (!File.Exists(path)) { Log("图片文件不存在", MessageLevel.Err); return; }
        // ⚠️ v5.27.0：显式记住"当前预览的图" —— 编辑/标注都以它为目标，
        //    而不是恒取历史最新一张（见 CurrentItem 的说明）。
        _currentItem = item;
        OnPropertyChanged(nameof(CurrentItem));
        OnPropertyChanged(nameof(CurrentItemName));
        OnPropertyChanged(nameof(EditTip));
        // v0.5.33：编辑目标变了 → 「编辑图片」可用性随之变化（CanAnnotate 依赖 CurrentItem）
        OnPropertyChanged(nameof(CanAnnotate));
        PreviewPath = path;
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path);
            Dispatcher.UIThread.Post(() =>
            {
                // P0-2：替换预览图前释放旧位图（否则每切一次图泄漏一个）
                try
                {
                    ReplaceBitmap(PreviewImage, new Bitmap(new MemoryStream(bytes)),
                                  b => PreviewImage = b);
                }
                catch (Exception ex) { AppLog.Debug("替换预览位图失败", ex, where: "MainViewModel.SwapPreview"); }
            });
        }
        catch (Exception ex)
        {
                        Log($"预览失败：{ex.Message}", MessageLevel.Err, where: "MainViewModel.ShowPreviewAsync", ex: ex);
        }
    }

    [RelayCommand]
    private async Task PreviewCurrentAsync()
    {
        if (_sess.Current is null) { Log("还没有图", MessageLevel.Warn); return; }
        await ShowPreviewAsync(_sess.Current);
        Log($"预览 {_sess.Current.File}", MessageLevel.Info);
    }

    [RelayCommand]
    private async Task OpenInViewerAsync()
    {
        if (PreviewPath is null) return;
        var r = await _svc.Storage.OpenInExternalViewerAsync(PreviewPath);
        ReportPlatformOp(r, "打开系统看图器");
    }

    /// <summary>
    /// 在文件管理器中定位当前图片（v0.5.38：补上这个**一直没有入口**的功能）。
    ///
    /// 背景：`PlatformStorage.RevealInFileManagerAsync` 早就实现了（Windows 走
    /// <c>explorer /select,</c>），但**没有任何 UI 调用它** —— 属"有实现无入口"的
    /// 假功能（review 里记为债务 #2）。这里补上入口。
    ///
    /// 非 Windows 平台会返回 `Unsupported`，由 <see cref="ReportPlatformOp"/> 如实提示
    /// （而不是静默什么都不做）。
    /// </summary>
    [RelayCommand]
    private async Task RevealInFileManagerAsync()
    {
        var path = PreviewPath ?? (_sess.Current is { } c ? c.Path(_sess.HomePath) : null);
        if (path is null || !File.Exists(path)) return;
        var r = await _svc.Storage.RevealInFileManagerAsync(path);
        ReportPlatformOp(r, "在文件管理器中显示");
    }

    [RelayCommand]
    private async Task SaveToGalleryAsync()
    {
        var srcPath = PreviewPath ?? (_sess.Current is { } c ? c.Path(_sess.HomePath) : null);
        if (srcPath is null || !File.Exists(srcPath)) return;
        try
        {
            var data = await File.ReadAllBytesAsync(srcPath);
            var r = await _svc.Storage.SaveToGalleryAsync(
                data, Path.GetFileName(srcPath), ImageCodec.MimeForExt(Path.GetExtension(srcPath)));
            ReportPlatformOp(r, "保存到相册");
        }
        catch (Exception ex)
        {
                        Log($"保存失败：{ex.Message}", MessageLevel.Err, where: "MainViewModel.SaveToGalleryAsync", ex: ex);
        }
    }

    /// <summary>
    /// 把平台操作结果翻译成「提示文案 + 级别」（**纯函数**，便于无 UI 线程下同步单测）。
    /// P0-5 修复点：旧代码把 <c>null</c> 一律说成「未保存（用户取消）」——
    /// 于是 **Android / 权限不足 / 磁盘满 全都被伪装成「用户自己取消的」**，
    /// 用户既没得到警告、也没机会重试。
    /// </summary>
    public static (string Text, MessageLevel Level) DescribePlatformOp(
        PlatformOpResult r, string action) => r.Status switch
        {
            // 成功：有路径就报路径，没有就给个完成提示
            PlatformOpStatus.Ok when !string.IsNullOrEmpty(r.Path)
                => ($"已保存到 {r.Path}", MessageLevel.Ok),
            PlatformOpStatus.Ok
                => ($"{action}：完成", MessageLevel.Ok),

            // 用户明确取消 → 中性提示，**不报错**
            PlatformOpStatus.Cancelled
                => ($"{action}：已取消", MessageLevel.Info),

            // 平台不支持 → 警告（不是错误，也不是静默）
            PlatformOpStatus.Unsupported
                => ($"{action}：当前平台不支持（{r.Error}）", MessageLevel.Warn),

            // 真失败 → 错误 + 真实原因
            _ => ($"{action}失败：{r.Error ?? "未知原因"}", MessageLevel.Err),
        };

    /// <summary>统一报告平台操作结果（P0-5）。四态分开，不再把失败伪装成取消。</summary>
    private void ReportPlatformOp(PlatformOpResult r, string action)
    {
        var (text, level) = DescribePlatformOp(r, action);
        Log(text, level, where: level == MessageLevel.Err ? "MainViewModel." + action : null);
    }

    // ================================================================ 设置
    [RelayCommand] private void ToggleOffline()
    {
        Offline = !Offline;
        Status = Offline ? "离线" : SelfCheckText;   // P5 就绪 = 自检结论
        Log($"离线模式 → {(Offline ? "开" : "关")}", MessageLevel.Ok);
    }

    [RelayCommand]
    private void RefreshHistory() => Safe(RefreshHistoryCore, "刷新历史");

    private void RefreshHistoryCore()
    {
        ClearAndDispose(History);   // P0-2：先释放上一批缩略图（最多 200 个非托管位图）
        // P0-4：走快照 —— 逐次读 Items[i] 会在并发写入时读到不一致的中途状态
        foreach (var item in _sess.Items.Snapshot())
            History.Add(new HistoryRow(item, _sess.HomePath));
        TotalCost = _sess.TotalCost;                          // = Items 求和
        ProviderCost = _sess.CostForProvider(_provider);      // 同源，天然一致
        UnknownCost = _sess.CostForUnknownProvider();
        // v0.5.33：历史列表变化会影响 CurrentItem（= Items[0]）→ 「编辑图片」可用性随之变化
        OnPropertyChanged(nameof(CanAnnotate));
    }

    [RelayCommand]
    private void RefreshPromptHistory() => Safe(RefreshPromptHistoryCore, "刷新提示词历史");

    private void RefreshPromptHistoryCore()
    {
        PromptHistory.Clear();
        foreach (var p in _sess.LoadPromptHistory(limit: 50)) PromptHistory.Add(p);
    }

    /// <summary>
    /// 读取即梦的第二把密钥（SecretAccessKey）。
    ///
    /// 与 <see cref="LoadApiKey"/> 同样的策略：环境变量优先 + 全程容错 + 不缓存
    /// （这个值只在发请求/切 provider 时读，频率远低于 key，缓存收益不值复杂度）。
    /// </summary>
    public string LoadSecret()
    {
        try
        {
            // 环境变量优先（与火山引擎 CLI 的习惯变量名对齐）
            var env = Environment.GetEnvironmentVariable("IMGHUB_JIMENG_SECRET_KEY")
                      ?? Environment.GetEnvironmentVariable("VOLC_SECRETKEY");
            if (!string.IsNullOrEmpty(env)) return env.Trim();
            return _sess.LoadSecret(Provider);
        }
        catch (Exception ex)
        {
            AppLog.Warn("读取即梦第二把密钥失败（按未配置处理）",
                        "MainViewModel.LoadSecret", ex: ex);
            return "";
        }
    }

    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        try
        {
            var key = LoadApiKey();
            if (string.IsNullOrEmpty(key)) { Log("需要先配置 API key", MessageLevel.Warn); return; }
            var models = await _svc.ImageApi.ListModelsAsync(key);
            RebuildModelChoices(models);
            Log($"账号可用模型 {models.Count} 个", MessageLevel.Ok);
        }
        catch (Exception ex)
        {
                        Log($"拉取模型失败：{ex.Message}", MessageLevel.Err, where: "MainViewModel.RefreshModelsAsync", ex: ex);
        }
    }

    private void RebuildModelChoices(IReadOnlyList<string>? fetched = null)
    {
        ModelChoices.Clear();
        var src = fetched is { Count: > 0 } ? fetched : Catalog.ModelChoices(_provider);
        foreach (var m in src) ModelChoices.Add(m);
        if (ModelChoices.Count > 0 &&
            (string.IsNullOrWhiteSpace(Model) || !ModelChoices.Contains(Model)))
            Model = ModelChoices[0];
    }

    private void RebuildQualityOptions() => OnPropertyChanged(nameof(QualityOptions));

    // ================================================================ key
    // ---- key 缓存（避免每次绑定求值都做文件 IO，且 IO 失败绝不抛异常）----
    private string? _cachedKeyProvider;
    private string _cachedKey = "";
    private DateTime _cachedKeyMtime = DateTime.MinValue;
    private bool _cachedKeyValid;

    /// <summary>
    /// 读取当前 provider 的 API key。
    /// ⚠️ 本方法被绑定属性（IsConfigured / KeyStatusText）**频繁调用**，
    /// 所以必须：① 全程 try/catch（IO 失败返回空，绝不抛）；② 缓存结果。
    /// </summary>
    public string LoadApiKey()
    {
        try
        {
            // 环境变量优先（不缓存，成本低）
            // 兼容：改名前的 IMGAGENT_* 仍可识别（老用户的环境变量不必改）
            var env = Provider == ApiProvider.Apimart
                ? Environment.GetEnvironmentVariable("IMGHUB_APIMART_API_KEY")
                  ?? Environment.GetEnvironmentVariable("IMGHUB_API_KEY")
                  ?? Environment.GetEnvironmentVariable("IMGAGENT_APIMART_API_KEY")
                  ?? Environment.GetEnvironmentVariable("IMGAGENT_API_KEY")
                : Provider == ApiProvider.Jimeng
                  ? Environment.GetEnvironmentVariable("IMGHUB_JIMENG_ACCESS_KEY")
                    ?? Environment.GetEnvironmentVariable("VOLC_ACCESSKEY")
                  : Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
                    ?? Environment.GetEnvironmentVariable("IMGHUB_API_KEY")
                    ?? Environment.GetEnvironmentVariable("IMGAGENT_API_KEY");
            if (!string.IsNullOrEmpty(env)) return env.Trim();

            var providerKey = Provider.Key();
            var f = _sess.KeyFile(Provider);
            var legacy = _sess.LegacyKeyFile(Provider);

            // 缓存命中：同 provider 且文件未改动
            if (_cachedKeyValid && _cachedKeyProvider == providerKey)
            {
                var mtime = File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue;
                if (mtime == _cachedKeyMtime) return _cachedKey;
            }

            // 重读（新名优先，改名前的旧名回退 —— 见 ReadKeyFileCompat）
            var val = Session.ReadKeyFileCompat(f, legacy);

            _cachedKeyProvider = providerKey;
            _cachedKey = val;
            _cachedKeyMtime = File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue;
            _cachedKeyValid = true;
            return val;
        }
        catch
        {
            // 任何 IO/权限异常 → 视为未配置（不崩 UI）
            return "";
        }
    }

    /// <summary>让 key 缓存失效（保存/删除 key 后调用）。</summary>
    public void InvalidateKeyCache()
    {
        _cachedKeyValid = false;
        _cachedKey = "";
        _cachedKeyMtime = DateTime.MinValue;
    }

    [RelayCommand]
    private void SaveApiKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        _sess.SaveKey(key.Trim(), Provider);
        InvalidateKeyCache();          // 缓存失效，下次读取走文件
        RefreshConfigured();
        Log("key 已保存", MessageLevel.Ok);
    }

    // ================================================================ 设置

    private void RefreshKeyStatus()
    {
        var k = LoadApiKey();
        var s = Provider.NeedsAccessKeyPair() ? LoadSecret() : "";
        // 即梦要两把：显示"AK + SK"的状态，避免用户以为配好了其实缺一半
        if (Provider.NeedsAccessKeyPair())
        {
            KeyStatusText = (string.IsNullOrEmpty(k), string.IsNullOrEmpty(s)) switch
            {
                (true, true) => "未配置（缺 AK 与 SK）",
                (false, true) => "只有 AK，还缺 SecretAccessKey",
                (true, false) => "只有 SK，还缺 AccessKeyId",
                _ => "已配置（AK + SK）",
            };
            return;
        }
        KeyStatusText = string.IsNullOrEmpty(k)
            ? "未配置" : (k.Length > 10 ? k[..6] + "..." + k[^4..] : "已配置");
    }

    [RelayCommand]
    private void OpenSettings() => Safe(OpenSettingsCore, "打开设置");

    private void OpenSettingsCore()
    {
        ApiKeyInput = LoadApiKey();
        SecretKeyInput = LoadSecret();
        BaseUrlInput = _sess.BaseUrlFor(Provider);
        PolishBaseInput = _svc.Polish.BaseUrl;
        PolishModelInput = _svc.Polish.Model;
        PolishKeyInput = _svc.Polish.ApiKey;
        RefreshKeyStatus();
        SettingsOpen = true;
        AppLog.Debug("打开设置浮层", where: "MainViewModel.OpenSettings");
    }

    [RelayCommand]
    private void CloseSettings()
    {
        SettingsOpen = false;
        AppLog.Debug("关闭设置浮层", where: "MainViewModel.CloseSettings");
    }

    [RelayCommand]
    private void SaveSettings() => Safe(SaveSettingsCore, "保存设置");

    private void SaveSettingsCore()
    {
        if (!string.IsNullOrWhiteSpace(ApiKeyInput))
            _sess.SaveKey(ApiKeyInput.Trim(), Provider);
        // 即梦第二把密钥（SecretAccessKey）：留空 = 不改（与润色 key 同样的策略）
        if (Provider.NeedsAccessKeyPair() && !string.IsNullOrWhiteSpace(SecretKeyInput))
            _sess.SaveSecret(SecretKeyInput.Trim(), Provider);
        // 端点覆盖：留空 = 回到官方默认（千问专属域名 / OpenAI 代理网关都靠这里）
        _sess.SetBaseUrl(Provider, BaseUrlInput);
        _svc.ImageApi = _svc.ImageApiFactory(Provider, _svc.Http, _sess.BaseUrlFor(Provider));
        WireSubmitProgress();
        WirePartialImage();
        _svc.Polish.BaseUrl = PolishBaseInput.Trim();
        _svc.Polish.Model = PolishModelInput.Trim();
        if (!string.IsNullOrWhiteSpace(PolishKeyInput))
        {
            _svc.Polish.ApiKey = PolishKeyInput.Trim();
            _sess.SavePolishKey(_svc.Polish.ApiKey);
        }
        _sess.Config.PolishBaseUrl = _svc.Polish.BaseUrl;
        _sess.Config.PolishModel = _svc.Polish.Model;
        _sess.Config.UseIcons = UseIcons;          // #7 图标显示开关持久化
        _sess.Config.UseHoverAnimation = EnableHoverAnimation;   // v5.24.0 悬停动画
        _sess.Config.UseToolTips = EnableToolTips;               // v5.24.0 悬停说明
        _sess.SaveConfig();
        RefreshKeyStatus();
        RefreshConfigured();
        Log("设置已保存", MessageLevel.Ok);
        SettingsOpen = false;
    }

    [RelayCommand]
    private async Task TestKeyAsync()
    {
        var key = string.IsNullOrWhiteSpace(ApiKeyInput) ? LoadApiKey() : ApiKeyInput.Trim();
        if (string.IsNullOrEmpty(key)) { KeyCheckStatus = "请先填入 API key"; return; }
        KeyChecking = true;
        KeyCheckStatus = "正在校验…";
        try
        {
            var info = await _svc.ImageApi.CheckKeyAsync(key);
            KeyCheckStatus = info.IsApimart
                ? $"校验通过 · 可用模型 {info.ModelCount} 个"
                : $"校验通过 · 累计已用 ${info.Usage ?? 0:0.0000}";
            Log(KeyCheckStatus, MessageLevel.Ok);
            RefreshConfigured();
        }
        catch (Exception ex)
        {
            KeyCheckStatus = "校验失败：" + ex.Message;
            Log(KeyCheckStatus, MessageLevel.Err);
        }
        finally
        {
            KeyChecking = false;
            OnPropertyChanged(nameof(KeyCheckStatus));
        }
    }

    // ================================================================ 润色
    /// <summary>清空提示词与当前标注（用户需求 #13）。</summary>
    public void ClearPrompt()
    {
        Safe(() =>
        {
            Prompt = "";
            RegionCount = 0;
            AnnotatedPath = "";
            AnnotatedMaskPath = "";
            ClearAndDispose(BatchResults);   // P0-2
            OnPropertyChanged(nameof(HasBatchResults));
            Log("已清空提示词与标注", MessageLevel.Info);
        }, "清空");
    }

    [RelayCommand]
    private async Task PolishPromptAsync()
    {
        if (Busy) return;
        var prompt = (Prompt ?? "").Trim();
        if (prompt.Length == 0) { Log("请先输入提示词", MessageLevel.Warn); return; }
        if (!_svc.Polish.Enabled)
        {
            Log(_svc.Polish.Configured ? "润色已关闭" : "润色未配置：请在设置里配置", MessageLevel.Warn);
            return;
        }
        Busy = true;
        Status = "润色中…";
        try
        {
            var options = await _svc.Polish.PicksAsync(prompt);
            if (options.Count == 0) { Log("没有拿到润色结果", MessageLevel.Warn); return; }
            PolishCandidates.Clear();
            foreach (var o in options) PolishCandidates.Add(o);
            PolishIndex = 0;
            PolishCurrent = options[0];
            PolishOpen = true;
            Log($"得到 {options.Count} 条润色候选", MessageLevel.Ok);
        }
        catch (Exception ex)
        {
                        Log($"润色失败：{ex.Message}", MessageLevel.Err, where: "MainViewModel.PolishPromptAsync", ex: ex);
        }
        finally { Busy = false; Status = Offline ? "离线" : SelfCheckText; }   // P5 就绪 = 自检结论
    }

    [RelayCommand]
    private void PolishPrev()
    {
        if (PolishCandidates.Count == 0) return;
        PolishIndex = (PolishIndex - 1 + PolishCandidates.Count) % PolishCandidates.Count;
        PolishCurrent = PolishCandidates[PolishIndex];
    }

    [RelayCommand]
    private void PolishNext()
    {
        if (PolishCandidates.Count == 0) return;
        PolishIndex = (PolishIndex + 1) % PolishCandidates.Count;
        PolishCurrent = PolishCandidates[PolishIndex];
    }

    [RelayCommand]
    private void PolishAccept()
    {
        if (PolishCandidates.Count == 0) { PolishOpen = false; return; }
        Prompt = PolishCandidates[PolishIndex];
        PolishOpen = false;
        Log($"已采用第 {PolishIndex + 1} 条润色结果", MessageLevel.Ok);
    }

    [RelayCommand]
    private void PolishCancel() => PolishOpen = false;

    [RelayCommand]
    private async Task PolishRetryAsync() { PolishOpen = false; await PolishPromptAsync(); }

    // ================================================================ 提示词指南浮窗（v0.5.31）
    /// <summary>
    /// 打开指南浮窗。默认直接切到**当前生效端点**那一页，省得用户自己找。
    /// </summary>
    [RelayCommand]
    private void OpenGuide() => Safe(() =>
    {
        var active = PromptGuide.For(_svc.Polish.EffectiveStyle == "qwen"
            ? ApiProvider.DashScope : ApiProvider.OpenAi);
        for (int i = 0; i < PromptGuide.All.Count; i++)
        {
            if (ReferenceEquals(PromptGuide.All[i], active)) { GuideTab = i; break; }
        }
        OnPropertyChanged(nameof(CurrentGuide));
        OnPropertyChanged(nameof(GuideActiveHint));
        GuideOpen = true;
        AppLog.Debug("打开提示词指南浮层", where: "MainViewModel.OpenGuide");
    }, "打开提示词指南");

    [RelayCommand]
    private void CloseGuide()
    {
        GuideOpen = false;
        AppLog.Debug("关闭提示词指南浮层", where: "MainViewModel.CloseGuide");
    }

    partial void OnGuideTabChanged(int value)
        => OnPropertyChanged(nameof(CurrentGuide));

    // ================================================================ 即梦提取浮窗（v0.5.31）
    /// <summary>
    /// 打开「提取」浮窗。
    ///
    /// ⚠️ 前置条件与提示要写清楚（文档要求**必传 1 张输入图**）：
    ///   用户最容易的困惑是"点了提取没反应" —— 实际是没选图。
    ///   所以这里在缺少输入图时**直接给出可执行的动作指引**，而不是开个空浮窗。
    /// </summary>
    [RelayCommand]
    private void OpenExtract() => Safe(() =>
    {
        if (!IsJimengProvider)
        {
            Log("「提取」是即梦（火山引擎）专属功能 —— 请先在顶栏把 provider 切到「即梦」",
                MessageLevel.Warn);
            return;
        }
        if (CurrentItem is null && RefImages.Count == 0)
        {
            Log("提取需要 1 张输入图：请在历史里点选一张图，或先添加参考图",
                MessageLevel.Warn);
            return;
        }
        OnPropertyChanged(nameof(ExtractPresets));
        OnPropertyChanged(nameof(ExtractAllowsItemName));
        OnPropertyChanged(nameof(ExtractHint));
        // 默认选中第一条预设，并把官方文案写进请求字段
        ExtractPresetIndex = 0;
        ExtractItemName = "";
        OnExtractPresetIndexChanged(0);
        ExtractOpen = true;
        AppLog.Debug("打开素材提取浮层", where: "MainViewModel.OpenExtract");
    }, "打开提取浮窗");

    [RelayCommand]
    private void CloseExtract()
    {
        ExtractOpen = false;
        AppLog.Debug("关闭素材提取浮层", where: "MainViewModel.CloseExtract");
    }

    /// <summary>
    /// 开始提取：把当前预览图（或第一张参考图）作为唯一输入，
    /// 切到对应 req_key，然后走正常的生成链路。
    ///
    /// ⚠️ 会**临时切换** <see cref="Model"/> 到提取 req_key —— 但结束后恢复原值，
    ///   避免用户下次点「生成」时意外又跑一次提取（且提取按次计费）。
    /// </summary>
    [RelayCommand]
    private async Task RunExtractAsync()
    {
        if (Busy) return;
        if (!IsJimengProvider) { Log("请在顶栏把 provider 切到「即梦」", MessageLevel.Warn); return; }
        if (!CanRun) { Log("尚未配置 API key", MessageLevel.Warn); return; }

        var kind = _extractTab == 1
            ? JimengExtract.JimengExtractKind.Element
            : JimengExtract.JimengExtractKind.Product;
        var reqKey = JimengExtract.ReqKey(kind);
        var presets = JimengExtract.PresetsOf(kind);
        if (ExtractPresetIndex < 0 || ExtractPresetIndex >= presets.Count) return;
        var preset = presets[ExtractPresetIndex];
        var instruction = JimengExtract.Customize(preset, _extractItemName);

        ExtractOpen = false;
        Busy = true;
        Status = "提取中…";
        ClearAndDispose(BatchResults);   // P0-2
        OnPropertyChanged(nameof(HasBatchResults));

        var modelBefore = Model;
        var extractBefore = JimengExtractPrompt;
        try
        {
            // 临时切到提取 req_key（并同步模型下拉，让界面如实反映在做什么）
            _switchingProvider = true;   // 避免触发 provider 那套收窄/快照逻辑
            try { Model = reqKey; }
            finally { _switchingProvider = false; }
            JimengExtractPrompt = instruction;

            // 输入图：优先用预览中的图，其次第一张参考图
            var refs = await CollectExtractInputAsync();
            if (refs.Count == 0)
            {
                Log("提取需要 1 张输入图（未找到可用图片文件）", MessageLevel.Err);
                return;
            }

            var progress = new Progress<string>(msg => Log(msg, MessageLevel.Info));
            var req = BuildRequest(instruction, LoadApiKey(), refs, mask: null, progress);
            var t0 = DateTime.UtcNow;
            var res = await _svc.ImageApi.GenerateAsync(req);
            await LandResultsAsync(res, $"[{preset.Name}] {instruction}", editMode: true, t0);
            Log($"提取完成：{preset.Name}", MessageLevel.Ok);
        }
        catch (Exception ex)
        {
                        Log($"提取失败：{ex.Message}", MessageLevel.Err, where: "MainViewModel.RunExtractAsync", ex: ex);
            foreach (var line in ErrorHints.Explain(ex).Skip(1)) Log(line.Trim(), MessageLevel.Dim);
        }
        finally
        {
            // 恢复模型与指令（提取只应是一次性动作）
            _switchingProvider = true;
            try { Model = modelBefore; }
            finally { _switchingProvider = false; }
            JimengExtractPrompt = extractBefore;
            Busy = false;
            Status = Offline ? "离线" : SelfCheckText;
        }
    }

    /// <summary>
    /// 收集提取的输入图（**恰好 1 张**）：优先预览中的图，其次第一张参考图。
    /// 与 <see cref="CollectRefsAsync"/> 分开，因为提取的语义是"只吃 1 张"。
    /// </summary>
    private async Task<List<(byte[], string)>> CollectExtractInputAsync()
    {
        var list = new List<(byte[], string)>();
        try
        {
            var path = CurrentItem?.Path(_sess.HomePath);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var bytes = await File.ReadAllBytesAsync(path);
                list.Add((bytes, ImageCodec.SniffMediaType(bytes) ?? "image/png"));
                return list;
            }
            // 退路：第一张参考图
            if (RefImages.Count > 0)
            {
                var rp = RefImages[0].Item.Path(_sess.HomePath);
                if (File.Exists(rp))
                {
                    var bytes = await File.ReadAllBytesAsync(rp);
                    list.Add((bytes, ImageCodec.SniffMediaType(bytes) ?? "image/png"));
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("读取提取输入图失败", "MainViewModel.CollectExtractInputAsync", ex: ex);
        }
        return list;
    }

    // ================================================================ 区域标注
    [RelayCommand]
    private void ToggleRegionMode() => Safe(ToggleRegionModeCore, "切换标注模式");

    private void ToggleRegionModeCore()
    {
        // ⚠️ 必须与 CanAnnotate 用同一判据（否则会出现"按钮可点但点了被拒"的不一致）。
        //    CurrentItem 优先取当前**预览中**的图，比 _sess.Current（恒为 Items[0]）更准确。
        if (CurrentItem is null) { Log("还没有图可标注", MessageLevel.Warn); return; }
        RegionMode = !RegionMode;
        AppLog.Debug($"标注模式切换 → {(RegionMode ? "开启" : "关闭")}（当前图={CurrentItemName}）",
                     where: "MainViewModel.ToggleRegionModeCore");
        if (RegionMode) { RegionCount = 0; Log("区域标注已开启", MessageLevel.Info); }
        else Log("区域标注已关闭", MessageLevel.Info);
    }

    [RelayCommand]
    private void ClearRegions() { RegionCount = 0; Log("已清除全部标注", MessageLevel.Info); }

    /// <summary>标注区域蒙版路径（**透明=要改，不透明=保留**，见 RegionCanvas.ExportMask）。</summary>
    [ObservableProperty] private string _annotatedMaskPath = "";

    /// <summary>
    /// 该蒙版所对应的**底图路径**（用于校验蒙版与参考图是否还是同一张）。
    ///
    /// ⚠️ 为什么必须记（v5.27.0）：蒙版尺寸必须与参考图第 1 位**逐像素一致**。
    ///   用户画完标注后若又切换/删除了预览图，旧蒙版就属于另一张图 ——
    ///   把它发出去会得到一个尺寸不匹配的蒙版，而 APIMart **不做预校验**，
    ///   结果就是"看起来发出了、重绘区域却完全错位"这种极难排查的现象。
    /// </summary>
    private string? _annotatedMaskFor;

    /// <summary>
    /// 退出编辑模式时清空标注相关状态（④）。
    ///
    /// ⚠️ 为什么必须清：<see cref="SetAnnotatedImage"/> / <see cref="SetAnnotatedMask"/>
    ///   会把合成图与蒙版**落盘并记在 VM 里**。用户取消编辑（再次点「编辑图片」）后若不清，
    ///   下一轮编辑会复用上一轮的旧标注 —— <see cref="LoadMaskAsync"/> 的归属校验
    ///   只比对"是否同一张底图"，对**同一张图重复编辑**会校验通过，
    ///   于是悄悄用了过期蒙版（重绘区域与用户这次画的对不上）。
    ///   磁盘文件保留（便于事后排查），只清 VM 侧的引用。
    /// </summary>
    public void ClearAnnotationState()
    {
        AnnotatedPath = "";
        AnnotatedMaskPath = "";
        _annotatedMaskFor = null;
        RegionCount = 0;
    }

    /// <summary>保存「修改区域蒙版」到数据目录（供支持 mask 的模型使用）。</summary>
    public void SetAnnotatedMask(byte[] maskPng, string? baseImagePath = null)
    {
        try
        {
            var name = Session.SafeFilename(
                $"mask_{DateTime.Now:MMdd_HHmmss}", "region", ".png", limit: 24);
            var path = Path.Combine(_sess.HomePath, name);
            File.WriteAllBytes(path, maskPng);
            AnnotatedMaskPath = path;
            _annotatedMaskFor = baseImagePath;
            var hasAlpha = Catalog.PngHasAlpha(maskPng);
            Log($"修改区域蒙版已保存：{name}（{maskPng.Length / 1024}KB"
                + (hasAlpha ? " · 含 Alpha" : " · ⚠ 缺 Alpha 通道") + "）",
                hasAlpha ? MessageLevel.Info : MessageLevel.Warn);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex.Message, where: "MainViewModel.SetAnnotatedMask", ex: ex);
            Log($"蒙版保存失败：{ex.Message}", MessageLevel.Warn,
                where: "MainViewModel.SetAnnotatedMask");
        }
    }

    public void SetAnnotatedImage(byte[] png, int regionCount)
    {
        try
        {
            var name = Session.SafeFilename(
                $"annotated_{DateTime.Now:MMdd_HHmmss}", "region", ".png", limit: 24);
            var path = Path.Combine(_sess.HomePath, name);
            File.WriteAllBytes(path, png);
            AnnotatedPath = path;
            RegionCount = regionCount;
            Log($"标注已保存：{name}", MessageLevel.Ok);
        }
        catch (Exception ex)
        {
                        Log($"标注保存失败：{ex.Message}", MessageLevel.Err, where: "MainViewModel.SetAnnotatedImage", ex: ex);
        }
    }

    [RelayCommand]
    private async Task EditWithRegionsAsync()
    {
        if (Busy) return;
        if (RegionCount == 0) { Log("还没有画任何标注", MessageLevel.Warn); return; }
        if (string.IsNullOrEmpty(AnnotatedPath) || !File.Exists(AnnotatedPath))
        { Log("请先圈画要修改的区域", MessageLevel.Warn); return; }
        if (CurrentItem is null) { Log("没有可编辑的图", MessageLevel.Warn); return; }
        var userPrompt = (Prompt ?? "").Trim();
        if (userPrompt.Length == 0) { Log("请描述要如何修改", MessageLevel.Warn); return; }

        // ⚠️ v5.27.0 关键修正（此前蒙版链路实际是死的）：
        //   旧实现把「底图 + 标注薄层的合成图」当作**唯一**参考图发出去，
        //   于是模型看到的是"画了红线的一张图"，只能靠猜测高亮区域；
        //   而导出好的 AnnotatedMaskPath 只写不读，mask_url 从未发送过。
        //
        //   现在按文档的正确做法：
        //     ① 参考图第 1 位 = **未标注的原图**（主体，模型按位置理解）；
        //     ② 若有真正的 alpha 蒙版 → 走 mask_url（alpha=0 = 要改的区域）；
        //     ③ 无蒙版时退回"合成图 + 提示词说明"（保住旧行为，不至于没图可用）。
        var refs = new List<(byte[], string)>();
        var curPath = CurrentItem.Path(_sess.HomePath);
        var mask = await LoadMaskAsync();
        var compositeUsed = false;

        // ⚠️ 蒙版链路的 provider 支持判断 —— **单一真源**，必须与 ImageApi 的实际实现一致：
        //    · APIMart：mask_url（先上传换公网 URL）→ ImageApi.GenerateApimartAsync
        //    · OpenAI 官方：/images/edits 的 multipart `mask` 文件字段 → ImageApi.GenerateOpenAiEditAsync
        //    · OpenRouter / 千问 / 即梦：文档无 mask 字段 → 用「原图 + 标注合成图」表达区域
        //
        // ⚠️ v0.5.32 修复：此前这里**只认 APIMart**，而 RegionEditTip 却告诉 OpenAI 用户
        //    "将发送真正的 Alpha 蒙版" —— 文案与行为不符，OpenAI 用户的蒙版被无谓丢弃
        //    （ImageApi 侧明明支持）。现在两边读同一个判断。
        bool maskSupported = Catalog.SupportsMaskChannel(_provider);
        if (mask is not null && !maskSupported)
        {
            Log($"当前 provider（{Provider.Label()}）文档没有 mask 字段 → " +
                "改用「原图 + 标注合成图」方式表达修改区域", MessageLevel.Warn);
            mask = null;
        }

        if (File.Exists(curPath))
        {
            var raw = await File.ReadAllBytesAsync(curPath);
            refs.Add((raw, ImageCodec.SniffMediaType(raw) ?? "image/png"));
        }
        if (mask is null)
        {
            // 无可用蒙版 → 追加标注合成图作为参考图，提示词补说明（保住"能改指定区域"的诉求）
            var comp = await File.ReadAllBytesAsync(AnnotatedPath);
            refs.Add((comp, "image/png"));
            compositeUsed = true;
        }
        foreach (var rf in RefImages)
        {
            if (refs.Count >= MaxRefs) break;
            var rp = rf.Item.Path(_sess.HomePath);
            if (!File.Exists(rp)) continue;
            var rb = await File.ReadAllBytesAsync(rp);
            refs.Add((rb, ImageCodec.SniffMediaType(rb) ?? "image/png"));
        }
        if (refs.Count == 0) { Log("找不到要编辑的图片文件", MessageLevel.Err); return; }

        var finalPrompt = mask is null
            ? $"{userPrompt}（图中高亮区域即需要修改的部分，请仅修改这些区域，其余保持不变）"
            : $"{userPrompt}（请仅修改蒙版标记的透明区域，其余区域保持完全不变）";
        _sess.PushPromptHistory(finalPrompt, "edit");
        RefreshPromptHistory();

        Busy = true;
        Status = "编辑中…";
        try
        {
            Log(mask is null
                ? "蒙版不可用 → 使用「原图 + 标注合成图」方式编辑"
                : $"使用蒙版局部重绘（alpha=0 区域 = 要改，{mask.Length / 1024}KB）",
                mask is null ? MessageLevel.Warn : MessageLevel.Ok);
            var req = BuildRequest(finalPrompt, LoadApiKey(), refs, mask,
                                   new Progress<string>(m => Log(m)));
            var res = await _svc.ImageApi.GenerateAsync(req);
            await LandResultsAsync(res, finalPrompt, true, DateTime.UtcNow);
            RegionMode = false;
            // 参考图默认「一次性」：用完即清，避免污染后续无关任务
            if (RefImages.Count > 0 && !KeepRefImages)
            {
                ClearAndDispose(RefImages);   // P0-2
                OnPropertyChanged(nameof(GenerateTip));
                OnPropertyChanged(nameof(EditTip));
                Log("参考图已用完并清空（如需复用请重新添加）", MessageLevel.Info);
            }
            if (compositeUsed) Log("（提示：本次未使用蒙版通道）", MessageLevel.Dim);
        }
        catch (Exception ex)
        {
                        Log($"区域编辑失败：{ex.Message}", MessageLevel.Err, where: "MainViewModel.EditWithRegionsAsync", ex: ex);
            foreach (var line in ErrorHints.Explain(ex).Skip(1)) Log(line.Trim(), MessageLevel.Dim);
        }
        finally { Busy = false; Status = Offline ? "离线" : SelfCheckText; }   // P5 就绪 = 自检结论
    }

    // ================================================================ 多图
    private void PublishBatchResults(IEnumerable<Item> items)
    {
        ClearAndDispose(BatchResults);   // P0-2：先释放旧的缩略图条位图
        foreach (var it in items)
            BatchResults.Add(new PreviewThumb(it) { HomePath = _sess.HomePath });
        _selectedThumbIndex = 0;
        OnPropertyChanged(nameof(SelectedThumbIndex));
        OnPropertyChanged(nameof(HasBatchResults));
    }

    private int _selectedThumbIndex;
    public int SelectedThumbIndex
    {
        get => _selectedThumbIndex;
        set
        {
            if (value >= 0 && value < BatchResults.Count && SetProperty(ref _selectedThumbIndex, value))
                _ = ShowPreviewAsync(BatchResults[value].Item);
        }
    }

    // ================================================================ 主题
    public void ApplyTheme(bool dark)
    {
        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = dark
                ? Avalonia.Styling.ThemeVariant.Dark
                : Avalonia.Styling.ThemeVariant.Light;
    }
    partial void OnDarkThemeChanged(bool value)
    {
        ApplyTheme(value);
        AppLog.Debug($"主题切换 → {(value ? "深色" : "浅色")}", where: "MainViewModel.OnDarkThemeChanged");
    }

    // ================================================================ 语言（v0.5.40）
    /// <summary>
    /// 界面语言（zh / en / ja）。改它会**立即**刷新界面上所有绑定文案（无需重启）。
    ///
    /// 机制：<see cref="Localizer"/> 是 INotifyPropertyChanged 单例，
    /// XAML 用 `{CompiledBinding [Key], Source={x:Static ui:Localizer.Instance}}` 取值；
    /// 这里改 `Localizer.Instance.Language` → 它发一次"所有属性都变了" →
    /// 所有索引器绑定自动重取 → 界面即时切换。
    /// </summary>
    private string _language = "zh";
    public string Language
    {
        get => _language;
        set
        {
            var v = Localizer.Normalize(value);
            if (!SetProperty(ref _language, v)) return;
            Localizer.Instance.Language = v;
            OnPropertyChanged(nameof(L));   // 让所有 L[key] 绑定重取
            NotifyLocalizedStrings();       // 让 VM 里"动态生成文案"的属性也重取
            _sess.Config.Language = v;
            SaveConfigQuietly();
        }
    }

    /// <summary>语言下拉的可选项（显示名 → 代码）。</summary>
    public IReadOnlyList<string> LanguageChoices { get; } = new[] { "中文", "English", "日本語" };

    /// <summary>
    /// 本地化表（XAML 用 <c>{CompiledBinding L[key]}</c> 取文案）。
    ///
    /// ## ⚠️ 为什么要在 VM 上再暴露一层（v0.5.40 实测踩坑，两轮才定位）
    ///
    /// 最初 XAML 写的是
    /// <c>{CompiledBinding [app.title], Source={x:Static ui:Localizer.Instance}}</c>，
    /// 结果**编译期**就报错：
    /// <code>The type 'ImgHub.App.ViewModels.MainViewModel' does not have an indexer</code>
    /// —— 即 **CompiledBinding 忽略 `Source=`，仍按 `x:DataType` 推断源类型**。
    /// 编译绑定是 AOT 的硬要求（docs/CONSTRAINTS.md H1d），所以必须让源类型就是 VM：
    /// 在 VM 上暴露这个属性，路径写 `L[key]`，编译期即可检查索引器存在。
    ///
    /// 通知：`Language` 变化时本类会 <c>OnPropertyChanged(nameof(L))</c> →
    ///   所有 `L[key]` 绑定自动重取。
    /// </summary>
    public Localizer L => Localizer.Instance;

    /// <summary>
    /// 语言下拉的选中索引（0=中文 / 1=English / 2=日本語）。
    /// 用索引而不是字符串，避免"显示名"与"配置值"耦合。
    /// </summary>
    public int LanguageIndex
    {
        get => _language switch { "en" => 1, "ja" => 2, _ => 0 };
        set
        {
            var v = value switch { 1 => "en", 2 => "ja", _ => "zh" };
            Language = v;
            OnPropertyChanged();
        }
    }

    /// <summary>把持久化语言应用到界面（启动时调用一次）。</summary>
    public void ApplyLanguage(string? lang)
    {
        _language = Localizer.Normalize(lang);
        Localizer.Instance.Language = _language;
        OnPropertyChanged(nameof(Language));
        OnPropertyChanged(nameof(LanguageIndex));
        OnPropertyChanged(nameof(L));   // 让所有 L[key] 绑定重取
        NotifyLocalizedStrings();       // 让 VM 里"动态生成文案"的属性也重取
    }

    /// <summary>
    /// 通知所有**由 Localizer 生成文案**的 VM 属性刷新（v0.5.41）。
    ///
    /// ⚠️ 为什么必须显式通知：
    ///   `{CompiledBinding L[key]}` 这类**索引器绑定**会随 Localizer 的通知自动刷新；
    ///   但 VM 里的**计算属性**（如 `HistoryMultiButtonText` / `ToolNames` / `RegionCountText`）
    ///   是普通属性 —— 它们内部会读 Localizer，可**绑定层并不知道要重取**
    ///   （Avalonia 只认 `OnPropertyChanged(nameof(X))`）。
    ///   症状：切语言后 `L[key]` 的地方变了，这些属性**仍是旧语言**（实测：界面上「多选」保持中文）。
    ///
    /// ⚠️ 新增"会随语言变化的 VM 属性"时，**必须加进这个列表**，
    ///   并由 `LocalizationContractTests.ViewModelBoundStrings_AreLocalized_NotHardcodedChinese` 兜底提醒。
    /// </summary>
    private void NotifyLocalizedStrings()
    {
        OnPropertyChanged(nameof(ToolNames));
        OnPropertyChanged(nameof(RegionCountText));
        OnPropertyChanged(nameof(EditTip));
        OnPropertyChanged(nameof(GenerateTip));
        OnPropertyChanged(nameof(RegionEditTip));
        OnPropertyChanged(nameof(ExtractHint));
        OnPropertyChanged(nameof(GuideActiveHint));
        OnPropertyChanged(nameof(PolishStyleNames));
        OnPropertyChanged(nameof(PolishStyleLabel));
        OnPropertyChanged(nameof(SetupHint));
        OnPropertyChanged(nameof(BatchNHint));
        OnPropertyChanged(nameof(HistoryMultiButtonText));
        OnPropertyChanged(nameof(PromptMultiButtonText));
    }

    /// <summary>保存配置（失败不抛 —— 语言偏好保存失败不该打断使用）。</summary>
    private void SaveConfigQuietly()
    {
        try { _sess.SaveConfig(); }
        catch (Exception ex) { AppLog.Debug("保存配置失败（静默）", ex, where: "MainViewModel.SaveConfigQuietly"); }
    }

    // ================================================================ 状态栏
    [RelayCommand]
    private void ShowShortcuts()
    {
        Log("Ctrl+G 生成 · Ctrl+D 编辑 · Ctrl+R 撤回 · Ctrl+L 预览 · Ctrl+S 设置", MessageLevel.Info);
    }

    [RelayCommand]
    private void ShowDataDir() => Log($"数据目录：{_sess.HomePath}", MessageLevel.Info);

    /// <summary>
    /// 需求③：解释"蒙版怎么用 / 传给服务端的流程 / 提示词怎么写"。
    ///
    /// ⚠️ 内容**按当前 provider 动态生成** —— 因为传递流程确实不同（这是用户最容易误解的点）：
    ///   · APIMart / OpenAI：真的发 Alpha 蒙版（局部重绘约束强）；
    ///   · 其它：只能发「原图 + 标注合成图」，模型只是"尽力遵守"。
    /// 若写成固定文案，用户切了 provider 后会拿到与实际行为不符的说明。
    ///
    /// 文案由 <see cref="BuildMaskHelpLines"/> 生成（纯函数 → 无需 UI 线程即可单测）。
    /// </summary>
    [RelayCommand]
    private void ShowMaskHelp()
    {
        foreach (var (text, level) in BuildMaskHelpLines(_provider))
            Log(text, level);
    }

    /// <summary>
    /// 生成蒙版说明的文案行（纯函数，便于单测）。
    /// 返回 (文本, 级别)，顺序即展示顺序。
    /// </summary>
    public static IReadOnlyList<(string Text, MessageLevel Level)> BuildMaskHelpLines(
        ApiProvider provider)
    {
        var supportsMask = Catalog.SupportsMaskChannel(provider);
        var label = provider.Label();
        var lines = new List<(string, MessageLevel)>
        {
            ("──── 蒙版用法说明 ────", MessageLevel.Info),

            ("① 怎么画：点「编辑图片」→ 选工具（方框/圆圈/马克笔/橡皮），"
             + "在图上圈出**要修改的区域**；矩形与圆圈是实心填充，覆盖范围即生效范围。",
             MessageLevel.Dim),

            ("② 红色 = 要改，未涂区域 = 保持原样。橡皮可擦掉画错的部分；"
             + "「复位」只还原缩放/平移，「清除」才删标注。", MessageLevel.Dim),
        };

        if (supportsMask)
        {
            var channel = provider switch
            {
                ApiProvider.Apimart => "走 image_urls + mask_url",
                ApiProvider.OpenAi => "走 POST /images/edits 的 mask 字段",
                _ => "走蒙版通道",
            };
            lines.Add(($"③ 传递流程（当前 provider：{label}）："
                       + "画完后点「用标注编辑」→ 程序导出**带 Alpha 通道的 PNG 蒙版**"
                       + "（透明处 = 要改）→ 连同**原图**一起发给服务端做局部重绘。"
                       + $"（{channel}）", MessageLevel.Ok));
        }
        else
        {
            lines.Add(($"③ 传递流程（当前 provider：{label}）："
                       + "该服务的文档**没有 mask 字段** → 程序会发送「原图 + 标注合成图」，"
                       + "由模型根据高亮区域尽力遵守。想用真正的蒙版局部重绘，"
                       + "请在设置里切到 APIMart 或 OpenAI 官方。", MessageLevel.Warn));
        }

        lines.Add(("④ 提示词怎么写：**只描述要改什么**，不要把整段画面重新描述一遍。"
                   + "程序会自动补上「请仅修改高亮/透明区域，其余保持不变」的约束。",
                   MessageLevel.Info));
        lines.Add(("   例：「把背景换成纯白色」·「去掉左侧的水印」·「把衣服改成蓝色」",
                   MessageLevel.Dim));
        lines.Add(("   反例：「一只戴帽子的猫，白色背景，4K，高质量」"
                   + " —— 这是重新生成整张图，会丢掉原图主体。", MessageLevel.Dim));
        lines.Add(("⑤ 提示：蒙版只**引导**编辑，官方文档明确不保证边界外像素 100% 不变；"
                   + "需要严格不变时，可在提示词里强调「其余区域保持完全不变」。",
                   MessageLevel.Dim));
        lines.Add(("─────────────────────", MessageLevel.Info));

        return lines;
    }

    /// <summary>
    /// 打开日志目录（v5.24.0 用户要求：日志落盘到 log/ 供诊断）。
    /// </summary>
    [RelayCommand]
    private void OpenLogDir()
    {
        Safe(() =>
        {
            var dir = AppLog.LogDirectory;
            if (string.IsNullOrEmpty(dir))
            {
                Log("日志目录不可用（磁盘不可写？）", MessageLevel.Warn,
                    where: "MainViewModel.OpenLogDir");
                return;
            }
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { AppLog.Debug($"创建目录失败（可能已存在）：{dir}", ex, where: "MainViewModel.OpenInViewer"); }
            _ = _svc.Storage.OpenInExternalViewerAsync(dir);
            Log($"已打开日志目录：{dir}", MessageLevel.Ok, where: "MainViewModel.OpenLogDir");
        }, "打开日志目录");
    }

    // ================================================================ 消息
    public enum MessageLevel { Info, Ok, Warn, Err, Dim }
    public sealed record Message(string Time, string Text, MessageLevel Level);

    public void LogPublic(string text, MessageLevel level = MessageLevel.Info) => Log(text, level);

    /// <summary>
    /// 命令安全执行包装：捕获任何异常 → **写日志（含位置）** + 显示到消息面板，
    /// **绝不让 UI 线程崩溃**。所有同步 [RelayCommand] 都应通过它执行。
    /// </summary>
    private void Safe(Action action, string opName)
    {
        try { action(); }
        catch (Exception ex)
        {
            // 用户要求：写清详细错误原因与位置，方便排查
            AppLog.Error($"{opName} 失败：{ex.Message}",
                         where: $"MainViewModel.Safe({opName})", ex: ex);
            Log($"{opName} 失败：{ex.Message}", MessageLevel.Err);
        }
    }

    private void Log(string text, MessageLevel level = MessageLevel.Info, string? where = null)
        => Log(text, level, where, ex: null);

    /// <summary>
    /// UI 消息 + 分级落盘（v5.24.0）。v0.5.43 增加 <paramref name="ex"/>：
    ///
    /// ⚠️ 为什么要有这个重载：以前调用处普遍写成**两行**
    /// <code>
    /// AppLog.Error(ex.Message, where: "...", ex: ex);   // 落盘（带堆栈）
    /// Log($"xx 失败：{ex.Message}", MessageLevel.Err);   // 冒泡到 UI
    /// </code>
    /// 而 <c>Log</c> **自己也会写一条 AppLog.Error** → **同一次失败写了两条日志**，
    /// 其中一条还丢掉了异常详情。现在统一成一行：
    /// <code>Log($"xx 失败：{ex.Message}", MessageLevel.Err, where: "...", ex: ex);</code>
    /// </summary>
    private void Log(string text, MessageLevel level, string? where, Exception? ex)
    {
        // UI 消息同步写入分级日志（info/warn/error）落盘。
        var site = where ?? "UI";
        switch (level)
        {
            case MessageLevel.Err: AppLog.Error(text, site, ex: ex); break;
            case MessageLevel.Warn: AppLog.Warn(text, site, ex: ex); break;
            default: AppLog.Info(text, site); break;   // Info / Ok / Dim
        }

        var act = () =>
        {
            Messages.Add(new Message(DateTime.Now.ToString("HH:mm:ss"), text, level));
            while (Messages.Count > 400) Messages.RemoveAt(0);
        };
        if (Dispatcher.UIThread.CheckAccess()) act();
        else Dispatcher.UIThread.Post(act);
    }

    // ================================================================ 调色板
    [RelayCommand]
    private void OpenPalette() => PaletteOpen = true;

    [RelayCommand]
    private void ClosePalette() => PaletteOpen = false;

    public void ApplyPaletteColor(string hex)
    {
        BrushColor = hex;
        PaletteOpen = false;
        Log($"画笔颜色 → {hex}", MessageLevel.Info);
    }

    // ================================================================ 释放（P0-2）

    /// <summary>
    /// 释放 VM 持有的全部非托管位图。应用退出时调用（见 <c>App</c> 的退出处理）；
    /// 也供测试断言"关掉之后不再持有位图"。
    /// 幂等：可重复调用。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ClearAndDispose(History);
        ClearAndDispose(BatchResults);
        ClearAndDispose(RefImages);

        PreviewImage?.Dispose();
        PreviewImage = null;
        PartialImage?.Dispose();
        PartialImage = null;
        OnPropertyChanged(nameof(HasPartialImage));
    }

    private bool _disposed;
}
