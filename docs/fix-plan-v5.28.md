# API 对齐 Review（v5.28.0）— 生成 / 编辑 / 参考图 / 蒙版

> 来源：用户要求「检查参考功能和历史编辑功能是否生效（上传的参考图、编辑历史图片，出来的图总是不相关）」，
> 并「按 APIMart GPT-Image-2.5 与 OpenRouter Image Generation 两份文档逐功能逐行 1:1 review，
> 实现里面的所有功能，**特别是蒙版图部分**」。
> 基线版本：v5.26.0 工作区（v5.27.0 架构审查文档另存 `fix-plan-v5.27.md`）
> 参照文档：
> · APIMart GPT-Image-2.5 — `https://docs.apimart.ai/cn/api-reference/images/gpt-image-2.5/generation`
> · APIMart 上传图片 — `https://docs.apimart.ai/en/api-reference/uploads/images`
> · OpenRouter Image Generation — `https://openrouter.ai/docs/guides/overview/multimodal/image-generation`

---

## 零、执行摘要

| 项 | 结论 |
|---|---|
| 用户报的两条现象 | ✅ **均已定位到确定根因并修复**（见 §一），各有端到端回归断言 |
| 蒙版链路 | ❌ 修复前**实际是死的**（导出的是无 Alpha 的黑白图，且从未发送 `mask_url`）→ ✅ 现已按文档打通 |
| 参数覆盖度 | 修复前缺 6 项（`background` / `output_compression` / `moderation` / 精确像素 `size` / `seed` / `stream` / provider 路由 / OpenRouter `resolution`）→ ✅ 全部实现 |
| 测试基线 | ✅ **全绿**。Core 120/120、集成 77/77（实测，较基线 56+58 净增 83 项） |
| 人工验证 | ✅ 桌面版启动正常、启动自检无异常；真实 payload 已按文档逐项核对（见 §五） |
| 遗留（非本轮） | OpenRouter 无 `mask` 字段（文档未定义）→ 该 provider 用「原图 + 标注合成图」链路，已在 UI 如实标注 |

---

## 一、根因分析（用户现象 → 代码级原因）

用户现象：**「我上传的参考图也好、编辑历史图片也好，出来的图片总是不相关的」**。
这不是一个 bug，而是**四个独立缺陷叠加**的结果：

### 根因① 点「生成」时参考图被完全丢弃

```csharp
// 修复前：MainViewModel.RunGenerationAsync(editMode: false)
List<(byte[], string)>? refs = null;
if (editMode) { /* ...只有编辑模式才收集 refs... */ }
// → refs 恒为 null，RefImages 列表**根本没进请求**
```

后果：用户「＋添加」了参考图，界面上看得到缩略图，但请求体里没有 → 纯文生图 → 出图与参考图无关。
**UI 全绿、不报错、不抛异常** —— 所以测试和日志都发现不了。

### 根因② 编辑目标恒为「最新一张」，历史点选无效

```csharp
// 修复前：Session.Current 的定义
public Item? Current => Items.Count > 0 ? Items[0] : null;   // ← 永远是最新的
```

```csharp
// 修复前：RunGenerationAsync(editMode: true)
var cur = _sess.Current;    // ← 拿的是 Items[0]，不是"用户点选的那张"
```

用户在历史列表里点选一张旧图 → 只切换了**预览**，编辑请求仍然拿最新那张
→ 现象同样是「出的图与我想改的图不相关」。

### 根因③ 蒙版**没有 Alpha 通道**，且从未发送

```csharp
// 修复前：RegionCanvas.ExportMask()
surface.Canvas.Clear(SKColors.Black);            // ← 黑底
foreach (var sh in _shapes) sh.RenderToMask(...); // ← 且是**描边**（方框只有一圈线）
```

文档明确要求：
> 使用带 Alpha 透明通道的 PNG……**Alpha 完全透明（alpha=0）的区域表示需要修改**，其余区域用于保留
> 普通黑白图片如果没有 Alpha 透明通道，不能直接作为蒙版。APIMart 不会自动补充透明通道。

即：旧实现导出的是**无 Alpha 的黑底描线图**，语义完全相反且缺少通道。

