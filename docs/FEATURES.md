# 功能清单（UI 重写依据）

> **这份文档回答什么**：ImgHub 目前**已实现**的全部功能，按「用户能做什么」组织，
> 每条给出**入口 / 依赖的业务 API / 状态归属**——目的是**重写 UI 时不必读代码就能列全需求**。
>
> 生成时间：2026-09-24｜**可用性复核时间：2026-09-24（本轮实测）**
> 代码位置见 `src/*/README.md`；架构见 [ARCHITECTURE.md](ARCHITECTURE.md)。

## 可用性验证结论（本轮实测）

| 验证项 | 命令 | 结果 |
|---|---|---|
| Core 单元测试 | `dotnet test tests\ImgHub.Core.Tests` | ✅ **233 通过 / 0 失败**（10 s） |
| 集成测试 | `dotnet test tests\ImgHub.Integration.Tests` | ✅ **112 通过 / 0 失败**（1 m 32 s） |
| 测试合计 | — | ✅ **345 项全绿**（基线 318 → 只多不少；新增 27 条防护测试） |
| 桌面 AOT publish | `dotnet publish src\ImgHub.Desktop -c Release -r win-x64 -p:PublishAot=true` | ✅ 0 error，产物 33.3 MB |
| AOT 原生依赖自检 | 3 个原生 DLL 是否与 exe 同目录 | ✅ `av_libglesv2.dll` + `libHarfBuzzSharp.dll` + `libSkiaSharp.dll` 均在 |
| AOT 启动验收 | 启动 → 等 12 s → 判存活 → 杀进程 | ✅ 存活（未秒崩），启动日志无异常 |
| 分层红线 A1 | Core 是否引用 UI / 平台 | ✅ **零命中**（全 Core 无任何 `using Avalonia.*` / `Android.*`） |
| 分层依赖方向 | 4 个 csproj 的 ProjectReference | ✅ `Android/Desktop → App → Core`，Core 无引用，**无环** |
| Android 构建 | `dotnet build` 全解决方案 | ❌ **本机 Android SDK 缺失（XA5207 / API 36）** —— 环境问题，**非代码回归**（见 AGENTS.md） |

### 第 1 批 P0 正确性修复（已落地，9 项）

| 项 | 症状 | 修复 |
|---|---|---|
| **P0-8** | Android 上「保存相册」**静默失效**（`MainView as TopLevel` 恒为 null） | 改用 `TopLevel.GetTopLevel(visual)` 反查；抽 `ITopLevelSource` 使失败路径可测 |
| **P0-5** | 失败被 **伪装成「用户取消」**（4 处 `catch { return null; }`） | 引入 `PlatformOpResult` 四态（Ok/Cancelled/Unsupported/Failed），文案+级别由纯函数 `DescribePlatformOp` 决定 |
| **P0-1** | 序列化异常**绕过日志**（`AtomicWrite` 参数先求值） | `AtomicWrite` 改收 `Func<string>`，序列化在 `try` 内 |
| **P0-3** | 写锁只覆盖 `AtomicWrite`，**另 5 个写盘点裸奔** | 抽 `WriteLocked` 统一入口；`Log`/`TrimLog`/提示词历史/key 全部并入；读+写同锁 |
| **P0-4** | `Items` 是裸 `List<T>` 却被跨线程访问 | 新 `ItemStore`：内部加锁 + **枚举走快照** + 原子 `TrimTail`/`TryTakeFirst` |
| **P0-2** | `Bitmap` **从不 Dispose**（全 App 层 0 处） | `HistoryRow`/`PreviewThumb` 实现 `IDisposable`；VM 批量释放 + `Dispose()`；App 退出时调用 |
| **P0-6** | 流式部分图**付了开销却看不到** | 宽/窄两套布局各加一层 `PartialImage` 绑定 |
| **P0-7** | `ReconcileCost()` 空方法，docs 却称被调用 | 删方法 + 同步 `port-status.md` / `ARCHITECTURE.md` |
| **P0-1b** | state.json 写 5 个**只写不读**的 config 镜像键 | 删除；`SaveState` 只留 `counter`/`total_cost`/`items`（`AutoPreview` 字段**保留**，理由见 fixplan） |


