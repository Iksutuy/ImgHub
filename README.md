# ImgHub

> 跨平台 AI 生图客户端 —— **Windows 桌面 + Android**，一套 UI 两端复用。
> 支持文生图、图生图（多参考图）、**区域标注重绘**、批量出图、提示词润色、历史与成本管理。

![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Android-blue)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![Avalonia](https://img.shields.io/badge/Avalonia-12.1.2-8B44AC)
![License](https://img.shields.io/badge/license-GPL--3.0-blue)
![Version](https://img.shields.io/badge/version-0.5.43-blue)
[![CI](https://github.com/Iksutuy/ImgHub/actions/workflows/ci.yml/badge.svg)](https://github.com/Iksutuy/ImgHub/actions/workflows/ci.yml)

> 📦 下载：**Windows 桌面版** 与 **Android APK** 见 [Releases](https://github.com/Iksutuy/ImgHub/releases)（仓库不入库构建产物）。

---

## 是什么

一个**图形化**的 AI 生图工作台：

| 功能 | 说明 |
|---|---|
| 文生图 | 多模型（OpenAI / Google / 国产 Seedream / Flux / Grok 等） |
| 图生图 | 多参考图（最多 16），待修改图恒为第 1 位（保证主体一致） |
| **蒙版局部重绘** | 画笔/马克笔/方框/圆圈圈出要改的区域 → 导出**带 Alpha 的蒙版**（`alpha=0` = 要改），APIMart 走 `mask_url` 真正限定改动范围 |
| 区域标注重绘 | 同上；无蒙版能力的 provider 自动退回「原图 + 标注合成图」链路 |
| 批量出图 | 一次多张（1–10；**上限随模型自动收窄** —— gemini 图像系服务端仅支持 1 张） |
| 提示词润色 | LLM 生成 4 条候选，浮窗内 4 选 1 |
| 成本管理 | 实时预估 + 按 provider 累计 + 总累计 |
| 离线模式 | 不花钱跑通全流程（确定性占位图） |
| 双 provider | OpenRouter（同步，支持 SSE 流式部分图）/ APIMart（异步轮询） |
| 五 provider | 另支持 **OpenAI 官方** / **千问 DashScope**（同步+异步两条链路）/ **即梦（火山引擎）**（AK/SK 签名），契约见 [docs/](docs/) |
| 文档对齐参数 | `background` / `output_compression` / `moderation` / 精确像素 `size` / `seed` / provider 路由（`only`/`order`/`ignore`/`sort`/`allow_fallbacks`） |

---

## 多语言（中 / 英 / 日）

界面支持**中文 / English / 日本語**，在**设置浮层**底部切换，**即时生效**（无需重启）。
偏好持久化在 `config.json` 的 `language` 字段。

新增或修改文案只需改 `src/ImgHub.App/Ui/Localizer.cs` 的三个字典；完整说明与三个「不报错」的坑见 [docs/i18n.md](docs/i18n.md)。

## 仓库结构

```
imghub/
├── src/                          C# 主实现
│   ├── ImgHub.Core/            平台无关业务层（无 UI 依赖 —— 架构红线）
│   ├── ImgHub.App/             共享 UI（XAML + MVVM），两端复用
│   ├── ImgHub.Desktop/         Windows / Linux / macOS head
│   └── ImgHub.Android/         Android head
├── tests/
│   ├── ImgHub.Core.Tests/      单元测试（236 项）
│   └── ImgHub.Integration.Tests/  端到端测试（213 项）
├── legacy/                       Python + curses 上一代实现（参考 + 行为契约）
├── docs/                         文档（见下）
├── tools/                        辅助脚本（图标生成、字体子集化）
├── release/                      构建产物（不入库，走 GitHub Releases）
├── build.ps1                     一键构建（桌面框架依赖 + Android APK）
├── make-icon.ps1                 PNG → 多尺寸 ICO（桌面）
├── Directory.Build.props         解决方案级 MSBuild 属性
├── AGENTS.md                     AI / 自动化 agent 操作约定
└── ImgHub.slnx                 解决方案文件
```

---

## 快速开始

### 前置

- **.NET 10 SDK**
- （Android）`dotnet workload install android` + Android SDK / JDK

### 跑起来

```powershell
# 跑测试（449 项，全部离线不花钱）
dotnet test tests\ImgHub.Core.Tests           # 236/236
dotnet test tests\ImgHub.Integration.Tests    # 213/213

# 跑桌面版
dotnet run --project src\ImgHub.Desktop
```

### 配置

窗口右下角 **⚙ 设置** → 选 Provider + 填 API Key → 保存。

五个 provider：**OpenRouter** / **APIMart** / **OpenAI 官方** / **千问 DashScope** / **即梦（火山引擎）**。
新增的两家在设置浮层里可另填**端点**（千问的业务空间专属域名、OpenAI 的企业网关）。

接入细节见 [docs/provider-openai-image-api.md](docs/provider-openai-image-api.md)
与 [docs/provider-qwen-dashscope-api.md](docs/provider-qwen-dashscope-api.md)。

或用环境变量：

```powershell
$env:OPENROUTER_API_KEY = "sk-or-v1-..."
# 或
$env:IMGHUB_APIMART_API_KEY = "sk-..."
```

### 先试离线模式（不花钱）

勾选左栏 **离线** → 输入提示词 → 点 **生成**。

> 数据目录默认 `%LOCALAPPDATA%\imghub`，可用 `IMGHUB_HOME` 覆盖。

---

## 打包

```powershell
# 桌面版（框架依赖，发布到 release/desktop）
powershell -File build.ps1 -Target desktop

# Android APK（发布到 release/android）
powershell -File build.ps1 -Target android

# 不带参数 = 两者都构建
powershell -File build.ps1
```

`build.ps1` 只覆盖 **框架依赖桌面版** 与 **Android APK**。追求最小体积的
**Native AOT** 需手动发布（详见 [docs/DELIVERY.md](docs/DELIVERY.md)）：

```powershell
dotnet publish src\ImgHub.Desktop -c Release -r win-x64 `
    -p:PublishAot=true -p:DebugType=none -p:DebugSymbols=false `
    -o release\desktop-aot
```

产物输出到 `release/`（**该目录不入库**，通过 GitHub Releases 分发）。

> ⚠️ AOT 产物必须**整目录**分发（`ImgHub.Desktop.exe` 需与 `libSkiaSharp.dll` /
> `av_libglesv2.dll` / `libHarfBuzzSharp.dll` 同目录），单独拷 exe 会闪退。

### 打一个 Release 包

一条命令产出可直接上传的发布资产（AOT 整目录 ZIP + APK + `SHA256SUMS.txt`）：

```powershell
# 全量构建并打包到 dist\
powershell -File tools\make-release.ps1

# 已手动跑过 AOT publish，只想打包现有产物
powershell -File tools\make-release.ps1 -SkipBuild
```

上传时的发布说明骨架见 [`.github/RELEASE_TEMPLATE.md`](.github/RELEASE_TEMPLATE.md)。

---

## 文档

| 文档 | 内容 |
|---|---|
| **[docs/README.md](docs/README.md)** | 文档索引（从这里进） |
| [docs/HANDOVER.md](docs/HANDOVER.md) | **新接手先读**：跑起来 / 改功能去哪 / 排错 |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | 四层结构、数据流、五 provider 差异、扩展点 |
| [docs/CONSTRAINTS.md](docs/CONSTRAINTS.md) | **写代码前必读**：A–I 硬约束 + 陷阱速查 |
| [docs/DELIVERY.md](docs/DELIVERY.md) | 打包发布（三级方案 + 检查清单） |
| [docs/FEATURES.md](docs/FEATURES.md) | 功能清单 + **已知缺口**（哪些是"有意不做"） |
| [docs/i18n.md](docs/i18n.md) | 多语言实现与 6 个"不报错"的坑 |
| [docs/port-status.md](docs/port-status.md) | 迁移进度与历次修复根因记录 |
| [CONSTRAINTS 索引](docs/CONSTRAINTS.md) / [fix-plan 各轮](docs/) | 每条改动的来由与实证 |
| [CHANGELOG.md](CHANGELOG.md) | 面向用户的版本变更记录 |
| [CONTRIBUTING.md](CONTRIBUTING.md) | 贡献指南（含"不能只靠编译通过"的验证纪律） |
| [SECURITY.md](SECURITY.md) | 安全模型、凭据处理、如何私密报告问题 |
| [AGENTS.md](AGENTS.md) | AI / 自动化 agent 的仓库操作约定 |
| [NOTICE.md](NOTICE.md) | **第三方许可声明**（含内嵌字体的 OFL 说明） |

---

## 架构一句话

```
Core（业务，无 UI 依赖） ← App（共享 UI） ← Desktop / Android（薄 head）
```

**架构红线**：`ImgHub.Core` 绝不引用 Avalonia / Android。
（实战验证：App 层曾被误删，因 Core 无 UI 依赖 + 测试契约完整，得以完整重建。）

详见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

---

## 开发

```powershell
# 只跑某个测试
dotnet test tests\ImgHub.Integration.Tests --filter "FullyQualifiedName~RegionEdit"

# 重新生成应用图标（桌面 ICO）
powershell -File make-icon.ps1

# 重新生成 Android 图标（多密度 mipmap）
powershell -File tools\make-android-icons.ps1
```

---

## 许可

**GPL-3.0** — 见 [LICENSE](LICENSE)。

第三方组件与**内嵌字体（Noto Sans SC, OFL-1.1）**的许可说明见 [NOTICE.md](NOTICE.md)。

本项目**不含任何 API 凭据**；所有 key 由用户自行配置并存储于本地用户目录。

---

## legacy/ 说明

`legacy/` 是上一代 **Python + curses 终端实现**（v5.18.3），保留原因：

1. **参考实现**：C# 版的行为契约来源（API 调用、成本估算、错误处理）
2. **回归基准**：959 项 Python 测试是行为契约的"活文档"
3. **低依赖场景**：纯标准库，可在 Termux / Pydroid 等环境跑

同样以 GPL-3.0 发布。详见 [legacy/README.md](legacy/README.md)。
