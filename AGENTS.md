# AGENTS.md — ImgHub 仓库操作约定

> 面向 AI / 自动化 agent。人类读者请从 [docs/HANDOVER.md](docs/HANDOVER.md) 开始。
> **本文件只写"怎么在这里安全地改代码"**；架构与业务细节在 `docs/`。

---

## 1. 一句话认识这个仓库

**ImgHub** —— 跨平台 AI 生图客户端（Windows 桌面 + Android，一套 Avalonia UI 两端复用）。
`legacy/` 是上一代的 Python + curses 实现，**保留作参考实现与行为契约**，
不是死代码 —— 不要清理它。

> ⚠️ **v0.5.28 已整体改名**：`Imgagent` → `ImgHub`（项目 / 文件夹 / 程序集 / 命名空间 /
> 应用显示名 / Android `ApplicationId` = `com.imghub.app`）。
> **兼容策略**：`AppPaths.ResolveHome()` 会沿用改名前的数据目录（`%LOCALAPPDATA%\imgagent`），
> `Session.ReadKeyFileCompat()` 会回退读 `.imgagent_*_key`，环境变量 `IMGAGENT_*` 仍被识别。
> 因此**不要把兼容代码当作"旧代码残留"删掉** —— 删了用户的历史图片与 API key 会失效。

```
ImgHub.Core    平台无关业务层（无 UI 依赖）
    ↑ 引用
ImgHub.App     共享 UI（XAML + MVVM，两端复用）
    ↑ 引用
ImgHub.Desktop / ImgHub.Android    薄平台 head
```

---

## 2. 环境与命令

已在本机验证可用的工具链：`.NET 10 SDK`（10.0.302）、`git`、`python`（3.13）。
`docker` / `make` / `go` **不可用**，不要使用。

**所有命令都在仓库根目录执行**。

```powershell
# 测试（必须全绿才继续）
dotnet test tests\ImgHub.Core.Tests           # 236 项，秒级
dotnet test tests\ImgHub.Integration.Tests    # 213 项，约 1.5 分钟
# 合计 449 项，全部离线（Offline=true + 确定性占位图），不联网、不花钱

# 只跑相关测试
dotnet test tests\ImgHub.Integration.Tests --filter "FullyQualifiedName~RegionEdit"

# 跑桌面版（会弹窗口，需交互式桌面）
dotnet run --project src\ImgHub.Desktop

# 构建（框架依赖桌面版 + Android APK）
powershell -File build.ps1 -Target desktop
powershell -File build.ps1 -Target android

# 桌面 Native AOT（build.ps1 不含此目标，需手动）
# ⚠️ 大改完**必须**跑这一条 —— 见 §3.4a「大改完必须编译 Native AOT」
Get-Process -Name "ImgHub*" -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish src\ImgHub.Desktop -c Release -r win-x64 `
    -p:PublishAot=true -p:DebugType=none -p:DebugSymbols=false -o release\desktop-aot
```

**Android 前置**（工具链在用户目录，免管理员）：
`%USERPROFILE%\android-sdk-imghub` + `%USERPROFILE%\jdk-imghub`。
缺了就按 `build.ps1` 头部注释里的 `InstallAndroidDependencies` 命令补装。
Android 构建可能因环境问题失败 —— **这不是代码回归**，别为此乱改代码。

**legacy 测试**（Python，纯标准库，无需 pytest）：

```powershell
cd legacy
python tests\test_impydroid.py    # 801
python tests\test_entrypoints.py  # 42
python tests\test_polish.py       # 38
python tests\test_presentation.py # 51
python tests\test_concurrency.py  # 27
# 合计 959 项
```

---

## 3. 工作纪律（踩坑沉淀，务必遵守）

### 3.1 架构红线：Core 不得引用 UI

```csharp
// ❌ ImgHub.Core 里出现这些 → 编译失败（且破坏分层）
using Avalonia.*;  using Android.App;  public Bitmap? Thumb { get; }