> ⚠️ **"测试全绿"不等于"功能可用"**：测试覆盖不到 XAML 绑定与 UI 入口可达性。
> 本轮已用「命令 ↔ 绑定」静态核对补上这一层，结果见 §八「已实现但当前不可达」——
> **那里列出的每一条都是真实缺口，重写 UI 时必须一并修掉，否则等于把缺陷带过去。**

---

## 零、UI 重写必读：状态归属三分法

重写 UI 时最需要知道的是「**这条状态归谁管**」。当前实现分三层：

| 层 | 位置 | 重写 UI 时 |
|---|---|---|
| **业务常量与能力矩阵** | `ImgHub.Core/Catalog.cs` + `PromptGuide.cs` + `JimengExtract.cs` | **原样复用**（纯数据，零 UI 依赖） |
| **持久化与领域模型** | `ImgHub.Core/Storage/Session.cs`、`Models/*`、`Services/*` | **原样复用**（除 `ApiKey` 需 UI 传入） |
| **视图状态与编排** | `ImgHub.App/ViewModels/MainViewModel.cs`（**2756 行**） | **需要拆分**，见 [ui-rewrite-fixplan.md](ui-rewrite-fixplan.md) |

⚠️ **当前的两个 UI 类型是重写的最大障碍**：
`ViewModels/HistoryRow.cs` 与 `PreviewThumb.cs` 把 `Avalonia.Media.Imaging.Bitmap` 直接嵌进列表项。
→ 新 UI 必须先处理这两个类（详见 fixplan 的 P0-2）。

---

## 一、五个生图 provider（能力矩阵）

顶栏下拉切换，**每个 provider 各自独立 API key**（互不覆盖）。

| Provider | 稳定键 | 调用模式 | 鉴权 | 特有功能 |
|---|---|---|---|---|
| **OpenRouter** | `openrouter` | 同步 | Bearer | provider 路由（only/order/ignore/sort/allow_fallbacks）、SSE 流式 |
| **APIMart** | `apimart` | 异步（轮询 task） | Bearer | `mask_url` 真蒙版、`moderation` |
| **OpenAI 官方** | `openai` | 同步 | Bearer | `/images/edits`(multipart)、SSE 部分图、`input_fidelity` |
| **千问 DashScope** | `dashscope` | 3.0 同步 / 2.0·max·plus 异步 | Bearer | `negative_prompt`、`prompt_extend`、`watermark` |
| **即梦（火山引擎）** | `jimeng` | 异步（提交→轮询） | **AK/SK 签名** | **素材提取（10 种预设）**、明水印、`req_json` |

**关键 UI 影响**：能力差异**全部由 `Catalog` 的能力函数给出**，不写死在 UI：

```csharp
Catalog.ModelChoices(p)          // 模型/req_key 清单
Catalog.AspectChoicesFor(p, m)   // 画幅可选值（比例名 或 像素串）
Catalog.QualityChoices(p, m)     // 质量档（即梦/千问无此参数 → 单元素）
Catalog.ResolutionChoicesFor(p, m)// 分辨率档（OpenAI/千问/即梦无 → 单元素）
Catalog.OutputFormatChoices(p, m)// 输出格式（dall-e-3 仅 png）
Catalog.MaxNFor(p, m)            // 批量上限（gemini 1 / dall-e-3 1 / 即梦 1）
Catalog.MaxRefsFor(p)            // 参考图上限（千问 3 / 即梦 10 / 其余 16）
Catalog.NeedsTransparentBackground(p, m)  // GPT Image 2/2.5 不支持 transparent
ApiProviderExtensions.NeedsAccessKeyPair(p) // 即梦需要两把密钥
```

---

## 二、生成与编辑

