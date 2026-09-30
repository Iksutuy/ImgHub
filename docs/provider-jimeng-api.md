# 即梦（火山引擎）· CVSync2Async 协议 —— 接入参考（ImgHub 实现契约）

> **这份文档回答什么**：ImgHub 的「即梦（火山引擎）」provider 怎么调、为什么它和另外四家
> 长得完全不一样、每个字段从哪来。**代码与本文不一致时以代码为准**，但两者必须同步修改。
>
> **一手来源**（均在仓库内）：
> `docs/即梦AI-图片生成4.md`、`docs/即梦AI-素材提取(商品提取)-接口文档.md`、
> `docs/即梦AI-素材提取(提取元素转为平面设计图)-接口文档.md`。

对应实现：
- 签名：`src/ImgHub.Core/Http/VolcSigner.cs`
- 提取预设（10 种）：`src/ImgHub.Core/JimengExtract.cs`
- 链路：`src/ImgHub.Core/Services/ImageApi.cs` 的 `GenerateJimengAsync` 一族
- 常量：`src/ImgHub.Core/Catalog.cs`（`Jimeng*` 系列）

---

## 一、与另外四家的三个根本差异

| 维度 | 其它四家 | **即梦（火山引擎）** |
|---|---|---|
| 接口风格 | REST 资源路径（`/images/generations`） | **RPC**：`Action` + `Version` 写在 **query** 上，同一 URL 靠 Action 区分能力 |
| 鉴权 | Bearer token（单 key） | **AK/SK 签名**（`HMAC-SHA256`），且 Region/Service 固定 |
| 模型概念 | `model` 名 | **`req_key`**（服务标识，没有"列模型"接口） |

```
POST https://visual.volcengineapi.com?Action=CVSync2AsyncSubmitTask&Version=2022-08-31
```

> 即梦**不使用** `Authorization: Bearer <key>`。它要的是
> `Authorization: HMAC-SHA256 Credential=AK/日期/区域/服务/request, SignedHeaders=..., Signature=...`

---

## 二、签名（最容易出错的一环）

`VolcSigner.Sign(...)` 是**纯函数**（不碰网络），便于直接对着已知向量做单测。
流程（火山引擎「公共参数 - 签名参数 - 在 Header 中的场景」）：

1. 拼 **CanonicalRequest**：`Method` / `CanonicalURI` / `CanonicalQuery` /
   `CanonicalHeaders` / `SignedHeaders` / `HashedPayload`（固定 6 行）；
2. 拼 **StringToSign**：`Algorithm` / `X-Date` / `CredentialScope` / `hash(①)`；
3. 派生密钥：`kDate → kRegion → kService → kSigning`（四级 HMAC，**每级以上一级的字节为 key**）；
4. `Signature = hex(HMAC-SHA256(kSigning, ②))`。

### ⚠️ 四个必须照做的细节（错了只会得到 403，服务端不会说哪一步）

| # | 细节 | 为什么 |
|---|---|---|
| ① | **CanonicalQuery 按参数名排序** + RFC3986 编码 | 不排序 → 换个参数顺序就签名不匹配（已由 `VolcSigner_SortsQueryByName` 锁住） |
| ② | **HashedPayload 是请求体的 SHA256**（**空体也要算**，不是省略） | 省略会让签名与服务端算的不一致 |
| ③ | **时间戳必须 UTC**，`X-Date` 与 Credential 里的日期部分一致 | 用本地时间会差时区 → 判"签名过期"（已由 `VolcSigner_UsesUtcTime_NotLocalTime` 锁住） |
| ④ | RFC3986 的 unreserved **只有** `A-Za-z0-9-_.~` | .NET 的 `Uri.EscapeDataString` **不编码** `!*'()` → 差一个字符就不匹配，故自己实现 `UriEncode` |

**固定的 Region / Service**（文档明确"本服务固定值"）：
`Region = cn-north-1`、`Service = cv`、`Version = 2022-08-31`。

---

## 三、两条链路：生成 与 提取

| | 生成 | 提取（商品） | 提取（元素） |
|---|---|---|---|
| `req_key` | `jimeng_t2i_v40` | `jimeng_i2i_extract_tiled_images` | `i2i_material_extraction` |
| 指令字段 | `prompt` | **`edit_prompt`** | **`image_edit_prompt`** |
| 输入图 | `image_urls`（0~10 张） | `binary_data_base64` / `image_urls`（**恰 1 张**） | 同左 |
| UI 入口 | 顶部「生成」 | 工具栏「**提取**」→ 标签页 | 同左 |

