# 更新日志（Changelog）

本文件记录本项目所有值得注意的变更。

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

> 📌 **关于版本号**：本项目在首次公开发布（v0.5.43）之前**未打 git tag**，早期版本日期未逐一记录。
> 另注意 **`0.5.26` 之前用的是 `5.22.0`~`5.25.0` 编号**（第十二轮起改为 `0.5.x` —— demo 阶段不该用高版本号），
> 因此本文件早期条目保留当时**原始的版本标识**。
> 每条改动**根因与实证过程**的完整记录见 [`docs/`](docs/) 下的 `fix-plan-*.md`
> 与 [`docs/port-status.md`](docs/port-status.md)。

## [Unreleased]

## [0.5.43] - 2026-09-30

### Added

- `AppLog` 新增 **Debug 级别**（默认关闭，`IMGHUB_LOG_DEBUG=1` 开启）——
  高频循环里的诊断细节不再污染 Info 日志。

### Fixed

- **静默 `catch { }` 全量审计**：29 处 → 3 处（剩余 3 处为 `AppLog` 自身清理等合法例外）。
  静默 catch 是"功能悄悄失效、事后查不到"的元凶（`docs/CONSTRAINTS.md` I1）。
- 10 处"同一件事写两行日志"合并为单行（`docs/CONSTRAINTS.md` I5）。

## [0.5.42]

### Fixed

- **exe 没有图标**（用户报「二进制文件没有图标」）—— 两个**独立**根因，都不报错：
  - **A. PNG-in-ICO**：`.ico` 内嵌 PNG blob → Explorer / `ExtractIcon` 不渲染。改为写 **BMP/DIB 帧**。
  - **B. ICO 目录表 offset 基准错**：每项 `dwImageOffset` 从 `0` 起（应为 `6+16×N`）→ 整个 ico 不可解析。
- `make-icon.ps1` 顶部三个**硬编码绝对路径** → `Join-Path $PSScriptRoot ...`（换机器不用改）。
- `make-icon.ps1` 自检改为 `try/catch` + **失败 `exit 1`**
  （旧自检的异常被 PowerShell 非终止错误吞掉 → 脚本照常打印 `ICON LOAD OK`，其实没加载成功）。

### Added

- `ExeIconContractTests`（4 条）—— 独立于生成脚本的图标检查；已用**注入法**验证"注入旧 bug → 正确失败"。

## [0.5.41]

### Fixed

- **切换语言后"部分界面不变"** —— 两个独立根因：
  - **坑 5**：VM 里**拼装文案的属性**硬编码中文（`ToolNames` / `RegionCountText` / `SetupHint` /
    `HistoryMultiButtonText` 等 13 个）→ 改走 `Localizer.Instance[...]`（补 21 条新键）。
  - **坑 6**：改走 Localizer 后**没发通知** → 新增 `NotifyLocalizedStrings()` 统一通知这 13 个属性。

### Added

- 2 条**源码扫描**测试（能主动发现遗漏，而非等人报）：
  `ViewModelBoundStrings_AreLocalized_NotHardcodedChinese`、
  `EveryLocalizedVmProperty_IsNotifiedWhenLanguageChanges`。

## [0.5.40]

### Added

- **多语言：中文 / English / 日本語**（233 键 × 3 语 = 699 条文案；XAML 238 处接入）。
  设置浮层底部切换，**即时生效**（无需重启），偏好持久化在 `config.json` 的 `language` 字段。
- `src/ImgHub.App/Ui/Localizer.cs`（三个普通 C# 字典，**不用 `.resx`** —— AOT 下反射资源查找被禁用）。
- 文档 [`docs/i18n.md`](docs/i18n.md)：方案选型、踩坑全过程、新增文案要改哪里。

### Fixed

- 沉淀 3 条"**不报错**"的坑为硬约束 H2（`{DynamicResource}` 不更新 / `string.Empty` 通知无效 /
  `CompiledBinding` 忽略 `Source=`）。

## [0.5.39]

### Fixed

- 拖动绘制时**不透明**、松手才变半透明（v0.5.38 加缓存时把拖动中那条挪出了 `PushOpacity` 的 `using` 块）。
- 其余 4 项用户反馈：见 [`docs/code-review-v0.5.37.md`](docs/code-review-v0.5.37.md)。

## [0.5.38]

### Added

- 底栏「**定位文件**」按钮 + `RevealInFileManagerCommand`（非 Windows 会如实提示"不支持"）。

### Changed

- `RegionCanvas.Render` 改为**缓存离屏扁平层**（键 = 形状版本号 / 画布宽 / 画布高）。
  修复前每帧新建与图片同尺寸的位图（4K 约 33 MB/帧）。

## [0.5.37]

### Added

- **全部按钮图标化**（46 个按钮）+ 均匀排布；内嵌 **Material Symbols Outlined** 子集
  （34 个码位，6.1 KB，Apache-2.0），替换不可分发的微软专有字体 `Segoe MDL2 Assets`。
- 文档 [`docs/code-review-v0.5.37.md`](docs/code-review-v0.5.37.md)：多维度代码审查结果。