| # | 功能 | 入口 | 业务调用 |
|---|---|---|---|
| 1 | **文生图** | 「生成」按钮 | `MainViewModel.GenerateCommand` → `IImageApi.GenerateAsync` |
| 2 | **编辑（图生图）** | 「编辑」按钮（需预览中有图） | `EditCommand`，`editMode: true` |
| 3 | **用标注编辑** | 画布圈画后出现 | 同编辑，附带 `Mask`（带 Alpha 的 PNG） |
| 4 | **批量生成** | 「批量」数字框 | `GenRequest.N`（上限见 `MaxNFor`） |
| 5 | **离线模式** | 设置 → 界面选项 → 离线 | `Offline=true` → 本地占位图，不发请求、不花钱 |
| 6 | **提示词润色** | 「润色」按钮 | `IPolishService.PicksAsync`（4 候选），浮窗 4 选 1 |
| 7 | **提示词工程指南** | 「生成提示」按钮 | `PromptGuide.All`（总览 / OpenAI / 千问 三页，Markdown 渲染） |
| 8 | **素材提取**（即梦专属） | 「提取」按钮 | `JimengExtract` 10 种预设 → 提交对应 `req_key` |

### 参考图

| # | 功能 | 说明 |
|---|---|---|
| 9 | 添加参考图 | 「+ 添加」；**第 1 位 = 主体**（硬约束 B1） |
| 10 | 保留参考图 | 「保留」勾选 → 用完不清空（默认一次性） |
| 11 | 移除单张 | 缩略图上的移除按钮 |
| 12 | 上限随 provider | 千问 3 / 即梦 10 / 其余 16 |

### 编辑目标

| # | 功能 | 说明 |
|---|---|---|
| 13 | **预览中的图 = 编辑目标** | 点历史任一项即设为编辑目标（**不是**"最新一张"——这是修过的真实缺陷） |
| 14 | 标注跟随图片 | 切图时保存旧图标注、恢复新图标注（`SaveRegionsFor`/`GetRegionsFor`） |
| 15 | 标注导出为蒙版 | `RegionCanvas.ExportMask()` → `SetAnnotatedMask()` |

---

## 三、参数（随 provider+模型变化）

| 参数 | 取值范围来源 | 备注 |
|---|---|---|
| 模型 / req_key | `ModelChoices(p)` | 切 provider 时自动换成合法值 |
| 质量 | `QualityChoices(p, m)` | 即梦/千问无此参数（下拉退化为单值） |
| 画幅 | `AspectChoicesFor(p, m)` | 比例名（OpenRouter/APIMart）或像素串（OpenAI/千问/即梦） |
| 分辨率 | `ResolutionChoicesFor(p, m)` | OpenAI/千问/即梦无档位概念 |
| 输出格式 | `OutputFormatChoices(p, m)` | dall-e-3 仅 png |
| 批量 n | `MaxNFor(p, m)` | 即梦无 n（用 `force_single`） |
| 背景 | `BackgroundChoices` | GPT Image 2/2.5 禁 `transparent` |
| 输出压缩 | 0~100 | 仅 jpeg/webp 生效 |
| 审核强度 | auto/low | APIMart / OpenAI |
| 种子 | 文本 | OpenRouter / 千问 |
| 流式 | 勾选 | OpenRouter / OpenAI（GPT Image 系） |
| 部分图张数 | 0~3 | OpenAI `partial_images` |
| 输入保真 | high/low | 仅 OpenAI `gpt-image-1` 系 |
| 风格（style） | vivid/natural | 仅 `dall-e-3` |
| 反向提示词 | 文本 | 仅千问 |
| 提示词改写 | 勾选 + 模式 | 仅千问（`prompt_extend` / `_mode`） |
| 水印 | 勾选 | 千问 / 即梦 |
| provider 路由 | only/order/ignore/sort | 仅 OpenRouter |
| **即梦：文本权重** | 0~1 | `scale` |
| **即梦：强制单张** | 勾选 | `force_single` |
| **即梦：LoRA 权重** | 0~2 | 仅元素提取 |
| **即梦：返回链接** | 勾选 | `req_json.return_url` |
| **即梦：明水印** | 勾选 + 文字/位置/语言/不透明度 | `req_json.logo_info` |