// ✅ 只允许 SkiaSharp 这类非 UI 跨平台库；
//    UI 类型放 App 层（HistoryRow / PreviewThumb）
```

### 3.2 改 UI 必须两套布局同步改

`MainView.axaml` 里**宽屏三栏**与**窄屏堆叠**是两个独立子树
（`IsWideLayout` 真/假切换），各持一个 `RegionCanvas` 实例。

⚠️ **v0.5.28 起规则收窄**（Plan A 抽出了 3 个共用 Section）：

| 要改的区域 | 怎么改 |
|---|---|
| **消息面板** / **历史面板** / **高级参数** | ✅ 改 `Views/Sections/*.axaml` —— **一处生效，两套布局自动同步** |
| 预览卡片（含标注工具条） | ❌ 仍需两处都改 |
| 主参数卡片（5 下拉 + 批量） | ❌ 仍需两处都改 |
| 新增控件/布局 | 先判断能否放进上述 3 个 Section；不能才两处都改 |

**为什么只抽了 3 个**（实测重复度，不是随意选择）：
消息 75% / 历史 49% / 高级参数（逐行对应）→ 抽；
预览 40%（含 4 个 `x:Name` 强依赖 + 变换矩阵共用约束）、主参数 19% → 抽了反而更差。
依据见 [docs/plan-a-section-refactor.md](docs/plan-a-section-refactor.md) §1.1。

⚠️ **Section 内不要用 `$parent[UserControl].Xxx`**（AVLN2000 编译失败，见 docs/CONSTRAINTS 与
plan-a §8.3）：`DataContext` 会向下继承，直接用 `{Binding Xxx}` 即可；
确实需要引用 Section 自身属性时用 `x:Name` + `{Binding Xxx, ElementName=Root}`。

### 3.3 命令必须有可达入口

新增/挪动按钮后，确认其 `Command` 落在 `IsVisible` 恒为真的容器里。
**历史事故**：设置按钮曾被放进整体隐藏的底栏（`IsVisible="False"`），
导致设置浮层在 UI 上完全打不开，而测试全绿。

### 3.4 不要把"改了"当成"验证了"

- 改 Core → 跑对应单测
- 改 UI/ViewModel → 跑集成测试
- **改 XAML → 测试覆盖不到，必须人工跑一遍桌面版点一下**
- AOT 产物问题 → 先看 3 个原生 DLL 是否同目录（缺一即 `0xC0000409` 秒崩）

### 3.4a 大改完必须编译 Native AOT（**v0.5.31 新增，强制**）

**规则**：**每次大改完之后，必须跑一次桌面 Native AOT publish，并且以"能启动"为验收**。
常规 `dotnet build` 绿了**不算验证完成**。

```powershell
Get-Process -Name "ImgHub*" -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish src\ImgHub.Desktop -c Release -r win-x64 `
    -p:PublishAot=true -p:DebugType=none -p:DebugSymbols=false -o release\desktop-aot
# ① 必须 0 error；② 必须有 3 个原生 DLL 与 exe 同目录；③ 能起来不秒崩
```

**为什么必须单独做这一道**（AOT 与 JIT 是两套运行时约束，JIT 全绿不代表 AOT 能用）：

| 会在 AOT 才炸的东西 | 症状 | 本项目的历史实例 |
|---|---|---|
| 反射序列化/反序列化 | 启动或首次调用即抛 `Reflection-based serialization has been disabled` | `config.json` 读不进 → 参数全默认（CONSTRAINTS H1，`AppJsonContext` 就是为此而生） |
| 反射绑定（`new Binding{}` / 字符串路径） | 界面**静默**拿不到值，不报错 | CONSTRAINTS H1c / D4f |
| 新增的 `[JsonSerializable]` 漏注册 | 该类型写入/读取失败，异常常被 catch 吞掉 | v5.24 的「统计不更新」 |
| 原生依赖未随产物拷贝 | `0xC0000409` **秒崩** | CONSTRAINTS E1（3 个原生 DLL 必须同目录） |

**因此**：凡是动过 `AppConfig` / `AppJsonContext` / `ImageApi` / `Catalog` /
`HttpJsonClient` / XAML 绑定 的改动，**一律要做这道验收**。
只改注释、只改 `docs/`、只改测试时不必跑（但要在汇报里说明"未跑 AOT 及原因"）。

**产物自检**（两秒，别跳过）：

```powershell
# 3 个原生 DLL + exe 必须在同一目录
Get-ChildItem release\desktop-aot -Filter "*.dll" |
    Where-Object { $_.Name -match 'SkiaSharp|libHarfBuzzSharp|libSkiaSharp' } |
    Select-Object Name
Test-Path release\desktop-aot\ImgHub.Desktop.exe
```


### 3.4c 多语言（v0.5.40 新增）：三条**不报错**的坑

界面文案已全部走 `Localizer`（中/英/日）。改 UI 文案时**必须**遵守：

```xml
<!-- ✅ 唯一正确写法（编译绑定，L 是 MainViewModel 暴露的 Localizer） -->
<TextBlock Text="{CompiledBinding L[param.model]}"/>

<!-- ❌ 运行时切换不生效（DynamicResource 不推送更新） -->
<TextBlock Text="{DynamicResource param.model}"/>

<!-- ❌ ContextMenu 内不继承 DataContext → 必须显式 Source -->
<MenuItem Header="{Binding [hist.removeOneTip], Source={x:Static ui:Localizer.Instance}}"/>
```

| 坑 | 症状 | 为什么 |
|---|---|---|
| 用 `DynamicResource` 取文案 | 切语言后界面**不更新**，不报错 | DynamicResource 只在附加到资源树时解析一次 |
| `Language` setter 发 `string.Empty` 或 `Item[key]` | 代码里取值对、**界面不变**，不报错 | 索引器绑定**只认 `Item`**（C# 索引器元数据名）；另两种写法都无效 |
| `CompiledBinding ... Source={x:Static ...}` | **编译期** AVLN2000 "does not have an indexer" | CompiledBinding 忽略 `Source=`，按 `x:DataType` 推断源类型 |
| VM 里拼装文案的属性硬编码中文 | 切语言时**这些地方不变**（"部分翻译"） | 必须走 `Localizer.Instance[...]`（坑 5） |
| 改了 Localizer 但**没发 `OnPropertyChanged`** | 仍显示旧语言 | 计算属性需**显式**通知 → 统一放 `NotifyLocalizedStrings()`（坑 6） |

**新增一条文案**：
1. `Localizer.cs` 三个字典各加一条（键一致，有测试钉住）；
2. XAML 用 `{CompiledBinding L[键]}`；
3. 若文案在 **VM 里拼装** → 改走 `Localizer.Instance[...]` **并加进 `NotifyLocalizedStrings()`**。

⚠️ 有 3 条扫描测试会提醒你漏了哪步（见 `LocalizationContractTests`）。
完整说明（**6 个坑**）与排查顺序见 **[docs/i18n.md](docs/i18n.md)**。

### 3.4b 先想工具，再动手（**v0.5.29 新增**）

改代码前的"查"环节，**优先用语义工具而不是文本搜索**：

| 要查什么 | 用什么（**别先 rg**） |
|---|---|
| 某符号被谁引用 / 谁调用 | roslyn MCP `find_references` / `find_callers` |
| 某类型有什么成员、依赖什么 | roslyn MCP `get_type_overview` |
| 全解决方案的编译错误/警告 | roslyn MCP `get_diagnostics` |
| 死代码 / 没测试覆盖的高复杂度代码 | roslyn MCP `find_unused_symbols` / `find_uncovered_symbols` |
| "这功能在哪些文件、怎么串起来" | `explore` 子代理 |
| "官方文档怎么规定的" | `research` 子代理（查一手资料） |
| 改完想找人挑错 | `review` / `security-review` 子代理 |

⚠️ **工具结论也要复核**：子代理的结论与 MCP 输出都可能不完整，
**关键判断必须自己验证**（读一次实现、跑一次测试、量一次像素）。
完整用法与限制见 [§8 工具与子代理](#8-工具与子代理主动使用别只靠-grep-硬啃)。

### 3.5 清理与文件操作

- 绝不对目录用 `Remove-Item -Recurse -Force`（历史上误删过整个 App 层）
- 构建/publish 前先杀进程：`Get-Process -Name "ImgHub*" | Stop-Process -Force`
  （否则 dll 被锁 → `MSB3021`）
- ⚠️ **跑完 AOT 启动验收后也要杀进程**（v0.5.31 踩到）：验收时启动的 exe 会锁住
  `release\desktop-aot\ImgHub.Desktop.exe`，下一次 publish 就会刷一串
  `MSB3026: ... 文件被 ImgHub.Desktop (PID) 锁定` 并最终失败。
  验收脚本里必须带 `Stop-Process`（见 §3.4a 的命令）：
  ```powershell
  # 启动 → 等几秒 → 判断存活 → **务必关掉**
  $p = Start-Process release\desktop-aot\ImgHub.Desktop.exe -PassThru
  Start-Sleep 12; if ($p.HasExited) { "秒崩：$($p.ExitCode)" } else { "OK" }
  Stop-Process -Id $p.Id -Force      # ← 别漏这行
  ```
- **不要在构造函数里读 `DataContext`** —— 父级注入晚于子控件构造，那里一定是 `null`
  （v0.5.29 真实回归：消息自动滚动订阅从未生效）。
  依赖 `DataContext` 的接线一律放 `DataContextChanged`，并先解绑再绑。
- **改 XAML 后必须清 Avalonia 缓存再验证**，否则改动不生效（构建日志会写
  "正在跳过 GenerateAvaloniaResources…已最新"）：
  ```powershell
  Get-ChildItem src\ImgHub.App\obj -Recurse -Directory -Filter "Avalonia" |
      ForEach-Object { cmd /c "rmdir /s /q `"$($_.FullName)`"" }
  ```
- **不要把临时脚本/探针留在仓库里**。仓库根的 `_*.ps1` / `_*.txt` 是历史遗留，
  `.gitignore` 已按 `_*` 前缀忽略 —— 新增临时文件请用系统临时目录。

### 3.6 提交前

- 只提交预期改动，确认没有产物入库（`release/`、`bin/`、`obj/`、`.codegraph/` 已在 `.gitignore`）
- **大改后必须已完成 §3.4a 的 AOT 编译 + 启动验收**（只改 `docs/`/注释/测试除外，
  但要在汇报里写明"未跑 AOT 及原因"）
- 换行符/编码：`.gitattributes` 强制 `eol=lf`。**PowerShell 脚本刻意写成 ASCII-only** ——
  Windows PowerShell 5.1 会把 UTF-8 无 BOM 当 ANSI 读，中文注释会导致解析失败。
  **这是踩过的坑（v0.5.28）**：给 `build.ps1` 加中文注释后脚本直接解析失败。
  改 `*.ps1` 时注释一律用英文；改完用下面两条自检：
  ```powershell
  # ① 非 ASCII 字节必须为 0
  (([System.IO.File]::ReadAllBytes('build.ps1')) | Where-Object { $_ -gt 127 }).Count
  # ② PS 5.1 能解析（无错误）
  $e=$null; $null=[System.Management.Automation.Language.Parser]::ParseFile(
      (Resolve-Path build.ps1).Path,[ref]$null,[ref]$e); $e
  ```
- **`build.ps1` 的 `OutDir` 相对仓库根解析**（v0.5.28 修复）：旧默认值 `..\release`
  是相对**当前目录**的，从仓库根调用会把产物写到仓库**外面**。改这个参数时别退回相对路径。
- 提交信息用中文，格式参考历史：`fix(#N): ...` / `feat(#N): ...` / `chore: ...`

---

## 4. 改代码去哪

| 要改… | 文件 |
|---|---|
| 界面布局/样式 | 优先改 **`src/ImgHub.App/Views/Sections/*.axaml`**（消息/历史/高级参数，一处生效）；预览区与主参数区才改 `Views/MainView.axaml`（**两套布局**） |
| **日志 / 容错** | `src/ImgHub.Core/Diagnostics/AppLog.cs`（分级 Debug/Info/Warn/Error）→ 见 [CONSTRAINTS §I](docs/CONSTRAINTS.md) |
| **界面文案（中/英/日）** | **`src/ImgHub.App/Ui/Localizer.cs`** 的三个字典（键必须一致）→ 见 [docs/i18n.md](docs/i18n.md) |
| 主题/字体/颜色 | `src/ImgHub.App/App.axaml` |
| **控件高度/度量** | `src/ImgHub.App/Ui/UiMetrics.cs`（**唯一真源**）+ XAML 里 `{x:Static ui:UiMetrics.*}` |
| 业务编排 | `src/ImgHub.App/ViewModels/MainViewModel.cs` |
| 区域标注画布 | `src/ImgHub.App/Controls/RegionCanvas.cs` |
| API 调用（**五个 provider**） | `src/ImgHub.Core/Services/ImageApi.cs` |
| 错误提示文案 | `src/ImgHub.Core/Http/ErrorHints.cs` |
| 模型清单/成本常量 | `src/ImgHub.Core/Catalog.cs`（**唯一真源**） |
| 价格预估 | `src/ImgHub.Core/Services/ModelStatsService.cs` |
| 配置/历史存储 | `src/ImgHub.Core/Storage/Session.cs` |
| 图像编解码/占位图 | `src/ImgHub.Core/Imaging/` |

### 4.1 XAML 绑定约定（v0.5.28 起）

**一律用 `{CompiledBinding X}`**，不要写 `{Binding X}`：

```xml
<!-- ❌ 属性名写错也编译通过 → 运行时静默拿不到值 -->
<ComboBox ItemsSource="{Binding ModelChoices}"/>
<!-- ✅ 编译期报错（AVLN2000，含类型与行号） -->
<ComboBox ItemsSource="{CompiledBinding ModelChoices}"/>
```

⚠️ **`DataTemplate` 内必须先声明 `x:DataType`** —— 那里的 DataContext 是**列表项类型**，不是 VM：
```xml
<DataTemplate x:DataType="vm:HistoryRow">      <!-- 项类型 -->
  <TextBlock Text="{CompiledBinding Prompt}"/>
</DataTemplate>
<!-- 嵌套类型用 '+'：Message 是 MainViewModel 的嵌套 record -->
<DataTemplate x:DataType="vm:MainViewModel+Message">
```

**保留普通 `{Binding}` 的例外**（这几处是刻意的，别"顺手统一"）：
空路径 `{Binding}`、`$parent[...]` 跨层查找、`ElementName=` 自引用、以及
项类型为 `string`（`ObservableCollection<string>`）因而无属性可绑的模板。

**另见硬约束**：H1d（为何必须编译绑定）、H1c（C# 里别手写 `new Binding{}`，AOT 会失效）、
D4f（Section 内别用 `$parent[UserControl]`）。

**扩展某功能需要改动哪几处**：见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) §六；
**加参数/加模型/加绘制工具**的分步清单见 [docs/HANDOVER.md](docs/HANDOVER.md) §三、四。

---

## 5. 硬约束索引（写代码前扫一眼）

完整版：[docs/CONSTRAINTS.md](docs/CONSTRAINTS.md)。最常踩的几条：

| 编号 | 约束 | 一句话代价 |
|---|---|---|
| A1 | Core 不引用 UI/平台 | 破坏分层，App 层被删后无法重建 |
| B1 | 待修改图排参考图**第 1 位** | 模型按位置理解参考图，放错主体就变 |
| B4 | `Sanitize()` 必须在 `Load()` 跑 | `model: null` → 启动 NullReference **闪退** |
| C1 | **余额判断先于权限判断** | APIMart 余额不足返回 403，被误诊为 key 无效 |
| C2 | 重试只针对 429/5xx | 4xx 重试浪费流量且掩盖真因 |
| C4 | 写盘全部走 `_writeLock` | UI 线程与 Timer 并发 `File.Move` 抛 IOException |
| C5 | 会调 API 的入口先查 `Busy` | 双击 → 两次调用 → **重复扣费** |
| D2 | `async void` 必须 try/catch | 异常直接崩进程 |
| D3 | 用 `Dispatcher.Post` 不用 `InvokeAsync` | 测试/无消息泵环境死锁 |
| D4 | 窗口图标用 PNG，exe 用 ICO（**ICO 内部须为 BMP 帧、目录 offset 从 `6+16N` 起**） | PNG-in-ICO → 窗口崩 / **exe 显示通用图标** |
| D4b/D4c | UI 两套布局同步 + 入口可达 | 功能在 UI 上找不到（测试查不出） |
| D6 | 内嵌字体必须**静态**（非 VF） | VF 默认字重 100 → 中文发虚模糊 |
| E1 | AOT exe 必须与 3 个原生 DLL 同目录 | 单独拷 exe → `0xC0000409` 秒崩 |
| **H2** | 文案取用**只准** `{CompiledBinding L[key]}`；切语言**必须**发 `Item[key]` 通知 | 用了 `DynamicResource` 或 `string.Empty` 通知 → 界面**不更新且不报错**（v0.5.40 实测） |
| **I1** | **禁止**静默 `catch { }`（唯一例外：`AppLog` 自身清理） | 否则故障表现为「功能悄悄失效」，事后查不到（v5.23 的 AOT bug 就这么被藏住） |
| **I2b** | 诊断细节用 `AppLog.Debug`（默认关；`IMGHUB_LOG_DEBUG=1` 开），**别当 Info 用** | Debug 量大；高频循环里打日志会拖慢界面 |

---

## 6. 已知缺口（别当成 bug 去"修"）

| 项 | 说明 |
|---|---|
| 快捷键提示 | 消息面板的快捷键文案**未绑定实际按键**（`ShowShortcuts` 只打印文字），且该按钮只在隐藏底栏 → **不可达** |
| 「数据目录」按钮 | 只在隐藏底栏（信息仍可达：设置浮层底部显示 `HomePath`） |
| 底栏整体隐藏 | `IsVisible="False"`，其中的按钮都不可达。**新功能不要往底栏加** |
| 「离线」选项 | debug 功能，默认隐藏，需 `IMGHUB_DEBUG=1`；左栏两处仍残留复选框 |
| Android 中文渲染 | 已内嵌静态字体兜底；桌面实测正常，**Android 待真机验证** |
| Android 保存到相册 | 走 SAF picker（用户选位置），非静默写 MediaStore |
| 环境诊断面板 | 原版 `doctor.py` 未移植 |
| ~~`make-icon.ps1`~~ | **v0.5.42 已修**：路径改为 `Join-Path $PSScriptRoot ...`；并改为生成 **BMP 帧** ICO + 带自检（PNG-in-ICO 会导致 exe 无图标） |
| ~~`avalonia/` 目录~~ | **已在 v0.5.28 清理**（原为重组后的残留，仅 bin 缓存、未入库）。不要再重建它 |

---

## 7. 文档地图

```
README.md                     项目总览 + 快速开始（英文）
README.zh.md / .ja.md / .ko.md  同上的中 / 日 / 韩版（改 README 时**四份都要同步**）
docs/README.md                文档索引（从这里进）
docs/HANDOVER.md              新接手先读：跑起来 / 改功能去哪 / 排错
docs/ARCHITECTURE.md          四层结构、数据流、五 provider 差异、扩展点
docs/CONSTRAINTS.md           写代码前必读：A–G 硬约束 + 陷阱速查
docs/DELIVERY.md              打包发布
docs/port-status.md           迁移进度 + 历次修复根因（查历史）
docs/fix-plan-v5.22.md        某一轮的修复方案与验收标准
docs/fix-plan-v5.28.md        API 1:1 对齐（生成/编辑/参考/蒙版）逐功能 review
docs/plan-a-section-refactor.md   共用 Section 重构 + 5 个 UI 回归的教训（§9 必读）
docs/plan-b-compiled-bindings.md  编译绑定强化（为何 {Binding} 不检查）
docs/plan-c-ui-metrics.md     UI 度量常量集中
docs/provider-openai-image-api.md  OpenAI 官方图像 API 接入契约（多模型参数域/SSE 事件名）
docs/provider-qwen-dashscope-api.md 千问 DashScope 原生协议契约（同步/异步两条链路）
docs/provider-jimeng-api.md   即梦（火山引擎）契约（AK/SK 签名 + 生成/提取两条链路）
docs/i18n.md                 **多语言（中/英/日）**实现与三个踩坑（选型/索引器绑定/通知格式）
docs/prompt-engineering.md    提示词工程指南（两个端点各自优化 + 浮窗内容来源）
src/*/README.md               各层职责与设计决策
tests/README.md               测试哲学 + 关键用例
NOTICE.md                     第三方许可（含内嵌字体的 OFL 说明）
```

---

## 8. 工具与子代理（**主动使用**，别只靠 grep 硬啃）

> 本仓库已配置 **roslyn-codelens MCP** 与一组 **skill 子代理**。
> 它们的价值在于：**用编译器的语义信息**替代「文本搜索 + 人脑推断」，能避免大量误判。
> **默认动作：遇到下列场景先想到它们，而不是先 `rg`。**

### 8.1 roslyn-codelens MCP（`reasonix.toml` 已配置，指向 `ImgHub.slnx`）

⚠️ **前提**：solution 需已加载（6 个项目）。若工具返回空，先 `list_solutions` 看状态，
必要时 `load_solution`（analyzer 类诊断还需 `trust_solution`，但**调用前必须问用户**）。

| 场景 | 用哪个工具 | 为什么优于 grep |
|---|---|---|
| **改代码前查"谁会受影响"** | `find_references` | grep 会漏掉同名不同类、误报字符串/注释里的匹配 |
| 查"这个方法被谁调用" | `find_callers` | 只给真实调用点，不含定义与注释 |
| **重构前评估改动面** | `get_type_overview` | 一次拿到成员/基类/接口/DI 依赖/该文件诊断 |
| 找个类/方法在哪 | `search_symbols` | 按符号而非文本匹配，排除注释与字符串 |
| 看某方法完整实现（含 XML 注释） | `get_method_source` | 原样返回源码，不必翻文件 |
| 类型继承关系 | `get_type_hierarchy` | 上下都走，grep 做不到 |
| **提交前/大改后扫错** | `get_diagnostics` | 一次拿全解决方案的 error/warning（比逐个 build 快） |
| **找死代码** | `find_unused_symbols` | 判断"是否有人引用"，人工判断极易漏 |
| **找没测试覆盖的高复杂度代码** | `find_uncovered_symbols` | 按圈复杂度排序 + riskHotspot，定位回归风险 |

**实用要点（实测）**：
```
# 符号名要足够精确 —— 只写 "GenerateAsync" 可能匹配不到
find_references  symbol="ImageApi.GenerateAsync"     ← 推荐
find_references  symbol="GenerateAsync"              ← 可能返回空
```
- `get_diagnostics` 的 analyzer 诊断需要 `trust_solution`（**先问用户**，analyzer DLL 会执行代码）
- 这些工具**多数标记 read_only=false**（会触发 solution 加载/分析），但**不改源码**，可放心用于调查

**典型组合拳**（重构某个 API 时）：
```
1. get_type_overview(ImageApi)        ← 先看它有什么、依赖什么
2. find_references(ImageApi.GenerateAsync)  ← 谁在用、怎么用
3. get_method_source(ImageApi.GenerateAsync) ← 看实现细节
4. 改完后 get_diagnostics              ← 确认没引入编译错误
```

### 8.2 子代理（skill）

> **原则**：子代理在**独立上下文**里工作，适合"读得多、结论短"的任务 ——
> 主线程不必被几十个文件的细节占满。**结论拿回来后要自己复核关键点。**

| skill | 什么时候用 | 典型问法 |
|---|---|---|
| **`explore`** 🧬 | 需要摸清"某功能在哪些文件、怎么串起来的"，而自己还没读过那些文件 | 「RegionCanvas 的导出流程经过哪些类？」 |
| **`research`** 🧬 | 需要查**外部一手资料**（官方文档/规范），且要带引用 | 「OpenAI Images API 当前的参数集是什么？」 |
| **`review`** 🧬 | 改完一批代码后，想要**独立视角**挑正确性/回归风险 | 「review 这次 ImageApi 的改动」 |
| **`security-review`** 🧬 | 改动涉及密钥、路径拼接、外部输入解析时 | 「review 上传参考图的路径处理」 |
| **`arch-optimize`** 🧬 | 阶段性架构审查（六类衰退风险、质量度量） | 「对本仓库做一轮架构衰退扫描」 |
| `test` | 跑测试并诊断失败 | 「跑集成测试，失败就定位原因」 |
| `init` | 依据仓库真实约定刷新项目说明文件 | 「更新 AGENTS.md」 |

**推荐的用武之地（本仓库具体场景）**：

| 场景 | 用什么 |
|---|---|
| 「这个 API 参数到底该怎么传」 | `research`（查官方文档）→ 回来自己核对 payload |
| 「新 provider 要改哪几处」 | `explore` 摸清链路 → 自己按 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) §六 动手 |
| 「这次改动有没有回归风险」 | `review` |
| 「找找还有哪些地方是硬编码/假实现」 | `explore` 或 `rg` + 自己确认（**结论必须自己验证**） |

⚠️ **不要**用子代理做这些：
- **需要改代码的任务** —— 子代理是只读的，改代码回主线程做
- **依赖本会话临时状态的事**（如"刚才那个截图"）
- **一句话就能回答的问题** —— 起子代理的开销大于收益

### 8.3 三者的分工

```
roslyn MCP   → 问编译器（符号/引用/诊断）—— 精确、快、无需读文件
子代理        → 问独立上下文（摸清链路 / 查外部文档 / 独立 review）
rg / read_file → 找文本、看具体实现（仍然常用，但别用它做符号级分析）
```

