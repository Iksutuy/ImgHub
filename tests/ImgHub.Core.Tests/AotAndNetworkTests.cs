using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using ImgHub.Core;
using ImgHub.Core.Diagnostics;
using ImgHub.Core.Http;
using ImgHub.Core.Models;
using ImgHub.Core.Services;
using ImgHub.Core.Storage;

namespace ImgHub.Core.Tests;

/// <summary>
/// v5.24.0 回归：AOT JSON 序列化 / 网络诊断 / 分级日志 / 重试语义。
/// 对应 docs/fix-plan-v5.24.md 与 docs/CONSTRAINTS.md H 类。
/// </summary>
public class AotAndNetworkTests : IDisposable
{
    private readonly string _home;

    public AotAndNetworkTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "imghub-aot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    // ---------------------------------------------------------------- AOT JSON

    [Fact]
    public void JsonObject_Payload_BuildsWithoutReflection()
    {
        // AOT 硬约束：HTTP 请求体必须用 JsonObject（DOM），不能用 object 走反射
        var payload = new JsonObject
        {
            ["model"] = "gpt-image-2.5-flare",
            ["prompt"] = "一只猫",
            ["n"] = 1,
        };
        var arr = new JsonArray();
        arr.AddString("https://a/1.png");
        arr.AddNode(new JsonObject { ["type"] = "image_url" });
        payload["image_urls"] = arr;

        var json = payload.ToJsonLine();   // 请求体风格：单行 + 中文不转义
        Assert.Contains("\"model\":\"gpt-image-2.5-flare\"", json);
        Assert.Contains("\"n\":1", json);
        Assert.Contains("\"image_urls\":[\"https://a/1.png\",{", json);
        // 中文不被转义（保持可读，与旧版一致）
        Assert.Contains("一只猫", json);
        Assert.DoesNotContain("\\u", json);
    }

    [Fact]
    public void JsonArray_AddNode_ProducesValidJson()
    {
        // JsonSafe.AddNode/AddString 避开泛型重载（否则 AOT 下报警告 IL2026/IL3050）
        var arr = new JsonArray();
        arr.AddNode(new JsonObject { ["k"] = "v" });
        arr.AddString("s");
        arr.AddNumber(1.5);
        arr.AddBool(true);
        // 注：默认 ToJsonString 会把非 ASCII 转义，这里都是 ASCII
        Assert.Equal("[{\"k\":\"v\"},\"s\",1.5,true]", arr.ToJsonString());
    }

    [Fact]
    public void AppConfig_RoundTrips_WithSnakeCase()
    {
        // source-gen 必须保持 snake_case（与 Python 版 config.json 互通）
        var cfg = new AppConfig { Model = "m", BatchN = 3, OutputFormat = "jpeg", Offline = true };
        var json = System.Text.Json.JsonSerializer.Serialize(cfg, AppJson.Default.AppConfig);

        Assert.Contains("\"batch_n\": 3", json);
        Assert.Contains("\"output_format\": \"jpeg\"", json);
        Assert.Contains("\"offline\": true", json);
        Assert.DoesNotContain("BatchN", json);

        var back = System.Text.Json.JsonSerializer.Deserialize(json, AppJson.Default.AppConfig);
        Assert.NotNull(back);
        Assert.Equal(3, back!.BatchN);
        Assert.Equal("jpeg", back.OutputFormat);        Assert.True(back.Offline);
    }

    [Fact]
    public void AppConfig_ReadsLegacyConfig_WithUnknownFields()
    {
        // 旧版 config 可能含本工程未用的键 → 宽松读取不得失败
        const string legacy = """
        {
          "model": "openai/gpt-image-2.5-flare",
          "quality": "low",
          "batch_n": 2,
          "key_saved": true,
          "some_future_field": [1, 2, 3]
        }
        """;
        var cfg = System.Text.Json.JsonSerializer.Deserialize(legacy, AppJson.Default.AppConfig);
        Assert.NotNull(cfg);
        Assert.Equal(2, cfg!.BatchN);
        Assert.Equal("low", cfg.Quality);
    }