⚠️ **两份提取文档的字段名不同**（`edit_prompt` vs `image_edit_prompt`）——
传错 = "缺少必选参数"。由 `JimengExtract.PromptField(kind)` 统一给出，
并在 `ExtractPayload_UsesTheRightPromptFieldName_PerKind` 里锁住。

⚠️ **只有元素提取有 `lora_weight`**（商品提取文档里没这个参数）。

### 提取：10 种官方预设（**逐字照抄**）

两份文档都写明「支持以下 N 种类型（N 选 1）」。模型是按这些**特定措辞**约束的，
用户改写会让提取退化（背景不白、不成平铺图）→ 所以 UI 只让**选**，不允许自由编辑。

- **商品提取 6 种**：提取全身衣服 / 提取鞋子 / 提取包包 / 提取沙发 / 提取日用品 / 提取饰品
- **元素提取 4 种**：提取图案 / 提取包装 / 提取logo / 提取纹理

唯一的例外：文档原文「提取饰品时，饰品可以替换为具体的物品，如耳坠、项链等。」
→ 只有「提取饰品」开放替换（`JimengExtract.Customize`），句式保持官方形态。

---

## 四、参数逐项对照（生成）

| 字段 | 来源 | 说明 |
|---|---|---|
| `req_key` | `GenRequest.Model` | 固定 `jimeng_t2i_v40` |
| `prompt` | `GenRequest.Prompt` | 建议 ≤800 字符；过长有概率出图异常 |
| `image_urls` | `GenRequest.Refs` | 0~10 张（这里以 data URL 内联） |
| `width` + `height` | `GenRequest.Aspect`（像素串） | **必须同时传**才生效；面积与宽高"2 选 1"，都传时**以宽高为准** |
| `size` | `GenRequest.Aspect` = `auto` 时 | 面积值，默认 `4194304`（=2048×2048，2K）→ 模型自判比例 |
| `scale` | `GenRequest.JimengScale` | 文本影响程度 `[0,1]`，默认 0.5；**null = 不传** |
| `force_single` | `GenRequest.JimengForceSingle` | 强制 1 张（默认 false，省时省钱） |

### 尺寸规则（与其它四家**都不同**）

`width × height` 面积 ∈ `[1024×1024, 4096×4096]`，
宽高比（宽/高）∈ `[1/3, 3]`；
**没有**"16 的倍数"要求（APIMart/OpenAI 有）。
→ 由 `Catalog.ValidatePixelSizeFor` 的即梦分支把关，**在花钱之前**拒绝。

文档给出的推荐宽高（已全列进 `Catalog.AspectsJimeng`）：
1K `1024x1024`；2K `2048x2048`/`2304x1728`/`2496x1664`/`2560x1440`/`3024x1296`；
4K `4096x4096`/`4694x3520`/`4992x3328`/`5404x3040`/`6198x2656`。

---

## 五、查询与 `req_json`

`Action=CVSync2AsyncGetResult`，body = `req_key` + `task_id` + 可选 `req_json`。

⚠️ **`req_json` 是"JSON 序列化后的字符串"**，不是嵌套对象！
```
"req_json": "{\"logo_info\":{...},\"return_url\":true}"
```
→ 由 `BuildJimengQueryPayload` 用 `ToJsonLine()` 生成，并在
`QueryPayload_SerializesReqJsonAsString` 里反解验证。

支持两项：
- `return_url`：是否同时返回图片**链接**（**有效期 24 小时**）；
- `logo_info`：明水印（`add_logo` / `position` / `language` / `opacity` / `logo_text_content`）。

### 状态机

| `data.status` | 处理 |
|---|---|
| `in_queue` | 继续轮询 |
| `generating` | 继续轮询 |
| `done` | 解析结果（**成功或失败都可能是 done**，看外层 `code`） |
| `not_found` | 报错（无此任务或已过期 12 小时） |
| `expired` | 报错（提示重新提交） |

⚠️ 文档明确：**先判 `code=10000`，再判 `data.status`**，否则解析可能异常
（`code != 10000` 时不会返回 `task_id`）。

