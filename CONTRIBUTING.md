# 贡献指南

> 先读 [`docs/HANDOVER.md`](docs/HANDOVER.md)（怎么跑起来 / 改功能去哪 / 排错），
> 再读 [`docs/CONSTRAINTS.md`](docs/CONSTRAINTS.md)（**哪些事绝对不能做**）。
> 用 AI agent 改代码的，另见 [`AGENTS.md`](AGENTS.md)。

---

## 一、环境

已在本机验证可用的工具链：

| 工具 | 版本 | 必需 |
|---|---|---|
| **.NET SDK** | **10.0.x** | ✅ |
| git | 任意较新版本 | ✅ |
| Python | 3.10+ | 仅跑 `legacy/` 测试时需要 |

`docker` / `make` / `go` **本项目不用**，不必安装。

**所有命令都在仓库根目录执行。**

---

## 二、跑起来

```powershell
# 1) 测试（449 项，全部离线 —— 不联网、不花钱）
dotnet test tests\ImgHub.Core.Tests           # 236 项，秒级
dotnet test tests\ImgHub.Integration.Tests    # 213 项，约 1.5 分钟

# 2) 跑桌面版（会弹窗口，需交互式桌面）
dotnet run --project src\ImgHub.Desktop
```

先试**离线模式**（左栏勾「离线」）→ 输入提示词 → 点生成，就能不花钱跑通全流程。

测试为什么全是离线的：`Offline=true` + 确定性占位图，
所以任何 PR 都不会因为跑测试而产生 API 费用。

---

## 三、改代码前必看的三条硬约束

完整版在 [`docs/CONSTRAINTS.md`](docs/CONSTRAINTS.md)，以下是**最常踩的**：

### 1. 架构红线：`ImgHub.Core` 不得引用 UI / 平台

```csharp
// ❌ Core 里出现这些 → 编译失败，且破坏分层
using Avalonia.*;  using Android.App;  public Bitmap? Thumb { get; }

// ✅ 只允许 SkiaSharp 这类非 UI 跨平台库；UI 类型放 App 层
```

分层：`Core`（业务，无 UI 依赖）← `App`（共享 UI）← `Desktop` / `Android`（薄 head）。

### 2. 改 UI 前先判断改哪：Section 还是 MainView

| 要改的区域 | 怎么改 |
|---|---|
| **消息面板 / 历史面板 / 高级参数** | ✅ 改 `src/ImgHub.App/Views/Sections/*.axaml` —— **一处生效，两套布局自动同步** |
| 预览卡片（含标注工具条）/ 主参数卡片（5 下拉 + 批量） | ❌ **宽屏与窄屏两处都要改** |
| 新增控件 / 布局 | 先判断能否放进上述 3 个 Section；不能才两处都改 |

⚠️ **Section 内不要用 `$parent[UserControl].Xxx`**（AVLN2000 编译失败）——
`DataContext` 会向下继承，直接用 `{Binding Xxx}` 即可。

### 3. XAML 一律用 `{CompiledBinding X}`

```xml
<!-- ❌ 属性名写错也编译通过 → 运行时静默拿不到值 -->
<ComboBox ItemsSource="{Binding ModelChoices}"/>
<!-- ✅ 编译期报错（AVLN2000，含类型与行号） -->
<ComboBox ItemsSource="{CompiledBinding ModelChoices}"/>
```

例外（这几处是刻意的，别"顺手统一"）：空路径 `{Binding}`、`$parent[...]` 跨层查找、
`ElementName=` 自引用、以及项类型为 `string` 因而无属性可绑的模板。

---

## 四、不能只靠"编译通过"就认为改好了

| 你改了什么 | 必须做什么 |
|---|---|
| Core 逻辑 | 跑对应单测 |
| UI / ViewModel | 跑集成测试 |
| **XAML** | ⚠️ **测试覆盖不到 —— 必须人工跑一遍桌面版点一下** |
| `AppConfig` / `AppJsonContext` / `ImageApi` / `Catalog` / `HttpJsonClient` / XAML 绑定 | **Native AOT publish + 以「能启动」为验收**（见下） |
| 界面文案 | `Localizer.cs` 三个字典各加一条 + XAML 用 `{CompiledBinding L[key]}` |

### Native AOT 验收（大改后强制）

AOT 与 JIT 是**两套运行时约束**，JIT 全绿不代表 AOT 能用：

```powershell
Get-Process -Name "ImgHub*" -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish src\ImgHub.Desktop -c Release -r win-x64 `
    -p:PublishAot=true -p:DebugType=none -p:DebugSymbols=false -o release\desktop-aot
```

① 必须 0 error；② 必须有 3 个原生 DLL 与 exe **同目录**；③ **能起来不秒崩**。

> ⚠️ 验收脚本里必须带 `Stop-Process` —— 否则启动的 exe 会锁住产物文件，
> 下一次 publish 会刷一串 `MSB3026: 文件被锁定` 并最终失败。

**历史教训**：反射序列化/绑定在 AOT 下要么炸、要么**静默失效**。
`config.json` 曾因此读不进（参数全默认）、界面曾静默拿不到绑定值。

---

## 五、提交

- 提交信息用**中文**，格式参考历史：`fix(#N): ...` / `feat(#N): ...` / `chore: ...`
- **只提交预期改动**，确认没有产物入库
  （`release/`、`bin/`、`obj/`、`.codegraph/`、`.reasonix/` 已在 `.gitignore`）
- 换行符由 `.gitattributes` 强制 `eol=lf`
- ⚠️ **`*.ps1` 的注释一律用英文**（ASCII-only）：
  Windows PowerShell 5.1 会把 UTF-8 无 BOM 当 ANSI 读，中文注释会导致**脚本解析失败**。
  这是踩过的坑，不是风格偏好。

---

## 六、不要做的事

| 别做 | 为什么 |
|---|---|
| 删 `legacy/` | 它是上一代 Python 实现的**参考实现与行为契约**（959 项测试），不是死代码 |
| 删 `AppPaths.ResolveHome()` 等改名兼容代码 | 删了用户的**历史图片与 API key 会失效**（旧路径 `%LOCALAPPDATA%\imgagent`、旧 key 文件名、`IMGAGENT_*` 环境变量） |
| 清理「已知缺口」项 | 见 [`docs/FEATURES.md`](docs/FEATURES.md) —— 有些是**有意不做** |
| 往隐藏底栏加新按钮 | 底栏 `IsVisible="False"`，加了功能在 UI 上完全打不开（历史事故） |

---

## 七、文档

改功能时顺手更新对应文档，别让文档和代码漂移。文档索引见 [`docs/README.md`](docs/README.md)。

| 改了什么 | 更新哪份 |
|---|---|
| 新增/调整一条硬约束 | `docs/CONSTRAINTS.md` |
| 新增一轮修复 | `docs/fix-plan-v*.md` + `docs/port-status.md` |
| 加了参数 / 模型 / 绘制工具 | `docs/HANDOVER.md` 的分步清单 |
| 打包方式变了 | `docs/DELIVERY.md` |
| 面向 AI agent 的约定变了 | `AGENTS.md` |
| 面向用户的变更 | `CHANGELOG.md` |
| 改了功能清单 / 已知缺口 | `docs/FEATURES.md` + `README.md` **四份语言版本** |

⚠️ **`README` 有四份**：`README.md`（英文，主文件）、`README.zh.md`、`README.ja.md`、`README.ko.md`。
四份结构必须一致——改了主文件的功能描述、下载说明或已知限制，**其余三份要同步改**，
否则会有一半用户看到过时信息。