### 配置记忆（v0.5.31）

- **按 `provider|model` 分别记参数快照**（`AppConfig.ParamPresets`）→ 切回来自动复原；
- 切换**前**先保存旧端点快照，切换**后**复原新端点快照，**最后**才收窄到合法值（顺序不可颠倒）；
- 坏快照自愈清理（错配键 / 全空快照 / 画幅不在当前端点集合）并**立即回写磁盘**；
- 下拉一律绑 `SelectedIndex`（不绑 `SelectedItem`）→ 避免回写空值（约束 D4h）。

---

## 四、历史与结果

| # | 功能 | 说明 |
|---|---|---|
| 16 | 历史列表 | `Session.Items`（最多 200，`Catalog.HistoryMax`） |
| 17 | 缩略图 | `HistoryRow.Thumb`（惰性解码，宽 80）⚠️ 见 fixplan P0-3 |
| 18 | 点选预览 | 设为当前预览 + 编辑目标 |
| 19 | 多选 | 「多选」按钮 → 批量删除 |
| 20 | 从列表移除 | 「撤回」/ 行内删除（**不删磁盘文件**） |
| 21 | 删除磁盘文件 | 行内删除（带确认浮窗） |
| 22 | 导入图片 | 「导入」按钮 → 文件选择器 |
| 23 | 保存到相册 | 「保存到相册」（走 `IPlatformStorage`） |
| 24 | 系统看图器 | 「系统看图器」（`OpenInExternalViewerAsync`） |
| 25 | 在文件管理器中显示 | `RevealInFileManagerAsync` ⚠️ 见 fixplan |
| 26 | **成本显示（三处口径）** | 顶栏 provider 累计 / 历史「总累计」/ 未归类小字 |
| 27 | 价格预估 | 「该模型暂无历史花费记录…」→ 有记录后按历史均价 |
| 28 | 提示词历史 | 最近 200 条，可点选复用、可删除 |

### 三处成本口径（易混淆，重写 UI 时必须保持）

| 显示位置 | 属性 | 口径 |
|---|---|---|
| 顶栏 provider 累计 | `ProviderCost` | 按 `Item.Provider` 分组求和 |
| 历史面板「总累计」 | `TotalCost` | **`Items` 求和**（单一真源） |
| 历史面板下方小字 | `UnknownCost` | 早期无 `provider` 字段的归属金额 |

---

## 五、区域标注（RegionCanvas）

| # | 工具 | 说明 |
|---|---|---|
| 29 | 方框 / 圆圈 / 马克笔 / 画笔 / 橡皮 | `RegionTool` 枚举（顺序与 `ToolNames` 严格对应） |
| 30 | 颜色调色板 | 16 色（`PaletteColors`） |
| 31 | 粗细 | 2~80px，默认 3 |
| 32 | 缩放 / 平移 / 复位 | 与底图变换同步（`ViewChanged`） |
| 33 | 撤销/清空标注 | — |
| 34 | 不遮挡原图 | 标注层透明叠加 |

---

## 六、设置

| # | 功能 | 说明 |
|---|---|---|
| 35 | Provider 选择 | 5 家 |
| 36 | API Key | 每家独立文件；即梦**两把**（AK + SK） |
| 37 | 校验 key | `CheckKeyAsync`（即梦无免费探活接口 → 只做本地检查）⚠️ 见 fixplan |
| 38 | 端点覆盖 | `base_urls`（千问专属域名 / OpenAI 网关） |
| 39 | 润色配置 | 端点 / 模型 / key（独立于生图） |
| 40 | 润色风格 | auto（跟随生图 provider）/ OpenAI / 千问 |
| 41 | 界面选项 | 悬停动画 / 悬停说明 / 深色主题 / **语言（中·英·日）** / 离线（debug） |
| 42 | **多语言** | 中 / 英 / 日，设置浮层底部切换，**即时生效**（不重启）；持久化在 `config.json` 的 `language`。⚠️ 图标开关已于 v0.5.37 删除（图标改为内嵌字体，永远可用）—— 见 [i18n.md](i18n.md) |