### Fixed

- 按钮的 `Content` 属性覆盖子元素 → **图标不显示**（46 处）。
- 脚本正则误删**绑定式** `Content` → 提示词多选 / 确认对话框按钮**变空**（4 处恢复）。
- `TryOwnerOnly` 注释与实现不符（安全误导）—— 注释改为如实描述 Windows 边界。

## [0.5.36]

### Changed

- 用户 8 项需求（含标注工具条改为**明确三行**，`UiMetrics` 高度 118 → 160）。

## [0.5.35]

### Fixed

- **蒙版可绘制范围超出图片**：落笔/拖动未做范围检查 → 在图片外的灰区也能落笔并记下越界坐标。
  修法：`ToNormalizedClamped`（钳制到 [0,1]）+ `IsInsideImage`；`ImportShapes` 同样钳制。

## [0.5.34]

### Fixed

- 标注**超出图片边界后"消失"**：语义澄清为「可绘制范围 = 图片尺寸，可查看范围 = 整个预览灰区」。
  画布铺满灰区 + `ClipToBounds=false` + 新增 `ImageContentRect` 作坐标基准。
- 图片**居中时缩放"变形/移位"**：矩阵缺少居中原点偏移 → `BuildMatrix` 末尾追加 `T(contentX, contentY)`。

## [0.5.33]

### Fixed

- **⚠️ 回归**：上一版把 `Image` / `RegionCanvas` 改成 `HorizontalAlignment="Center"`，
  而 Center 下未设尺寸的控件 `DesiredSize` 为 0 → 画布变成 0×0 → **工具与滚轮全部失效**
  （实测 `Bounds=(300,150,0,0)`）。改回 `Stretch`。

## [0.5.32]

### Fixed

- **缩放不以鼠标点为锚点**：新增 `ZoomBy(factor, anchor)`，公式 `pan' = pan·r + d·(1-r)`。
- **滚轮缩放"跑位"（拖动却正常）**：`Stretch="Uniform"` 下 `Image.Bounds` 会自动收缩为图片内容矩形，
  而画布铺满容器 → 两者坐标系不同。**根治**：新增 `RegionCanvas.FollowBoundsOf(target)`，
  画布订阅底图 `BoundsProperty` 自动跟随（时机问题从根上消失）。
- **点击处与落笔处偏移**：`BuildMatrix` 的 pan 在缩放之内但 `ToNormalized` 按屏幕单位写 → 三者不互逆。
  统一为 `screen = (local − c/2)·z + c/2 + pan`。

### Added

- `CanvasFollowsImageTests`（7 条，含"图片解码就绪后画布自动跟随"）。

## [0.5.31]

### Added

- **配置记忆**：上次的 provider / 模型 / 参数在重启后恢复。

### Fixed

- 下拉框一律绑 `SelectedIndex` 而非 `SelectedItem`（约束 D4h）。
- 代码里给控件上色**必须走 `DynamicResource` 绑定**，绝不能 `Foreground = null`（约束 D4i）——
  否则深色主题下文字不可见。

## [0.5.30]

### Fixed

- 批量张数 `n` 必须按**模型**钳制而非仅按 provider（两处真源统一）——
  gemini 图像系服务端仅支持 1 张。

## [0.5.29]

### Fixed

- **Plan A 重构引入的 5 个 UI 回归**：
  `Expander` 宽度不可控、`DataContext` 时序（构造函数里读不到）、`Auto` 行陷阱、
  消息自动滚动订阅从未生效等。详见 [`docs/plan-a-section-refactor.md`](docs/plan-a-section-refactor.md) §9。

## [0.5.28]

> 注：本轮文档编号记为 **v5.28.0**（`docs/fix-plan-v5.28.md`）；同一轮也做了整体改名，
> 因此发布标识用 `0.5.28`。

### Changed

- ⚠️ **整体改名 `Imgagent` → `ImgHub`**：项目 / 文件夹 / 程序集 / 命名空间 / 应用显示名，
  Android `ApplicationId` = `com.imghub.app`。
  **兼容层保留**（删掉会导致用户历史图片与 API key 失效）：
  `AppPaths.ResolveHome()` 沿用旧数据目录 `%LOCALAPPDATA%\imgagent`；
  `Session.ReadKeyFileCompat()` 回退读 `.imgagent_*_key`；环境变量 `IMGAGENT_*` 仍被识别。
- **Plan A**：抽出 3 个共用 Section（消息 / 历史 / 高级参数）—— 一处生效，两套布局自动同步。
- **Plan B**：XAML 绑定全量改为 `{CompiledBinding}`（属性名写错在**编译期**报 AVLN2000）。
- **Plan C**：UI 度量常量集中到 `Ui/UiMetrics.cs`（唯一真源）。

### Added

- **蒙版局部重绘**：画笔/马克笔/方框/圆圈 → 导出**带 Alpha 的蒙版**（`alpha=0` = 要改），
  APIMart 走 `mask_url` 真正限定改动范围；无蒙版能力的 provider 自动退回「原图 + 标注合成图」链路。

### Fixed

