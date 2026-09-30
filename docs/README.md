# ImgHub（Avalonia 版）— 文档索引

> 跨平台图形化工作台：**Windows 桌面** + **Android**，一套 UI 两端复用。
> 技术栈：Avalonia 12.1.2 / .NET 10 / C#
> 上一代：`legacy/`（Python + curses TUI，作为参考实现与行为契约来源）

> 📛 **v0.5.28 已整体改名 `Imgagent` → `ImgHub`**（项目/文件夹/程序集/命名空间/应用显示名，
> Android `ApplicationId` = `com.imghub.app`）。
> 兼容：旧数据目录（`%LOCALAPPDATA%\imgagent`）、旧 key 文件（`.imgagent_*_key`）、
> 旧环境变量（`IMGAGENT_*`）仍会被读取，用户数据无需搬迁。

---

## 文档分工（先读哪份）

| 文档 | 回答什么问题 | 适合 |
|---|---|---|
| **[HANDOVER.md](HANDOVER.md)** | 怎么跑起来 / 改功能去哪 / 出问题查什么 | 新接手的人（**先读这份**） |
| **[ARCHITECTURE.md](ARCHITECTURE.md)** | 系统怎么搭的 / 数据怎么流 / 五 provider 差异 | 要改架构的人 |
| **[CONSTRAINTS.md](CONSTRAINTS.md)** | **哪些事绝对不能做** / 为什么 | 写代码前必读 |
| **[DELIVERY.md](DELIVERY.md)** | 怎么打包 / 产物在哪 / 各平台注意事项 | 要发布的人 |
| **[port-status.md](port-status.md)** | 迁移进度 / 历次修复记录 / 已知限制 | 查历史与现状 |
| [../AGENTS.md](../AGENTS.md) | AI / 自动化 agent 的仓库操作约定 | 用 agent 改代码时 |
| [fix-plan-v5.22.md](fix-plan-v5.22.md) | v5.22.0 那一轮的 14 项修复方案与验收 | 查某条改动的来由 |
| [fix-plan-v5.23.md](fix-plan-v5.23.md) | **v5.23.0 那一轮**：UI/交互 13 项 + 实证根因与验收 | 查某条改动的来由 |
| [fix-plan-v5.24.md](fix-plan-v5.24.md) | **v5.24.0 那一轮**：AOT JSON 修复 / 网络加固 / 分级日志 / 悬停交互 | 查某条改动的来由；AOT 问题先看它 |
| [fix-plan-v5.25.md](fix-plan-v5.25.md) | **v5.25.0**：UI 微调/美化 + 缩放修复 | UI 问题查它 |
| [fix-plan-v5.26.md](fix-plan-v5.26.md) | **v5.26.0**：生图断点恢复（task_id 落盘） | 「图找不回来」查它 |
| [fix-plan-v0.5.md](fix-plan-v0.5.md) | **v0.5.x**：UI 对齐微调 + 马克笔不叠加 + 缩放分层修复 + 消息自动滚动 | UI/标注问题查它 |
| [fix-plan-v5.27.md](fix-plan-v5.27.md) | **v5.27.0**：架构审查（R1–R6 衰退风险）+ Roslyn 分析器/MCP 引入 | 架构与代码质量查它 |
| [fix-plan-v5.28.md](fix-plan-v5.28.md) | **v5.28.0**：API 1:1 对齐（生成/编辑/参考图/**蒙版**）+ 逐功能 review + 改名 ImgHub + 清理 | 「参考图/编辑不生效」「蒙版」「改名」查它 |
| [ui-csharp-feasibility.md](ui-csharp-feasibility.md) | **UI 构建方式评估**：本项目用 XAML（非 YAML）；全量改纯 C# 的可行性/风险/工作量 | 想换 UI 构建方式前先看它 |
| [ui-csharp-migration-plan.md](ui-csharp-migration-plan.md) | **若决定迁移的执行计划**：5 批次分步 + 验收清单 + 放弃判据 + 更优替代方案 | 仅在确定迁移时参考 |
| [plan-a-section-refactor.md](plan-a-section-refactor.md) | **已执行**：抽 3 个共用 Section 消除两套布局重复（含 AVLN2000 踩坑） | 改 UI 前必看（决定改哪里） |
| [plan-b-compiled-bindings.md](plan-b-compiled-bindings.md) | **已执行**：`{Binding}`→`{CompiledBinding}`，绑定名写错改为编译期报错 | 写/改绑定时看 |
| [plan-c-ui-metrics.md](plan-c-ui-metrics.md) | **已执行**：UI 度量常量集中（`UiMetrics` + `x:Static`） | 改控件高度时看 |
| [avalonia-migration-feasibility.md](avalonia-migration-feasibility.md) | 当初为什么选 Avalonia（可行性研究） | 想换技术栈 / 复盘决策 |
| [provider-openai-image-api.md](provider-openai-image-api.md) | **OpenAI 官方图像 API 接入契约**：两个端点、各模型参数域、mask、SSE 事件名 | 改「OpenAI 官方」provider 时必看 |
| [provider-qwen-dashscope-api.md](provider-qwen-dashscope-api.md) | **千问 DashScope 原生协议契约**：为何按模型分同步/异步两条链路、字段差异 | 改「千问 DashScope」provider 时必看 |
| [provider-jimeng-api.md](provider-jimeng-api.md) | **即梦（火山引擎）契约**：AK/SK 签名、两条链路、10 种提取预设、req_json | 改「即梦」provider 时必看 |
| [prompt-engineering.md](prompt-engineering.md) | **提示词工程指南**：两个端点为何分开写、润色用哪套怎么决定、浮窗内容摘要 | 改提示词/润色/指南浮窗时必看 |
| **[FEATURES.md](FEATURES.md)** | **目前已实现的全部功能清单**：按「用户能做什么」组织，含入口/业务 API/状态归属 + **本轮可用性实测** + **已实现但当前不可达的 10 项** | **要重写 UI 时先读这份** |
| **[ui-rewrite-fixplan.md](ui-rewrite-fixplan.md)** | **重写 UI 的配套修复 plan**：P0（正确性缺陷 8 项）/ P1（解耦 7 项）/ P2（降熵 2 项），每条含证据+改法+回归防护 | **重写 UI 前的施工图** |
| **[code-review-v0.5.37.md](code-review-v0.5.37.md)** | **多维度代码审查**（正确性/回归、安全、架构债、UI 一致性、测试覆盖）：本轮修复 3 个缺陷（Content 覆盖图标、绑定式 Content 被误删、`TryOwnerOnly` 注释误导）+ 5 条待决债务 + 2 条新增硬约束 | 接手前扫一眼"待决"表 |
| **[i18n.md](i18n.md)** | **多语言（中/英/日）**：方案选型（为何弃 .resx / DynamicResource）+ **3 个不报错的坑**（索引器绑定 / 通知格式 / ContextMenu）+ 新增文案的步骤 | **改界面文案前必读** |

**建议顺序**：HANDOVER → ARCHITECTURE → CONSTRAINTS → DELIVERY

---

## 一句话架构

```
ImgHub.Core    平台无关业务层（无 UI 依赖，可单测）
    ↑
ImgHub.App     共享 UI（XAML + MVVM，两端复用）
    ↑
ImgHub.Desktop / ImgHub.Android    各平台 head（只做平台适配）
```

**关键原则**：`Core` **绝不引用 Avalonia/Android** —— 这是架构红线。
（实战验证：App 层曾被误删，因 Core 无损，仅凭测试契约就完整重建。）

---

## 快速入口

```powershell
# 在仓库根目录执行

# 跑测试（449 项：Core 236 + 集成 213）
dotnet test tests\ImgHub.Core.Tests
dotnet test tests\ImgHub.Integration.Tests

# 跑桌面版
dotnet run --project src\ImgHub.Desktop

# 打包（桌面框架依赖 + Android APK）
powershell -File build.ps1
```

> Native AOT 桌面发布需手动 `dotnet publish`（`build.ps1` 不含该目标），
> 见 [DELIVERY.md](DELIVERY.md)。

---

## 目录速览

| 路径 | 说明 |
|---|---|
| `src/ImgHub.Core/` | 业务逻辑（API/存储/图像/润色），**平台无关** |
| `src/ImgHub.App/` | XAML 视图 + ViewModel + 控件 |
| `src/ImgHub.Desktop/` | Windows/Linux/macOS head + 图标 + 清单 |
| `src/ImgHub.Android/` | Android head + 图标资源 + 清单 |
| `tests/` | Core 单测 + 端到端集成测试（离线跑，不花钱） |
| `legacy/` | 上一代 Python + curses 实现（参考 + 行为契约） |
| `tools/` | 辅助脚本（Android 图标、静态字体子集化） |
| `docs/` | 本目录的文档 |
| `release/` | 构建产物（.gitignore 排除，走 GitHub Releases） |
| `build.ps1` | 一键构建（桌面框架依赖 + Android APK） |
| `make-icon.ps1` | 由 PNG 生成桌面多尺寸 ICO |

每个目录都有自己的 `README.md` 说明职责 —— 见下节「目录级说明」。

---

## 目录级说明文档

| 目录 | 说明文件 |
|---|---|
| `src/ImgHub.Core/` | [README.md](../src/ImgHub.Core/README.md) |
| `src/ImgHub.App/` | [README.md](../src/ImgHub.App/README.md) |
| `src/ImgHub.Desktop/` | [README.md](../src/ImgHub.Desktop/README.md) |
| `src/ImgHub.Android/` | [README.md](../src/ImgHub.Android/README.md) |
| `tests/` | [README.md](../tests/README.md) |