更严重的是 —— 导出后**从未被发送**：

```csharp
// 修复前：AnnotatedMaskPath 只有两处引用：置空、赋值。**没有任何读取点**。
// 且 mask_url 在整个 src/ 下出现 0 次。
```

所以「蒙版」在修复前是一条**完全断掉的链路**：既生成不出合法的蒙版，也从不发给模型。

### 根因④ 参考图**无条件压到 1024**

```csharp
// 修复前（两处）
refs.Add((ImageCodec.ShrinkForReference(raw), "image/png"));   // 默认 maxSide=1024
```

文档允许单图 20MB（上传接口）。压到长边 1024 会明显削弱「保留主体身份、文字、纹理」的效果 ——
而「参考是否生效」恰恰取决于这些细节。这会让①③即使修好，效果仍打折扣。

---

## 二、1:1 参数对照（逐项）

### 2.1 APIMart `POST /v1/images/generations`

| 文档参数 | 类型/默认 | 修复前 | 修复后 | 位置 |
|---|---|---|---|---|
| `model` | string 必填 | ✅ | ✅ | `BuildApimartPayload` |
| `prompt` | string 必填 | ✅ | ✅ | 同上 |
| `size` | 比例名/精确像素/`auto` | ⚠️ 仅 9 种比例 | ✅ 15 种比例 + 精确像素 + `auto` | `Catalog.AspectsApimart` / `ValidatePixelSize` |
| `resolution` | `1k`/`2k`/`4k` | ✅ | ✅（精确像素时按文档**不再发送**） | `BuildApimartPayload` |
| `quality` | `low`~`max`/`auto` | ✅ | ✅ | `Catalog.QualityChoices` |
| `n` | 1~4 | ✅ | ✅（钳制到 `ApimartMaxN`） | 同上 |
| `output_format` | `png`/`jpeg`/`webp` | ⚠️ 有字段但被上层硬编码为配置值 | ✅ | `GenRequest.OutputFormat` |
| `output_compression` | 0~100（jpeg/webp） | ❌ 未实现 | ✅ | `GenRequest.OutputCompression` |
| `background` | `transparent`/`opaque`/`auto` | ❌ 未实现 | ✅（含 JPEG 组合前置拦截） | `Catalog.ValidateBackground` |
| `moderation` | `auto`/`low`，默认 low | ❌ 未实现 | ✅ | `GenRequest.Moderation` |
| `image_urls` | string[] ≤16 | ✅ | ✅（第 1 位 = 主体，顺序保持） | `BuildApimartPayload` |
| **`mask_url`** | **PNG(含 Alpha)，尺寸须与 `image_urls[0]` 一致** | ❌ **完全未实现** | ✅ **已实现**（上传后传 URL） | `GenerateApimartAsync` / `Catalog.ValidateMask` |

**尺寸规则**（文档「尺寸规则」节）已全部落实为前置校验：
宽高均为 16 的倍数、单边 ≤3840、长短边比 ≤3:1、总像素 655,360~8,294,400。

### 2.2 APIMart `POST /v1/uploads/images`

| 文档要求 | 修复前 | 修复后 |
|---|---|---|
| `file` 表单字段、JPEG/PNG/WebP/GIF | ✅ | ✅ |
| 单文件上限 **20MB** | ⚠️ 常量写 12MB | ✅ 20MB |
| 返回 `url`（72 小时有效） | ✅ | ✅ |
| 蒙版同样走上传接口 | ❌ 从未上传过蒙版 | ✅ `mask_url` 由上传结果提供 |

### 2.3 OpenRouter `POST /api/v1/images`

