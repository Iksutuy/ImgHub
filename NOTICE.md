# 第三方组件与许可声明（NOTICE）

本项目（imghub）以 **GPL-3.0** 发布。以下为所使用的第三方组件及其许可。

---

## 1. 内嵌字体（**重要：有独立许可**）

### Noto Sans SC
- **文件**：`src/ImgHub.App/Assets/Fonts/NotoSansSC-Regular.ttf`（7.15 MB）
- **用途**：Android 上的中文字体渲染兜底（Avalonia Android CJK 回归规避）
- **来源**：Google Noto 字体项目（`NotoSansSC-VF.ttf` 可变字体，Version 2.04）
- **加工**：用 fontTools 实例化到 `wght=400` 并子集化（脚本：`tools/make-static-font.py`），
  产物已**不含** `fvar`/`gvar`/`HVAR` 等可变字体表，是静态字体
- **许可**：**SIL Open Font License 1.1 (OFL-1.1)**
- **许可全文**：https://scripts.sil.org/OFL
  （字体文件内的 `name` 表 nameID 13/14 即 OFL 声明原文，已随文件保留）

> ⚠️ **OFL 与 GPL 的兼容说明**：
> OFL-1.1 允许将字体嵌入到任何软件（包括 GPL 软件）中分发，
> 但 **字体文件本身仍受 OFL 约束**（不得单独售卖；修改版不得使用保留字体名）。
> 本项目只做**格式实例化与子集化**（未改变字形设计），属于 OFL 允许的修改，
> 因此保留 "Noto Sans SC" 原名。
> 若移除该字体，Android 中文将显示为方框（见 docs/CONSTRAINTS.md D5/D6）。

### Material Symbols Outlined（按钮图标）
- **文件**：`src/ImgHub.App/Assets/Fonts/MaterialSymbols.ttf`（**6.1 KB**）
- **用途**：全部按钮/工具的图标（v0.5.37 起统一风格）
- **来源**：Google Material Design Icons 项目的
  `variablefont/MaterialSymbolsOutlined[FILL,GRAD,opsz,wght].ttf`（可变字体，10.2 MB）
- **加工**：用 fontTools **实例化**（FILL=0, GRAD=0, opsz=24, wght=400）+
  **子集化到 34 个图标码位**（脚本：`tools/make-icon-font.py`），
  产物**不含** `fvar`/`gvar`/`HVAR`（静态字体，规避 Avalonia 可变字体发虚问题）
- **许可**：**Apache License 2.0**
- **许可全文**：https://www.apache.org/licenses/LICENSE-2.0
  （已随文件写入 `name` 表 nameID 13/14 —— 实例化会丢掉这两条，脚本里显式补回）
- 源文件 `MaterialSymbolsOutlined.ttf`（10.2 MB）**不入库**（已在 `.gitignore`）

> ⚠️ **为什么不用系统自带的 `Segoe MDL2 Assets` / `Segoe UI Symbol`**：
> 那是**微软专有字体** —— ① 不能随程序分发；② **Android 上根本不存在**。
> 项目历史上用 `UseIcons` 开关"绕开"，等价于 Android 上永远没有图标。
> 改为内嵌开源字体后两端行为一致（v0.5.37）。
> **新增图标时必须**先把它加进 `tools/make-icon-font.py` 的 `ICON_CODEPOINTS` 再重跑脚本，
> 否则字形不在子集里 → 界面上显示空白。

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
| `OPENROUTER_API_KEY` | `%LOCALAPPDATA%\imghub\.imghub_key` |
| `IMGHUB_APIMART_API_KEY` | `...\.imghub_apimart_key` |
| `IMGHUB_POLISH_API_KEY` | `...\.imghub_polish_key` |

这些文件已在 `.gitignore` 中排除。

---

## 6. 商标声明

第三方模型名称（如 `gpt-image-2.5-flare`、`gemini-3.1-flash-image`、`seedream-5-0-pro`）
为其各自所有者的商标或产品名。本项目仅为调用其 API 的客户端，
与该等公司无隶属或背书关系。
