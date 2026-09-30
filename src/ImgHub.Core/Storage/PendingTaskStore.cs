using System.Text.Json;
using System.Text.Json.Nodes;
using ImgHub.Core.Diagnostics;
using ImgHub.Core.Models;

namespace ImgHub.Core.Storage;

/// <summary>一条"已提交但未完成"的生图任务。</summary>
public sealed class PendingTask
{
    /// <summary>服务端任务号（APIMart 的 task_id；OpenRouter 同步接口为空）。</summary>
    public string TaskId { get; set; } = "";
    /// <summary>提交到哪个 provider（恢复时必须用它，而不是"当前选中的"）。</summary>
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Quality { get; set; } = "";
    public string Aspect { get; set; } = "";
    public string Resolution { get; set; } = "";
    public string OutputFormat { get; set; } = "";
    public int N { get; set; } = 1;
    /// <summary>提交时间（Unix 秒）。</summary>
    public double CreatedAt { get; set; }
    /// <summary>pending | done | failed | timeout | sent（同步接口已发出）</summary>
    public string Status { get; set; } = "pending";
    /// <summary>最近一次失败/超时原因（便于用户判断）。</summary>
    public string Note { get; set; } = "";
    /// <summary>已拿到的图片 URL（下载失败时留作重试线索）。</summary>
    public List<string> ImageUrls { get; set; } = new();

    public bool IsOpen => Status is "pending" or "timeout";
}

/// <summary>
/// 「未完成生成任务」持久化（用户要求：提交后意外退出，也能找回图片）。
///
/// 设计要点：
///   · 文件 = <c>&lt;数据目录&gt;/pending_tasks.jsonl</c>，**一行一条**，可人工编辑；
///   · **提交拿到 task_id 后立即写入** —— 这一步是"退出也能找回"的关键；
///   · 恢复时只做 <c>GET /tasks/{id}</c>（**查询，不重新提交 → 不重复扣费**）；
///   · **绝不保存 API key**（只记 provider 名，key 恢复时现读）；
///   · 写失败只记日志不抛（不阻断生成主流程）。
/// </summary>
public sealed class PendingTaskStore
{
    private readonly string _filePath;
    private readonly object _lock = new();

    public PendingTaskStore(string homePath)
    {
        _filePath = Path.Combine(homePath, "pending_tasks.jsonl");
    }

    public string FilePath => _filePath;