---

## 七、健壮性机制（重写 UI 时**不要漏掉**）

| # | 机制 | 为何重要 |
|---|---|---|
| 42 | **未完成任务恢复** | 提交时落盘 `task_id`；崩溃/重启后只 GET 查询取回，**不重复扣费** |
| 43 | 消息面板 | 分级日志（Info/Ok/Warn/Err/Dim）+ 按需滚动（贴底才跟随） |
| 44 | 启动自检 | key / 模型合法性 / 数据目录可写 / 润色 → 就绪徽标 |
| 45 | 错误提示增强 | `ErrorHints.Explain` 把异常翻成可执行建议 |
| 46 | 前置校验（花钱前） | 蒙版无 Alpha / 尺寸不匹配 / 尺寸规则 / 参考图超限 → 不发请求 |
| 47 | 重试策略 | 仅 429/5xx 指数退避；业务 4xx 立刻抛 |
| 48 | 并发防重复扣费 | 会调 API 的入口先查 `Busy` |
| 49 | 主题（深/浅） | `App.axaml` ThemeDictionaries |
| 50 | 两套布局 | 宽屏三栏 / 窄屏堆叠（阈值 900px） |
| 51 | 确认浮窗 | 危险操作二次确认 |
| 52 | 平台适配 | 桌面 / Android 各一个 head |

---

## 八、已实现但当前不可达（**本轮静态核对新增，重写 UI 必须一并修**）

> 核对方法：抽出 `MainViewModel` 全部 **43 个 `[RelayCommand]`**，按 MVVM Toolkit 命名规则
> 推导生成的命令名（去 `Async` 后缀 + `Command`），再在整个 `src/` 的 XAML / code-behind 里查绑定。
> **本节每条都已亲自复核过定义与调用点。**

| # | 功能 | 实现位置 | 现状 | 严重度 |
|---|---|---|---|---|
| 1 | **手动重试未完成任务** | `MainViewModel.cs:909 RetryPendingAsync` | 命令**零绑定**；注释写「设置里可点」，设置浮层里没有这个按钮。仅启动时自动跑一次 `ResumePendingAsync`（`:97`） | 🟡 中（自动恢复仍在，只是不能手动重试） |
| 2 | **拉取账号可用模型** | `MainViewModel.cs:2252 RefreshModelsAsync` | **零绑定**。整个 `ListModelsAsync` 通路（含 5 个 provider 的模型拉取）用户无法触发 | 🟠 高（白写的功能） |
| 3 | **流式部分图预览** | `MainViewModel.cs:291/340 PartialImage`/`HasPartialImage` | 回调链路完整（`ImageApi` 三个 provider 真的回调），但**全仓库无 XAML 绑定**。开了 `partial_images` 付了流式开销却看不到图 | 🟠 高（付费功能不可见） |
| 4 | **删除单条历史** | `MainViewModel.cs:1885 DeleteHistoryItem` | 命令零绑定。**但有替代路径**：右键 → `MainView.axaml.cs:330 RequestDeleteHistory`（带确认，语义相同）→ 功能可达 | 🟢 低（死命令，可删） |
| 5 | **删除单条提示词历史** | `MainViewModel.cs:1923 DeletePromptHistory` | 命令零绑定。**替代路径**：多选后 `MainView.axaml.cs:421 BatchDeletePromptHistory` → 可达 | 🟢 低（死命令，可删） |
| 6 | **保存 API key（单独命令）** | `MainViewModel.cs:2349 SaveApiKey` | 命令零绑定。`SaveSettingsCore`（`:2401-2404`）内部已直接调 `_sess.SaveKey` → 可达 | 🟢 低（死命令，可删） |
| 7 | **离线开关（命令）** | `MainViewModel.cs:2198 ToggleOffline` | 命令零绑定。UI 走 `CheckBox IsChecked="{CompiledBinding Offline}"`（`MainView.axaml:751`）直接双向绑定属性 → 可达 | 🟢 低（死命令，可删） |
| 8 | **快捷键提示 / 数据目录** | `MainView.axaml:640-641` | 两个按钮的 **唯一入口在隐藏底栏**（`MainView.axaml:628 IsVisible="False"`）→ 永久不可达 | 🟡 中（信息可另达：设置浮层底部有 `HomePath`） |
| 9 | **「在文件管理器中显示」** | `PlatformStorage.RevealInFileManagerAsync` | 实现存在（`explorer.exe /select`），但 `IPlatformStorage` 无 UI 入口调用 | 🟡 中 |
| 10 | **Android 保存相册 / 打开外看图** | `PlatformStorage.cs:12-16` | `TopLevel` 取值写成 `ISingleViewApplicationLifetime.MainView as TopLevel`，Android 下**很可能恒为 null** → `SaveToGalleryAsync` 直接 `return null` 被误报「用户取消」；`OpenInExternalViewerAsync`/`RevealInFileManagerAsync` 只有 `IsWindows()` 分支 → Android 静默无效 | 🔴 **高（Android 端真实失效，待真机确认）** |

