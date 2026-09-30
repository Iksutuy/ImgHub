# 千问 Qwen-Image · DashScope 原生协议 —— 接入参考（ImgHub 实现契约）

> **这份文档回答什么**：ImgHub 的「千问 DashScope」provider 为什么按模型分两条链路、
> 字段怎么组织、哪些值传了会 400。**代码与本文不一致时以代码为准**，但两者必须同步修改。
>
> **一手来源**（均在仓库内）：
> `docs/千问-图像生成与编辑3.0 API参考.md`、`docs/千问-文生图API参考.md`。

对应实现：`src/ImgHub.Core/Services/ImageApi.cs` 的
`GenerateDashScopeAsync` / `BuildDashScopeMultimodalPayload` /
`BuildDashScopeText2ImagePayload` / `ParseDashScopeResponse` / `PollDashScopeTaskAsync`；
常量在 `src/ImgHub.Core/Catalog.cs`。

---

## 一、端点

默认走**通用域名**（无需 WorkspaceId，开箱可用）：

```
https://dashscope.aliyuncs.com            （华北2 北京）
https://dashscope-intl.aliyuncs.com       （新加坡）
```

官方推荐迁移到**业务空间专属域名**（性能/稳定性更好）：

```
https://{WorkspaceId}.cn-beijing.maas.aliyuncs.com
```

⚠️ **WorkspaceId 属于用户账号，程序无法预置** —— 所以端点被做成
**可在设置里覆盖**的配置项（`AppConfig.BaseUrls`，UI 在设置浮层的「端点」输入框），
由 `Session.BaseUrlFor(provider)` 解析。这正是不把端点写死成常量的原因。

⚠️ **跨地域/跨业务空间调用会鉴权失败**。端点必须与 API Key 同地域 ——
设置浮层里有明确提示。

---

## 二、为什么按模型分两条链路（关键设计）

| 模型 | 协议 | 端点 | 同步/异步 | 参考图 | n |
|---|---|---|---|---|---|
| `qwen-image-3.0-pro` / `-3.0` | **DashScope multimodal** | `/api/v1/services/aigc/multimodal-generation/generation` | **同步** | **1-3 张** | 1-6 |
| `qwen-image-2.0-pro` / `-2.0` | DashScope text2image | `/api/v1/services/aigc/text2image/image-synthesis` | **异步**（轮询 `/api/v1/tasks/{id}`） | 否 | 1-6 |
| `qwen-image-max` / `-plus` / `qwen-image` | DashScope text2image | 同上 | **异步** | 否 | **固定 1** |

**判定逻辑**（`GenerateDashScopeAsync`）：

```csharp
bool multimodal = model.StartsWith("qwen-image-3.0", StringComparison.OrdinalIgnoreCase);
return multimodal ? await GenerateDashScopeMultimodalAsync(req)
                  : await GenerateDashScopeAsyncTaskAsync(req);
```

⚠️ **混用会失败**：官方明确 text2image 的异步端点*只受理* `qwen-image` / `qwen-image-plus`；
用同步端点调 2.0 系会返回 *"current user api does not support synchronous calls"*。
反过来，3.0 系的功能（参考图、`negative_prompt`、`prompt_extend_mode`）同步接口最全。

> **为什么不用 OpenAI 兼容模式？**
> 3.0 文档提供了 `/compatible-mode/v1/images/generations`，接入更省事。但官方明确该模式
> **不支持**异步、流式、partial image、`/images/edits`、multipart 与 mask，
> 且 `response_format=b64_json` 会被忽略（只返 URL）。
> 为拿到完整能力（尤其 **I2I 参考图**），本项目选了 DashScope 原生协议。

---

## 三、链路 A：3.0 系（同步 multimodal）

### 请求体

```json
{
  "model": "qwen-image-3.0-pro",
  "input": {
    "messages": [
      { "role": "user",
        "content": [
          { "image": "data:image/png;base64,…" },
          { "text": "保留主体，换背景为海边" }
        ] }
    ]
  },
  "parameters": {
    "n": 2, "size": "1024x1024",
    "negative_prompt": "低分辨率", "prompt_extend": true,
    "prompt_extend_mode": "direct", "watermark": true, "seed": 42
  }
}
```

### 逐项要点

- **`content` 数组顺序**：参考图在前、`text` 在最后。官方原文：
  *"多图输入时，按照数组顺序定义图像顺序"* —— 与项目「第 1 位 = 主体」的约定一致。
- **`image` 接受 Base64 data URL**：`data:{MIME_type};base64,{base64_data}`。
  代码用 `DataUrl()` 内联（无需先上传换公网 URL，与 APIMart 不同）。
- **参考图上限 3 张**（`ImageApi.DashScopeMaxRefImages`，`Catalog.MaxRefsFor` 同步收窄），
  超出会在**花钱之前**被 `ValidateRequest` 拒绝。
- **`prompt_extend_mode=agent` 仅支持 T2I**：官方原文 *"图生图（I2I）场景传入 agent
  将返回 400 错误"* → 代码在**有参考图时自动降级为 `direct`**。
- **`n`**：1-6（`Catalog.MaxNFor(ApiProvider.DashScope, model)`）。
- **`size`**：`宽x高`（**字母 x**）或 `auto`。面积须在 **512×512 ~ 2048×2048**，
  宽高比 ≤ 8:1（`Catalog.ValidatePixelSizeFor`，与 APIMart 的 16 倍数规则**不同**）。