- **「参考图 / 编辑历史图片出来的图总是不相关」** —— 实为**四个独立缺陷叠加**：
  - 点「生成」时参考图被完全丢弃（`refs` 恒为 `null`）；
  - 编辑目标图未放在参考图**第 1 位**（模型按位置理解参考图）；
  - 蒙版链路**实际是死的**（导出的是无 Alpha 的黑白图，且从未发送 `mask_url`）；
  - 参数缺 6 项（`background` / `output_compression` / `moderation` / 精确像素 `size` /
    `seed` / provider 路由 `only`·`order`·`ignore`·`sort`·`allow_fallbacks`）。
- `build.ps1` 的 `OutDir` 默认值 `..\release` 相对**当前目录**解析 → 从仓库根调用会把产物写到仓库**外面**。

### Removed

- `avalonia/` 残留目录（仅 bin 缓存、未入库；`.gitignore` 已加 `/avalonia/` 防止误重建）。

## [0.5.26]

### Added

- **生图断点恢复**：`task_id` 落盘 → 提交后意外退出也能找回图片。
  详见 [`docs/fix-plan-v5.26.md`](docs/fix-plan-v5.26.md)。

### Changed

- **版本号体系改为 `0.5.x`**（原为 `5.26.0`）—— demo 阶段不该用高版本号。
  `ApplicationDisplayVersion` 是显示串，与 Android `ApplicationVersion`（整数构建号）**解耦、不冲突**。

## [5.25.0]

### Changed

- UI 微调与美化；修复缩放到一定比例后「复位」失效。
  详见 [`docs/fix-plan-v5.25.md`](docs/fix-plan-v5.25.md)。

## [5.24.0]

### Fixed

- **AOT 下「生成失败：Reflection-based serialization has been disabled」** ——
  `HttpJsonClient` 用反射序列化动态 `Dictionary<string,object?>`。
  修法：固定结构走 **source generation**（`AppJsonContext`），动态结构走 **`JsonObject`**（DOM，AOT 安全）。
  > 注：`docs/DELIVERY.md` 曾把 `IL2026`/`IL3050` 判为"无害残留警告"——**该结论已更正**：
  > 框架依赖构建下"看起来正常"，AOT 产物下**直接导致生成不可用**。
- **4xx 被误重试 3 次**（违反约束 C2）：`ApiError.Retryable` 显式标记（默认 false）→ 401 只请求 1 次。

### Added

- `NetworkDiagnostics`：无网络 / DNS / 拒绝 / 重置 / 不可达 / 超时 / TLS / 代理 / 取消 **9 类分类**，
  每类给出**具体动作**；解包 `HttpRequestException.InnerException`（`SocketException`）找真正原因。
- `AppLog` **分级日志**（info/warn/error）：按天分文件、单文件 5 MB 滚动、保留最近 10 个、
  每条带位置、**凭据脱敏**（key 绝不落盘）、写盘失败降级内存。
- 悬停交互样式 `hoverfx`（只过渡 `Background`/`Opacity`，**不动尺寸**，避免布局抖动）。

### Removed

- **所有静默 `catch { }`** —— 改为 `AppLog.*` + 位置（约束 I1）。

## [5.23.0]

### Changed

- UI / 交互 **13 项**修复（按用户截图标注）；方法为**先实证定位根因，再动手**。
  详见 [`docs/fix-plan-v5.23.md`](docs/fix-plan-v5.23.md)。

## [5.22.0]

### Added

- **首次交付**：桌面 Native AOT + Android APK 构建产物。
- `build.ps1` 一键构建（框架依赖桌面版 + Android APK）。
- 文档体系：`docs/HANDOVER.md` / `ARCHITECTURE.md` / `CONSTRAINTS.md` / `DELIVERY.md` / `port-status.md`，
  以及面向 AI agent 的 `AGENTS.md`。

## 早期开发（未打版本号）

### Added

- **架构审查（v5.27.0）**：R1–R6 衰退风险扫描 + **Roslyn 分析器与 MCP 工具链**引入
  （`AnalysisMode=All`，实测仅增加约 4% 构建时间）。详见 [`docs/fix-plan-v5.27.md`](docs/fix-plan-v5.27.md)。
- **Avalonia（C# / .NET 10）重写起点**（2026-09-19）：四层结构
  `ImgHub.Core`（平台无关业务层，无 UI 依赖）← `ImgHub.App`（共享 UI，XAML + MVVM）
  ← `ImgHub.Desktop` / `ImgHub.Android`（薄 head），**一套 UI 两端复用**。
- `legacy/` 保留上一代 **Python + curses** 实现（v5.18.3）作为**参考实现与行为契约**
  （959 项 Python 测试），**不是死代码**。
- 五个生图 provider：OpenRouter / APIMart / OpenAI 官方 / 千问 DashScope / 即梦（火山引擎）。

[Unreleased]: https://github.com/YOUR_GITHUB_USER/ImgHub/compare/v0.5.43...HEAD
[0.5.43]: https://github.com/YOUR_GITHUB_USER/ImgHub/releases/tag/v0.5.43
