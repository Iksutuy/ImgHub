# OpenAI 官方图像 API —— 接入参考（ImgHub 实现契约）

> **这份文档回答什么**：ImgHub 的「OpenAI 官方」provider 到底怎么调官方接口、每个字段从哪来、
> 哪些坑必须绕开。**代码与本文不一致时以代码为准**，但两者必须同步修改。
>
> **一手来源**：`docs/GPT Image generation.md`（OpenAI 官方文档快照，仓库内）。
> 交叉核对过本机安装的官方 SDK 快照，见文末「来源与核对」。

对应实现：`src/ImgHub.Core/Services/ImageApi.cs` 的
`GenerateOpenAiAsync` / `BuildOpenAiGenerationPayload` / `BuildOpenAiEditFields` /
`ParseOpenAiResponse`；常量在 `src/ImgHub.Core/Catalog.cs`。

---

## 一、两个端点，不是一个

| 场景 | 端点 | 请求格式 |
|---|---|---|
| **纯文生图**（无参考图） | `POST /v1/images/generations` | **JSON** |
| **有参考图**（含蒙版重绘） | `POST /v1/images/edits` | **multipart/form-data** |

官方原文：*"The Image API provides two endpoints, each with distinct capabilities"*。

⚠️ **不能只用一个端点**：`edits` 走 multipart（官方 curl 示例即 `-F "image[]=@a.png"`），
`generations` 只收 JSON。把参考图塞进 `generations` 会被直接拒绝。

**代码怎么选**（`GenerateOpenAiAsync`）：

```csharp
return req.Refs is { Count: > 0 }
    ? await GenerateOpenAiEditAsync(req)        // → /images/edits
    : await GenerateOpenAiGenerationAsync(req); // → /images/generations
```

---

## 二、模型清单与各自的参数域

清单见 `Catalog.ModelChoicesOpenAi`（**顺序即推荐度**）。

| 模型 | size | quality | 备注 |
|---|---|---|---|
| `gpt-image-2.5-sunburst` / `-flare` | 推荐 3 档 + 任意合规 `WxH` + `auto` | `low`/`medium`/`high`/`xhigh`/`max`/`auto` | 官方当前主力；「新增 xhigh 与 max」 |
| `gpt-image-2` | 同上（1K~4K 任意合规值） | `low`/`medium`/`high`/`auto` | **不接受 `background=transparent`** |
| `gpt-image-1.5` / `gpt-image-1` | 仅 `1024x1024`/`1536x1024`/`1024x1536`/`auto` | 同上 | 仅 1 系支持 `input_fidelity` |
| `gpt-image-1-mini` | 同上 | 同上 | **不支持** `input_fidelity` |
| `chatgpt-image-latest` | 同 2 系 | 同 2 系 | |
| `dall-e-3` | 固定 `1024x1024`/`1792x1024`/`1024x1792`（**无 auto**） | **`standard`/`hd`** | 另一套质量取值；有 `style`；`n` 仅 1 |

### 尺寸约束（GPT Image 2 / 2.5 的任意 `WxH`）

宽高均为 **16 的倍数**、长短边比 **≤ 3:1**、单边 **≤ 3840**、
总像素 **655,360 ~ 8,294,400**；`>2560x1440` 属实验区间。

→ 由 `Catalog.ValidatePixelSize` 前置把关（在**花钱之前**拒绝），与 APIMart 规则一致，故共用。

### 质量档收窄

`Catalog.QualityChoices(ApiProvider.OpenAi, model)` 按模型返回合法值。
⚠️ **`dall-e-3` 的 `auto` 是非法的** —— `AddOpenAiCommonFields` 会在模型不支持时
回退到该模型合法集合的第一项（`standard`），否则请求必被 400。

---

## 三、参数逐项对照（`/images/generations`）