- **`seed`**：`[0, 2147483647]`，复用 UI 的「种子」字段（与 OpenRouter 共用）。
- 仅 `qwen-image-max/plus/qwen-image` 的 `size` 是**固定 5 档**
  （`Catalog.AspectsDashScopeFixed`：`1664x928`/`1472x1104`/`1328x1328`/`1104x1472`/`928x1664`）。

### 响应

```json
{
  "output": { "choices": [ { "finish_reason": "stop",
    "message": { "role": "assistant",
      "content": [ { "image": "https://…png" } ] } } ] },
  "usage": { "output_width": 1024, "output_height": 1024,
             "input_image_count": 0, "output_image_count": 1,
             "input_image_type": "qima_input_1k", "output_image_type": "qima_output_1k" },
  "request_id": "…"
}
```

⚠️ **`usage` 是"图片计量"不是 token** —— 代码不把它当 token 记录
（`ParseDashScopeResponse` 里显式注释）。

⚠️ **只返回 URL**（有效期 **24 小时**）→ `ResolveImagesAsync` 在 Core 内**立即下载**成字节
再交给上层（上层契约是「(字节, media)」，直接写盘，传 URL 会写成损坏文件）。

---

## 四、链路 B：2.0 / max / plus（异步任务）

### 步骤 1：创建任务

`POST /api/v1/services/aigc/text2image/image-synthesis`
**必须带请求头** `X-DashScope-Async: enable`（缺了报 *"current user api does not support
synchronous calls"*）。代码通过 `PostJsonAsync(..., extraHeaders: …)` 传这个头。

```json
{
  "model": "qwen-image-plus",
  "input": { "prompt": "一只猫", "negative_prompt": "低分辨率" },
  "parameters": { "size": "1664*928", "prompt_extend": true }
}
```

⚠️ **两处与 3.0 不同**：
- `size` 用 **`宽*高`（星号）**，不是 `宽x高` → 代码显式 `Replace('x', '*')`；
- `n` *"当前固定为 1，设置其他值将导致报错"* → **索性不传**（而不是传 1）。

响应：`{ "output": { "task_id": "…", "task_status": "PENDING" }, "request_id": "…" }`

### 步骤 2：轮询

`GET /api/v1/tasks/{task_id}` × `Authorization: Bearer <key>`
（代码用 `GetJsonWithHeadersAsync`）。

`task_status`：`PENDING` / `RUNNING` / `SUCCEEDED` / `FAILED` / `CANCELED` / `UNKNOWN`。

成功时的响应结构与 3.0 **完全同形**（`output.choices[].message.content[].image`），
因此共用 `ParseDashScopeResponse`。

### 💰 拿 task_id 立即落盘（不重复扣费）

与 APIMart 同一机制：拿到 `task_id` 就通过 `OnSubmitProgress(AsyncSubmitted, …)`
交给 `PendingTaskStore` 落盘。程序意外退出后重启**只 GET 查询、不重新提交** ——
所以「找回图片」不会重复扣费（`docs/fix-plan-v5.26.md`）。

---

## 五、成本

官方文档**未给出**逐档单价（与 OpenAI 的价格表不同）。因此：

- `GenResult.Cost` 在千问链路记为 `0`；
- UI 的预估走 `ModelStatsService` 的历史均价（`provider|model` 键，见 `ARCHITECTURE.md` §三）；
- 首次使用该模型时显示「暂无历史花费记录」——不编造估算。

---

## 六、错误处理

千问的错误体是**顶层 `code` + `message`**，且 **HTTP 可能仍是 200**：

```json
{ "request_id": "…", "code": "InvalidParameter", "message": "n must be 1" }
```

→ `ThrowIfDashScopeError` 在**解析响应与轮询的每一步**都先检查 `code`，
避免把错误体当成正常结果继续解析（那会变成"成功但没图"这种难查的现象）。

---

## 七、来源与核对

| 一手来源（仓库内） | 用途 |
|---|---|
| `docs/千问-图像生成与编辑3.0 API参考.md` | 3.0 的 OpenAI 兼容 / DashScope 同步 / 异步三套协议、参数、响应 |
| `docs/千问-文生图API参考.md` | 2.0 / max / plus 系模型清单、异步接口、固定分辨率档 |

⚠️ 本仓库开发机的**网络出站被封锁**，无法在线核对百炼控制台。
更新本文时请以这两份仓库内快照为准，不要凭记忆臆测参数名。

---

## 八、改动检查清单

改千问链路时逐条核对：

1. `Catalog.ModelChoicesDashScope` / `DashScopeMaxNFor` / `DashScopeAspectChoices`
   / `MaxRefsFor` / `ValidatePixelSizeFor`（千问分支）是否同步；
2. `BuildDashScopeMultimodalPayload` 与 `BuildDashScopeText2ImagePayload` 的**差异项**
   （size 分隔符、是否传 n）是否都正确 —— 两者容易互相抄错；
3. `NewProviderPayloadTests`（`tests/ImgHub.Core.Tests/`）是否补了对应断言；
4. 跑 `dotnet test tests\ImgHub.Core.Tests`（离线、不花钱）。
