using System.Text.Json;
using ImgHub.Core.Diagnostics;
using ImgHub.Core.Models;

namespace ImgHub.Core.Services;

/// <summary>
/// 「provider+模型」花费统计与价格预估。
///
/// 需求（用户 #10）：
///   · 预估绑定到**每一个 provider 的每一个模型**；
///   · **用过** → 按该模型历史**平均花费**预估；
///   · **没用过** → **不显示预估**（避免用错表导致误导）；
///   · 数据保存为 JSON（可读）。
/// </summary>
public sealed class ModelStatsService
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private ModelStatsFile? _cache;

    public ModelStatsService(string homePath)
    {
        _filePath = Path.Combine(homePath, "model_stats.json");
        Load();
    }

    public string FilePath => _filePath;

    // ---------------------------------------------------------------- 读写
    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) { _cache = new ModelStatsFile(); return; }
            // source-gen（AOT 安全）
            _cache = JsonSerializer.Deserialize(
                File.ReadAllText(_filePath), AppJson.Default.ModelStatsFile)
                ?? new ModelStatsFile();
        }
        catch (Exception ex)
        {
            // 损坏配置：重建（不阻断启动），但留痕便于排查
            AppLog.Warn("model_stats.json 读取失败，已重建统计",
                        "ModelStatsService.Load", detail: _filePath, ex: ex);
            _cache = new ModelStatsFile();
        }
    }

    private void Save()
    {
        try
        {
            if (_cache is null) return;
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _filePath + ".tmp";
            // source-gen（AOT 安全）；不再用裸 JsonSerializerOptions（隐式反射）
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                _cache, AppJson.Default.ModelStatsFile));
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("model_stats.json 写入失败（不影响生成）",
                        "ModelStatsService.Save", detail: _filePath, ex: ex);
        }
    }

    private static string KeyOf(string provider, string model)
        => $"{provider}|{model}";

    // ---------------------------------------------------------------- API
    /// <summary>记录一次成功生成（用于统计平均花费）。</summary>
    public void Record(string provider, string model, double cost, int images)
    {
        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(model)) return;
        lock (_lock)
        {
            _cache ??= new ModelStatsFile();
            var key = KeyOf(provider, model);
            if (!_cache.Models.TryGetValue(key, out var e))
            {
                e = new ModelStatsFileEntry { Provider = provider, Model = model };
                _cache.Models[key] = e;
            }
            e.Calls++;
            e.TotalCost += cost;
            e.TotalImages += Math.Max(1, images);
            e.AvgCost = e.Calls > 0 ? e.TotalCost / e.Calls : 0;
            e.LastUsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            Save();
        }
    }

    /// <summary>
    /// 取某「provider+模型」的平均单次花费。**没用过返回 null**（UI 不显示预估）。
    /// </summary>
    public double? GetAvgCost(string provider, string model)
    {
        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(model)) return null;
        lock (_lock)
        {
            if (_cache?.Models.TryGetValue(KeyOf(provider, model), out var e) == true
                && e.Calls > 0)
                return e.AvgCost;
            return null;
        }
    }

    /// <summary>是否用过该模型。</summary>
    public bool HasStats(string provider, string model)
        => GetStats(provider, model) is not null;

    /// <summary>取完整条目（可能为 null）。</summary>
    public ModelStatsFileEntry? GetStats(string provider, string model)
    {
        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(model)) return null;
        lock (_lock)
        {
            return _cache?.Models.GetValueOrDefault(KeyOf(provider, model));
        }
    }

    /// <summary>删除某个模型的统计（配合数据目录手动清理）。</summary>
    public void Remove(string provider, string model)
    {
        lock (_lock)
        {
            if (_cache is null) return;
            if (_cache.Models.Remove(KeyOf(provider, model))) Save();
        }
    }

    /// <summary>清空全部统计。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _cache = new ModelStatsFile();
            Save();
        }
    }
}