| 字段 | 来源 | 说明 |
|---|---|---|
| `model` | `GenRequest.Model` | |
| `prompt` | `GenRequest.Prompt` | GPT Image 系上限 32000 字符 |
| `n` | `GenRequest.N` | 经 `Catalog.MaxNFor` 钳制（dall-e-3 → 1） |
| `size` | `GenRequest.Aspect` | **像素串或 `auto`**；⚠️ 内部默认 `"1:1"` 必须转 `auto`（见下） |
| `quality` | `GenRequest.Quality` | 经 `QualityChoices` 收窄 |
| `background` | `GenRequest.Background` | 仅 GPT Image 系；2/2.5 禁 `transparent` |
| `output_format` | `GenRequest.OutputFormat` | 仅 GPT Image 系（`png`/`jpeg`/`webp`） |
| `output_compression` | `GenRequest.OutputCompression` | 仅 jpeg/webp 生效 |
| `moderation` | `GenRequest.Moderation` | `auto`/`low` |
| `input_fidelity` | `GenRequest.InputFidelity` | **仅 gpt-image-1 系**（不含 mini） |
| `style` | `GenRequest.Style` | **仅 dall-e-3**（`vivid`/`natural`） |
| `stream` + `partial_images` | `GenRequest.Stream` / `.PartialImages` | 仅 GPT Image 系 |

### ⚠️ 三个必须照做的细节

**① `size` 没有比例名。** 内部统一画幅默认是 `"1:1"`（给 OpenRouter/APIMart 用的比例名），
但 OpenAI 的 `size` 只认 `WIDTHxHEIGHT` 或 `auto`。代码里显式转换：

```csharp
bool sizeIsRatioOrEmpty = size.Length == 0 ||
                          size.Equals("1:1", StringComparison.OrdinalIgnoreCase);
payload["size"] = sizeIsRatioOrEmpty ? "auto" : size;
```

**② `response_format` 不发送。** 官方原文：GPT Image 系*始终*返回 base64，
该参数*"isn't supported for the GPT image models"*。我们只解析 `data[].b64_json`，
并在只拿到 `url`（dall-e 系）时打标记交给下载环节（见 `UrlMediaMarker`）。

**③ `background=transparent` 只给 1 系。** 官方原文：*"gpt-image-2 and
gpt-image-2-2026-04-21 do not support transparent backgrounds. Requests with
background set to transparent will return an error for these models"*。

---

## 四、编辑 / 参考图（`/images/edits`，multipart）

**文件字段**

| 字段名 | 内容 | 约束 |
|---|---|---|
| `image[]` | 参考图（**可多张**，1 位 = 主体） | GPT Image 系最多 16 张、每张 < 50MB、`png`/`webp`/`jpg` |
| `mask` | 局部重绘蒙版 | **必须 PNG 且含 Alpha 通道**，尺寸与第 1 张图一致，< 50MB |

**文本字段**：`model` / `prompt` / `n` / `size` / `quality` / `background` /
`output_format` / `output_compression` / `moderation` / `input_fidelity`
（与文件**同级**平铺，见 `BuildOpenAiEditFields`）。

### ⚠️ mask 的两条硬要求（官方 "Mask requirements"）

1. *"The image to edit and mask must be of the same format and size"*；
2. *"The mask image must also contain an alpha channel"*。

→ 由 `Catalog.ValidateMask` + `Catalog.PngHasAlpha` 前置把关（同 APIMart 语义：
**alpha=0 的透明区域 = 要修改的地方**）。这解释了 UI 上
「用标注编辑」在 APIMart 与 OpenAI 下文案不同（`MainViewModel.RegionEditTip`）。

### multipart 能力

项目原有的 `PostMultipartAsync` 只能传**一个** `file` 字段 —— 无法表达
「多个 `image[]` + 可选 `mask` + 同级文本字段」。为此在 `HttpJsonClient` 新增：

- `PostFormDataAsync(url, fields, files, …)` —— 通用 multipart；
- `PostFormDataStreamAsync(...)` —— 保留响应流（供 SSE 流式编辑）。

---

## 五、流式（SSE）

官方：`stream: true` + `partial_images: 0~3`。

| 端点 | 部分图事件 | 完成事件 |
|---|---|---|
| `/images/generations` | `image_generation.partial_image` | `image_generation.completed` |
| `/images/edits` | **`image_edit.partial_image`** | **`image_edit.completed`** |

