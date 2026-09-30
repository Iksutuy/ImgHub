# ImgHub（Avalonia 版）— 架构文档

> 讲清「系统怎么搭的、数据怎么流、两端怎么复用」。
> 读之前建议先看 [README.md](README.md) 了解文档分工。

---

## 一、四层结构

```
┌─────────────────────────────────────────────────────────────┐
│  ImgHub.Desktop (net10.0)     ImgHub.Android (net10.0-android)
│  ── 只做平台适配 ──                    ── 只做平台适配 ──
│  · Program.cs 启动              · MainActivity 宿主
│  · app.manifest DPI             · AndroidApp 生命周期
│  · app.ico 文件图标              · mipmap 各密度图标
│  · win-x64 RID / Native AOT     · APK 打包
└──────────────────────────┬──────────────────────────────────┘
                           │ 引用
┌──────────────────────────▼──────────────────────────────────┐
│  ImgHub.App (net10.0)  ── 共享 UI ──                       │
│  · App.axaml        主题/样式/字体/颜色字典（含深浅两套）      │
│  · Views/           MainView（三栏工作台）、MainWindow        │
│  · ViewModels/      MainViewModel（编排层）、HistoryRow 等    │
│  · Controls/        RegionCanvas（区域标注画布）              │
│  · Services/        平台抽象接口 + AppPaths + 组合根容器      │
│                                                              │
│  ⚠️ 不引用任何平台特定类型，只依赖 Avalonia 抽象层            │
└──────────────────────────┬──────────────────────────────────┘
                           │ 引用
┌──────────────────────────▼──────────────────────────────────┐
│  ImgHub.Core (net10.0)  ── 平台无关业务 ──                  │
│  · Catalog.cs        模型/质量/画幅/成本常量（唯一真源）      │
│  · Models/           Item / GenResult / AppConfig / ApiProvider
│                      / ModelStats（按模型统计）               │
│  · Http/             HttpJsonClient / ApiError / ErrorHints   │
│  · Services/         ImageApi（双 provider）/ PolishService   │
│                      / ModelStatsService（价格预估）          │
│  · Storage/          Session（配置/历史/提示词/key）           │
│  · Imaging/          ImageCodec / Placeholder（SkiaSharp）    │
│                                                              │
│  ⚠️ 架构红线：不引用 Avalonia、不引用 Android、不引用 UI      │
│  ✅ 只依赖 SkiaSharp（跨平台图像库，非 UI 框架）              │
└─────────────────────────────────────────────────────────────┘
```

### 为什么这么分？

| 分层 | 收益 | 实战验证 |
|---|---|---|
| **Core 零 UI 依赖** | 可单元测试、可换 UI 框架 | App 层被误删时，**Core + 测试无损**，凭测试契约完整重建 |
| **App 共享 UI** | 一套 XAML 两端跑 | Android 竖屏只需改 `MainView.axaml` 一处 |
| **Head 极薄** | 平台差异隔离 | `Program.cs` 16 行；`MainActivity.cs` + `AndroidApp.cs` 合计 43 行 |

---

## 二、数据流

### 生成一张图（完整链路）

```
用户点「生成」
   ↓
MainViewModel.GenerateCommand
   ├─ 校验：Busy? CanRun? 提示词非空?
   ├─ PushPromptHistory（提交即记录 —— 铁律 D3）
   ├─ 组装 refs：编辑模式下 [待修改图(第1位), ...RefImages]
   ├─ FlushConfig()（确保参数落盘）
   ↓
ImageApi.GenerateAsync(prompt, key, model, quality, aspect, n, refs, offline, ...)
   ├─ offline? → Placeholder.Png（确定性占位图，尊重 n）
   ├─ OpenRouter → POST /images（同步返回 b64）
   └─ APIMart   → POST /images/generations → 并发轮询 /tasks/{id}
                  （参考图先 POST /uploads/images 换公网 URL）
   ↓
GenResult { Images: [(bytes, media)], Cost, Tokens }
   ↓
MainViewModel.LandResultsAsync
   ├─ SaveImageToHome()：按输出格式定扩展名 + SkiaSharp 转码
   ├─ Session.Push(item) + Log(item)（写 state.json + history.jsonl）
   ├─ TotalCost / ProviderCost 累加
   ├─ RefreshHistory() / PublishBatchResults()
   └─ ShowPreviewAsync()（Dispatcher.Post，不阻塞）
```