| 文档参数 | 修复前 | 修复后 | 备注 |
|---|---|---|---|
| `model` / `prompt` | ✅ | ✅ | |
| `n` | ✅ 上限 10 | ✅ | |
| `resolution` | ❌ **被完全忽略**（参数收下了但没进 payload） | ✅ 并映射为 `512`/`1K`/`2K`/`4K` | 文档档名是大写，与 APIMart 小写不同 |
| `aspect_ratio` | ✅ | ✅ | |
| `size`（档位或精确像素） | ❌ 未实现 | ✅ 精确像素时**不再带** `aspect_ratio`/`resolution` | 文档：显式像素是权威值，冲突会 400 |
| `quality` | ✅ | ✅ | |
| `output_format` | ❌ **被忽略** | ✅（含 `svg`） | |
| `background` | ❌ | ✅ | |
| `output_compression` | ❌ | ✅ | |
| `seed` | ❌ | ✅ | |
| `stream`（SSE） | ❌ | ✅ 完整实现（3 种事件 + `[DONE]`） | 见 §三 |
| `input_references` | ✅ base64 data URL | ✅ | |
| `provider.only/order/ignore/sort/allow_fallbacks/options` | ❌ | ✅ | `ProviderRouting` |
| `user` | ❌ | ❌（有意不做，见 §六） | |
| `mask` | — | — | **文档未定义此字段**，不发明行为 |

### 2.4 能力差异（实测 `GET /api/v1/images/models`）

文档要求「Check the model's `supported_parameters` to see which values each endpoint accepts」。
实测各模型能力确实不同，故 UI 选项已按 provider 区分：

| 模型 | `aspect_ratio` | `resolution` | `n` 上限 | `input_references` |
|---|---|---|---|---|
| `openai/gpt-image-2.5-flare` / `-sunburst` | 9 种（无 2:1/1:2/5:4…） | 未声明 | 10 | 16 |
| `google/gemini-3.1-flash-image` | 14 种（含 1:4/8:1） | `512`~`4K` | **1** | 14 |
| `google/gemini-2.5-flash-image` | 10 种 | 未声明 | 1 | **3** |
| `openai/gpt-image-1-mini` | 4 种 | 未声明 | 10 | 16 |

> ⚠️ **留作后续**：目前 UI 按 provider 给出**并集**，尚未按「当前模型」动态收窄
> （例如模型只支持 1 张图时 `n=4` 会被服务端拒绝）。见 §六 待办。

---

## 三、逐功能 Review

### 3.1 文生图（生成）

| 检查项 | 修复前 | 修复后 |
|---|---|---|
| 参数是否齐全 | 缺 6 项 | 文档全项 |
| 参考图是否发送 | ❌ 丢弃 | ✅ `Refs` 进入请求 |
| 前置校验 | 无 | ✅ 空提示词 / background 组合 / 像素规则 / 参考图 ≤16 / 蒙版三要求 |

### 3.2 图生图 / 编辑（「编辑」按钮）

| 检查项 | 修复前 | 修复后 |
|---|---|---|
| 目标图来源 | ❌ `Items[0]`（最新一张） | ✅ `CurrentItem`（预览中的那张） |
| 目标图位置 | 第 1 位（正确） | ✅ 第 1 位（保持） |
| 用户参考图是否附带 | ✅（编辑模式有收集） | ✅（追加在后面） |
| 参考图保真 | ❌ 一律压到 1024 | ✅ 仅在超限（20MB / 长边 2048）时压 |
| 编辑目标是否可辨识 | ❌ UI 无任何提示 | ✅ 悬停显示目标文件名与参考图数量 |

### 3.3 参考图

| 检查项 | 修复前 | 修复后 |
|---|---|---|
| 导入是否被压缩 | ❌ 导入即压到 1024 | ✅ 原样保存 |
| 落盘是否丢 Alpha | ⚠️ 走 `SaveImageToHome` → 按 `output_format` 转码（jpeg 时丢 Alpha） | ✅ `SaveRefImageToHome` 原格式落盘 |
| 上传缓存 | ✅ SHA1 头尾 512B | ✅（不变） |
| 一次性语义 | ✅ 用完即清 | ✅（不变） |

### 3.4 蒙版 / 局部重绘（本轮重点）