    // ---------------------------------------------------------------- 读
    /// <summary>读取全部条目（按提交时间倒序）。损坏行跳过并记日志。</summary>
    public List<PendingTask> LoadAll()
    {
        var list = new List<PendingTask>();
        try
        {
            if (!File.Exists(_filePath)) return list;
            foreach (var line in File.ReadAllLines(_filePath))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                try
                {
                    var node = JsonNode.Parse(t);
                    if (node is not JsonObject o) continue;
                    list.Add(FromNode(o));
                }
                catch (Exception ex)
                {
                    AppLog.Warn("pending_tasks.jsonl 有一行无法解析（已跳过）",
                                "PendingTaskStore.LoadAll", detail: t, ex: ex);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("pending_tasks.jsonl 读取失败", "PendingTaskStore.LoadAll",
                        detail: _filePath, ex: ex);
        }
        return list.OrderByDescending(x => x.CreatedAt).ToList();
    }

    /// <summary>
    /// 还没完成的任务（恢复流程用）。
    /// 包含 <c>pending</c> / <c>timeout</c>（可重试）与 <c>sent</c>
    /// （同步接口已发出、需**如实告知**用户去后台核对）。
    /// </summary>
    public List<PendingTask> LoadOpen()
        => LoadAll().Where(t => t.IsOpen || t.Status == "sent").ToList();

    private static PendingTask FromNode(JsonObject o)
    {
        string S(string k) => o[k]?.GetValue<string>() ?? "";
        int I(string k) { try { return o[k]?.GetValue<int>() ?? 1; } catch { return 1; } }
        double D(string k) { try { return o[k]?.GetValue<double>() ?? 0; } catch { return 0; } }
        var urls = new List<string>();
        if (o["image_urls"] is JsonArray arr)
            foreach (var u in arr) if (u is not null) urls.Add(u.GetValue<string>());

        return new PendingTask
        {
            TaskId = S("task_id"),
            Provider = S("provider"),
            Model = S("model"),
            Prompt = S("prompt"),
            Quality = S("quality"),
            Aspect = S("aspect"),
            Resolution = S("resolution"),
            OutputFormat = S("output_format"),
            N = I("n"),
            CreatedAt = D("created_at"),
            Status = string.IsNullOrEmpty(S("status")) ? "pending" : S("status"),
            Note = S("note"),
            ImageUrls = urls,
        };
    }

    // ---------------------------------------------------------------- 写
    /// <summary>登记一批刚提交的任务（**拿到 task_id 后立即调用**）。</summary>
    public void Add(IEnumerable<string> taskIds, string provider, string model,
                    string prompt, int n,
                    string quality = "", string aspect = "",
                    string resolution = "", string outputFormat = "")
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        lock (_lock)
        {
            foreach (var id in taskIds)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                Append(new PendingTask
                {
                    TaskId = id.Trim(),
                    Provider = provider,
                    Model = model,
                    Prompt = prompt,
                    Quality = quality,
                    Aspect = aspect,
                    Resolution = resolution,
                    OutputFormat = outputFormat,
                    N = Math.Max(1, n),
                    CreatedAt = now,
                    Status = "pending",
                });
            }
        }
    }

    /// <summary>
    /// 登记一条「同步接口已发出请求」记录（OpenRouter 等无 task_id 的接口）。
    /// 目的：重启后能**如实告知**用户"上次请求可能已完成并扣费"。
    /// 返回 <c>created_at</c>（作为随后清理的 key）。
    /// </summary>
    public double AddSyncSent(string provider, string model, string prompt,
                              string quality = "", string aspect = "",
                              string resolution = "", string outputFormat = "")
    {
        lock (_lock)
        {
            var t = new PendingTask
            {
                TaskId = "",      // 同步接口没有任务号
                Provider = provider,
                Model = model,
                Prompt = prompt,
                Quality = quality,
                Aspect = aspect,
                Resolution = resolution,
                OutputFormat = outputFormat,
                N = 1,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                Status = "sent",
            };
            Append(t);
            return t.CreatedAt;
        }
    }

    /// <summary>
    /// 同步请求**正常完成**后清除对应的 sent 记录（避免正常流程留下垃圾）。
    /// 若进程在响应前被杀，记录会留在文件里 → 重启时如实提示用户。
    /// </summary>
    public void ClearSyncSent(double createdAt)
    {
        lock (_lock)
        {
            try
            {
                var all = LoadAll();
                var keep = all.Where(t => !(t.Status == "sent" &&
                                            Math.Abs(t.CreatedAt - createdAt) < 0.001)).ToList();
                if (keep.Count != all.Count) Rewrite(keep);
            }
            catch (Exception ex)
            {
                AppLog.Warn("清理 sent 记录失败", "PendingTaskStore.ClearSyncSent", ex: ex);
            }
        }
    }

    private void Append(PendingTask t)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.AppendAllText(_filePath, ToNode(t).ToJsonLine() + Environment.NewLine);
            AppLog.Info($"已登记未完成任务 {t.TaskId}（{t.Provider}）",
                        "PendingTaskStore.Add");
        }
        catch (Exception ex)
        {
            // 写不进去只记日志 —— 不能因为登记失败而不让用户生成
            AppLog.Error("pending_tasks.jsonl 写入失败（不影响本次生成）",
                         "PendingTaskStore.Append", detail: _filePath, ex: ex);
        }
    }

    /// <summary>更新某任务状态（done/failed/timeout + 备注）。</summary>
    public void Update(string taskId, string status, string note = "",
                       IEnumerable<string>? imageUrls = null)
    {
        if (string.IsNullOrEmpty(taskId)) return;
        lock (_lock)
        {
            try
            {
                var all = LoadAll();
                var target = all.FirstOrDefault(t => t.TaskId == taskId);
                if (target is null) return;
                target.Status = status;
                if (!string.IsNullOrEmpty(note)) target.Note = note;
                if (imageUrls is not null) target.ImageUrls = imageUrls.ToList();
                Rewrite(all);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"更新任务 {taskId} 状态失败", "PendingTaskStore.Update", ex: ex);
            }
        }
    }

    /// <summary>
    /// 清理：移除已完成（done）条目；failed/timeout 超过保留上限或时效也移除。
    /// </summary>
    public void Compact(int keepFailed = 100, int keepDays = 30)
    {
        lock (_lock)
        {
            try
            {
                var all = LoadAll();
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                var cutoff = now - keepDays * 86400.0;

                var keep = all.Where(t => t.Status != "done").ToList();
                // 超龄的 failed 先丢
                keep = keep.Where(t => !(t.Status == "failed" && t.CreatedAt < cutoff)).ToList();
                // 超出上限的 failed 丢最旧的
                var failed = keep.Where(t => t.Status == "failed").OrderByDescending(t => t.CreatedAt).ToList();
                if (failed.Count > keepFailed)
                {
                    var drop = failed.Skip(keepFailed).Select(t => t.TaskId).ToHashSet();
                    keep = keep.Where(t => !drop.Contains(t.TaskId)).ToList();
                }
                if (keep.Count != all.Count) Rewrite(keep);
            }
            catch (Exception ex)
            {
                AppLog.Warn("pending_tasks.jsonl 清理失败", "PendingTaskStore.Compact", ex: ex);
            }
        }
    }

    /// <summary>清空全部（测试/手动重置用）。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            try { if (File.Exists(_filePath)) File.Delete(_filePath); }
            catch (Exception ex)
            {
                AppLog.Warn("pending_tasks.jsonl 删除失败", "PendingTaskStore.Clear", ex: ex);
            }
        }
    }

    private void Rewrite(IEnumerable<PendingTask> items)
    {
        var lines = items.Select(t => ToNode(t).ToJsonLine());
        var tmp = _filePath + ".tmp";
        File.WriteAllLines(tmp, lines);
        File.Move(tmp, _filePath, overwrite: true);
    }

    private static JsonObject ToNode(PendingTask t)
    {
        var o = new JsonObject
        {
            ["task_id"] = t.TaskId,
            ["provider"] = t.Provider,
            ["model"] = t.Model,
            ["prompt"] = t.Prompt,
            ["quality"] = t.Quality,
            ["aspect"] = t.Aspect,
            ["resolution"] = t.Resolution,
            ["output_format"] = t.OutputFormat,
            ["n"] = t.N,
            ["created_at"] = t.CreatedAt,
            ["status"] = t.Status,
            ["note"] = t.Note,
        };
        if (t.ImageUrls.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var u in t.ImageUrls) arr.AddString(u);
            o["image_urls"] = arr;
        }
        // ⚠️ 绝不写入 API key
        return o;
    }
}