### 配置持久化

```
用户改参数（模型/质量/画幅/分辨率/格式/批量/离线/润色）
   ↓
On*Changed → PersistConfig()
   ├─ 同步到 _sess.Config（内存态，立即）
   └─ 尾触发节流 Timer（120ms 安静后写一次，**最后一次必落盘**）
   ↓
Session.SaveConfig() → 原子写 config.json（.tmp → File.Move）
   ↑
   └─ Session._writeLock 全局串行化（防 UI 线程与 Timer 回调并发写）
```

**config.json 字段名是 snake_case**（`batch_n`/`output_format`），与 Python 版互通 ——
靠 `AppConfig` 上的 `[JsonPropertyName]` 映射。

---

## 三、五个 provider 的差异（关键）

> v0.5.31 起从「双 provider」扩到 **5 家**：OpenRouter / APIMart / OpenAI 官方 /
> 千问 DashScope / **即梦（火山引擎）**。新增三家各有专文：
> [provider-openai-image-api.md](provider-openai-image-api.md)、
> [provider-qwen-dashscope-api.md](provider-qwen-dashscope-api.md)、
> [provider-jimeng-api.md](provider-jimeng-api.md)。

| 维度 | OpenRouter | APIMart | OpenAI 官方 | 千问 DashScope | 即梦（火山引擎） |
|---|---|---|---|---|---|
| 调用模式 | 同步 | **异步**（轮询 `/tasks/{id}`） | 同步 | 3.0 同步 / 2.0·max·plus **异步** | **异步**（提交 → 轮询） |
| 鉴权 | Bearer | Bearer | Bearer | Bearer | **AK/SK 签名**（HMAC-SHA256） |
| 接口风格 | REST | REST | REST | REST | **RPC**（`Action` 写在 query） |
| 端点 | `/images` | `/images/generations` | `/images/generations` + `/images/edits` | `/services/aigc/multimodal-generation/generation` 或 `text2image/image-synthesis` | `visual.volcengineapi.com`（+`?Action=`） |
| 请求体格式 | JSON | JSON | JSON / **multipart**（有参考图时） | JSON | JSON |
| 模型命名 | `provider/model` | 裸名 | 裸名 | 裸名 | **`req_key`**（服务标识） |
| 画幅参数 | `aspect_ratio`（另有 `resolution` 档） | `size`（比例名或像素） | `size`（**像素串 / auto**） | `size`（**像素串**；text2image 用 `宽*高`） | `width`+`height`（**必须成对**）或 `size`（面积） |
| 参考图 | `input_references`（base64 内联） | `image_urls`（**需先上传换公网 URL**） | `image[]`（multipart，可多张） | `content[].image`（base64 内联），**上限 3 张** | `image_urls`（**0~10 张**）；提取类**恰 1 张** |
| 蒙版 | ❌ 文档无 mask 字段 | ✅ `mask_url` | ✅ multipart `mask` 字段 | ❌ 文档无 mask 字段 | ❌ 文档无 mask 字段 |
| 质量档 | auto/low/medium/high | +xhigh/max（仅 2.5 系） | low/medium/high[/xhigh/max]；**dall-e-3 是 standard/hd** | **无 quality 参数** | **无 quality 参数**（用 `scale`） |
| 批量 `n` | 1~10（gemini 系 1） | 1~4 | 1~10（**dall-e-3 仅 1**） | 1~6（**max/plus 固定 1**） | **无 `n`**（用 `force_single`；模型自定最多 15） |
| 流式 | ✅ SSE | ❌ | ✅ SSE（事件名 generators/edits 不同） | ❌ | ❌ |
| 输出格式 | png/jpeg/webp/svg | png/jpeg/webp | png/jpeg/webp（dall-e-3 仅 png） | **png** | **png** |
| 返回图像 | base64 | 公网 URL（需下载） | base64（dall-e 系可能 URL） | **URL**（有效期 24h） | **base64**（优先）或 URL（24h） |
| 特有功能 | provider 路由 | — | partial images | prompt_extend / watermark | **素材提取（10 种预设）** / 明水印 / req_json |

**跨 provider 的四个"真源"**（`Catalog`，任何一处漏改都会静默发错参数）：
`QualityChoices` / `AspectChoicesFor` / `ResolutionChoicesFor` / `MaxNFor`，
外加 `MaxRefsFor`（千问 3 张）与 `ValidatePixelSizeFor`（千问与其余家规则不同）。

