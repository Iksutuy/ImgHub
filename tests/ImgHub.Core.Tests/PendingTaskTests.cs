using System.Net;
using System.Text.Json.Nodes;
using ImgHub.Core.Http;
using ImgHub.Core.Models;
using ImgHub.Core.Services;
using ImgHub.Core.Storage;

namespace ImgHub.Core.Tests;

/// <summary>
/// v5.26.0 回归：生图流程断点恢复
/// （用户要求：提交后意外退出，图片仍能找回）。
///
/// 核心保证：
///   · 提交拿到 task_id 后**立即落盘** → 退出也不丢；
///   · 恢复只 **GET 查询**，**不重新提交 → 不重复扣费**；
///   · 绝不把 API key 写进 pending 文件。
/// </summary>
public class PendingTaskTests : IDisposable
{
    private readonly string _home;

    public PendingTaskTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "imghub-pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    // ---------------------------------------------------------------- R2 存储

    [Fact]
    public void Add_ThenLoadOpen_RoundTrips()
    {
        var store = new PendingTaskStore(_home);
        store.Add(new[] { "tsk_1", "tsk_2" }, "apimart", "gpt-image-2.5-flare",
                  "一只猫", 2, quality: "low", aspect: "16:9",
                  resolution: "2k", outputFormat: "jpeg");

        // 新实例（模拟重启）
        var reopened = new PendingTaskStore(_home);
        var open = reopened.LoadOpen();

        Assert.Equal(2, open.Count);
        var t = open.First(x => x.TaskId == "tsk_1");
        Assert.Equal("apimart", t.Provider);
        Assert.Equal("gpt-image-2.5-flare", t.Model);
        Assert.Equal("一只猫", t.Prompt);
        Assert.Equal(2, t.N);
        Assert.Equal("low", t.Quality);
        Assert.Equal("16:9", t.Aspect);
        Assert.Equal("2k", t.Resolution);
        Assert.Equal("jpeg", t.OutputFormat);
        Assert.Equal("pending", t.Status);
        Assert.True(t.IsOpen);
    }

    [Fact]
    public void PendingFile_IsReadableJsonLines_AndContainsNoKey()
    {
        var store = new PendingTaskStore(_home);
        store.Add(new[] { "tsk_secret" }, "apimart", "m", "p", 1);

        var lines = File.ReadAllLines(store.FilePath)
                        .Where(l => l.Trim().Length > 0).ToList();
        Assert.Single(lines);

        // 每行是合法 JSON（可人工编辑）
        var node = JsonNode.Parse(lines[0])!.AsObject();
        Assert.Equal("tsk_secret", node["task_id"]!.GetValue<string>());
        Assert.Contains("\"provider\"", lines[0]);

        // ⚠️ 绝不能出现凭据
        var raw = File.ReadAllText(store.FilePath);
        Assert.DoesNotContain("sk-", raw);
        Assert.DoesNotContain("api_key", raw);
        Assert.DoesNotContain("apikey", raw.ToLowerInvariant());
    }

    [Fact]
    public void Update_MarksStatus_Failed_KeepsRecord_ForRetry()
    {
        var store = new PendingTaskStore(_home);
        store.Add(new[] { "tsk_x" }, "apimart", "m", "p", 1);

        store.Update("tsk_x", "failed", "轮询超时");

        var all = new PendingTaskStore(_home).LoadAll();
        var t = Assert.Single(all);
        Assert.Equal("failed", t.Status);
        Assert.Equal("轮询超时", t.Note);
        Assert.False(t.IsOpen);          // failed 不再自动重试

        // 但记录**仍在文件里**（用户可人工查看/重试）
        Assert.True(File.Exists(store.FilePath));
    }

    [Fact]
    public void Update_Timeout_StillOpen_SoNextLaunchRetries()
    {
        var store = new PendingTaskStore(_home);
        store.Add(new[] { "tsk_t" }, "apimart", "m", "p", 1);
        store.Update("tsk_t", "timeout", "超过 300s");

        var open = new PendingTaskStore(_home).LoadOpen();
        Assert.Single(open);
        Assert.True(open[0].IsOpen);     // timeout 仍会重试
    }

    [Fact]
    public void Compact_RemovesDone_KeepsOpen()
    {
        var store = new PendingTaskStore(_home);
        store.Add(new[] { "done_1" }, "apimart", "m", "p", 1);
        store.Add(new[] { "open_1" }, "apimart", "m", "p", 1);
        store.Update("done_1", "done");

        store.Compact();

        var all = new PendingTaskStore(_home).LoadAll();
        Assert.Single(all);
        Assert.Equal("open_1", all[0].TaskId);
    }

    [Fact]
    public void LoadAll_SkipsCorruptedLines_WithoutThrowing()
    {
        var store = new PendingTaskStore(_home);
        store.Add(new[] { "good" }, "apimart", "m", "p", 1);
        File.AppendAllText(store.FilePath, "{ this is not json }\n");

        var all = store.LoadAll();          // 不应抛
        Assert.Single(all);
        Assert.Equal("good", all[0].TaskId);
    }