### 结果

优先用 `binary_data_base64`（自包含、无过期问题），缺失时才用 `image_urls`（再下载）。

### 💰 拿到 task_id 立即落盘

与 APIMart / 千问同一机制：`AsyncSubmitted` → `PendingTaskStore` 落盘 →
程序意外退出后重启**只 GET 查询、不重新提交**，所以「找回图片」不会重复扣费。

---

## 六、错误码（文档「业务错误码」）

`ThrowIfJimengError` 会把 `code` + `message` + `request_id` 一起带出，便于排查。

| HttpCode | code | 含义 | 可重试 |
|---|---|---|---|
| 400 | 50411 | 输入图片前审核未通过 | ❌ |
| 400 | 50511 | 输出图片后审核未通过 | ✅ |
| 400 | 50412 / 50512 / 50413 | 文本审核未通过 | ❌ |
| 400 | 50518 / 50519 | 版权图审核 | ❌ / ✅ |
| 429 | 50429 / 50430 | QPS / 并发超限 | ✅ |
| 500 | 50500 / 50501 | 内部 / RPC 错误 | ❌ |

> 本项目对 **429 / 5xx** 统一重试（`Catalog.MaxAttempts`），业务 4xx 立刻抛出。

---

## 七、AK/SK 的存储

即梦要**两个值**：`AccessKeyId`（AK）+ `SecretAccessKey`（SK）。

- AK 存 `<数据目录>/.imghub_jimeng_key`（复用现有 key 机制）；
- SK 存 **单独文件** `.imghub_jimeng_secret`。

⚠️ 为什么不拼进同一文件：其余 provider 都是单 key，`ReadKeyFileCompat` 的读取逻辑
（trim 后当单值用）会被污染。分成两个文件则完全复用现有读写/权限/兼容机制。

环境变量：`IMGHUB_JIMENG_ACCESS_KEY` + `IMGHUB_JIMENG_SECRET_KEY`
（也认火山引擎习惯名 `VOLC_ACCESSKEY` / `VOLC_SECRETKEY`）。

**设置浮层**会在 provider = 即梦时多出 Secret 输入框；状态栏会如实显示
「只有 AK，还缺 SecretAccessKey」这种半配置提示。

---

## 八、「校验 key」的取舍

其它 provider 的「校验」用一次**免费只读**请求探活。即梦**没有这种接口**
（查任务必须带有效 task_id）→ 所以即梦的「校验」**不发网络请求**，
只返回本地模型数，避免"校验"本身产生费用或制造垃圾任务。
真正能不能用，第一次生成时错误码会明确说清是签名问题还是配额问题。

---

## 九、来源与核对

| 一手来源（仓库内） | 用途 |
|---|---|
| `docs/即梦AI-图片生成4.md` | 生成链路：全部参数、`req_json`、状态机、错误码 |
| `docs/即梦AI-素材提取(商品提取)-接口文档.md` | 商品提取 6 种预设、`edit_prompt`、`jimeng_i2i_extract_tiled_images` |
| `docs/即梦AI-素材提取(提取元素转为平面设计图)-接口文档.md` | 元素提取 4 种预设、`image_edit_prompt`、`lora_weight`、`i2i_material_extraction` |

⚠️ 三份文档都把**签名细节指向外链**（`volcengine.com/docs/6369/67268`、
`docs/AIGCAIzhongtaigongyongwendang/api-key-usage-guide`），而本仓库开发机
**网络出站被封锁**，无法在线核对。`VolcSigner` 按火山引擎公开的签名 V4 流程实现，
并用**固定输入 → 固定输出**的基线测试锁住形态。

---

## 十、改动检查清单

1. `Catalog` 的 `Jimeng*` 常量（Region/Service/Version/Action/尺寸）是否与文档一致；
2. `JimengExtract` 的 10 条预设是否仍是**逐字原文**（改了措辞会让提取退化）；
3. 两个提取 `req_key` 的**字段名差异**（`edit_prompt` vs `image_edit_prompt`）是否保持；
4. `VolcSigner` 的签名形态是否变化（跑 `JimengTests` 的签名基线测试）；
5. 跑 `dotnet test tests\ImgHub.Core.Tests`（离线、不花钱）。