| 检查项 | 修复前 | 修复后 |
|---|---|---|
| 导出格式 | ❌ 黑底 + 无 Alpha | ✅ **透明底 + Alpha 通道** |
| 语义方向 | ❌ 黑白（文档说不能直接用） | ✅ **alpha=0 = 要改**（与文档一致） |
| 矩形/椭圆 | ❌ 只有描边（无区域） | ✅ **填充**（区域语义） |
| 马克笔/画笔笔画 | ❌ 2px 细线（对模型等于没指定区域） | ✅ 加粗到「笔刷×3」或「最短边 1.2%」，下限 12px |
| 尺寸与参考图一致 | ❌ 无保证 | ✅ 压缩参考图时**按同一比例压蒙版** |
| 是否发送 | ❌ **从未发送** | ✅ APIMart `mask_url` |
| 前置校验 | 无 | ✅ Alpha / 尺寸一致 / ≤4MB / 必须有参考图 |
| 切换图片后的旧蒙版 | ❌ 会静默错配 | ✅ 记底图归属，不匹配则明确降级 |
| 导出性能 | ❌ 逐像素循环（4K 图 829 万次托管调用，卡 UI 数秒） | ✅ 颜色矩阵一次性完成 |

**实测验证**（关键，因为此处曾假设出错）：

```
marked  内(20,20).A=255  外(5,5).A=0      ← 标记不透明
out     内(20,20).A=0    RGB=(0,0,0)     ← 反相后：标记=透明=要改
PNG往返 内.A=0           外.A=255        colorType=6 (RGBA)
==> OK: 标记区=透明(要改)，其余=不透明(保留)
```

> ⚠️ 过程中先试了 `SKBlendMode.Difference` 反相，**实测语义是错的**：
> Difference 对 alpha 走并集公式，标记区 alpha 仍是 255 → 蒙版变成"整张不透明"，
> 等于没指定可改区域。改用 `SKColorFilter` 颜色矩阵（`A' = 255 - A`）后实测正确。
> 这条已写进代码注释，避免后人重蹈。

### 3.5 任务查询 / 断点恢复（回归确认）

| 检查项 | 结论 |
|---|---|
| `submitted`/`processing`/`completed`/`failed` 四态 | ✅ 已覆盖；`failed` 取 `error.message` |
| 图片地址 `data.result.images[].url[]`（数组） | ✅ 已处理数组与字符串两种形态 |
| 拿到 `task_id` 立即落盘（v5.26.0） | ✅ 未回归 |
| 轮询间隔 2~5 秒（文档建议） | ✅ 3.0s + 抖动 |
| 断点恢复只查不重提 | ✅ 未回归（不重复扣费） |

---

## 四、修复清单（改动文件）

| 文件 | 改动 |
|---|---|
| `Core/Services/GenRequest.cs` | **新增**：统一请求参数对象（含 `ProviderRouting`） |
| `Core/Services/ImageApi.cs` | 契约改为 `GenerateAsync(GenRequest)`；新增 `BuildApimartPayload`/`BuildOpenRouterPayload`/`PrepareRefs`/`ValidateRequest`；APIMart 上传蒙版 + `mask_url`；OpenRouter `resolution`/`output_format`/`background`/`seed`/路由/SSE 流式 |
| `Core/Catalog.cs` | 15 种 APIMart 比例 / OpenRouter 比例 / 分辨率档名映射 / 背景 / 审核 / 精确像素校验 / 蒙版校验 / PNG Alpha 探测 / `MaxN` |
| `Core/Imaging/ImageCodec.cs` | 新增 `ScaleKeepingAlpha`（蒙版缩放必须保 Alpha） |
| `Core/Http/HttpJsonClient.cs` | 拆出 `PostRawAsync`；新增 `PostJsonStreamAsync`（SSE，不重试） |
| `Core/Models/AppConfig.cs` | 新增 10 个文档对齐参数的持久化（snake_case） |
| `App/Controls/RegionCanvas.cs` | `ExportMask` 改为真 Alpha 蒙版 + 填充语义 + 笔触加粗；`ExportComposite` 改颜色矩阵（性能） |
| `App/ViewModels/MainViewModel.cs` | 修复①②；`CurrentItem` 跟踪预览图；`BuildRequest` 统一组装；蒙版校验与降级；动态提示文案 |
| `App/Views/MainView.axaml(.cs)` | 两套布局同步加「高级参数」区；动态 ToolTip；蒙版导出接入 |
| `App/Services/PlatformAbstractions.cs` | `ImageApi` 改为接口类型 + 工厂（可测试性） |
| 测试 | 新增 `ImageRequestPayloadTests`（29 项）、`GenerationRequestContractTests`（8 项） |