### 另有 3 个「state.json 只写不读」字段（本轮新发现）

`Session.SaveState()` 写了这些键，但 `LoadState()`（`:247-280`）**只读回 `counter` / `total_cost` / `items`**：

| 键 | 写入行 | 实际影响 |
|---|---|---|
| `preview`（`Config.AutoPreview`） | `Session.cs:478` | **只写不读**，且 `AutoPreview` 在 App 层零引用 → 整个字段是死的 |
| `offline`（`Config.Offline`） | `Session.cs:477` | state.json 里白写；但 `config.json` 会存 `Offline`，重启后仍能恢复 |
| `aspect`（`Config.Aspect`） | `Session.cs:476` | 同上，`config.json` 是真正生效的通道 |

> 判断：**不是 bug**，但 `state.json` 里这 3 个键是**误导性冗余**（后来者会以为它生效）。建议删键或补读回，二选一。

---

## 九、已知缺口（**不要当 bug 修**）

| 项 | 说明 |
|---|---|
| 「离线」选项 | debug 功能，默认隐藏（`IMGHUB_DEBUG=1`） |
| Android 中文渲染 | 已内嵌静态字体兜底，**待真机验证** |
| Android 保存相册 | 走 SAF picker（用户选位置），非静默写 MediaStore |
| 环境诊断面板 | 原版 `doctor.py` 未移植 |
| `make-icon.ps1` | 顶部是**绝对路径**，换机器需先改 |
| Android 工具链 | 本机未装（`%USERPROFILE%\android-sdk-imghub` 不存在）→ `dotnet build` 会因 XA5207 失败，**这是环境问题不是代码回归** |

---

## 十、重写 UI 的最小对接面（建议）

新 UI 只需要这 4 组接口就能覆盖上表全部功能：

```
① 能力矩阵（纯函数，无状态）
   Catalog.*            → 下拉可选项 / 上限 / 校验
   PromptGuide.All      → 指南内容
   JimengExtract.All    → 提取预设

② 领域服务（Core）
   IImageApi            → GenerateAsync(req) / ListModelsAsync / CheckKeyAsync
   IPolishService       → PicksAsync / RunAsync
   Session              → 配置 / 历史 / key / 参数快照
   PendingTaskStore     → 未完成任务

③ 请求对象（唯一真源，加参数只改这里）
   GenRequest           → 全部生成参数

④ 平台抽象
   IPlatformStorage     → 保存相册 / 打开外部程序
   IPlatformInfo        → 平台名 / 版本
```

> ⚠️ 当前 `MainViewModel` 把 ①③④ 混在一起且含 UI 类型。
> 拆分方案见 **[ui-rewrite-fixplan.md](ui-rewrite-fixplan.md)**。