⚠️ **`ModelMatchesProvider` 不能用"含斜杠"判断**：那只对 OpenRouter vs APIMart 成立，
对新增的裸名 provider 会恒为假 → 每次启动都把用户手填的模型名重置为默认值
（v0.5.31 已改成"该 provider 清单内命中 或 OpenRouter 形态放行"）。

**成本估算差异**：`Catalog.EstimateCost` 按 quality × resolution 倍数 × n。
`auto` 档实测恒落 `low`（output_tokens 恒 196），故按 low 估。
新增的两家**不**用这张表：OpenAI 用官方价格表仅作对照、千问记为 0 ——
两者展示预估都走 `ModelStatsService` 历史均价（见下）。

### 三处成本数字的口径（易混淆）

| 显示位置 | 属性 | 口径 |
|---|---|---|
| 顶栏 provider 累计 | `ProviderCost` | `Session.CostForProvider(当前 provider)`，按 `Item.Provider` 分组求和 |
| 历史面板「总累计」 | `TotalCost` | `Session.TotalCost` = **`Items` 求和**（单一真源，不单独累加） |
| 历史面板下方小字 | `UnknownCost` | 早期数据无 `provider` 字段的归属金额，避免切换 provider 时数字漂移 |

> 历史沿革：早期版本 `TotalCost` 累加 API 实际值、而每项 `Cost` 是**均摊值**
> （`res.Cost / N`），两者会漂移；现已统一为「从 `Items` 求和」。
> 详见 `docs/fix-plan-v5.22.md` 第 2 项。`TotalCost` 现为**计算属性**（从 `Items` 求和），
> 偏差在结构上已不可能出现；原先的 `Session.ReconcileCost()` 空方法已于 P0-7 删除。

### 价格预估（与估算表不同）

`ModelStatsService` 维护 `model_stats.json`（键 = `provider|model`）：

- **该模型用过** → 显示其**历史平均花费**（`avg_cost`）
- **没用过** → **不显示预估**（避免按错表误导）

这取代了「照 `Catalog` 死表估」的做法，两者不要混用。

---

## 四、UI 结构

### 三栏布局（桌面横屏）

```
┌──────────────┬────────────────────┬──────────────┐
│  左栏         │  中栏               │  右栏         │
│  · 生成参数   │  · 预览（大图）      │  · 历史/累计  │
│  · 提示词     │  · 多图缩略图条      │  · 消息流     │
│  · 主操作     │  · 编辑图片工具栏    │              │
│  · 参考图     │  · 系统看图器/保存   │              │
│  · 提示词历史  │                    │              │
└──────────────┴────────────────────┴──────────────┘
│ 顶栏：标题 + 就绪徽标 + 模型 + 进度 + 本 provider 累计 │
│       + provider 下拉 + 深色(窄屏隐藏) + 设置(窄屏仅图标) │
└──────────────────────────────────────────────────────┘
```

> **底栏当前隐藏**（`IsVisible="False"`，结构保留）。原先挂在底栏的
> 撤回/预览/保存/润色/快捷键/数据目录等命令，已分别有顶栏或左栏入口。

### 响应式（竖屏）

窄屏（< 900px）时切换为**纵向堆叠**：预览+工具 → 提示词+操作 → 参数 → 历史+消息。
两套布局是 `MainView.axaml` 里两个独立子树，由 `IsVisible="{Binding IsWideLayout}"`
与 `IsVisible="{Binding !IsWideLayout}"` 切换，`MainView.axaml.cs` 的
`OnSizeChanged` 按宽度设 `Vm.IsWideLayout`。

> ⚠️ 两套布局**各有一个 `RegionCanvas` 实例**，切换时靠
> `ExportShapes` / `ImportShapes` 搬运标注（否则用户画好的标注会消失）。

### 浮层

| 浮层 | 触发 | 内容 |
|---|---|---|
| 设置 | 顶栏「设置」（深色按钮旁） | Provider / API Key（含校验）/ 润色配置 / 界面选项 / 数据目录 |
| 调色板 | 编辑工具栏「调色板」 | 16 色网格，点选即用 |
| 润色候选 | 「润色」按钮 | 4 选 1，←→ 翻页 + 重新生成 + 采用 |

> 设置里的「离线模式（调试）」仅在环境变量 `IMGHUB_DEBUG=1` 时显示。

---