    [Fact]
    public void AppConfig_RoundTrips_DocumentedParams_WithSnakeCase()
    {
        // v5.28.0：新增的文档对齐参数也要走 source-gen 的 snake_case
        // （若漏了 JsonPropertyName 或没进 source-gen 上下文，重启后会静默回默认值）
        var cfg = new AppConfig
        {
            Background = "transparent",
            OutputCompression = 80,
            Moderation = "low",
            Seed = "42",
            Stream = true,
            ProviderOnly = "google-ai-studio",
            ProviderIgnore = "b",
            ProviderOrder = "a,b",
            ProviderSort = "price",
            AllowFallbacks = false,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(cfg, AppJson.Default.AppConfig);

        Assert.Contains("\"background\": \"transparent\"", json);
        Assert.Contains("\"output_compression\": 80", json);
        Assert.Contains("\"moderation\": \"low\"", json);
        Assert.Contains("\"seed\": \"42\"", json);
        Assert.Contains("\"stream\": true", json);
        Assert.Contains("\"provider_only\": \"google-ai-studio\"", json);
        Assert.Contains("\"allow_fallbacks\": false", json);
        // 不得出现 PascalCase（否则与旧配置格式不互通）
        Assert.DoesNotContain("OutputCompression", json);
        Assert.DoesNotContain("ProviderOnly", json);

        var back = System.Text.Json.JsonSerializer.Deserialize(json, AppJson.Default.AppConfig);
        Assert.NotNull(back);
        Assert.Equal("transparent", back!.Background);
        Assert.Equal(80, back.OutputCompression);
        Assert.Equal("low", back.Moderation);
        Assert.Equal("42", back.Seed);
        Assert.True(back.Stream);
        Assert.Equal("google-ai-studio", back.ProviderOnly);
        Assert.Equal("price", back.ProviderSort);
        Assert.False(back.AllowFallbacks);
    }

    [Fact]
    public void AppConfig_AllowFallbacks_IsTriState()
    {
        // AllowFallbacks 是 bool?：null = "不传（用服务端默认）"，与 false（明确禁止回退）语义不同。
        // 若被序列化成 false，用户"没设置"会被误解为"明确禁止回退"。
        var jsonNull = System.Text.Json.JsonSerializer.Serialize(
            new AppConfig { AllowFallbacks = null }, AppJson.Default.AppConfig);
        var back = System.Text.Json.JsonSerializer.Deserialize(jsonNull, AppJson.Default.AppConfig);
        Assert.Null(back!.AllowFallbacks);

        var jsonFalse = System.Text.Json.JsonSerializer.Serialize(
            new AppConfig { AllowFallbacks = false }, AppJson.Default.AppConfig);
        var back2 = System.Text.Json.JsonSerializer.Deserialize(jsonFalse, AppJson.Default.AppConfig);
        Assert.False(back2!.AllowFallbacks);

        var jsonTrue = System.Text.Json.JsonSerializer.Serialize(
            new AppConfig { AllowFallbacks = true }, AppJson.Default.AppConfig);
        var back3 = System.Text.Json.JsonSerializer.Deserialize(jsonTrue, AppJson.Default.AppConfig);
        Assert.True(back3!.AllowFallbacks);
    }

    // ---------------------------------------------------------------- 按模型收窄能力（v5.28.0）

    [Fact]
    public void MaxNFor_GeminiImageModels_IsOne()
    {
        // 实测 supported_parameters.n.max：gemini 图像系只支持 1 张。
        // 若 UI 给 1~4，用户拉到 2 会被服务端拒绝且看不出原因。
        Assert.Equal(1, Catalog.MaxNFor(ApiProvider.OpenRouter, "google/gemini-3.1-flash-image"));
        Assert.Equal(1, Catalog.MaxNFor(ApiProvider.OpenRouter, "google/gemini-2.5-flash-image"));
        Assert.Equal(1, Catalog.MaxNFor(ApiProvider.OpenRouter, "google/gemini-3-pro-image"));
        // 未登记的 gemini 命名也按 1 保守处理
        Assert.Equal(1, Catalog.MaxNFor(ApiProvider.OpenRouter, "google/gemini-9-future-image"));
        // openai 系 10
        Assert.Equal(10, Catalog.MaxNFor(ApiProvider.OpenRouter, "openai/gpt-image-2.5-flare"));
        Assert.Equal(10, Catalog.MaxNFor(ApiProvider.OpenRouter, "openai/gpt-image-1"));
        // APIMart 一律 4（文档：n 取值 1~4）
        Assert.Equal(4, Catalog.MaxNFor(ApiProvider.Apimart, "gpt-image-2.5-flare"));
        Assert.Equal(4, Catalog.MaxNFor(ApiProvider.Apimart, "gemini-3.1-flash-image"));
        // 未知模型回退到 provider 上限
        Assert.Equal(10, Catalog.MaxNFor(ApiProvider.OpenRouter, "some/unknown-model"));
    }

    [Fact]
    public void AspectChoicesFor_GptImage2_ExcludesExtendedRatios()
    {
        // 实测：openai/gpt-image-2.5 系只支持 9 种比例，不含 2:1 / 1:2 / 5:4
        var gpt = Catalog.AspectChoicesFor(ApiProvider.OpenRouter, "openai/gpt-image-2.5-flare");
        Assert.Contains("16:9", gpt);
        Assert.Contains("auto", gpt);
        Assert.DoesNotContain("2:1", gpt);
        Assert.DoesNotContain("1:2", gpt);
        Assert.DoesNotContain("5:4", gpt);

        // gemini 系含扩展比例，但不含 2:1 / 3:1
        var gem = Catalog.AspectChoicesFor(ApiProvider.OpenRouter, "google/gemini-3.1-flash-image");
        Assert.Contains("1:4", gem);
        Assert.Contains("8:1", gem);
        Assert.DoesNotContain("2:1", gem);

        // gpt-image-1 只 4 种
        var g1 = Catalog.AspectChoicesFor(ApiProvider.OpenRouter, "openai/gpt-image-1");
        Assert.Equal(4, g1.Length);

        // APIMart = 文档 15 种 + auto
        var am = Catalog.AspectChoicesFor(ApiProvider.Apimart, "gpt-image-2.5-flare");
        Assert.Contains("3:1", am);
        Assert.Contains("9:21", am);
        Assert.Contains("auto", am);
    }

    [Fact]
    public void AspectSupported_RespectsModel_ButAllowsExactPixels()
    {
        // 模型不支持的比例 → false
        Assert.False(Catalog.AspectSupported(ApiProvider.OpenRouter, "2:1", "openai/gpt-image-2.5-flare"));
        // 该模型支持的比例 → true
        Assert.True(Catalog.AspectSupported(ApiProvider.OpenRouter, "16:9", "openai/gpt-image-2.5-flare"));
        // APIMart 支持 3:1，OpenRouter gpt-image 不支持 → 同一比例两端结论不同
        Assert.True(Catalog.AspectSupported(ApiProvider.Apimart, "3:1", "gpt-image-2.5-flare"));
        Assert.False(Catalog.AspectSupported(ApiProvider.OpenRouter, "3:1", "openai/gpt-image-2.5-flare"));
        // 精确像素尺寸恒为 true（合法性由 ValidatePixelSize 把关）
        Assert.True(Catalog.AspectSupported(ApiProvider.OpenRouter, "1600x1200", "openai/gpt-image-2.5-flare"));
    }

    [Fact]
    public void ResolutionChoicesFor_LiteModel_OnlyOneK()
    {
        // 实测 google/gemini-3.1-flash-lite-image 的 resolution.values 仅 ["1K"]
        var lite = Catalog.ResolutionChoicesFor(ApiProvider.OpenRouter,
                                                "google/gemini-3.1-flash-lite-image");
        Assert.Single(lite);
        // ⚠️ 内部统一**小写**（1k/2k/4k），大写只出现在发给 OpenRouter 的协议层
        //    （Catalog.OpenRouterResolution 负责映射）。早期这里断言大写，实际是把
        //    "下拉选不中 + 每次切换都被重置"的 bug 固化进了测试（v0.5.31 修正）。
        Assert.Equal("1k", lite[0]);

        // 其它 OpenRouter 模型 4 档
        Assert.Equal(4, Catalog.ResolutionChoicesFor(ApiProvider.OpenRouter,
                                                     "google/gemini-3.1-flash-image").Length);
        // APIMart 小写 3 档
        Assert.Equal(3, Catalog.ResolutionChoicesFor(ApiProvider.Apimart, "gpt-image-2.5-flare").Length);

        // 关键不变量：下拉集合与内部语义必须能互相匹配（否则 ComboBox 选不中）
        foreach (var p in Enum.GetValues<ApiProvider>())
        {
            foreach (var m in Catalog.ModelChoices(p))
            {
                var choices = Catalog.ResolutionChoicesFor(p, m);
                Assert.Contains("1k", choices);
            }
        }
    }

    [Fact]
    public void Session_SaveState_WritesReadableJson_WithSnakeCase()
    {
        var sess = new Session(_home);
        sess.Config.Model = "m";
        sess.Push(new Item { File = "a.png", Prompt = "猫", Kind = "gen", Provider = "apimart" });

        var json = File.ReadAllText(sess.StateFile);
        Assert.Contains("\"total_cost\"", json);
        Assert.Contains("\"counter\"", json);
        Assert.Contains("\"items\"", json);
        Assert.Contains("a.png", json);
        Assert.Contains("猫", json);        // 中文可读（未转义）
    }

    [Fact]
    public void Session_PromptHistoryLine_IsReadableJson()
    {
        var sess = new Session(_home);
        sess.PushPromptHistory("一只猫在窗台上", "gen");
        var line = File.ReadAllLines(sess.PromptHistoryFile).Last();
        Assert.Contains("\"prompt\"", line);
        Assert.Contains("一只猫在窗台上", line);
        Assert.Contains("\"kind\"", line);
    }

    [Fact]
    public void ModelStats_SavesReadableJson()
    {
        var svc = new ModelStatsService(_home);
        svc.Record("openrouter", "m", cost: 0.02, images: 1);
        var json = File.ReadAllText(svc.FilePath);
        Assert.Contains("\"models\"", json);
        Assert.Contains("openrouter|m", json);
        Assert.Contains("\"total_cost\"", json);
    }

    // ---------------------------------------------------------------- 网络诊断

    [Theory]
    [InlineData(SocketError.HostNotFound, NetworkKind.DnsFailure)]
    [InlineData(SocketError.ConnectionRefused, NetworkKind.ConnectionRefused)]
    [InlineData(SocketError.ConnectionReset, NetworkKind.ConnectionReset)]
    [InlineData(SocketError.NetworkDown, NetworkKind.NoNetwork)]
    [InlineData(SocketError.NetworkUnreachable, NetworkKind.NoNetwork)]
    [InlineData(SocketError.HostUnreachable, NetworkKind.HostUnreachable)]
    [InlineData(SocketError.TimedOut, NetworkKind.Timeout)]
    public void NetworkDiagnostics_ClassifiesSocketErrors(SocketError code, NetworkKind expected)
    {
        var ex = new HttpRequestException("outer", new SocketException((int)code));
        Assert.Equal(expected, NetworkDiagnostics.Classify(ex));
    }

    [Fact]
    public void NetworkDiagnostics_ClassifiesDnsFromMessage()
    {
        var ex = new HttpRequestException("No such host is known");
        Assert.Equal(NetworkKind.DnsFailure, NetworkDiagnostics.Classify(ex));
    }

    [Fact]
    public void NetworkDiagnostics_ClassifiesTls()
    {
        var ex = new HttpRequestException("The SSL connection could not be established");
        Assert.Equal(NetworkKind.TlsError, NetworkDiagnostics.Classify(ex));
    }

    [Fact]
    public void NetworkDiagnostics_Describe_GivesActionableChineseHint()
    {
        var dns = NetworkDiagnostics.Describe(NetworkKind.DnsFailure);
        Assert.Contains("解析", dns);

        var noNet = NetworkDiagnostics.Describe(NetworkKind.NoNetwork);
        Assert.Contains("Wi-Fi", noNet);

        var tls = NetworkDiagnostics.Describe(NetworkKind.TlsError);
        Assert.Contains("证书", tls);

        var timeout = NetworkDiagnostics.Describe(NetworkKind.Timeout);
        Assert.Contains("超时", timeout);
    }

    [Fact]
    public void NetworkDiagnostics_RootMessage_UnwrapsInner()
    {
        var ex = new HttpRequestException("An error occurred while sending the request",
                                          new SocketException((int)SocketError.HostNotFound));
        var msg = NetworkDiagnostics.RootMessage(ex);
        Assert.DoesNotContain("An error occurred while sending", msg);
        Assert.False(string.IsNullOrWhiteSpace(msg));
    }

    // ---------------------------------------------------------------- 重试语义（C2）

    [Fact]
    public void ApiError_DefaultsToNotRetryable()
    {
        // 业务错误默认不重试（4xx 不该浪费流量）
        var e = new ApiError("key 无效", 401);
        Assert.False(e.Retryable);
    }

    [Fact]
    public async Task PostJson_BusinessError_DoesNotRetry()
    {
        // 401 必须**只请求一次**（实测曾误重试 3 次，违反 CONSTRAINTS C2）
        var handler = new CountingHandler(HttpStatusCode.Unauthorized,
            """{"error":{"message":"User not found.","code":401}}""");
        var http = new HttpJsonClient(new HttpClient(handler), maxAttempts: 3, backoffBase: 1.0);
        var client = http;

        var ex = await Assert.ThrowsAsync<ApiError>(() =>
            client.PostJsonAsync("https://example.invalid/images",
                                 new JsonObject { ["model"] = "m" }, "sk-test"));

        Assert.Equal(1, handler.Calls);
        Assert.Equal(401, ex.Status);
        Assert.False(ex.Retryable);
    }

    [Fact]
    public async Task PostJson_ServerError_RetriesUpToMax()
    {
        // 5xx 可重试 → 应达到 maxAttempts 次
        var handler = new CountingHandler(HttpStatusCode.InternalServerError,
            """{"error":{"message":"boom"}}""");
        var client = new HttpJsonClient(new HttpClient(handler), maxAttempts: 3, backoffBase: 1.0);

        await Assert.ThrowsAsync<ApiError>(() =>
            client.PostJsonAsync("https://example.invalid/images",
                                 new JsonObject { ["model"] = "m" }, "sk-test"));

        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task PostJson_RateLimited_Retries()
    {
        var handler = new CountingHandler((HttpStatusCode)429, """{"error":{"message":"slow down"}}""");
        var client = new HttpJsonClient(new HttpClient(handler), maxAttempts: 2, backoffBase: 1.0);

        await Assert.ThrowsAsync<ApiError>(() =>
            client.PostJsonAsync("https://example.invalid/images",
                                 new JsonObject { ["model"] = "m" }, "sk-test"));

        Assert.Equal(2, handler.Calls);
    }

    // ---------------------------------------------------------------- 分级日志

    [Fact]
    public void AppLog_WritesGradedLines_WithLocation_ToLogFolder()
    {
        AppLog.Init(_home);
        AppLog.Info("普通消息", where: "Test.Info");
        AppLog.Warn("警告消息", where: "Test.Warn");
        AppLog.Error("错误消息", where: "Test.Error", ex: new InvalidOperationException("根因"));

        var logDir = Path.Combine(_home, "log");
        Assert.True(Directory.Exists(logDir));
        var file = Directory.GetFiles(logDir, "*.log").Single();
        var text = File.ReadAllText(file);

        Assert.Contains("[INFO ]", text);
        Assert.Contains("[WARN ]", text);
        Assert.Contains("[ERROR]", text);
        Assert.Contains("(Test.Error)", text);          // 位置
        Assert.Contains("InvalidOperationException", text);  // 原因
        Assert.Contains("根因", text);
    }

    [Fact]
    public void AppLog_MasksApiKeys()
    {
        AppLog.Init(_home);
        AppLog.Info("key=sk-or-v1-abcdefghijklmnop tail", where: "Test.Mask");

        var file = Directory.GetFiles(Path.Combine(_home, "log"), "*.log").Single();
        var text = File.ReadAllText(file);
        Assert.DoesNotContain("abcdefghijklmnop", text);
        Assert.Contains("sk-or-v1-abc***", text);
    }

    [Fact]
    public void AppLog_Memory_KeepsRecentEvenWithoutDisk()
    {
        AppLog.ClearMemory();
        for (int i = 0; i < 5; i++) AppLog.Info($"m{i}", "Test.Mem");
        var recent = AppLog.Recent(10);
        Assert.Contains(recent, e => e.Message == "m4");
    }

    /// <summary>统计请求次数的假 Handler（验证重试语义）。</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public int Calls { get; private set; }

        public CountingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
