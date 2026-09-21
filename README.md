# imgagent

> 跨平台 AI 生图客户端 —— **Windows 桌面 + Android**，一套 UI 两端复用。
> 支持文生图、图生图（多参考图）、**区域标注重绘**、批量出图、提示词润色、历史与成本管理。

![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Android-blue)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![Avalonia](https://img.shields.io/badge/Avalonia-12.1.2-8B44AC)
![License](https://img.shields.io/badge/license-GPL--3.0-blue)

---

## 是什么

一个**图形化**的 AI 生图工作台：

| 功能 | 说明 |
|---|---|
| 文生图 | 多模型（OpenAI / Google / 国产 Seedream / Flux / Grok 等） |
| 图生图 | 多参考图，待修改图恒为第 1 位（保证主体一致） |
| **区域标注重绘** | 画笔/马克笔/方框/圆圈直接在图上圈出要改的区域，重复涂色不叠加 |
| 批量出图 | 一次 1–4 张，缩略图条切换预览 |
| 提示词润色 | LLM 生成 4 条候选，浮窗内 4 选 1 |
| 成本管理 | 实时预估 + 按 provider 累计 + 总累计 |
| 离线模式 | 不花钱跑通全流程（确定性占位图） |
| 双 provider | OpenRouter（同步）/ APIMart（异步轮询） |

---

## 仓库结构

```
imgagent/
├── src/                          C# 主实现
│   ├── Imgagent.Core/            平台无关业务层（无 UI 依赖 —— 架构红线）
│   ├── Imgagent.App/             共享 UI（XAML + MVVM），两端复用
│   ├── Imgagent.Desktop/         Windows / Linux / macOS head
│   └── Imgagent.Android/         Android head
├── tests/
│   ├── Imgagent.Core.Tests/      单元测试（55 项）
│   └── Imgagent.Integration.Tests/  端到端测试（37 项）
├── legacy/                       Python + curses 上一代实现（参考 + 行为契约）
├── docs/                         文档（见下）
├── tools/                        辅助脚本（图标生成等）
├── build.ps1                     一键构建
├── make-icon.ps1                 PNG → 多尺寸 ICO
├── Directory.Build.props         解决方案级 MSBuild 属性
└── Imgagent.slnx                 解决方案文件
```

---

## 快速开始

### 前置

- **.NET 10 SDK**
- （Android）`dotnet workload install android` + Android SDK / JDK

### 跑起来

```powershell
# 跑测试（92 项，全部离线不花钱）
dotnet test tests\Imgagent.Core.Tests           # 55/55
dotnet test tests\Imgagent.Integration.Tests    # 37/37

# 跑桌面版
dotnet run --project src\Imgagent.Desktop
```

### 配置

窗口右下角 **⚙ 设置** → 选 Provider + 填 API Key → 保存。或用环境变量：

```powershell
$env:OPENROUTER_API_KEY = "sk-or-v1-..."
# 或
$env:IMGAGENT_APIMART_API_KEY = "sk-..."
```

### 先试离线模式（不花钱）

勾选左栏 **离线** → 输入提示词 → 点 **生成**。

> 数据目录默认 `%LOCALAPPDATA%\imgagent`，可用 `IMGAGENT_HOME` 覆盖。

---

## 打包

```powershell
# Windows Native AOT（23 MB exe + 3 原生 DLL，启动最快）
powershell -File build.ps1 -Target desktop-aot

# Windows 单文件 + 裁剪（41 MB 单 exe）
powershell -File build.ps1 -Target desktop-single

# Android APK（~64 MB，含内嵌中文字体）
powershell -File build.ps1 -Target android
```

产物输出到 `release/`（**该目录不入库**，通过 GitHub Releases 分发）。

> ⚠️ AOT 产物必须**整目录**分发（`Imgagent.Desktop.exe` 需与 `libSkiaSharp.dll` /
> `av_libglesv2.dll` / `libHarfBuzzSharp.dll` 同目录），单独拷 exe 会闪退。

---

## 文档

| 文档 | 内容 |
|---|---|
| **[docs/README.md](docs/README.md)** | 文档索引（从这里进） |
| [docs/HANDOVER.md](docs/HANDOVER.md) | **新接手先读**：跑起来 / 改功能去哪 / 排错 |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | 四层结构、数据流、双 provider 差异、扩展点 |
| [docs/CONSTRAINTS.md](docs/CONSTRAINTS.md) | **写代码前必读**：A–G 七类硬约束 + 陷阱速查 |
| [docs/DELIVERY.md](docs/DELIVERY.md) | 打包发布（三级方案 + 检查清单） |
| [docs/port-status.md](docs/port-status.md) | 迁移进度与历次修复根因记录 |
| [NOTICE.md](NOTICE.md) | **第三方许可声明**（含内嵌字体的 OFL 说明） |

---

## 架构一句话

```
Core（业务，无 UI 依赖） ← App（共享 UI） ← Desktop / Android（薄 head）
```

**架构红线**：`Imgagent.Core` 绝不引用 Avalonia / Android。
（实战验证：App 层曾被误删，因 Core 无 UI 依赖 + 测试契约完整，得以完整重建。）

详见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

---

## 开发

```powershell
# 只跑某个测试
dotnet test tests\Imgagent.Integration.Tests --filter "FullyQualifiedName~RegionEdit"

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