## 五、区域标注（RegionCanvas）

```
预览图（Image 控件）
   ↑ 叠加
RegionCanvas（自定义 Control）
   ├─ 五种工具：马克笔 / 画笔 / 方框 / 圆圈 / 橡皮
   ├─ 无「语义」概念：统一用当前画笔色表示「这里要改」
   │  （早期版本有红=改 / 绿=保留 两种语义，已按用户要求移除）
   ├─ 坐标**归一化**（0~1）→ 与控件尺寸无关，导出到原分辨率对齐
   ├─ 视图变换：滚轮缩放 / 右键或空格+左键平移 / `0` 复位
   └─ 导出
        ├─ ExportComposite()  自由笔迹 → 先画到不透明 mask
        │                     → mask 像素统一 alpha=LayerAlpha(默认110) → 贴回
        │                     （**蒙版式非叠加**：重复涂色不加深）
        │                     方框/圆圈 → 直接描边
        └─ ExportMask()       导出「修改区域蒙版」：黑底 + 不透明笔迹
                              （笔迹用当前画笔色，非白色）
                              存为 mask_*.png，供支持 inpainting 的模型
```

标注图作为**参考图第 1 位**（铁律 D1：第 1 位=主体）传给模型，
并自动追加提示词说明「高亮区域即需修改部分」。

---

## 六、扩展点（加功能去哪）

| 想加什么 | 改哪里 |
|---|---|
| 新 provider | 见下方「加 provider 的完整清单」 |
| 新参数（如 seed） | `Catalog` 常量 + `AppConfig` + `MainViewModel` 属性 + `MainView.axaml` 控件（宽屏/窄屏两处） |
| 新工具（如箭头） | `RegionCanvas.RegionTool` 枚举 + `Shape` 子类 + 工具栏下拉 |
| 新平台（如 iOS） | 新建 head 工程引用 `ImgHub.App` + 实现 `IPlatformStorage` |
| 新测试 | `tests/ImgHub.Integration.Tests/WorkbenchFlowTests.cs`（离线，不花钱） |

### 加 provider 的完整清单（v0.5.31 实测，漏一处即"选得到但跑不通"）

1. `ApiProvider` 枚举加值；
2. `ApiProviderExtensions` 的 `Key`（**一旦发布不可改**）/ `Label` / `Parse`（含中文别名）；
3. `Catalog`：`ModelChoices*` + `DefaultModel*` + `BaseUrlDefault` 分支 + `Providers` 列表，
   以及 5 个能力函数（`QualityChoices`/`AspectChoicesFor`/`ResolutionChoicesFor`/
   `MaxNFor`/`OutputFormatChoices`）与 `MaxRefsFor`/`ValidatePixelSizeFor`/`KeyHint`；
4. `Session`：`KeyFile` / `LegacyKeyFile`（独立 key 文件）、`BaseUrlFor`；
5. `HttpJsonClient`：是否够用（multipart / 自定义 header / 流式？）；
6. `ImageApi.GenerateAsync` 的 `switch` 加分支 + `ListModelsAsync` / `CheckKeyAsync` 分支；
7. `MainViewModel`：`IsXxxProvider` 属性 + `OnProviderChanged` 的通知列表
   （**含 baseUrl 传递**）+ 能力收窄；
8. XAML：`Views/Sections/AdvancedParamsPanel.axaml` 加专属参数（一处生效）；
9. 测试：`tests/ImgHub.Core.Tests/NewProviderPayloadTests.cs` 补断言；
10. 文档：本文件 §三 差异表 + 一份 `provider-*.md` 专文。

---

## 七、与 Python 版的关系

```
legacy/（Python + curses TUI）          src/（C# + Avalonia GUI）
───────────────────────────────         ─────────────────────────────
api.py            ──── 契约一致 ────→   Services/ImageApi.cs
store.py          ──── 契约一致 ────→   Storage/Session.cs
settings.py       ──── 契约一致 ────→   Catalog.cs
polish.py         ──── 契约一致 ────→   Services/PolishService.cs
curses_ui.py      ──── 重写为 ─────→   Views/MainView.axaml + MainViewModel
                                        （去掉终端绘制，保留 _act_* 业务逻辑）

legacy/docs/      ──── 平移 ─────→     docs/（CONSTRAINTS 等，逐条平移 + 新增）
```

**Python 版保留为**：参考实现 + 行为契约基准（959 项测试）。