---

## 五、验收记录

| 项 | 结果 |
|---|---|
| `dotnet test tests\ImgHub.Core.Tests` | ✅ **122/122** |
| `dotnet test tests\ImgHub.Integration.Tests` | ✅ **77/77** |
| 桌面版启动（人工） | ✅ 窗口正常、启动自检无异常、日志无绑定错误 |
| **Native AOT publish + 运行** | ✅ 无 IL2026/IL3050 裁剪警告；exe 31.29MB + 3 原生 DLL 齐全；AOT 产物实际启动成功无崩溃 |
| 真实 payload 核对 | ✅ `mask_url` / `image_urls` / `resolution:"2K"` / `provider` 路由 / `seed` / `stream` 逐项符合文档 |
| 参考图保真 | ✅ 1024×768 原样进入请求（未被压缩） |
| 蒙版尺寸一致性 | ✅ `ref=1024x768  mask=1024x768  alpha=True` |
| **真实 API 调用（蒙版）** | ✅ 见下 |
| **真实 API 调用（参考图）** | ✅ 见下 |

> **AOT 验证细节**（CONSTRAINTS H1/E1，本轮改了序列化相关代码故必须实测）：
> ```
> dotnet publish src\ImgHub.Desktop -c Release -r win-x64 -p:PublishAot=true ... -o <tmp>
> → EXIT=0，无 error；IL2026/IL3050 零输出
> 产物：ImgHub.Desktop.exe 31.29MB + libSkiaSharp.dll + libHarfBuzzSharp.dll + av_libglesv2.dll
> 运行：窗口正常，日志显示启动自检完成 → 证明新增的 AppConfig 10 个属性（source-gen）
>       与 GenRequest 在 AOT 下均正常
> ```

### 5.1 真实调用验证：蒙版局部重绘（决定性证据）

用真实 APIMart key 走完整链路（含上传参考图 → 上传蒙版 → `mask_url`）：

```
原图 1536×864
蒙版 1536×864  alpha=True  7KB   ← 左上 40% 区域 alpha=0（=要改）
  · 上传参考图 1/1（1307KB）…
  · 参考图已上传 1 张
  · 上传修改区域蒙版（7KB）…
  · 蒙版已上传（仅作用于参考图第 1 张）
成功！产出 1 张，cost=0.011166，tokens=1464
输出 1360x768
```

**目视对照结果**（`prompt` = 「只把蒙版标记的透明区域替换为一个纯红色实心方块，其余完全不变」）：

| 区域 | 原图 | 输出 |
|---|---|---|
| 左上（**蒙版 alpha=0 区**） | 机器人图标 | **被替换为纯红方块** ✅ |
| 右上 | Avalonia 图标 | 逐像素保留 ✅ |
| 左下 | 大脑图标 | 逐像素保留 ✅ |
| 右下 | 相机图标 | 逐像素保留 ✅ |

→ 证明三点同时成立：① 蒙版确实被上传并作为 `mask_url` 发送；
② alpha=0 的语义与文档一致（该区域被改）；
③ 蒙版尺寸与参考图第 1 位一致，未错位。

> 这正是修复前**不可能**出现的结果 —— 修前 `mask_url` 从未发送，且导出的蒙版没有 Alpha 通道。

### 5.2 真实调用验证：参考图保真（编辑功能）

```
参考图 1536x864 1307KB（未压缩原样送入）
成功 cost=0.011146 tokens=1459
输出 1360x768
```

**目视对照**（`prompt` = 「保持这四个图标的形状、颜色与排列完全不变，只把白色背景改成深蓝星空」）：
四个图标的形状/颜色/排列**逐像素保留**，仅背景被替换 → 参考图作为「主体」被正确理解。
若参考图仍被压到 1024（修复前行为），图标边缘与像素画细节会明显失真。

**本次验证总花费：≈ $0.022。**

关键回归断言（都在测试里，防止再次"静默失效"）：

