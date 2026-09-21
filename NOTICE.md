# 第三方组件与许可声明（NOTICE）

本项目（imgagent）以 **GPL-3.0** 发布。以下为所使用的第三方组件及其许可。

---

## 1. 内嵌字体（**重要：有独立许可**）

### Noto Sans SC
- **文件**：`src/Imgagent.App/Assets/Fonts/NotoSansSC.ttf`
- **用途**：Android 上的中文字体渲染兜底（Avalonia Android CJK 回归规避）
- **来源**：Google Noto 字体项目（`NotoSansSC-VF.ttf` 可变字体）
- **许可**：**SIL Open Font License 1.1 (OFL-1.1)**
- **许可全文**：https://scripts.sil.org/OFL

> ⚠️ **OFL 与 GPL 的兼容说明**：
> OFL-1.1 允许将字体嵌入到任何软件（包括 GPL 软件）中分发，
> 但 **字体文件本身仍受 OFL 约束**（不得单独售卖、衍生字体不得用保留字体名）。
> 本项目仅**原样嵌入**该字体，未做修改与改名，符合 OFL 要求。
> 若移除该字体，Android 中文将显示为方框（见 docs/CONSTRAINTS.md D5）。

---

## 2. 框架与库

| 组件 | 版本 | 许可 | 用途 |
|---|---|---|---|
| **Avalonia** | 12.1.2 | MIT | 跨平台 UI 框架 |
| **Avalonia.Themes.Fluent** | 12.1.2 | MIT | Fluent 主题 |
| **Avalonia.Fonts.Inter** | 12.1.2 | MIT | Inter 字体（西文） |
| **CommunityToolkit.Mvvm** | 8.4.2 | MIT | MVVM 源生成器 |
| **SkiaSharp** | 3.119.4 | MIT | 跨平台 2D 渲染/图像编解码 |
| **AvaloniaUI.DiagnosticsSupport** | 2.2.3 | MIT | 开发期诊断（仅 Debug） |
| **Xamarin.AndroidX.Core.SplashScreen** | 1.0.1.1 | Apache-2.0 | Android 启动屏 |

## 3. .NET 运行时
- **.NET 10**（含 Native AOT 运行时）：MIT
- 分发 AOT 产物时会内联运行时代码（MIT 允许）

---

## 4. 参考实现（同仓库，同为 GPL-3.0）
- `legacy/`：上一代 Python + curses 实现（本项目的参考实现与行为契约来源）

---

## 5. 无内置凭据声明（安全相关）

本项目**不包含任何 API key / 端点凭据**。
所有凭据由用户自行配置（环境变量或本地文件），存储于用户数据目录：

| 环境变量 | 文件 |
|---|---|
| `OPENROUTER_API_KEY` | `%LOCALAPPDATA%\imgagent\.imgagent_key` |
| `IMGAGENT_APIMART_API_KEY` | `...\.imgagent_apimart_key` |
| `IMGAGENT_POLISH_API_KEY` | `...\.imgagent_polish_key` |

这些文件已在 `.gitignore` 中排除。

---

## 6. 商标声明

第三方模型名称（如 `gpt-image-2.5-flare`、`gemini-3.1-flash-image`、`seedream-5-0-pro`）
为其各自所有者的商标或产品名。本项目仅为调用其 API 的客户端，
与该等公司无隶属或背书关系。
