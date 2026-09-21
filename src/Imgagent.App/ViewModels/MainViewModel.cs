using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Imgagent.App.Services;
using Imgagent.Core;
using Imgagent.Core.Http;
using Imgagent.Core.Imaging;
using Imgagent.Core.Models;
using Imgagent.Core.Services;
using Imgagent.Core.Storage;

namespace Imgagent.App.ViewModels;

public partial class MainViewModel : ObservableObject
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
        _offline = _sess.Config.Offline;

        foreach (var p in Catalog.Providers)
            Providers.Add(new ProviderOption(p.Provider, p.Label));
        RebuildModelChoices();
        OnPropertyChanged(nameof(SelectedProvider));
        UpdateEstimate();
        RefreshConfigured();

        Log($"imgagent 工作台已启动 · {_svc.Platform.PlatformName}");
        Log($"数据目录 {_sess.HomePath}");
        RefreshHistory();
        RefreshPromptHistory();
        if (_sess.Current is { } first) _ = ShowPreviewAsync(first);
    }

    // ================================================================ 可绑定
    [ObservableProperty] private string _prompt;
    [ObservableProperty] private string _quality;
    [ObservableProperty] private string _aspect;
    [ObservableProperty] private string _resolution;
    [ObservableProperty] private string _outputFormat;
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
    [ObservableProperty] private bool _regionMode;
    [ObservableProperty] private string _brushColor = "#FF3B30";
    [ObservableProperty] private double _brushSize = 24;

    /// <summary>可用粗细档（像素）。UI 用 Slider 或下拉都能绑。</summary>
    public double BrushSizeMin => 2;
    public double BrushSizeMax => 80;

    /// <summary>粗细显示文本（供 UI 展示当前值）。</summary>
    public string BrushSizeText => $"{BrushSize:0}px";
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
    public string HomePath => _sess.HomePath;
    public string[] QualityOptions => Catalog.QualityChoices(_provider, _model);
    public string[] AspectOptions => Catalog.Aspects;
    public string[] ResolutionOptions => Catalog.Resolutions;
    public string[] OutputFormatOptions => Catalog.OutputFormats;
    /// <summary>与 RegionCanvas.RegionTool 枚举顺序严格对应：
    /// 0=马克笔（薄层涂抹）1=画笔 2=方框 3=圆圈 4=橡皮。</summary>
    public IReadOnlyList<string> ToolNames { get; } =
        new[] { "马克笔", "画笔", "方框", "圆圈", "橡皮" };
    public IReadOnlyList<string> PaletteColors { get; } = new[]
    {
        "#FF3B30", "#FF9500", "#FFD60A", "#32D74B", "#00C7BE", "#0A84FF",
        "#5E5CE6", "#BF5AF2", "#FF2D55", "#A2845E", "#FFFFFF", "#C7C7CC",
        "#8E8E93", "#636366", "#48484A", "#1C1C1E",
    };
    public Controls.RegionCanvas.RegionTool CanvasTool =>
        (Controls.RegionCanvas.RegionTool)Math.Clamp(_toolIndex, 0, 4);
    public string PolishCounterText =>
        PolishCandidates.Count == 0 ? "" : $"{PolishIndex + 1}/{PolishCandidates.Count}";

    // ================================================================ 状态
    public bool IsConfigured => !string.IsNullOrEmpty(LoadApiKey()) || Offline;
    public bool CanRun => IsConfigured;
    public bool NeedsSetup => !IsConfigured;

    // ================================================================ 响应式布局
    /// <summary>
    /// 宽屏（≥900px）三栏并排；窄屏（<900px，手机竖屏）改纵向堆叠。
    /// 由视图在 SizeChanged 时设置（Avalonia 无内置媒体查询）。
    /// </summary>
    [ObservableProperty] private bool _isWideLayout = true;

    /// <summary>
    /// 调试选项是否可见（#3：离线模式归入 debug 功能）。
    /// 仅当环境变量 IMGAGENT_DEBUG=1 时为 true。
    /// </summary>
    public bool ShowDebugOptions =>
        string.Equals(Environment.GetEnvironmentVariable("IMGAGENT_DEBUG"), "1",
                      StringComparison.OrdinalIgnoreCase);

    /// <summary>是否显示按钮图标（用户需求 #7：可在设置里开关）。默认开。</summary>
    [ObservableProperty] private bool _useIcons = true;


    /// <summary>参考图是否保留（默认 false = 一次性，用完即清，避免污染后续任务）。</summary>
    [ObservableProperty] private bool _keepRefImages;
    public string SetupHint => "尚未配置 API key —— 点右下角「设置」填写后即可开始生成";
    public bool HasBatchResults => BatchResults.Count > 1;

    private void RefreshConfigured()
    {
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(NeedsSetup));
        OnPropertyChanged(nameof(CanRun));
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
    partial void OnProviderChanged(ApiProvider value)
    {
        if (_switchingProvider) return;
        _switchingProvider = true;
        try
        {
            OnPropertyChanged(nameof(ProviderLabel));
            OnPropertyChanged(nameof(SelectedProvider));
            OnPropertyChanged(nameof(KeyHint));
            _sess.Config.Provider = value.Key();
            var nextModel = Catalog.ModelMatchesProvider(Model, value)
                ? Model : Catalog.DefaultModel(value);
            if (string.IsNullOrEmpty(nextModel)) nextModel = Catalog.DefaultModel(value);
            Model = nextModel;
            if (!Catalog.QualitySupported(value, Quality, Model)) Quality = "low";
            RebuildModelChoices();
            RebuildQualityOptions();
            _svc.ImageApi = new ImageApi(value, _svc.Http, null);
            PersistConfig();
            ProviderCost = _sess.CostForProvider(value);
            Log($"Provider → {value.Label()} · 模型 → {Model}", MessageLevel.Ok);
        }
        finally { _switchingProvider = false; }
    }

    partial void OnQualityChanged(string value) { UpdateEstimate(); PersistConfig(); }
    partial void OnResolutionChanged(string value) { UpdateEstimate(); PersistConfig(); }
    partial void OnBatchNChanged(int value) { UpdateEstimate(); PersistConfig(); }
    partial void OnAspectChanged(string value) => PersistConfig();
    partial void OnOutputFormatChanged(string value) => PersistConfig();
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
    partial void OnModelChanged(string value)
    {
        _sess.Config.Model = value;
        if (!Catalog.QualitySupported(_provider, _quality, value))
        {
            var old = _quality;
            Quality = "low";
            Log($"模型不支持 {old}，质量已回退 low", MessageLevel.Warn);
        }
        RebuildQualityOptions();
        UpdateEstimate();     // 模型变了 → 预估按新模型统计重算（可能变'暂无'）
        PersistConfig();
    }
    partial void OnToolIndexChanged(int value) => OnPropertyChanged(nameof(CanvasTool));

    partial void OnBrushSizeChanged(double value) => OnPropertyChanged(nameof(BrushSizeText));
    partial void OnPolishIndexChanged(int value) => OnPropertyChanged(nameof(PolishCounterText));

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
                try { _sess.SaveConfig(); } catch { }
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
        try { _sess.SaveConfig(); } catch { }
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
        EstimatedCostText = $"预估 ≈ ${cost:0.0000}（{_batchN} 张 · {model} · 历史平均 ${avg:0.0000}/次）";
    }

    // ================================================================ 生成
    [RelayCommand] private async Task GenerateAsync() => await RunGenerationAsync(false);
    [RelayCommand] private async Task EditAsync() => await RunGenerationAsync(true);

    private async Task RunGenerationAsync(bool editMode)
    {
        if (Busy) return;
        if (!CanRun) { Log("尚未配置 API key", MessageLevel.Warn); return; }
        var prompt = (Prompt ?? "").Trim();

        List<(byte[], string)>? refs = null;
        if (editMode)
        {
            if (_sess.Items.Count == 0) { Log("还没有可编辑的图", MessageLevel.Warn); return; }
            if (prompt.Length == 0) { Log("请输入编辑要求", MessageLevel.Warn); return; }
        }
        else if (prompt.Length == 0) { Log("请输入提示词", MessageLevel.Warn); return; }

        _sess.PushPromptHistory(prompt, editMode ? "edit" : "gen");
        RefreshPromptHistory();

        if (editMode)
        {
            refs = new List<(byte[], string)>();
            var cur = _sess.Current;
            if (cur is not null)
            {
                var bp = cur.Path(_sess.HomePath);
                if (File.Exists(bp))
                {
                    var raw = await File.ReadAllBytesAsync(bp);
                    refs.Add((ImageCodec.ShrinkForReference(raw), "image/png"));
                }
            }
            foreach (var rf in RefImages)
            {
                var rp = rf.Item.Path(_sess.HomePath);
                if (!File.Exists(rp)) continue;
                var rb = await File.ReadAllBytesAsync(rp);
                refs.Add((ImageCodec.ShrinkForReference(rb), "image/png"));
                if (refs.Count >= Catalog.MaxRefs) break;
            }
            if (refs.Count == 0) { Log("找不到当前图片文件", MessageLevel.Err); return; }
        }

        Busy = true;
        Status = editMode ? "编辑中…" : "生成中…";
        BatchResults.Clear();                      // 清掉上一次的缩略图（本次会重填）
        OnPropertyChanged(nameof(HasBatchResults));
        FlushConfig();
        var t0 = DateTime.UtcNow;
        try
        {
            var progress = new Progress<string>(msg => Log(msg, MessageLevel.Info));
            var key = LoadApiKey();
            var res = await _svc.ImageApi.GenerateAsync(
                prompt, key, _model, _quality, _aspect, Math.Max(1, _batchN),
                refs, Offline, _sess.Counter, _resolution, _outputFormat, progress);
            await LandResultsAsync(res, prompt, editMode, t0);
        }
        catch (Exception ex)
        {
            Log($"生成失败：{ex.Message}", MessageLevel.Err);
            foreach (var line in ErrorHints.Explain(ex).Skip(1)) Log(line.Trim(), MessageLevel.Dim);
            Prompt = prompt;
        }
        finally
        {
            Busy = false;
            Status = Offline ? "离线" : "就绪";
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
            var batch = _sess.Items.Take(done).Reverse().ToList();
            PublishBatchResults(batch);
            await ShowPreviewAsync(_sess.Current);
            Prompt = "";
        }
        else
        {
            Log("生成失败：图片未能保存", MessageLevel.Err);
            BatchResults.Clear();                  // 失败清空缩略图（避免显示旧结果）
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
        catch (Exception ex) { Log($"落盘失败：{ex.Message}", MessageLevel.Err); return null; }
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
            var path = SaveImageToHome(data, media, "参考_" + name, _sess.Counter + 1);
            if (path is null) return;
            var item = new Item { File = Path.GetFileName(path), Prompt = "参考图：" + name, Kind = "import" };
            RefImages.Add(new PreviewThumb(item) { HomePath = _sess.HomePath });
            Log($"参考图已加入（{RefImages.Count} 张）", MessageLevel.Ok);
        }
        catch (Exception ex) { Log($"参考图加入失败：{ex.Message}", MessageLevel.Err); }
    }

    [RelayCommand]
    private void RemoveRefImage(PreviewThumb? thumb)
    {
        if (thumb is null) return;
        RefImages.Remove(thumb);
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
    private void DeleteHistoryItem(Imgagent.Core.Models.Item item)
    {
        Safe(() =>
        {
            if (item is null) return;
            if (_sess.RemoveItem(item))
            {
                var path = item.Path(_sess.HomePath);
                try { if (File.Exists(path)) File.Delete(path); } catch { }
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
    private void DeleteListItem(Imgagent.Core.Models.Item item)
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

    private void UndoCore()
    {
        if (_sess.Items.Count == 0) { Log("没有可撤回的图", MessageLevel.Warn); return; }
        var removed = _sess.Items[0];
        _sess.Items.RemoveAt(0);
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
        PreviewPath = path;
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path);
            Dispatcher.UIThread.Post(() =>
            {
                try { PreviewImage = new Bitmap(new MemoryStream(bytes)); } catch { }
            });
        }
        catch (Exception ex) { Log($"预览失败：{ex.Message}", MessageLevel.Err); }
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
        if (PreviewPath is not null) await _svc.Storage.OpenInExternalViewerAsync(PreviewPath);
    }

    [RelayCommand]
    private async Task SaveToGalleryAsync()
    {
        var srcPath = PreviewPath ?? (_sess.Current is { } c ? c.Path(_sess.HomePath) : null);
        if (srcPath is null || !File.Exists(srcPath)) return;
        try
        {
            var data = await File.ReadAllBytesAsync(srcPath);
            var saved = await _svc.Storage.SaveToGalleryAsync(
                data, Path.GetFileName(srcPath), ImageCodec.MimeForExt(Path.GetExtension(srcPath)));
            Log(saved is null ? "未保存（用户取消）" : $"已保存到 {saved}", MessageLevel.Ok);
        }
        catch (Exception ex) { Log($"保存失败：{ex.Message}", MessageLevel.Err); }
    }

    // ================================================================ 设置
    [RelayCommand] private void ToggleOffline()
    {
        Offline = !Offline;
        Status = Offline ? "离线" : "就绪";
        Log($"离线模式 → {(Offline ? "开" : "关")}", MessageLevel.Ok);
    }

    [RelayCommand]
    private void RefreshHistory() => Safe(RefreshHistoryCore, "刷新历史");

    private void RefreshHistoryCore()
    {
        History.Clear();
        for (int i = 0; i < _sess.Items.Count; i++)
            History.Add(new HistoryRow(_sess.Items[i], _sess.HomePath));
        TotalCost = _sess.TotalCost;                          // = Items 求和
        ProviderCost = _sess.CostForProvider(_provider);      // 同源，天然一致
        UnknownCost = _sess.CostForUnknownProvider();
    }

    [RelayCommand]
    private void RefreshPromptHistory() => Safe(RefreshPromptHistoryCore, "刷新提示词历史");

    private void RefreshPromptHistoryCore()
    {
        PromptHistory.Clear();
        foreach (var p in _sess.LoadPromptHistory(limit: 50)) PromptHistory.Add(p);
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
        catch (Exception ex) { Log($"拉取模型失败：{ex.Message}", MessageLevel.Err); }
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
            var env = Provider == ApiProvider.Apimart
                ? Environment.GetEnvironmentVariable("IMGAGENT_APIMART_API_KEY")
                  ?? Environment.GetEnvironmentVariable("IMGAGENT_API_KEY")
                : Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
                  ?? Environment.GetEnvironmentVariable("IMGAGENT_API_KEY");
            if (!string.IsNullOrEmpty(env)) return env.Trim();

            var providerKey = Provider.Key();
            var f = _sess.KeyFile(Provider);

            // 缓存命中：同 provider 且文件未改动
            if (_cachedKeyValid && _cachedKeyProvider == providerKey)
            {
                var mtime = File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue;
                if (mtime == _cachedKeyMtime) return _cachedKey;
            }

            // 重读
            var val = "";
            if (File.Exists(f))
            {
                // 共享读：避免"文件被占用"导致异常
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                val = (sr.ReadToEnd() ?? "").Trim();
            }

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
        KeyStatusText = string.IsNullOrEmpty(k)
            ? "未配置" : (k.Length > 10 ? k[..6] + "..." + k[^4..] : "已配置");
    }

    [RelayCommand]
    private void OpenSettings() => Safe(OpenSettingsCore, "打开设置");

    private void OpenSettingsCore()
    {
        ApiKeyInput = LoadApiKey();
        PolishBaseInput = _svc.Polish.BaseUrl;
        PolishModelInput = _svc.Polish.Model;
        PolishKeyInput = _svc.Polish.ApiKey;
        RefreshKeyStatus();
        SettingsOpen = true;
    }

    [RelayCommand]
    private void CloseSettings() => SettingsOpen = false;

    [RelayCommand]
    private void SaveSettings() => Safe(SaveSettingsCore, "保存设置");

    private void SaveSettingsCore()
    {
        if (!string.IsNullOrWhiteSpace(ApiKeyInput))
            _sess.SaveKey(ApiKeyInput.Trim(), Provider);
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
            BatchResults.Clear();
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
        catch (Exception ex) { Log($"润色失败：{ex.Message}", MessageLevel.Err); }
        finally { Busy = false; Status = Offline ? "离线" : "就绪"; }
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

    // ================================================================ 区域标注
    [RelayCommand]
    private void ToggleRegionMode() => Safe(ToggleRegionModeCore, "切换标注模式");

    private void ToggleRegionModeCore()
    {
        if (_sess.Current is null) { Log("还没有图可标注", MessageLevel.Warn); return; }
        RegionMode = !RegionMode;
        if (RegionMode) { RegionCount = 0; Log("区域标注已开启", MessageLevel.Info); }
        else Log("区域标注已关闭", MessageLevel.Info);
    }

    [RelayCommand]
    private void ClearRegions() { RegionCount = 0; Log("已清除全部标注", MessageLevel.Info); }

    /// <summary>标注区域蒙版路径（白=要改，黑=保留）。可随参考图一起送给模型。</summary>
    [ObservableProperty] private string _annotatedMaskPath = "";

    /// <summary>保存「修改区域蒙版」到数据目录（供支持 mask 的模型使用）。</summary>
    public void SetAnnotatedMask(byte[] maskPng)
    {
        try
        {
            var name = Session.SafeFilename(
                $"mask_{DateTime.Now:MMdd_HHmmss}", "region", ".png", limit: 24);
            var path = Path.Combine(_sess.HomePath, name);
            File.WriteAllBytes(path, maskPng);
            AnnotatedMaskPath = path;
            Log($"修改区域蒙版已保存：{name}", MessageLevel.Info);
        }
        catch (Exception ex) { Log($"蒙版保存失败：{ex.Message}", MessageLevel.Warn); }
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
        catch (Exception ex) { Log($"标注保存失败：{ex.Message}", MessageLevel.Err); }
    }

    [RelayCommand]
    private async Task EditWithRegionsAsync()
    {
        if (Busy) return;
        if (RegionCount == 0) { Log("还没有画任何标注", MessageLevel.Warn); return; }
        if (string.IsNullOrEmpty(AnnotatedPath) || !File.Exists(AnnotatedPath))
        { Log("请先圈画要修改的区域", MessageLevel.Warn); return; }
        if (_sess.Current is null) { Log("没有可编辑的图", MessageLevel.Warn); return; }
        var userPrompt = (Prompt ?? "").Trim();
        if (userPrompt.Length == 0) { Log("请描述要如何修改", MessageLevel.Warn); return; }

        var refs = new List<(byte[], string)>();
        var raw = await File.ReadAllBytesAsync(AnnotatedPath);
        refs.Add((ImageCodec.ShrinkForReference(raw), "image/png"));
        foreach (var rf in RefImages)
        {
            var rp = rf.Item.Path(_sess.HomePath);
            if (!File.Exists(rp)) continue;
            var rb = await File.ReadAllBytesAsync(rp);
            refs.Add((ImageCodec.ShrinkForReference(rb), "image/png"));
            if (refs.Count >= Catalog.MaxRefs) break;
        }

        var finalPrompt = $"{userPrompt}（图中高亮区域即需要修改的部分，请仅修改这些区域）";
        _sess.PushPromptHistory(finalPrompt, "edit");
        RefreshPromptHistory();
        Busy = true;
        Status = "编辑中…";
        try
        {
            var res = await _svc.ImageApi.GenerateAsync(
                finalPrompt, LoadApiKey(), _model, _quality, _aspect,
                Math.Max(1, _batchN), refs, Offline, _sess.Counter,
                _resolution, _outputFormat, new Progress<string>(m => Log(m)));
            await LandResultsAsync(res, finalPrompt, true, DateTime.UtcNow);
            RegionMode = false;
            // 参考图默认「一次性」：用完即清，避免污染后续无关任务
            if (RefImages.Count > 0 && !KeepRefImages)
            {
                RefImages.Clear();
                Log("参考图已用完并清空（如需复用请重新添加）", MessageLevel.Info);
            }
        }
        catch (Exception ex) { Log($"区域编辑失败：{ex.Message}", MessageLevel.Err); }
        finally { Busy = false; Status = Offline ? "离线" : "就绪"; }
    }

    // ================================================================ 多图
    private void PublishBatchResults(IEnumerable<Item> items)
    {
        BatchResults.Clear();
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
    partial void OnDarkThemeChanged(bool value) => ApplyTheme(value);

    // ================================================================ 状态栏
    [RelayCommand]
    private void ShowShortcuts()
    {
        Log("Ctrl+G 生成 · Ctrl+D 编辑 · Ctrl+R 撤回 · Ctrl+L 预览 · Ctrl+S 设置", MessageLevel.Info);
    }

    [RelayCommand]
    private void ShowDataDir() => Log($"数据目录：{_sess.HomePath}", MessageLevel.Info);

    // ================================================================ 消息
    public enum MessageLevel { Info, Ok, Warn, Err, Dim }
    public sealed record Message(string Time, string Text, MessageLevel Level);

    public void LogPublic(string text, MessageLevel level = MessageLevel.Info) => Log(text, level);

    /// <summary>
    /// 命令安全执行包装：捕获任何异常并转为日志，**绝不让 UI 线程崩溃**。
    /// 所有同步 [RelayCommand] 都应通过它执行（避免个别异常点炸掉整个应用）。
    /// </summary>
    private void Safe(Action action, string opName)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log($"{opName} 失败：{ex.Message}", MessageLevel.Err);
        }
    }

    private void Log(string text, MessageLevel level = MessageLevel.Info)
    {
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
}