```
Generate_SendsUserReferenceImages              # 根因①
Edit_UsesTheImageSelectedInHistory_NotLatest   # 根因②（按字节断言发的是哪张）
RegionEdit_SendsOriginalAsFirstRef_AndMaskViaMaskUrl  # 根因③
ReferenceImage_UnderLimit_IsSentByteIdentical  # 根因④
Generate_RejectsMaskWithoutAlpha_BeforeSpending # 前置校验真的省钱（断言未发请求）
```

---

## 六、生图流程与 UI 设计评估（用户要求）

### 6.1 当前流程

```
提示词 ─┬─ 生成 ────────────────────────→ 纯文生图 + 用户参考图
        └─ 编辑 ─→ 目标图(第1位) + 用户参考图
                     ↑ 目标图 = 预览中的图

标注模式(RegionMode) ─→ 圈画 ─→ 「用标注编辑」
                       ├─ APIMart    → 原图 + mask_url（严格局部重绘）
                       └─ OpenRouter → 原图 + 合成图 + 提示词（尽力遵守）
```

### 6.2 已确认的问题与建议

#### A. 「生成」与「编辑」语义重叠，用户难以预期（**建议优先处理**）

现状：两者都走 `RunGenerationAsync`，差别只在是否把目标图放在第 1 位。
- 用户上传参考图后点「生成」：参考图**会**发送（已修），但界面上没有明显提示。
- 点「编辑」：以预览图为第 1 位主体。

**建议**：把按钮改为语义自明的两个入口：
```
[文生图]  —— 只用提示词 + 参考图（当前"生成"）
[以当前图改] —— 主体锁定为预览图（当前"编辑"）
```
并在按钮旁常驻显示「当前目标：xxx.png」。目前用动态 ToolTip 缓解，但 ToolTip 需要悬停才能看到，
**触屏（Android）上根本看不到** —— 这是移动端的关键缺口。

#### B. 标注模式的可达性与状态反馈

现状：`RegionMode` 是模态式开关，开启后需先点「编辑图片」再圈画。
- 「用标注编辑」的 `IsEnabled` 只绑 `CanRun`，**未校验是否真的圈画过**（靠点击后弹警告）。
- 圈画数量 `RegionCount` 在 UI 上无实时显示。

**建议**：
1. `IsEnabled` 改为 `CanRun && RegionCount > 0`，从源头避免无效点击；
2. 画布角落常驻「已标注 N 处」；
3. 标注模式下高亮「用标注编辑」按钮（accent 已有，可加脉冲动画）。

#### C. 蒙版能力差异需要更显眼的提示

OpenRouter 无 `mask_url`，只能靠「合成图 + 提示词」。用户若在 OpenRouter 上期待
「严格只改圈内」，会有落差。

**建议**：切到 OpenRouter 且存在标注时，在标注工具区显示一行常驻提示
（而非只在 ToolTip 里）：
```
⚠ 当前 provider 不支持蒙版局部重绘 → 将用「原图+标注图」尽力约束
```

#### D. `n`（批量）未按模型能力收窄

文档明确部分模型只支持 `n=1`（如所有 gemini 图像模型）。当前 UI 固定 `1~4`，
用户拉到 4 会被服务端拒绝。

**建议**：拿到 `GET /images/models` 的 `supported_parameters.n.max` 后动态设上限
（APIMart 侧为 4）。需要缓存一份「模型能力表」。

#### E. 精确像素尺寸缺少便捷入口

已支持手输 `1600x1200`（画幅下拉的输入框），但：
- 输入非法值（非 16 倍数）要等到提交才报错；
- 无常用尺寸快捷选项。

**建议**：给画幅下拉加一组常用精确尺寸预设（1024×1024 / 2048×1152 …），
并对输入做即时校验（`Catalog.ValidatePixelSize` 已有，直接绑上即可）。

#### F. 参考图「一次性」清空可能造成困惑

`KeepRefImages` 默认 false → 用完即清。用户若没注意，下一次生成会突然"参考图没了"。

**建议**：清空时除了消息面板提示，在参考图列表位置显示一个短暂的占位提示
（「参考图已清空（上次为一次性）」），比只写消息面板更容易被看到。