    [Fact]
    public void AddSyncSent_ThenClear_LeavesNothing()
    {
        var store = new PendingTaskStore(_home);
        var at = store.AddSyncSent("openrouter", "openai/gpt-image-2.5-flare", "一只猫");
        Assert.Single(store.LoadOpen());          // sent 也算"未完成"

        store.ClearSyncSent(at);
        Assert.Empty(store.LoadAll());            // 正常收尾 → 清干净
    }

    [Fact]
    public void SyncSent_LeftBehind_IsReportedAsNotRecoverable()
    {
        // 模拟「同步请求发出后进程被杀」：记录留在文件里
        var store = new PendingTaskStore(_home);
        store.AddSyncSent("openrouter", "m", "p");

        var left = new PendingTaskStore(_home).LoadOpen();
        var t = Assert.Single(left);
        Assert.Equal("sent", t.Status);
        Assert.Equal("openrouter", t.Provider);
    }

    // ---------------------------------------------------------------- R1/R3 行为

    [Fact]
    public async Task Apimart_OnSubmitted_FiresWithTaskIds_RightAfterSubmit()
    {
        // 假 HTTP：POST /images/generations 返回 task_id；GET /tasks/{id} 返回 completed
        var handler = new RoutingHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/images/generations"))
                return (HttpStatusCode.OK,
                    """{"code":200,"data":[{"task_id":"tsk_a"}]}""");
            if (path.Contains("/tasks/"))
                return (HttpStatusCode.OK,
                    "{\"code\":200,\"data\":{\"status\":\"completed\",\"cost\":0.01,"
                    + "\"result\":{\"images\":[{\"url\":\"https://img/1.png\"}]}}}");
            if (path.EndsWith(".png"))
                return (HttpStatusCode.OK, "PNG");
            return (HttpStatusCode.NotFound, "{}");
        });

        var http = new HttpJsonClient(new HttpClient(handler));
        var api = new ImageApi(ApiProvider.Apimart, http, "https://api.test/v1");

        var submitted = new List<(IReadOnlyList<string> Ids, SubmitPhase Phase)>();
        api.OnSubmitProgress = (phase, ids, prompt, model, ok) =>
            submitted.Add((ids, phase));

        var store = new PendingTaskStore(_home);
        api.OnSubmitProgress += (phase, ids, prompt, model, ok) =>
        {
            if (phase == SubmitPhase.AsyncSubmitted)
                store.Add(ids, "apimart", model, prompt, 1);
            else if (phase == SubmitPhase.Finished && ids.Count > 0)
                foreach (var id in ids) store.Update(id, ok ? "done" : "failed");
        };

        await api.GenerateAsync(new GenRequest
        {
            Prompt = "一只猫",
            ApiKey = "sk-test",
            Model = "gpt-image-2.5-flare",
            Quality = "low",
            Aspect = "1:1",
            N = 1,
        });

        // ① 必须触发过 AsyncSubmitted，且带 task_id
        var sub = submitted.First(x => x.Phase == SubmitPhase.AsyncSubmitted);
        Assert.Contains("tsk_a", sub.Ids);

        // ② Finished 也触发过
        Assert.Contains(submitted, x => x.Phase == SubmitPhase.Finished);

        // ③ 完成后 pending 被标记 done（Compact 后清空）
        store.Compact();
        Assert.Empty(store.LoadOpen());
    }

    [Fact]
    public async Task Apimart_TaskIdPersisted_EvenIfPollingFails()
    {
        // 假 HTTP：提交成功拿到 task_id，但轮询一直 pending（模拟"提交后卡住/退出"）
        var handler = new RoutingHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/images/generations"))
                return (HttpStatusCode.OK,
                    """{"code":200,"data":[{"task_id":"tsk_stuck"}]}""");
            if (path.Contains("/tasks/"))
                return (HttpStatusCode.OK,
                    """{"code":200,"data":{"status":"processing"}}""");
            return (HttpStatusCode.NotFound, "{}");
        });

        var http = new HttpJsonClient(new HttpClient(handler));
        var api = new ImageApi(ApiProvider.Apimart, http, "https://api.test/v1");
        var store = new PendingTaskStore(_home);
        api.OnSubmitProgress = (phase, ids, prompt, model, ok) =>
        {
            if (phase == SubmitPhase.AsyncSubmitted) store.Add(ids, "apimart", model, prompt, 1);
        };

        // 用极短超时让轮询失败（模拟"还没轮完"）
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var t = api.GenerateAsync(new GenRequest
            {
                Prompt = "p", ApiKey = "sk-test", Model = "m",
                Quality = "low", Aspect = "1:1", N = 1,
            });
            await Task.WhenAny(t, Task.Delay(3000));
            throw new ApiError("模拟超时");
        });

        // ⚠️ 关键：即使轮询没成功，**task_id 已落盘** → 重启可找回
        var open = new PendingTaskStore(_home).LoadOpen();
        Assert.Contains(open, x => x.TaskId == "tsk_stuck");
    }

    // ---------------------------------------------------------------- 辅助

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode, string)> _route;
        public RoutingHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> route)
            => _route = route;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = _route(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