⚠️ **两套事件名不同** —— 混用会一个事件都收不到（静默卡住直到超时）。
部分图经 `OnPartialImage` 回调到 UI（`MainViewModel.PartialImage`），不落盘。

⚠️ 流式**不做重试**（`PostJsonStreamAsync` / `PostFormDataStreamAsync` 都不带重试）：
一旦开始推事件，重试会造成重复计费与重复部分图。
`dall-e-3` 不支持 `stream`，代码按模型自动降级为非流式。

响应体形态（与 OpenRouter SSE 的区别）：事件是**裸对象** `{type, b64_json, …}`，
而 OpenRouter 是 `data[]` 包裹。

---

## 六、响应与成本

```json
{
  "created": 1700000000,
  "data": [ { "b64_json": "…" } ],
  "output_format": "png", "quality": "high", "size": "1024x1024",
  "usage": {
    "input_tokens": 10,
    "input_tokens_details": { "image_tokens": 0, "text_tokens": 10 },
    "output_tokens": 200,
    "total_tokens": 210
  }
}
```

- `data[].b64_json`（GPT Image 系恒有）/ `data[].url`（dall-e 系，有效期 60 分钟）；
- `usage` 仅 GPT Image 系返回 → 我们取 `total_tokens` 记录（`GenResult.Tokens`）。

**官方每图价格（1K 基准，`docs/GPT Image generation.md` 价格表）**

| 模型 | low | medium | high |
|---|---|---|---|
| GPT Image 2 | $0.006 | $0.053 | $0.211 |
| GPT Image 1.5 | $0.009 | $0.034 | $0.133 |
| GPT Image 1 | $0.011 | $0.042 | $0.167 |
| GPT Image 1 Mini | $0.005 | $0.011 | $0.036 |

> 项目**不**用这张表做展示预估 —— 预估走 `ModelStatsService` 的历史均价
> （见 `ARCHITECTURE.md` §三）。价格表仅作为「用量是否异常」的对照基准。

---

## 七、错误处理

- 瞬时失败（429 / 5xx）→ `HttpJsonClient` 按 `Catalog.MaxAttempts` 指数退避重试；
- 业务 4xx → **不重试**（浪费流量且掩盖真因）；
- 用户可纠正的失败 → `error.type = "image_generation_user_error"`，用 `error.code` 判别；
- `error.code = "moderation_blocked"` 时可能带 `error.moderation_details`
  （`moderation_stage` = `input`/`output`/`unknown`，以及粗粒度 `categories`）——
  官方建议**只写进开发者日志**，给终端用户的文案保持笼统。

---

## 八、来源与核对

| 一手来源 | 用途 |
|---|---|
| `docs/GPT Image generation.md` | 全部字段/取值域/事件名/价格（仓库内快照） |
| `openai-python` 2.7.1（`C:\Python313\Lib\site-packages\openai\types\image_*.py`） | 交叉核对参数与模型 id |
| `openai` JS SDK 6.40.0（`resources/images.d.ts`） | 交叉核对 `ImageModel` 联合类型与 `ImageGenCompletedEvent` |

**两处来源不一致时的处理**：以 `docs/GPT Image generation.md` 为准（它更新，
含 `gpt-image-2.5-*`）；SDK 快照仅用于补字段名细节。

⚠️ **网络不可用的环境**：本仓库的开发机出站被封锁，无法在线抓取官方文档。
更新本文时请用**仓库内的文档快照**或**本机已安装的官方 SDK**作为来源，不要凭记忆臆测。

---

## 九、改动检查清单

改 OpenAI 链路时逐条核对：

1. `Catalog.ModelChoicesOpenAi` 与 `Catalog.OpenAiQualityChoices` / `OpenAiAspectChoices`
   / `MaxNFor` / `OutputFormatChoices` 是否同步；
2. `BuildOpenAiGenerationPayload` 与 `BuildOpenAiEditFields` 是否都覆盖了新字段
   （**只改一个会导致编辑路径悄悄丢参数**）；
3. `NewProviderPayloadTests`（`tests/ImgHub.Core.Tests/`）是否补了对应断言；
4. 跑 `dotnet test tests\ImgHub.Core.Tests`（离线、不花钱）。