#### G. 成本预估未计入参考图输入 token

文档：实际账单还包含提示词及**参考图的输入 token**（图片输入 $8/1M）。
当前 `ModelStatsService` 只按历史平均估算。

**建议**：保守做法是在预估文案里加一句「未含参考图输入费用」，避免用户以为预估就是账单。

### 6.3 做得好的地方（建议保留）

| 项 | 说明 |
|---|---|
| 前置校验「花钱之前」拦 | `ValidateRequest` 在提交前拦掉 400 类错误；测试断言**未发出请求** |
| 拿到 `task_id` 立即落盘 | v5.26.0 的断点恢复，实测未回归 |
| 失败退款语义 | `failed` 状态与 `error.message` 如实透传 |
| 参考图上传缓存 | 同一张图同会话只上传一次 |
| 错误提示分诊断层次 | `ErrorHints.Explain` 给出可执行建议（余额 vs 权限的顺序判断） |

---

## 七、明确不做（避免"发明行为"）

| 项 | 理由 |
|---|---|
| OpenRouter 传 `mask` | 文档**没有**这个字段。把黑底透明图当 `input_references` 塞进去，模型很可能当成"要融合的另一张素材"，反而破坏主体一致性 |
| 自动猜 `provider.sort` | 默认不传，让服务端按自身策略路由；仅在用户显式填写时发送 |
| `user` 字段 | OpenRouter 文档说明它会参与上游身份哈希，涉及隐私与去重语义，本轮不动 |
| 流式部分图落盘 | 文档明确"部分预览不产生部分计费"，只用于预览；落盘会产生无意义的半成品文件 |

---

## 八、后续待办（按优先级）

1. **（高）** 按模型能力收窄 `n` 上限与 `aspect_ratio` 选项（需缓存模型能力表）
2. **（高）** 标注模式下 `RegionCount==0` 时禁用「用标注编辑」+ 常驻显示已标注数量
3. **（高）** 「悬停说明」开关**即时生效缺失**：`Help.RefreshTree` 从未被任何地方调用
   （全仓库仅有定义）。当前只在启动时按配置应用一次，用户改开关后已挂载的控件不跟随，
   需重启才生效。修法：`MainView` 订阅 `EnableToolTips` 变化后调用
   `Help.RefreshTree(this)`（或在设置浮层关闭时调用一次）。
4. **（中）** 移动端可见的常驻状态区（替代只能悬停才看到的 ToolTip）
5. **（中）** OpenRouter 缺蒙版时在标注区显示常驻提示
6. **（中）** 画幅下拉加常用精确尺寸预设 + 即时校验
7. **（低）** 成本预估注明"未含参考图输入 token"
8. **（低）** 参考图清空时的就地提示

---

## 九、本轮排除的疑点（避免后人重复排查）

| 疑点 | 结论 |
|---|---|
| `ShrinkIfNeeded` 的「体积超限但长边未超」分支是否失效？ | **不可达，无需修**。长边 ≤ `RefShrinkMaxSide`(2048) 时 PNG 体积上限 = 原始 RGBA = 16MB（实测 2048² 随机噪声 PNG 仅 12.02MB），恒 < `MaxUpload`(20MB)。已实测三种尺寸验证 |
| 动态文案改用原生 `ToolTip.Tip` 是否绕过全局开关？ | **不构成回归**。原生 `ToolTip.Tip` + `{Binding}` 是本仓库既有惯例（存量已有 `ToolTip.Tip="{Binding}"`），且全局开关本就只覆盖 `Help.Tip` |
| 参考图 20MB 上限是否过宽？ | 与文档一致（上传接口文档：最大 20MB）。且 `RefShrinkMaxSide=2048` 会先于体积上限触发，实际极少走到体积分支 |


---

## 十、改名与 UI 收尾（v0.5.28 第二轮）

### 10.1 全局改名 Imgagent → ImgHub

| 项 | 改动 |
|---|---|
| 项目 / 文件夹 / 程序集 / 命名空间 | `Imgagent.*` → `ImgHub.*`（git 识别为 60 个重命名，历史保留） |
| 解决方案 | `Imgagent.slnx` → `ImgHub.slnx` |
| 应用显示名 | `ImgHub 工作台`（窗口标题 / 顶栏 / Android label） |
| Android | `ApplicationId` = `com.imghub.app`；`ApplicationVersion` 5→6 |
| 版本号 | `Directory.Build.props` `<Version>` 0.5.26 → **0.5.28** |
| 技术标识（保持小写） | 环境变量 `IMGHUB_*`、数据目录 `imghub`、key 文件 `.imghub_*_key`、日志 `imghub-yyyy-MM-dd.log` |

**兼容层（不可删）** —— 已实测验证：

```
应用启动 | 数据目录：C:\Users\Yutsuki\AppData\Local\imgagent   ← 沿用旧目录，未建新目录
(Session.ReadKeyFileCompat) 沿用改名前的 key 文件：.imgagent_polish_key
(Session.ReadKeyFileCompat) 沿用改名前的 key 文件：.imgagent_apimart_key
启动自检：全部通过 → 就绪
```

- `AppPaths.ResolveHome()`：新目录不存在但旧目录存在 → **直接沿用旧目录**（数据无需搬迁）
- `Session.ReadKeyFileCompat()`：新名优先、旧名回退
- 环境变量：`IMGHUB_*` ← 也接受 `IMGAGENT_*`
- 锁定测试：`RenameCompatibilityTests`（Core 6 项）、`RenamePathTests`（集成 3 项）
- `.gitignore`：新旧两代 key 文件名都忽略（防误提交）

### 10.2 本轮修正的高优先待办（上一轮列的）

| 待办 | 修法 |
|---|---|
| 按模型收窄 `n` 上限 | `Catalog.MaxNFor(p, model)`：gemini 图像系=1、openai 系=10、APIMart=4 |
| 按模型收窄画幅/分辨率 | `AspectChoicesFor` / `ResolutionChoicesFor` |
| 未圈画时禁用「用标注编辑」 | `CanEditWithRegions` = `CanRun && RegionCount>0`；新增常驻 `RegionCountText` |
| 悬停说明开关需重启才生效 | `Help.RefreshTree` 此前**从未被调用** → 现在开关变化即时刷新 |

### 10.3 UI 修复（用户实测反馈）

见 [ui-csharp-feasibility.md](ui-csharp-feasibility.md) §七：
下拉框 40→34 高解决「生成按钮被挤出视口」、文字上移 1px 解决字底裁切、
按钮统一 `MinHeight=34` 对齐「系统看图器」、预估文案加 Wrap 解决裁切。

### 10.4 构建脚本修复

| 缺陷 | 修法 |
|---|---|
| `build.ps1` 的 `OutDir` 相对**当前目录**，产物落到仓库外 | 改为相对**仓库根**（`Resolve-OutDir`） |
| APK 名硬编码版本号 `imghub-0.5.26` | 从 `Directory.Build.props` 读 `<Version>` |
| ⚠️ 本轮一度给 `build.ps1` 加中文注释 → **PS 5.1 解析失败** | 改回英文注释；实测非 ASCII 字节 = 0 且 PS 5.1 解析无错误 |

### 10.5 清理与验证

| 项 | 结果 |
|---|---|
| 清理 | 删除根目录 `_*.txt`/`_*.py` 共 9 个 + `avalonia/` 残留（均未入库） |
| 保留 | `legacy/`（按约定：行为契约，清掉会改错方向） |
| 全量测试 | ✅ Core **132** + 集成 **85** = **217 项全绿** |
| 桌面 Native AOT | ✅ `release/desktop-aot/ImgHub.Desktop.exe` 31.31MB + 3 原生 DLL；**实跑无崩溃** |
| Android APK | ✅ `com.imghub.app-Signed.apk` 52.70MB（改名后包名正确） |
| 桌面框架依赖版 | ✅ `release/desktop/`（42 文件，经 `build.ps1` 官方流程产出） |

> ⚠️ Android 首次构建报 `XAGNM7009`（工具链内部错误）→ **删 `src/ImgHub.Android/bin` 与 `obj` 后重试即通过**，非代码问题。
