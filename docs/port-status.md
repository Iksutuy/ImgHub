# ImgHub Avalonia 跨平台重写 —— 施工状态

> 目标：把原 Python + curses 的 imghub 重写为 **Avalonia (C#/.NET 10)** 图形化工作台，
> 一套 UI 同时跑 **Windows 桌面**与 **Android**。
> 施工起点：2026-09-19 · 前置研究见 [avalonia-migration-feasibility.md](avalonia-migration-feasibility.md)

> 📌 **本文件的「一、当前进度总览」与「二、代码结构」是最初施工期的快照，
> 数字已过时**（当时 Core 55 / 集成 37，仓库还在 `avalonia/` 子目录下）。
> **最新状态看文末「四·十六」起的各轮记录**，以及 [HANDOVER.md](HANDOVER.md) §八。
> 下文各轮记录**保留当时的原始数字不改**，以便对照历史。

---

## 一、当前进度总览

| 阶段 | 内容 | 状态 |
|---|---|---|
| P0 | 可行性验证（字体/图片/HTTP PoC） | ✅ 完成 |
| **P1** | **Core 层移植**（业务逻辑，平台无关） | ✅ **完成 · 53/53 测试通过** |
| **P2** | **主界面 + MVVM**（三栏工作台） | ✅ **完成 · 已实测运行** |
| **P3** | **端到端集成测试**（离线生成闭环） | ✅ **完成 · 13/13 测试通过** |
| **P4** | **Desktop head** | ✅ **Release 版已发布并实测运行** |
| **P5** | **Android head** | ✅ **APK 构建成功（arm64+x86_64）** |
| **P6** | **高级功能（设置浮层/状态栏按钮/深浅主题）** | ✅ **完成 · 已实测** |
| P7 | 响应式布局（手机竖屏）打磨 | ✅ 完成（见四·十三） |
| **P8** | **区域标注重绘 / 多图预览 / 配置引导** | ✅ **完成 · 已实测** |
| P9 | 润色候选选择 UI（4 条候选的图形化挑选） | ✅ 完成（见四·八 #8） |
| P10 | Android 真机中文渲染验证 | ⏳ **需真机** |

**测试合计（最新）：449/449 通过**（Core 236 + 集成 213）

**构建产物**（`release/`，v0.5.43 实测）：
```
release/desktop-aot/ImgHub.Desktop.exe          AOT（须整目录附带 3 个原生 DLL）
release/desktop/                                框架依赖版（build.ps1 -Target desktop）
release/android/imghub-0.5.43-android.apk       arm64-v8a + x86_64
```

**已实测验证（截图 + 日志）**：
```
[OK] 内存字节 -> Avalonia Bitmap      ← 生成结果不落盘直接显示
[OK] SkiaSharp.Decode(byte[])         ← 图片解码
[OK] 系统字体数: 319, 中文正常渲染      ← 全界面零乱码
[OK] HttpClient(异步)                  ← 网络连通
[OK] 离线生成 -> 落盘 -> 历史 -> 撤回 -> 持久化 闭环
[OK] Android APK 构建成功（含 ImgHub.App/Core/Android 的 AOT 原生库）
```


---

## 二、已完成：代码结构

```
（仓库根，早期在 avalonia/ 子目录下，2026-09-21 重组——见四·十四）
├── ImgHub.slnx
├── Directory.Build.props            Avalonia 12.1.2 统一版本
├── src/
│   ├── ImgHub.Core/               ★ 平台无关（net10.0，无 UI 依赖）
│   │   ├── Catalog.cs               模型/质量/画幅/成本（对应 settings.py）
│   │   ├── Models/                  Item / GenResult / AppConfig / ApiProvider
│   │   ├── Http/                    ApiError + ErrorBody + ErrorHints + HttpJsonClient
│   │   ├── Imaging/                 ImageCodec + Placeholder（SkiaSharp）
│   │   ├── Services/                ImageApi（双 provider）+ PolishService
│   │   └── Storage/                 Session（配置/历史/提示词/key）
│   ├── ImgHub.App/                ★ 共享 UI（XAML + ViewModel）
│   │   ├── App.axaml(.cs)           深色主题 + CJK 字体回退链 + 组合根
│   │   ├── Services/                平台抽象（IPlatformStorage 等）
│   │   ├── ViewModels/              MainViewModel（编排层）
│   │   └── Views/                   MainView（三栏）+ MainWindow
│   ├── ImgHub.Desktop/            net10.0（Windows/Linux/macOS）
│   └── ImgHub.Android/            net10.0-android（结构就位）
└── tests/
    ├── ImgHub.Core.Tests/         53 项（常量/错误/文件名/图像/存储/润色）
    └── ImgHub.Integration.Tests/  13 项（端到端离线生成闭环）
```

> ⬆️ 上面的项数是**施工期快照**；当前为 Core 56 + 集成 58 = **114 项**。

**架构原则（延续原项目工程约束）**：
- `ImgHub.Core` **不引用任何 UI/平台类型** → 可单元测试、可换 UI 框架；
- 平台差异走接口注入（`IPlatformStorage` / `IPlatformInfo`），各 head 提供实现；
- 业务铁律逐条平移（见 §三）。

---

## 三、逐条平移的"铁律"（对应 docs/CONSTRAINTS.md）

| 原约束 | C# 实现位置 | 测试 |
|---|---|---|
| **E2/E3 403 是余额不是权限**（判断顺序：余额→权限） | `ErrorHints.Hint/Explain` | ✅ |
| **D4 质量档不能给会 400 的选项** | `Catalog.QualityChoices` | ✅ |
| **D8 成本估算不能有 10 倍偏差** | `Catalog.EstimateCost` | ✅ |
| **D5 非法配置启动时纠正** | `Session.Sanitize` | ✅ |
| **D3 提示词失败要放回输入框 / 提交即记录** | `MainViewModel` | ✅ |
| **D1 待修改图永远排第 1 位** | `MainViewModel.RunGenerationAsync` | ✅ |
| **D6 参考图上传失败不静默降级** | `ImageApi.GenerateApimartAsync` | ✅（抛错不吞） |
| **文件名三道防线（白名单/basename/剥点）** | `Session.SafeFilename` | ✅ |
| **离线模式确定性占位图** | `Placeholder.Png`（CRC32 同 Python） | ✅ |

---

## 四、⚠️ 未完成/受阻项（如实记录）

### 4.1 Android 构建 —— ✅ 已解决（记录完整解法）

**当初的障碍**：`dotnet build` 报 `NETSDK1147: 必须安装工作负载 android`。

**根因链（三层，逐个突破）**：

| # | 现象 | 根因 | 解法 |
|---|---|---|---|
| 1 | workload 装了但构建说没装 | 本机 pack 只有 `Microsoft.Android.Ref.35`，而 manifest 10.0.100 要求 **Ref.36** | 补装：`dotnet workload install android --skip-manifest-update` |
| 2 | `dotnet workload install android` 失败 | 提权（`Start-Process -Verb RunAs`）被沙箱静默拒绝；但其实是**上次操作被中断**留下的假象 | **非提权**直接重跑即可成功（wasm-tools 的 MSI 通道已验证可写） |
| 3 | 报 `XA5207: 找不到 API 36 的 android.jar` | 系统 Android SDK 在 `C:\Program Files (x86)\...`（**不可写**） | 用官方目标把 SDK/JDK 装到**用户目录**：<br>`dotnet build src\ImgHub.Android -t:InstallAndroidDependencies -f net10.0-android -p:AcceptAndroidSDKLicenses=True -p:AndroidSdkDirectory=%USERPROFILE%\android-sdk-imghub -p:JavaSdkDirectory=%USERPROFILE%\jdk-imghub` |
| 4 | `MainActivity` 编译错误 | Avalonia 12 的 API 变更：`AvaloniaMainActivity` 改为**非泛型**，App 类型由 `AvaloniaAndroidApplication<TApp>` 指定 | 见 `src/ImgHub.Android/MainActivity.cs` 与 `AndroidApp.cs` |
| 5 | `APT2260: drawable/icon not found` | 缺应用图标资源 | 复制 `Icon.png` 并加入 `<AndroidResource>` |

**最终结果**：
```
src/ImgHub.Android/bin/Release/net10.0-android/
  com.imghub.app-Signed.apk   42.12 MB   ← arm64-v8a + x86_64，含 AOT 原生库
```

**复现命令**（已封装进 `build.ps1`）：
```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Target android
```

> ⚠️ 注：Android 工具链装在**用户目录**（`%USERPROFILE%\android-sdk-imghub`、
> `%USERPROFILE%\jdk-imghub`），不污染系统 SDK，也不需要管理员权限。
> 若换机器，先跑一次 §4.1 表格第 3 行的 `InstallAndroidDependencies` 命令。

### 4.2 Android 中文渲染 —— 仍需真机验证

如可行性报告 §四所述：Avalonia Android 有过 **CJK 渲染回归**（#19868 / #20195 / #19931）。
本工程已在 `App.axaml` 显式指定中文优先字体链作为缓解：

```xml
<FontFamily x:Key="AppFont">Microsoft YaHei UI, Microsoft YaHei, Noto Sans CJK SC, Noto Sans SC, Source Han Sans SC, Segoe UI, sans-serif</FontFamily>
```

**APK 已能构建，但"中文在 Android 上是否正常显示"尚未真机验证**（当前环境无 Android 设备/模拟器）。
若乱码，备选：嵌入 Noto Sans SC 为 `AvaloniaResource` + `EmbeddedFontCollection`。

### 4.3 尚未实现的功能（对齐原版清单）

| 原功能 | 新工程状态 |
|---|---|
| 生成 / 编辑（多参考图） | ✅ 已实现 |
| 批量 1–4 张 | ✅ 已实现 |
| 分辨率 1k/2k/4k | ✅ 已实现 |
| 输出格式 png/jpeg/webp | ✅（参数已传，格式转换待接） |
| 质量档（含 xhigh/max） | ✅ 已实现 |
| 成本估算显示 | ✅ 已实现 |
| 历史 / 撤回 | ✅ 已实现 |
| 提示词历史 | ✅ 已实现 |
| 提示词润色（4 候选） | ⚠️ 服务已实现；**候选选择 UI 待做** |
| 离线模式 | ✅ 已实现 |
| 相册选图 / 存图 | ✅ Desktop 用 StorageProvider；Android 待验证 |
| 系统看图器 | ✅ Desktop（Shell 打开）；Android 待做 |
| 设置页（provider/key/润色配置） | ⏳ 待做 |
| 环境诊断面板 | ⏳ 待做 |

---

## 四·五、UI 改造（2026-09-20，按用户标注）

按用户截图标注完成 4 项改造 + 配色重做：

| # | 用户标注 | 改动 |
|---|---|---|
| 1 | 「改为下拉选择框，选择提供商，内置 openrouter 和 apimart」 | 顶栏 `OpenRouter` 静态文本 → **ComboBox 下拉**（`ProviderLabels` + two-way `ProviderSelection`）；切换时自动纠正模型名与质量档（沿用原铁律 D4/D5） |
| 2 | 「这部分没有作用，按不了也没提示快捷键」 | 底栏整行纯文本 → **8 个真实按钮**：生成/编辑/撤回/预览/保存到相册/润色/快捷键/数据目录；「快捷键」按钮会把键位提示写进消息面板 |
| 3 | 「这里加一个设置按钮，设置 apikey」 | 右下角新增 **设置** 按钮 → 弹出**设置浮层**：生图 API key（含「校验」）、润色端点/模型/key、数据目录显示 |
| 4 | 「界面需要修改（配色参考图2 rikkahub）」 | 主题重做为 **RikkaHub 风格**（Material 3 中性灰 + 蓝色强调）：近白底 `#F7F8FA`、白卡片 `#FFFFFF`、细边框 `#E5E6EB`、强调蓝 `#3491FA`；**深浅双主题**（顶栏可切换） |

**实现要点**：

- 颜色统一收敛到 `App.axaml` 的 `ThemeDictionaries`（Light/Dark 两套），
  视图只引用 `{DynamicResource AppXxxBrush}`，切主题即时生效；
- 新增样式类：`card`（卡片）、`accent`（主按钮）、`toolbar`（紧凑按钮）、
  `section`/`dim`/`mono`（文字层级）；
- 设置浮层用半透明遮罩 + 居中卡片；API key 用 `PasswordChar` 掩码输入，
  状态显示为 `sk-or-...3456`（**刻意不回显明文**）。

**新增回归测试**（`WorkbenchFlowTests`，本轮 +9 项，集成测试达 22 项）：
provider 下拉切换与持久化、切 provider 后模型仍合法、设置保存 key/润色配置、
设置开关、主题切换、状态栏命令可执行不抛、预览当前图。

---

## 四·六、UI 修复 + 新功能（2026-09-20 第二轮，按图3 标注）

### 修复的真实缺陷（都有回归测试锁死）

| # | 用户标注 | 根因（实测定位） | 修复 |
|---|---|---|---|
| 1 | 「参数需要记录并每次启动恢复」 | `On*Changed` 只更新估算，**从不写 config**；且 `AppConfig` 的 C# 属性是 PascalCase 而 Python 版 config.json 是 **snake_case**（`batch_n`/`output_format`），`JsonSerializer` 无映射 → **整份配置读不进来** | ① `AppConfig` 加 `[JsonPropertyName]` 映射；② 所有参数变更走 `PersistConfig()`；③ 反序列化开 `PropertyNameCaseInsensitive` + 容忍未知字段 |
| 2 | 「下拉框没有默认提供商、没记录上次选择」 | `ItemsSource` 是 `(ApiProvider,string)` 元组而 `SelectedItem` 绑 `string` → 类型不匹配，**下拉框显示空白** | 新增 `ProviderOption` 类型；`ItemsSource`/`SelectedItem` 同类型；启动时 `OnPropertyChanged(nameof(SelectedProvider))` 选中持久化的 provider |
| 3 | 「滚动条和 UI 重叠」 | 左栏 `ScrollViewer` 无内边距，滚动条压在 ComboBox 右边缘 | 卡片右侧留 6px 内边距 + 内容右侧留 12px 边距 |
| 4 | 「两分键位和功能有重叠，精简分区」 | 左栏 `[生成][编辑][润色][撤回]` 与底栏重复 4 项 | 左栏精简为 `[生成][编辑][导入]`（贴近提示词的主操作）；底栏为**唯一完整工具条**（撤回/预览/保存到相册/润色提示词/快捷键/数据目录/设置） |
| 5 | 「节流后参数仍丢失」 | `PersistConfig` 用「距上次写入」判断，**最后一串变更全被丢掉** | 改为「尾触发」节流（安静 120ms 后写一次，最后一次必落盘）+ 生成前 `FlushConfig()` |

### 新增功能

| # | 用户标注 | 实现 |
|---|---|---|
| 6 | 「先检查配置和端点，没有则提示配置并**禁用所有按钮**直到通过」 | 新增 `IsConfigured`/`CanRun`/`NeedsSetup`：无 key 且非离线时，左栏 [生成][编辑] 与底栏操作按钮全部**禁用**，底栏显示蓝色引导条「尚未配置 API key —— 点右下角设置填写后即可开始生成」；顶栏「就绪」徽标**仅配置后显示** |
| 7 | 「检查有没有生成多幅图后在预览框内并排预览」 | 新增 `BatchResults`/`HasBatchResults`/`PreviewThumb`：一次生成 >1 张时，预览区下方出现**缩略图条**，点击任一缩略图切换主预览。**注意**：离线占位图原实现忽略 `n`（只返回 1 张），已修复为尊重批量张数 |
| 8 | 「黄圈：增加编辑图片功能，可圈画/半透明涂色提示模型改哪里」 | 新增 `RegionCanvas` 控件（`Controls/RegionCanvas.cs`）：预览图上**手绘半透明遮罩**（笔刷色/粗细可调、多笔、可清除）；导出时用 SkiaSharp 把遮罩**按原图分辨率**合成成 PNG；「用标注编辑」把该图作为参考图第 1 位，并自动追加提示词说明「图中高亮区域即需修改的部分」。笔画坐标**归一化**存储，与控件尺寸无关 |

### 验证

| 项 | 结果 |
|---|---|
| Core 测试 | **55/55** ✅（+2 项 snake_case 配置读写） |
| 集成测试 | **28/28** ✅（+6 项：参数持久化、多图缩略、未配置禁用、区域编辑） |
| GUI 实测（截图） | ✅ 未配置引导/禁用；✅ 参数重启恢复（medium/16:9/2k/jpeg/3）；✅ 批量 3 张 + 缩略图条；✅ 区域标注画出 2 笔 + 工具条 |

---

## 四·七、第二轮用户实测修复（2026-09-20 下午）

用户真实使用后报 4 个问题，其中 1 个是**致命闪退**：

### 闪退（关闭后再启动，进程闪现即消失）—— 根因与修复

用户切换 provider 后 `config.json` 里被写入了 **`"model": null`**：

```json
{ "model": null, "provider": "apimart", ... }
```

二次启动时 `Session.Sanitize()` 把 `null` 直接传进
`Catalog.ModelMatchesProvider()` → `model.Contains('/')` →
**NullReferenceException → 进程闪退**。这正是"进程出现一下又消失"的原因。

`model:null` 的来源：`OnProviderChanged` 里 `RebuildModelChoices()` 清空下拉期间，
ComboBox 会瞬发一次 `SelectedItem = null` → `OnModelChanged(null)` → 立即落盘。

修复（三处防御 + 一处根治）：

1. `Session.Sanitize()`：读入后立即把 `Model/Quality/Aspect/Resolution/OutputFormat/Provider/Polish*` 的 null 归一化为安全默认值；
2. `Catalog.ModelMatchesProvider / QualityChoices / QualitySupported` 全部改为 **nullable 参数 + 空值安全**；
3. `OnProviderChanged` 加 `_switchingProvider` **防重入**，并把「选默认模型 → 刷新下拉 → 落盘」做成**原子序列**（中间不再有空模型可见）；
4. `OnModelChanged` 收到 null/空时直接落到该 provider 的默认模型，不再把 null 写盘。

实测：用用户真实 `%LOCALAPPDATA%\imghub` 配置（含 model:null）二次启动，**正常打开**。

### 其余修复

| 问题 | 修复 |
|---|---|
| 切 provider 后左上模型框空白、不自动选 | 见上：`OnProviderChanged` 现在保证 `Model` 总是合法默认模型并同步下拉 |
| 设置「校验」点了无反应 | 新增 `KeyChecking/KeyCheckStatus`，设置浮层内**实时显示**「正在校验… / 校验通过 · 可用模型 N 个 / 校验失败：原因」 |
| 润色端点/模型重启后丢失 | `SaveSettings` 同时落 `config.json`（polish_base_url/polish_model）与 `.imghub_polish_key`；启动时组合根回填 `PolishService`（已验证用户配置里三项均正确持久化） |
| 左栏滚动条与提示词历史/按钮重叠 | 左栏改为 `Grid = [内容, 14px 滚动条列]`，滚动条独占列不再压控件；提示词历史放进固定 MaxHeight=96 的容器 |

### 配置兼容性提醒

用户旧配置可能含 `"model": null` 或 Python 版硬编码润色端点（`api.prc.dpdns.org`）。
新版读入后会自动归一化 model 并保留用户自填的润色配置 —— 不做静默覆盖。

---

## 四·八、第三轮用户实测修复（2026-09-20 晚，按图 7 标注）

### 修复的真实缺陷

| # | 用户标注 | 根因（实测定位） | 修复 |
|---|---|---|---|
| 1 | 「累计显示错误，应显示供应商累计」 | `Session.LoadState()` **漏读 `total_cost`** → 重启后累计永远 $0.0000（用户 state.json 里其实是 0.147186） | 补读 `total_cost`；新增 `Item.Provider` + `Session.CostForProvider()` |
| 2 | 「顶栏=供应商累计，历史=总累计」 | 两处原绑同一个值，语义混用 | 顶栏绑 `ProviderCost`（`APIMart 累计 $0.1635`），历史面板绑 `TotalCost`（`总累计 $0.1472`） |
| 3 | 「切换供应商后模型依然空白，禁止空白参数」 | 构造函数直接赋 `_model = Config.Model`，空/null 无兜底；`RebuildModelChoices()` 也不修正 | 构造时校验并提供默认模型；`RebuildModelChoices()` 发现当前值不在清单即自动选第一个 |
| 4 | 「历史里没正确显示图片，累计使用金额」 | 历史 `DataTemplate` 只有文字、无缩略图 | 新增 `HistoryRow`（App 层包装，带懒加载 `Thumb`，80px 缩略图）+ 模板加缩略图列 |
| 5 | 「预览区改小，下方放编辑工具栏（画笔/马克笔等）」 | 中栏无工具条 | 预览区改 `2*` 行高；下方固定工具条：`工具[画笔/马克笔/橡皮] 语义[要修改/要保留] 画笔颜色 清除` |
| 6 | 「加编辑图片按钮，可画笔圈画或半透明涂色，指示保留或修改区域」 | 原只有「区域标注」开关 | 按钮改名 **「编辑图片」**；`RegionCanvas` 新增 `RegionTool`（画笔/马克笔/橡皮）与 `RegionIntent`（要改=红 / 保留=绿）；橡皮按命中判定擦除；马克笔更透明更宽 |
| 7 | 「提示词历史显示不全，改为悬停显示详细」 | `MaxHeight` 截断且无 tooltip | 每项加 `ToolTip.Tip="{Binding}"`；标题注明「悬停看全文」 |
| 8 | 「润色功能无作用 → 做成按钮，弹浮窗 4 选 1，之前检查提示词为空」 | `PolishCandidates` **没有任何 UI 消费**，点了看不到变化 | 重做：① 前置检查空提示词（不发请求）；② 未配置润色则提示引导；③ 拿到候选后**弹出浮窗**（正文大字 + `1/4` 计数 + ←→ 翻页 + 重新生成 + 采用这条 + 取消）；④ 采用后写回输入框 |
| 9 | （实测新增发现）启动后预览区空白 | 构造函数未触发首帧预览 | 启动自动 `ShowPreviewAsync(当前图)` |
| 10 | （实测新增发现）编辑模式下看不到底图 | `Image IsVisible="{Binding !RegionMode}"` 把底图藏了起来 | 改为**画布叠加在底图之上**（底图始终可见，否则无法圈画） |

### 架构约束遵守

缩略图**没有**加在 `ImgHub.Core.Models.Item` 上 —— Core 层是平台无关层，
不得引用 Avalonia。改为 App 层新增 `HistoryRow` 包装类型承载 `Bitmap`。
（首版误加到 Item 上导致编译失败，已纠正。）

### 验证

| 项 | 结果 |
|---|---|
| Core 测试 | **55/55** ✅ |
| 集成测试 | **37/37** ✅（本轮 +9） |
| GUI 实测 | ✅ 累计 `APIMart 累计 $0.1635` / `总累计 $0.1472`（与 state.json 0.147186 吻合）；✅ 启动自动预览；✅ 历史缩略图；✅ 编辑模式下底图可见且可画笔画 |

---

## 四·九、全量代码 Review（2026-09-20 晚）

对 3,400+ 行 C#（Core/App/Controls/Services）做控制流、逻辑、函数调用、
并发与线程安全的全量排查。

### 已确认正确（无需改动）

- HTTP 客户端单例复用（`App` 组合根创建一次，全应用共享连接池）
- APIMart 多任务并发轮询 `Task.WhenAll`（总耗时≈最慢任务）+ `ConfigureAwait(false)` 全程一致
- 上传缓存 FIFO 淘汰（防内存泄漏）
- `ErrorHints` 余额判断先于权限（E3 铁律）
- `SafeFilename` 三道防线、`Placeholder` CRC32 确定性
- Core 层零 Avalonia 依赖（架构红线保持）
- 无 TODO/FIXME/NotImplemented 残留

### 发现并已修复（本轮）

| # | 级别 | 问题 | 修复 |
|---|---|---|---|
| 1 | 🔴 P0 | `EditWithRegionsAsync` 无 `Busy` 守卫 → 双击两次并发 API 调用**重复扣费** | 入口加 `if (Busy) return;` |
| 2 | 🔴 P0 | `RunGenerationAsync` 只查 `Busy` 不查 `CanRun` → 未配置时也能发起（之前靠按钮 IsEnabled，但代码层不设防） | 入口加 `CanRun` 检查 |
| 3 | 🔴 P0 | `EditWithRegionsAsync` 无空标注守卫 → 用户没画也发起编辑（底图被当"已标注"图） | `RegionCount == 0` 直接提示返回 |
| 4 | 🔴 P0 | `SaveImageToHome` **硬编码 `.png`**：用户选 jpeg/webp 输出也落 .png 文件（扩展名与真实编码不符） | 按输出格式定扩展名；`SniffMediaType` 检测不符时 SkiaSharp 转码 |
| 5 | 🟡 P1 | 3 处 `async void` 事件处理器异常直接**崩进程** | 拆出 Core Async 方法，事件壳 try/catch 全兜 |
| 6 | 🟡 P1 | `PostJsonAsync` 2xx 但 body 非法 JSON → 掉进 `catch(Exception)` 被**当作网络错误重试 3 次**（无意义且掩盖问题） | JSON 解析单独 catch，抛**非重试** ApiError |
| 7 | 🟡 P1 | `FlushConfig`（UI 线程）与节流 Timer 回调（线程池）可能**并发写同一文件** → Windows `File.Move` 抛 IOException | `Session._writeLock` 全局串行化所有写盘 |
| 8 | 🟡 P2 | 缩略图条顺序与生成顺序相反（`Push` 是 Insert(0)） | `Take(done).Reverse()` |
| 9 | 🟡 P0 | `EditWithRegionsAsync` 未检查 `RegionCount==0`（`ExportComposite` 会把无标注底图当结果返回） | `RegionCanvas.ExportComposite` 无笔画返回 null + VM 层 `RegionCount==0` 双重守卫 |
| 10 | 🟢 P2 | `RegionCanvas.OnPointerMoved` 无节流：快速拖动 Points 无上限增长 → 内存/SKPath 膨胀 | 距离阈值降采样（<0.4% 较短边跳过） |
| 11 | 🟢 P2 | `ProviderSelection` setter 条件恒真（按 Label 查找后 `found.Label == value` 恒真）→ 重复选择同项也绕一圈 | 注释说明；行为无害（幂等），保留 |

### 已知限制（有意/后续）

| 项 | 说明 |
|---|---|
| Android `SaveToGallery` 走 SaveFilePicker | 符合 SAF 语义；"自动存相册"需 MediaStore API，列为后续增强 |
| 润色/生成共用 `Busy` | 单任务队列语义（生成时不能润色）—— 符合"防重复扣费"目标，保留 |
| `HistoryRow.Thumb` 每次重建列表重解码 | 列表 ≤200 条、80px 宽，实测无卡顿；如需优化可加 LRU（暂不做） |

### 验证

修复后全量测试 **92/92 通过**（Core 55 + 集成 37），无回归。

---

## 四·十、第四轮修复（2026-09-20 晚，按图 8 标注 8 项）

| # | 用户标注 | 修复 |
|---|---|---|
| 1 | 「画笔要用半透明马克笔；点击是半透明的，一拖动就变不透明；把马克笔改为**蒙版类型，重复点击颜色不叠加**」 | `RegionCanvas` 全面重写：**导出时先把自由笔迹以不透明画到 mask 位图 → mask 像素统一替换为「原色+110 alpha」→ 整体贴回底图**。同区域重复涂色只保留一份不透明像素 → 颜色**不会加深**（真正的非叠加蒙版）。预览侧因 Avalonia 顺序混合，叠加是视觉近似，导出结果才是准的 |
| 2 | 「生成参数下方的价格估算不见了」 | 构造函数用**字段赋值**不走 setter → `UpdateEstimate()` 从未触发 → 空白。修复：构造末尾显式 `UpdateEstimate()` + `RefreshConfigured()` |
| 3 | 「画笔颜色点击后弹调色板浮窗以便自定义颜色」 | 新增 `PaletteOpen` + `PaletteColors`（16 色）+ **调色板浮窗**（4×4 色块网格，点选即用）；工具条按钮改为「调色板」 |
| 4 | 「检查有没有把编辑后的图片作为要修改的图片」 | 已验证：`EditWithRegionsAsync` 把**标注合成图**作为参考图第 1 位（铁律 D1「第 1 位=主体」），并新增集成测试 `RegionEdit_UsesAnnotatedImageAsReference` 锁死 |
| 5 | 「没找到导入参考图的功能和区域」 | 左栏新增**「参考图（编辑时附带）」区**：`＋ 添加`按钮（多选 SAF）→ 落盘 + `RefImages` 列表（缩略图 + ✕ 移除）；「用标注编辑」时自动附带全部参考图（主体之后，≤16 张上限） |
| 6 | 「画笔加入更多颜色」 | 调色板 16 色（红橙黄绿青蓝紫粉棕 + 6 灰阶白黑） |
| 7 | 「加入方框、圆圈等编辑工具」 | `RegionTool` 扩为 **马克笔/画笔/方框/圆圈/橡皮** 五种；方框/圆圈拖拽成形（`OutlineShape`），误点（起止重合）自动丢弃；工具下拉对齐枚举顺序 |
| 8 | 「润色的分割偶尔会有问题」（截图：候选 2 里出现 `---DIDIER---`） | LLM 把 `---DIVIDER---` 拼错 → `Split` 漏分 → 两条黏成一条。新增 **`NormalizeDividers()`**：正则匹配 `---Xxx---` 变体，与 DIVIDER 相似度 ≥60% 即归一化（不误伤 `---END---`）。回归测试待补 |

### 工具/语义联动

- 语义下拉切换 → 笔刷色自动变（红 `#FF3B30`=改 / 绿 `#32D74B`=保留），并通知 `CanvasIntent`
- 工具下拉切换 → 通知 `CanvasTool`
- XAML 直连 `CanvasTool`/`CanvasIntent`（去掉 code-behind 手工同步）
- 橡皮：按下/拖动持续按命中擦除，预览显示虚线圈光标

### 验证

| 项 | 结果 |
|---|---|
| Core | 55/55 ✅ |
| 集成 | **37/37** ✅（工具枚举断言已对齐新枚举） |

---

## 四·十一、桌面版单文件 / Native AOT 打包实测（2026-09-20 晚）

**三级方案全部实测通过**（本工程 Avalonia 12.1.2 / net10.0）：

| 级别 | 配置 | exe 体积 | 启动 | 运行 |
|---|---|---|---|---|
| L1 | 自包含单文件 | 99.0 MB | 正常 | 正常 |
| **L2** | + `PublishTrimmed` (`TrimMode=partial`) | **41.0 MB** (-59%) | 正常 | 正常 |
| **L3** | **Native AOT** (`PublishAot=true`) | **22.9 MB** (-77%) | 更快 | 正常（截图验证） |

> L1 首测 99 MB 是因为 **pdb 默认生成**（exe 99MB 是真的，目录里另有 100MB pdb）。
> 关键属性：`-p:DebugType=none -p:DebugSymbols=false`。
> L3 目录里还须带 3 个原生 DLL：`av_libglesv2.dll`、`libHarfBuzzSharp.dll`、`libSkiaSharp.dll`。

### 结论与推荐

| 场景 | 推荐 |
|---|---|
| 追求最小体积 + 最快启动 | **L3 Native AOT**（22.9 MB + 3 DLL） |
| 稳妥优先（AOT 风险规避） | **L2 单文件+裁剪**（41 MB 单个 exe） |
| 调试/开发 | 普通 publish（framework 依赖，~1 MB 自身 + 系统装运行时） |

**风险说明（来自官方文档与 issue）**：
- 裁剪警告集中在 `Session`/`HttpJsonClient` 的 System.Text.Json 反射序列化 ——
  功能已实测正常；如需彻底消除，可迁移到 **JsonSerializer source generation**（后续增强）。
- 社区报告 `TrimMode=link`（激进链接）可能运行时不一致崩溃 —— 本次用 `partial` 模式，实测稳定。
- Avalonia 官方 Native AOT 要求：编译绑定（✅ 已默认开启）、避免动态 XAML（✅ 未使用）、
  资源走 `AvaloniaResource`（✅）、避免反射式服务定位（✅ 手工组合根）。

### 本工程已具备的 AOT 友好条件

| 条件 | 状态 |
|---|---|
| `AvaloniaUseCompiledBindingsByDefault=true` | ✅（App.csproj） |
| 全部 XAML 编译期绑定 + `x:DataType` | ✅ |
| 无动态 XAML / XamlReader.Load | ✅ |
| 无反射式 DI/服务定位 | ✅（手工组合根） |
| 资源 `AvaloniaResource` | ✅ |

---

## 四·十二、应用图标 + App 层重建（2026-09-21）

### 应用图标（用户提供：蓝色像素机器人）

| 用途 | 方案 |
|---|---|
| **exe 文件图标**（资源管理器/任务栏） | 多尺寸 ICO（16/24/32/48/64/128/256，PNG-in-ICO），由 `make-icon.ps1` 从 `app-icon.png` 生成；csproj 设 `<ApplicationIcon>app.ico</ApplicationIcon>` |
| **窗口标题栏图标** | `MainWindow.axaml` 用 `Icon="avares://ImgHub.App/Assets/app-icon.png"` |

> ⚠️ 踩坑记录：窗口 Icon **不能用 PNG-in-ICO** —— Avalonia 的 `IconTypeConverter`
> 走 `Bitmap(Stream)` 单帧解码，遇到 PNG-in-ICO 会抛
> `ArgumentException: Unable to load bitmap from provided data` 而**直接崩溃**。
> 正确做法：exe 用 ICO，窗口用 PNG。两者分开。

### App 层重建（意外删除后）

一次清理操作误删了 `src/ImgHub.App` 整目录（含 `MainViewModel.cs` 899 行、
`MainView.axaml`、`Controls/RegionCanvas.cs` 等）。恢复方式：

1. `ImgHub.Core` + 两个测试工程**未受影响**（55 + 37 项测试仍全绿）——
   这正是「Core 层零 UI 依赖」架构红利的体现；
2. 依据**集成测试里锁死的 37 项契约**逐条反推重建 App 层：
   - `MainViewModel`：全部属性/命令/行为按测试断言还原
   - `RegionCanvas`：五工具（马克笔/画笔/方框/圆圈/橡皮）+ 蒙版式非叠加导出
   - `MainView.axaml`：三栏 + 设置/调色板/润色浮窗
3. 重建后 **92/92 测试全部通过**，GUI 实测正常（截图为证）。

### 验证

| 项 | 结果 |
|---|---|
| Core 测试 | 55/55 ✅ |
| 集成测试 | 37/37 ✅ |
| GUI 实测 | ✅ 图标生效、三栏完整、预估价格显示、引导条正常 |
| Native AOT 发布 | ✅ 23.31 MB exe + 3 原生 DLL，运行正常 |

---

## 四·十三、文档体系补齐 + Android 三项修复（2026-09-21）

### 文档体系（本轮新建 12 份）

| 文档 | 行数 | 内容 |
|---|---|---|
| `README.md`（根） | 90 | 项目总览 + 快速开始 |
| `docs/README.md` | 84 | 文档索引 |
| `docs/HANDOVER.md` | 155 | **新接手先读**：跑起来/改功能去哪/排错 |
| `docs/ARCHITECTURE.md` | 198 | 四层结构、数据流、双 provider 差异、扩展点 |
| `docs/CONSTRAINTS.md` | 292 | **写代码前必读**：A–G 七类硬约束 + 常见陷阱速查 |
| `docs/DELIVERY.md` | 181 | 三级打包方案 + Android 前置 + 检查清单 |
| `src/ImgHub.Core/README.md` | 43 | 业务层职责 + 关键不变量 |
| `src/ImgHub.App/README.md` | 55 | UI 层结构与设计决策 |
| `src/ImgHub.Desktop/README.md` | 44 | 桌面 head + 图标 + 打包 |
| `src/ImgHub.Android/README.md` | 56 | Android head + mipmap/自适应图标 |
| `tests/README.md` | 43 | 测试哲学 + 关键用例 |
| `tools/make-android-icons.ps1` | — | 图标生成脚本 |

### Android 修复 1：图标同步

**问题**：旧版用单个 `drawable/Icon.png`（8 KB），各密度设备拉伸模糊/显示默认图标。

**修复**：生成**多密度 mipmap + 自适应图标**：
```
mipmap-mdpi/ic_launcher.png            48×48   (+ round)
mipmap-hdpi/ic_launcher.png            72×72   (+ round)
mipmap-xhdpi/ic_launcher.png           96×96   (+ round)
mipmap-xxhdpi/ic_launcher.png          144×144 (+ round)
mipmap-xxxhdpi/ic_launcher.png         192×192 (+ round + foreground 432×432)
mipmap-anydpi-v26/ic_launcher.xml      自适应图标
values/colors.xml                      ic_launcher_background = #1B6FE8
```
清单改为 `android:icon="@mipmap/ic_launcher"` + `android:roundIcon=...`。

### Android 修复 2：中文显示方框

**根因**：Avalonia Android CJK 回归（#19868/#20195）—— 系统字体名不可靠，
`Microsoft YaHei`/`Noto Sans CJK SC` 在 Android 上都不存在 → 渲染成方框。

**修复**：**内嵌 CJK 字体**（这是唯一可靠方案）：
```
src/ImgHub.App/Assets/Fonts/NotoSansSC.ttf   16.95 MB（从系统 NotoSansSC-VF.ttf 复制）
```
`App.axaml` 字体链改为**内嵌字体优先**：
```xml
<FontFamily x:Key="AppFont">avares://ImgHub.App/Assets/Fonts/NotoSansSC.ttf#Noto Sans SC,
    Microsoft YaHei UI, Microsoft YaHei, Noto Sans CJK SC, Segoe UI, sans-serif</FontFamily>
```

**桌面实测**：中文全部正常渲染（无方框）。

> 代价：字体增加 ~17 MB（APK 从 42 MB → 预计 ~59 MB）。
> 后续优化：用 fontTools 子集化（只留常用 3500 汉字）可压到 ~3 MB。

### Android 修复 3：横竖屏布局

**问题**：三栏固定 `1.02*,1.18*,1*`，手机竖屏挤压变形（用户图 3）。

**修复**：响应式双布局 —— 按宽度 900px 断点切换：

| 布局 | 触发 | 结构 |
|---|---|---|
| **宽屏** | `IsWideLayout=true`（≥900px） | 三栏并排（参数 \| 预览 \| 历史） |
| **窄屏** | `IsWideLayout=false`（手机竖屏） | **纵向堆叠**：①预览+工具 ②提示词+操作 ③参数 ④历史+消息 |

**功能分区重排**（竖屏）：把**最常用的预览放最上方**，操作按钮紧随，
参数折叠到底部（可滚动）。顶栏在窄屏隐藏模型名与累计金额，避免挤出。

实现：`MainView.axaml.cs` 的 `OnSizeChanged` 按宽度设 `Vm.IsWideLayout`；
两套布局用 `IsVisible` 切换；窄屏有独立的 `RegionCanvas` 实例（事件都接）。

**实测**：446×859 手机竖屏截图确认 —— 纵向堆叠正确、中文正常、顶栏不挤出。

### 验证

| 项 | 结果 |
|---|---|
| Core 测试 | 55/55 ✅ |
| 集成测试 | 37/37 ✅ |
| 中文显示（桌面实测） | ✅ 无方框 |
| 竖屏布局（446×859 实测） | ✅ 纵向堆叠 |
| Android 构建 | 见下 |

---

## 四·十四、仓库标准化 + 全量 Review 与鲁棒性加固（2026-09-21）

### 仓库标准化（面向开源）

**目录重组**（C# 提到根，Python 归 legacy）：

```
imghub/                          原 avalonia/ 的内容提到根目录
├── src/ImgHub.Core|App|Desktop|Android
├── tests/ImgHub.Core.Tests|Integration.Tests
├── legacy/                        原 mobile/（Python + curses 版）
├── docs/                          （含原 avalonia/docs）
├── tools/make-android-icons.ps1
├── build.ps1 / make-icon.ps1 / Directory.Build.props / ImgHub.slnx
├── README.md / LICENSE / NOTICE.md / .gitignore
└── release/                       （.gitignore 排除，走 GitHub Releases）
```

**新增文件**：
| 文件 | 说明 |
|---|---|
| `LICENSE` | GPL-3.0 全文 |
| `NOTICE.md` | **第三方许可声明** —— 重点说明内嵌字体 Noto Sans SC 是 **OFL-1.1**（与 GPL 兼容，需保留声明） |
| `.gitignore` | 排除 bin/obj、release、运行时 key/config、临时文件 |

**git 规范化**：`core.autocrlf=false`（避免 Windows 换行污染），首个提交 **123 个文件**（源码 + 文档 + 测试，无产物）。

### 全量 Review 发现并修复

| # | 级别 | 问题 | 修复 |
|---|---|---|---|
| 1 | 🔴 | **宽/窄屏各持一个 `RegionCanvas`，切换布局时标注丢失**（用户画好标注 → 转屏 → 全没了） | `RegionCanvas.ExportShapes/ImportShapes` 快照传输；`OnSizeChanged` 切换前把数据搬到另一实例 |
| 2 | 🔴 | **`RefImages` 无生命周期** —— 导入一次参考图后，**每次编辑都误带上**（污染结果） | 改为**一次性**（编辑后自动清空）；新增 `KeepRefImages` 开关 + XAML「保留」勾选（默认不勾） |
| 3 | 🟡 | **单张图片下载失败静默丢弃** —— 4 张成功 1 张失败，用户看到 3 张却不知情 | `PollOutcome.DownloadFailures` 计数 → 汇总后在进度里明确提示「⚠ 部分产出缺失：N 个任务失败，M 张下载失败」+ 失败原因 |
| 4 | ✅ | **`TotalCost` 与 `Items` 求和可能偏差**（Undo 减的是均摊值，TotalCost 是 API 实际值） | 已彻底统一：`TotalCost` 改为**计算属性**（从 `Items` 求和），偏差在结构上不可能出现。`Session.ReconcileCost()`（空方法）已于 P0-7 删除 —— 此前文档误称它在 `RefreshHistory` 时被调用 |
| 5 | 🟡 | **生成失败后 `BatchResults` 不清理** → 旧缩略图条残留，误导用户 | 生成开始前 + 失败分支都清空 |
| 6 | 🟢 | 全部任务失败时错误信息不含原因 | 汇总 `failureReasons` 到异常消息 |

### 死代码 / 假实现检查

| 检查项 | 结果 |
|---|---|
| TODO / FIXME / HACK / NotImplemented | **0 处** |
| 空 `catch` | 全部为**有意的容错**（存储写失败不阻断、缩略图解码失败返回 null），且均有注释说明 |
| 未使用的公开成员 | 无（新增成员均已被调用；`ReconcileCost` 空方法已于 P0-7 删除） |
| 硬编码凭据 | **无**（key 全由用户配置） |
| Core 引用 UI 框架 | **无**（架构红线保持） |

### 验证

| 项 | 结果 |
|---|---|
| Core 测试 | **55/55** ✅ |
| 集成测试 | **42/42** ✅（本轮 +5 项回归） |
| 合计 | **97/97** ✅ |
| 桌面构建 | ✅ |
| Android APK | 见下（含图标 + 字体 + 响应式 + 本轮修复） |

**新增回归测试**：
- `RefImages_ClearedAfterUse_ByDefault` / `RefImages_KeptWhenOptedIn`
- `BatchResults_ClearedOnFailedGenerate`
- `TotalCost_ReconcilesWithItems`
- `RegionCanvas_ShapeTransferPreservesData`

---

## 四·十五、第六轮修复（2026-09-21，按用户实测反馈 7 项）

### #1 字体模糊 —— 根因是可变字体的默认字重

**现象**：内嵌 NotoSansSC 后中文能显示，但**笔画极细、发虚模糊**。

**根因（实测定位）**：`NotoSansSC-VF.ttf` 是**可变字体**，
其 `wght` 轴的 **`defaultValue` = 100（Thin 极细）**！
Avalonia 按默认值渲染 → 笔画细弱发虚。

```python
# 验证输出的轴信息
轴: {'wght': 100.0}   ← 默认就是 100，不是 400
```

**修复**：用 fontTools 把可变字体**实例化到 wght=400（Regular）**并子集化：

```python
font = instancer.instantiateVariableFont(font, {"wght": 400}, updateFontNames=True)
subsetter.populate(text=常用汉字+拉丁+标点)   # 21483 字符
```

产出 `NotoSansSC-Regular.ttf`：**静态**（无 fvar/gvar/HVAR）、**7.15 MB**（原 16.95 MB）。
脚本归档为 `tools/make-static-font.py`（可复现）。

**实测**：中文笔画饱满清晰，模糊消失。

### #2 自绘标题栏（仿 Reasonix）

```xml
<Window ExtendClientAreaToDecorationsHint="True"
        ExtendClientAreaTitleBarHeightHint="40"
        SystemDecorations="None" Background="Transparent">
  <Border CornerRadius="10" ...>           <!-- 圆角 -->
    <Grid RowDefinitions="40,*">
      <Border Name="TitleBar" ...>          <!-- 自绘标题栏 -->
        <!-- 左：图标 + 标题 + 状态；中：拖拽区；右：最小化/最大化/关闭 -->
```

- 手动实现拖拽移动（`BeginMoveDrag`）+ 双击最大化
- 窗口按钮用 Segoe MDL2 字形，关闭按钮悬停变红
- **踩坑**：Avalonia 12 **移除了 `ExtendClientAreaChromeHints`**（编译报 AVLN2000），
  只需 `ExtendClientAreaToDecorationsHint` + `SystemDecorations="None"`

### #3 马克笔改为「蒙版薄层」+ 导出修改蒙版

**现象**：马克笔拖动后变成不透明，且重复涂抹颜色加深。

**修复**：
1. **蒙版式薄层**：所有标记先画到**不透明 mask**，再统一替换为
   「原色 + 固定 alpha(110)」贴回 → **重复涂抹不加深**，始终薄薄一层
2. **新增 `ExportMask()`**：导出「修改区域蒙版」（**白=要改，黑=保留**），
   保存为 `mask_*.png` 供支持 inpainting/mask 的模型使用
3. `LayerAlpha` 可绑定调节（默认 110）

**传给模型的内容**：
- 参考图第 1 位 = **标注合成图**（原图 + 薄层标记）
- 提示词追加说明「图中高亮区域即需修改的部分」
- 同时保存 mask 文件（`AnnotatedMaskPath`），为将来接 mask 参数预留

### #4 粗细不可调 —— 缺 UI 控件

**根因**：`BrushSize` 有绑定但 **XAML 里没有对应控件**。

**修复**：工具栏加 **Slider**（2–80px）+ 实时显示 `24px`；
`BrushSizeText` 属性随值变化通知。

### #5 去掉「语义」功能

移除 `IntentNames` / `IntentIndex` / `CanvasIntent` 及全部 XAML 下拉与绑定。
统一用当前画笔色表示"这里要改"。

### #6 标注跟随图片 + 编辑历史

**现象**：切换历史图片时，上一个图的标注不消失（串图）。

**修复**：`MainViewModel` 新增**按图缓存**：
```csharp
private readonly Dictionary<string, RegionSnapshot> _regionCache;
public void SaveRegionsFor(string? imagePath, RegionSnapshot snap);
public RegionSnapshot? GetRegionsFor(string? imagePath);
```
`MainView` 订阅 `PreviewPath` 变化 → **切换前保存旧图标注，切换后恢复新图标注**。
同一张图来回切换，标注不丢（即"编辑历史"）。

### #7 画布式缩放（标记跟随）

`RegionCanvas` 新增视图变换：
- **滚轮缩放**（0.2×–8×）——**以鼠标所在点为锚点**（v0.5.32 修，见下）
- **右键 / 中键 / 空格+左键平移**
- **快捷键 `0` 复位**；工具栏另有 `−` `＋` `复位` 按钮
- 坐标**归一化存储** + 渲染时统一变换 → **标记天然绑定在图片上，随缩放平移同步**

#### v0.5.32 修正（用户报的 4 项）

| 问题 | 根因 | 修法 |
|---|---|---|
| 缩放不以鼠标点为原点 | 滚轮调用 `ZoomBy(factor)` 无锚点，恒绕控件中心 | 新增 `ZoomBy(factor, anchor)`，滚轮传 `e.GetPosition(this)`；公式 `pan' = pan·r + d·(1-r)`（`r = z'/z`） |
| **滚轮缩放"跑位"（拖动却正常）** | ⚠️ **两者坐标系不同**：`Stretch="Uniform"` 的 `Image` 在 Avalonia 里 **`Bounds` 会自动收缩为「图片内容矩形」**（实测：400×400 图放 600×300 容器 → `Image.Bounds = (150,0,300,300)`），而 `RegionCanvas` 默认铺满容器 `(0,0,600,300)`。**平移**时两者位移量恰好相同 → 看着正常；**缩放**时范围不同被同比例放大 → 分离 | ⭐ **根治**：新增 `RegionCanvas.FollowBoundsOf(target)` —— 画布**订阅**底图 `Bounds` 变化，自动跟随尺寸；图片层套用**同一个** `ViewMatrix`。 |
| **（上一版为何没修好）对齐时机漏了** | 旧实现靠若干时机**手工**调 `AlignCanvasToImage`（DataContext / PreviewPath / PreviewImage / resize / 布局切换 / 进出编辑模式）。而 `Image.Bounds` 要等**位图异步解码完成**才变成内容尺寸 —— 漏掉任一时机就偏，这正是"改了不生效"的根源。 | 改为**订阅 `BoundsProperty`**：位图就绪、resize、布局切换全部自动触发，时机问题从根上消失。防护见 `CanvasFollowsImageTests`（7 条，含"图片解码就绪后画布自动跟随"）。 |
| **（v0.5.33 新回归）工具与滚轮全部失效** | ⚠️ 上一版把 XAML 的 `Image`/`RegionCanvas` 改成 `HorizontalAlignment="Center"` —— 而 **Center 下未设尺寸的控件 `DesiredSize` 为 0** → 画布变成 0×0 → **没有任何命中区域**（工具画不上、滚轮不响应）。实测：`Center`+无尺寸 → `Bounds=(300,150,0,0)`；`Stretch`+无尺寸 → `(0,0,600,300)` | 改回 `Stretch`（对齐没跑时至少铺满容器可交互）+ 跟随逻辑在拿不到 `Image.Bounds` 时**显式清掉尺寸**退回 Stretch |
| **（附带发现）点击处与落笔处偏移** | `BuildMatrix` 把 `pan` 放在**缩放之内**（实际屏幕平移 = `-pan·z`），而 `ToNormalized` 与拖拽逻辑都按**屏幕单位 pan** 写 → 三者不互逆 | 统一为 `screen = (local − c/2)·z + c/2 + pan`，与 `ScreenToImage` 严格互逆（实测修前 z=2、pan=(50,30) 时点 (300,150) 反解映回是 (150,60)） |
| **（附带发现）`RenderTransformOrigin` 语义** | 默认值是 **`50%,50%`（Center）** 而非 `(0,0)`；Avalonia 会组合 `T(o)·M·T(-o)`。**但注意**：平移不受它影响（`T(-c)·T(pan)·T(c) = T(pan)`），只有缩放受影响 | `SyncPreviewTransform` 显式设 `origin = RelativePoint.TopLeft`，保证与 `RegionCanvas` 的裸矩阵语义一致 |
| **（附带发现）位图异步就绪后未重新对齐** | `Image.Bounds` 要等位图**解码完成**才变成内容尺寸；只监听 `PreviewPath` 会在"图还没加载完"时对齐到 0 → 等于没对齐（这正是"改了不生效"的重要原因） | 监听 `PreviewImage` 变化 + `RefreshCanvasAlignmentDeferred`（Bounds 仍为 0 时最多重试 5 帧） |
| 取消编辑后不复位 | 退出编辑只切 `RegionMode`，缩放/标注/蒙版残留 | 新增 `RegionCanvas.ResetView()` + `ClearRegions()` + `MainViewModel.ClearAnnotationState()`，由 `OnVmPropertyChanged` 监听 `RegionMode` 统一触发（覆盖"编辑成功后自动退出"路径） |
| 需求② 框/圈改实心 | 此前矩形/椭圆只描边；作为 mask 送模型时"一圈线"= 没指定区域 | `OutlineShape.DrawPreview` 与 `RenderToMask` 都改为**填充**（预览与导出一致） |
| 需求③ 蒙版说明入口 | 用户不知道蒙版怎么用、传给谁、提示词怎么写 | 工具栏加「蒙版说明」按钮 + `MainViewModel.BuildMaskHelpLines(provider)` 纯函数生成文案（**按 provider 动态**，避免文案与实际行为不符） |
| **「蒙版说明」被遮挡** | 标注工具条是**固定高度**（`AnnotationToolbarHeightWide=92`）只放得下 2 行；按钮加进去后第 1 行超宽 → 换行到第 3 行 → **被裁**（用户报"被遮挡"） | 移到**底栏**（空间独立、不受工具条高度约束）。`MainViewLayoutContractTests` 钉住"必须在底栏内" |
| **「系统看图器/保存到相册」被遮挡** | 同上：它们原在中栏标注工具条下方（Row4），工具条换行后互相压住 | 用户要求：**移到底栏左侧**。中栏随之删掉 Row4（行数 5→4）；窄屏那份也一并移除（底栏对宽窄都显示） |
| 底栏原本整条 `IsVisible="False"` | 历史事故：隐藏底栏里的按钮全部不可达（AGENTS.md §3.3/§6） | **启用底栏**并把常用操作放进去（看图/保存/撤回/重新预览/蒙版说明/快捷键/数据目录/设置）—— 全部可达 |
| **切换到未就绪端点就"自动取消编辑"** | 「编辑图片」按钮 `IsEnabled` 绑的是 `IsConfigured` → 切到没配 key 的端点时按钮**变灰**，用户既进不去、也退不出（看着像被自动取消）。但**标注是纯本地操作，不需要 API key** | 新增 `CanAnnotate`（= `CurrentItem is not null`）并改绑；`ToggleRegionModeCore` 守卫同步改用 `CurrentItem`（与按钮判据一致，避免"可点但被拒"） |
| **（v0.5.34）标注超出图片边界后"消失"** | 用户澄清的语义：**可绘制范围 = 图片尺寸**，**可查看范围 = 整个预览灰区**。旧实现把画布尺寸设成图片尺寸且 `ClipToBounds = true` → 缩放/平移后超出图片的标注**被裁掉**（"超出起始上界后上面部分消失"） | ① 画布 `Bounds` **铺满灰区**（`ClipToBounds = false`）+ 新增 `ImageContentRect` 作**坐标基准**（= 图片矩形）；② 图片层用 `ImageMatrix`（不含图片原点偏移）；③ 预览 Grid 再套一层 `ClipToBounds` Border，把可查看范围严格限定为灰区 |
| **（v0.5.34）图片居中时缩放"变形/移位"** | 图片在灰区里**居中**（不是从 (0,0) 起），而矩阵少了这层原点偏移 → 缩放中心跑到灰区中心 | `BuildMatrix` 末尾追加 `T(contentX, contentY)`；`ImageMatrix` **不含**该偏移（`Image` 已被布局放在那里）。关系：`ViewMatrix == ImageMatrix · T(origin)` |
| **（v0.5.35）蒙版可绘制范围超出图片** | 落笔/拖动**没有做范围检查** → 在图片外的灰区也能落笔，记下越界归一化坐标（矩形画到灰区里；送服务端也是越界无效区域） | `ToNormalizedClamped`（钳制到 [0,1]）+ `IsInsideImage`（图片外按下不落笔）；`ImportShapes` 也钳制（旧快照可能含越界坐标） |

#### v0.5.36 修正（用户 8 项需求）

| 需求 | 根因/说明 | 修法 |
|---|---|---|
| ① 底栏精简 | 底栏的「撤回/重新预览/设置」与顶栏/左栏重复 | 删掉这三个；「未配置」提示文案改指**右上角**（设置按钮在顶栏）；版本号移到**底栏最右** |
| ② 标注编辑历史 | 标错一笔只能「清除」全部重画 | `RegionCanvas` 加**撤销/重做栈**（按动作顺序，上限 100 步）：落笔成形状/擦除一次/清除/载入各算一个动作；UI 加「撤销/重做/图片居中」按钮（两套布局同步） |
| ③ 重叠不叠加 | `PushOpacity` 只统一整组不透明度，**组内重叠处仍会 alpha 累加** | 改为**离屏合成**（与 `ExportComposite` 同一套语义）：先把标记画到不透明临时层，再整体以固定 alpha 贴回 |
| ④ 单色蒙版 | **核实结论**：端点的官方 "Mask requirements" 只要求 **尺寸一致 + 含 Alpha**，**没有**"必须单色"。千问明确不支持 mask | 现有实现已输出 RGB=0 的单色蒙版（消除"按亮度解读"的歧义）；新增 `RegionMaskFormatTests`（4 条）钉住尺寸/Alpha/单色/alpha=0=要改；不支持蒙版的端点走 `SupportsMaskChannel` 降级为合成图 |
| ⑤ 去掉画笔 | 「画笔」与「马克笔」走**完全相同**的绘制路径（仅默认粗细不同），属重复功能 | 从 `RegionTool` 枚举与 `ToolNames` 移除；索引左移（旧 4=橡皮 → 新 3），`CanvasTool` 用 `Clamp(_toolIndex, 0, 3)` 保证旧值安全落位 |
| ⑥ 方框/圆圈隐藏粗细条 | 它们是**区域**语义（填充整块），粗细无意义 | 新增 `ToolUsesBrushSize`（只对马克笔/橡皮为真），粗细组按它显隐（两套布局同步） |
| ⑦ 橡皮粗细不一致 | 指示圈用 `BrushSize*1.2`（屏幕像素），实际擦除用 `NormSize()*2.5`（归一化）→ **基准不同**，擦除范围明显大于圆圈 | 统一到 `EraserNormRadius()` 单一真源；指示圈与擦除判定从它派生 |
| ⑧ 马克笔加指示圈 | 马克笔没有落笔预览 | 与橡皮同样的指示圈（虚线样式区分），半径 = 笔迹宽度的一半；两者都套同一个变换矩阵以保证与判定一致 |

> 附带修掉：`AnnotationToolbarHeightWide` 被提示词输入框**错误复用**（改工具条高度会连带拉长左栏输入框）→ 拆出独立的 `PromptBoxHeightWide`。
> 回归防护见 `RegionCanvasHistoryTests`（9 条）、`RegionCanvasOverlapTests`（2 条）、`RegionMaskFormatTests`（4 条）
> 与 `WorkbenchFlowTests` 里的工具映射/粗细显隐断言。
| 工具与下拉框未对齐 | 下拉 `Height=34` + 文字默认基线；且高度常量两处硬编码 | 行内元素统一 `VerticalAlignment="Center"`，下拉高度走 `UiMetrics.FieldHeightCompact` 单一真源 |
| （核查）编辑工具是否真传蒙版 | **APIMart / OpenAI 确实传**；OpenRouter/千问/即梦文档无 mask 字段，按设计降级为「原图 + 标注合成图」 | 抽出真源 `Catalog.SupportsMaskChannel`；修掉 App 层曾**只认 APIMart** 的漏判（OpenAI 蒙版被无谓丢弃，且提示文案与行为不符） |

> ⚠️ **走弯路的记录（别再重复）**：这个问题先后修过三次 ——
> ① 归因到 `RenderTransformOrigin` 默认 50%（它只影响缩放，不是主因）；
> ② 在矩阵里补「内容矩形偏移」（要同时维护两套坐标系 + 两个矩阵，越改越复杂）；
> ③ 才用 **`RenderTargetBitmap` 读像素** 实测出真相：两者 `Bounds` 根本不是同一个矩形。
> **教训**：这类"看代码想不出来"的坐标问题，直接渲染一帧读像素最快。

> 回归防护见 `tests/ImgHub.Integration.Tests/RegionCanvasSyncTests.cs`（7 条，含**真实渲染读像素**的端到端断言）、
> `RegionCanvasViewTests.cs`（14 条）、`RegionShapeAndHelpTests.cs`（4 条）
> 与 `tests/ImgHub.Core.Tests/MaskChannelTests.cs`（3 条）。

### 验证

| 项 | 结果 |
|---|---|
| Core 测试 | 55/55 ✅ |
| 集成测试 | **45/45** ✅（更新语义相关断言为新的工具/粗细/缓存断言） |
| 合计 | **100/100** ✅ |
| 桌面实测 | ✅ 字体清晰、自绘标题栏、工具栏（工具+粗细滑块+缩放按钮+调色板） |
| 死代码检查 | ✅ 无残留（`IntentIndex`/`CanvasIntent` 已彻底移除） |

---

## 四·十六、文档与指引校正 + 需求 #6 补实（后续轮次）
用户要求「用 codegraph 索引项目并理解，完善所有文档和指引」。本轮先建索引、通读代码，
再逐条核对文档与**实际代码**的差异，并顺带补上一个被漏做的需求。

### codegraph 索引

```
Files: 68   Nodes: 1,974   Edges: 5,723
csharp 36 · python 27 · xml 5
[OK] Index is up to date
```

用 `codegraph status / files / context / node` 定位核心链路，
与人工阅读交叉印证（如「生成 → ImageApi → LandResults → Session」的数据流）。

### 🔴 发现并修复的真实缺陷：需求 #6 从未落实

| 项 | 事实 |
|---|---|
| commit 06f1dd4 记录 | 「#6 设置按钮移到右上角（深色按钮旁，齿轮图标）」 |
| 实际代码 | 顶栏（`MainView.axaml` 第 38 行深色按钮旁）**没有**设置按钮 |
| 唯一入口 | 第 525 行，位于**整体隐藏的底栏**（`IsVisible="False"`） |
| 后果 | **设置浮层在 UI 上完全无法打开** —— 用户只能手改 `config.json` |
| 为何测试没抓到 | 集成测试直接调 `OpenSettingsCommand`（`WorkbenchFlowTests`），不经过 XAML |

**修复**：在顶栏「深色」按钮旁补回设置按钮。两个取舍：

1. 用**普通 Unicode ⚙**（`&#x2699;`）而非 Segoe MDL2 字形 —— Android 无 MDL2 字体，
   用它会渲染成方框；
2. **窄屏（<900px）隐藏文字标签与「深色」按钮** —— 顶栏还要容纳 provider 下拉（132px）
   与设置按钮，446px 宽下会溢出约 20px；深色切换在**设置浮层里有等价开关**
   （「界面选项 → 深色主题」），功能不丢失。

> 这是「两套布局」约束（D4b）的实例：同一元素在不同宽度下需要不同的可见性。

### 🔴 第二个同源缺陷：需求 #14 把「导入」按钮删掉了却没加回

| 项 | 事实 |
|---|---|
| commit 06f1dd4 记录 | 「#14 导入按钮移到历史 / 累计面板标题行后方」 |
| 实际代码 | `OnImportClick` 在 `MainView.axaml` 里**已无任何引用**（成了死代码） |
| 后果 | **用户无法导入图片**（导入是标注编辑的前置功能） |
| 为何测试没抓到 | `WorkbenchFlowTests.ImportImage_AddsAsImported` 直接调 `_vm.ImportImageAsync`，不经过 XAML |

**修复**：在宽屏与窄屏两处的「历史 / 累计」标题行各补一个「导入」按钮
（`Click="OnImportClick"`），与 #6 是同一 commit 里两个同类的"删了没加回"问题。

> 这两个缺陷印证了同一个教训：**`Command`/方法的单元测试通过，不代表 UI 上能点到它**。
> 已写入 [CONSTRAINTS.md](CONSTRAINTS.md) D4c 与 [tests/README.md](../tests/README.md)。

### 文档中已过时/错误的事实（逐条校正）

| # | 位置 | 原文档 | 实际 |
|---|---|---|---|
| 1 | 根 README、docs/README、HANDOVER、tests/README、Core README | 测试 55 + 37 = 92 | **56 + 58 = 114** |
| 2 | 根 README、docs/README、DELIVERY、HANDOVER、Desktop README | `cd avalonia` / `src/ImgHub.App` 相对旧根 | 仓库已重组，命令在**仓库根**执行 |
| 3 | 根 README、DELIVERY、HANDOVER | `build.ps1 -Target desktop-aot` / `-Target desktop-single` | `build.ps1` 的 `ValidateSet` 只有 `all\|desktop\|android`，**无 AOT 目标** |
| 4 | docs/README、HANDOVER、ARCHITECTURE、CONSTRAINTS | `mobile/`（上一代 Python） | 已改名 `legacy/` |
| 5 | ARCHITECTURE §四 | 底栏有「⚙ 设置」；设置触发 = 底栏 ⚙ | 底栏已隐藏；设置入口在顶栏 |
| 6 | ARCHITECTURE §五 | RegionCanvas「两种语义：要改红/要保留绿」 | 语义已移除，统一用当前画笔色 |
| 7 | ARCHITECTURE §五 | `ExportMask` =「白=要改，黑=保留」 | 实为**黑底 + 不透明笔迹**（笔迹用当前画笔色） |
| 8 | ARCHITECTURE §一 | Desktop 43 行、Android 约 60 行 | `Program.cs` 16 行；Android 两文件合计 43 行 |
| 9 | CONSTRAINTS §D | D6 章节被插进 G 的表格中间（**表格断裂**） | 已重排，D6 归入 D 类，并补 D7 |
| 10 | CONSTRAINTS §D5 | 字体链只列系统字体名 | 实为**内嵌 `NotoSansSC-Regular.ttf` 优先** |
| 11 | CONSTRAINTS §E3 | 误删的 ViewModel「899 行」 | 现为 1099 行 |
| 12 | NOTICE.md §1 | 字体文件 `NotoSansSC.ttf`，「原样嵌入未做修改」 | 实为 `NotoSansSC-Regular.ttf`，经**实例化 wght=400 + 子集化**（7.15 MB，已无 fvar/gvar） |
| 13 | DELIVERY §一 | AOT 23.3 MB、APK ~42 MB | AOT exe **30.59 MB**、APK **51.42 MB** |
| 14 | DELIVERY §三 | 图标文件 `values/ic_launcher_background.xml` | 实为 `values/colors.xml`；另有 `ic_launcher_round` 系列与 `roundIcon` |
| 15 | DELIVERY §五 | 版本「如 5.19.0」 | 当前 5.22.0；且 `build.ps1` 里**硬编码**了版本（改版本要两处同步） |
| 16 | Android README | 「竖屏三栏挤压 待改」 | 已完成（`IsWideLayout` 断点，446×859 实测） |
| 17 | Core README | 缺 `Models/ModelStats.cs`、`Services/ModelStatsService.cs` | 已补，并新增「数据目录文件」表 |
| 18 | docs/README | 未收录 `fix-plan-v5.22.md`、`avalonia-migration-feasibility.md` | 已补 |
| 19 | HANDOVER §八 | 「Android 横竖屏布局待完善」「Android 图标待同步 mipmap」 | 均已完成；如实标注真实缺口 |
| 20 | Desktop README | `cd avalonia` 后跑 `make-icon.ps1` | `make-icon.ps1` 内部是**绝对路径**，换机器需先改 |

### 如实记录的「文档原本没写」的缺口

| 项 | 说明 |
|---|---|
| **快捷键是空壳** | `ShowShortcuts` 只往消息面板打印「Ctrl+G 生成 · Ctrl+D 编辑 …」，**全仓库没有任何 `KeyBinding`**。文档此前未说明这一点 |
| 「快捷键」按钮不可达 | 该按钮只在**隐藏底栏**里（与 #6/#14 同源）。但因其指向的快捷键是空壳，本轮**未**为它新增入口，仅记录 |
| 「数据目录」按钮不可达 | 同样只在隐藏底栏。**信息仍可达** —— 设置浮层底部已直接显示 `HomePath` |
| 「离线」入口隐藏 | 该复选框是 debug 功能（`IMGHUB_DEBUG=1`），且设置里还有一个同名项；旧文档仍教用户「勾选左栏离线」 |
| `avalonia/` 残留目录 | 重组后只剩 `bin` 缓存，无源码 |

### 新增：`AGENTS.md`（项目级 agent 指引）

仓库此前**没有任何** `AGENTS.md` / `CLAUDE.md` / `.cursorrules`。
本轮新建根目录 `AGENTS.md`：环境与命令、工作纪律（架构红线 / 两套布局 / 入口可达 /
清理禁忌 / 提交规范）、改代码去哪、硬约束索引、已知缺口、文档地图。

### 文档结构层面的改进

- `docs/ARCHITECTURE.md` 新增「三处成本数字的口径」与「价格预估 vs 成本估算」两节
  （`ProviderCost` / `TotalCost` / `UnknownCost` 易混淆，且预估机制已从死表改为按模型统计）
- `docs/CONSTRAINTS.md` 新增 **D4b（两套布局同步）**、**D4c（入口必须可达）**、
  **D7（Avalonia 12 移除 ExtendClientAreaChromeHints）**，并在 G 表补 3 条症状
- `docs/CONSTRAINTS.md` F3 回归测试表补齐（从 6 行扩到 15 行，覆盖本轮与近期新增功能），
  并加红字提醒：**命令可执行 ≠ UI 有入口**
- `tests/README.md` 加「测试覆盖不到的一类缺陷」警示
- `docs/HANDOVER.md` §三 / §五 补「两套布局」「宽窄屏各持一个画布」

### 验证

| 项 | 结果 |
|---|---|
| Core 测试 | **56/56** ✅ |
| 集成测试 | **58/58** ✅ |
| 合计 | **114/114** ✅（XAML 改动后重跑，无回归） |
| `ImgHub.App` 编译 | ✅ 0 错误（41 个既有警告，均为 MVVMTK0034 / AVLN5001，与本次改动无关） |
| legacy 测试 | **959/959** ✅（801 + 42 + 38 + 51 + 27，未改动 legacy） |
| 人工核对 | ✅ 顶栏设置按钮可见、`OpenSettingsCommand` 有可达入口 |
| 人工核对 | ✅ 「历史 / 累计」标题行有「导入」按钮，`OnImportClick` 不再是死代码 |

---

## 四·十七、第八轮修复：UI/交互 13 项（v5.23.0，按用户截图标注）

> 完整计划、根因分析与验收见 **[fix-plan-v5.23.md](fix-plan-v5.23.md)**。

### 方法：先实证，再动手

本轮不靠猜测 —— 用三层证据：

1. **静态**：`codegraph` 索引 + 通读 XAML/VM/控件源码
2. **运行时**：跑 Release 版（`%TEMP%` 下的数据目录**副本**，不碰用户数据）
3. **UI 探针**：`UIAutomation`（控件树 / `IsEnabled` / `FromPoint`）+ 合成鼠标事件 + 截图

### 🔴 三个"测试全绿但用户用不了"的真实缺陷

| # | 用户报告 | 实测根因 |
|---|---|---|
| 1 | 「右键无法点击」 | 探针测得菜单项 `IsEnabled=false`。双重原因：① `ContextMenu` 是弹出层，`$parent[ListBox]` 追溯不到 → `Command=null`；② `ListBox.ItemsSource` 是 `HistoryRow`，而命令参数声明为 `Item` → `CanExecute` 类型检查失败 |
| 2 | 「编辑图片功能区内，全部无法正常使用」 | `RegionCanvas : Control` **没有 Background**，Avalonia 命中测试基于已绘制几何。无标注时不绘制 → `FromPoint` 返回下层 `Image` → 指针穿透，**怎么画都画不上** |
| 3 | 「点击编辑图片，图片会上下移动」 | 中栏 `RowDefinitions="Auto,2*,Auto,Auto,Auto"`，`Row3`（工具栏）从 0 高度变出 → `2*` 的预览行被压缩 78px |

**修复**：① 改 `Click` 事件（`DataContext` 天然继承）；② `Render` 起始处
`ctx.FillRectangle(Brushes.Transparent, …)`；③ 工具栏区域**高度恒定**（112px），
非编辑态显示引导文案。

### 「就绪」名不副实（用户：「这么多功能都用不了，这里写个就绪？就绪了什么？」）

原实现：`Status` 默认硬编码 `"就绪"`，徽标 `IsVisible="{Binding IsConfigured}"`
—— 只要有 key 文件就显示「就绪」。

**改为启动自检**：逐项检查（生图 key / 模型合法性 / 数据目录可写 / 润色配置），
**全通过才写「就绪」**，否则写「未就绪：<首个原因>」，点击可查看详情并跳到设置。
按用户口径，**润色未配置也算未就绪**（不是"全功能可用"）。

### 其余 9 项

| 项 | 改动 |
|---|---|
| 提示词/图片历史**整行**可右键 | `ContextMenu` 移到 DataTemplate 根 `Border` + `Transparent` 背景 |
| 「仅从列表移除」置前 | 菜单顺序调整（破坏性小的在前） |
| 删除文件**弹窗确认** | 新增确认浮层（含文件名与「不可恢复」提示） |
| 历史/提示词**多选批量** | 「多选」开关 + `SelectionMode` 切换 + 批量移除/删除（确认 N 项） |
| 左栏下拉框字底被裁 | `MinHeight` 32 → **38**（内嵌 Noto Sans SC 行高较大） |
| 预览路径不对齐 | 右对齐到「编辑图片」左缘 |
| 「离线」复选框残留 | 删除宽/窄屏两处（debug 功能，改由设置 + `IMGHUB_DEBUG=1`） |
| 左栏滚动条太挤 | 滚动条列 14 → 18px，内容右边距 6 → 10px |
| 「用标注编辑」超出边界 | 工具栏改 `WrapPanel` 自动换行 + `MinWidth=96` |
| 底栏加版本号 | `Directory.Build.props` 统一 `<Version>`；VM 读程序集版本（预留 GitHub 链接） |

### 验证

| 项 | 结果 |
|---|---|
| Core 测试 | **56/56** ✅ |
| 集成测试 | **68/68** ✅（**+10** 项回归） |
| 合计 | **124/124** ✅ |
| UI 实测 | ✅ 13 项逐项经 UIAutomation 探针 + 截图确认 |
| 用户真实数据 | ✅ 未触碰（实测用 `%TEMP%` 副本） |

### 沉淀为新约束（docs/CONSTRAINTS.md）

- **D4d**：`ContextMenu` / 弹出层里不要用 `$parent[...]` 查找命令
- **D4e**：自绘 `Control` 必须填充透明矩形才可命中
- G 表新增 3 条症状（右键点不动 / 画不出标注 / 点按钮后预览跳动）

---

---

## 四·十八、第九轮：AOT 修复 + 网络加固 + 分级日志 + 悬停交互（v5.24.0）

> 完整计划与实测见 **[fix-plan-v5.24.md](fix-plan-v5.24.md)**；
> 踩坑沉淀见 [CONSTRAINTS.md](CONSTRAINTS.md) §H（AOT）与 §I（日志/容错）。

### 🔴 用户报「生成失败」的根因（AOT）

截图错误：

```
生成失败：Reflection-based serialization has been disabled for this application.
Either use the source generator APIs or explicitly configure the
'JsonSerializerOptions.TypeInfoResolver' property.
```

**用最小 AOT 工程复现，证据决定性**：

```
A) Dictionary<string,object?> + JsonSerializer.Serialize -> FAIL（与截图一致）
B) 强类型 POCO + JsonSerializer.Serialize                -> FAIL
C) JsonObject（System.Text.Json.Nodes）                  -> OK
D) JsonSerializer.IsReflectionEnabledByDefault           = False   ← 根因
```

**根因**：Native AOT **默认禁用反射序列化**；`HttpJsonClient.PostJsonAsync` 用了
`JsonSerializer.Serialize(payload)`（`payload` 是 `Dictionary<string,object?>`）。
**为什么上传成功而生成失败**：`PostMultipartAsync`（上传）不含 JSON 序列化。

**修复**：
- 固定结构（`AppConfig`/`ModelStatsFile`）→ **source generation**（`AppJsonContext`）
- 动态结构（HTTP 体 / `state.json` / JSONL）→ **`JsonObject`**（DOM，AOT 安全）
- `JsonArray.Add<T>` 泛型重载会刷 IL2026/IL3050 → 新增 `JsonSafe.AddNode/AddString`
  （走 `IList<JsonNode?>.Add`）→ **AOT 发布 20+ 条警告清零**

> ⚠️ **更正 v5.23.0 的错误结论**：`docs/DELIVERY.md` 曾写
> 「`IL2026`/`IL3050` 是残留警告（**无害**，功能实测正常）」——
> 它在框架依赖构建下"看起来正常"，**AOT 产物下直接导致生成不可用**。已更正。

### 🟡 执行中发现的**额外真实缺陷**

| # | 缺陷 | 实测 | 修复 |
|---|---|---|---|
| 1 | **4xx 被误重试 3 次**（日志说"不重试"却重试了）→ 违反铁律 C2 | 假 Handler 计数：401 请求 3 次 | `ApiError.Retryable` 显式标记（默认 false）→ 401 只 1 次 |
| 2 | **XAML 编译错误不阻断 build，但应用启动即崩** | 改悬停样式后 `XamlLoadException`，`dotnet build` 却报成功 | 模板内改 `$parent[UserControl].DataContext.*`；记为约束 **H8** |
| 3 | **JSONL 用缩进序列化 → 一条记录拆多行 → 历史项数变 0** | 既有测试直接失败 | 分 `ToReadableJson()`（缩进）/ `ToJsonLine()`（单行）→ 约束 **H7** |

### 网络健壮性（N1/N2）

- 新增 `NetworkDiagnostics`：无网络 / DNS / 拒绝 / 重置 / 不可达 / 超时 / TLS / 代理 / 取消
  **9 类分类**，每类给**具体动作**（例：DNS → "检查网络、DNS 或被代理拦截"）
- 解包 `HttpRequestException.InnerException`（`SocketException`）找真正原因
  —— 只看外层只会得到「An error occurred while sending the request」
- 连接超时（20s）与整体超时分离，避免"卡满 300 秒"
- 用户取消不当作网络错误；连接类错误退避 ×1.5

### 分级日志（L1/L2，用户追加要求）

- 新增 `AppLog`：**info / warn / error**，落盘 `<数据目录>/log/imghub-yyyy-MM-dd.log`
  - 按天分文件、追加写、单文件 5 MB 滚动、保留最近 10 个
  - **每条带位置**（`where`：类名.方法名），异常记 `InnerException` 链
  - **凭据脱敏**（`sk-or-v1-abc***`），key 绝不落盘
  - 写盘失败绝不抛（降级内存，保留 500 条）
- **消除所有静默 `catch { }`**（Core 的 `Session`/`ModelStatsService` + VM 各处）
  → 改为 `AppLog.*` + 位置。**这正是 AOT bug 难以定位的元凶**（约束 I1）
- 设置里新增「打开日志目录」按钮

### 悬停交互（U1/U2/U3，用户追加要求）

- `hoverfx` 样式：只过渡 `Background`/`Opacity`（**不动尺寸**，避免布局抖动）；
  新增 `AppHoverBrush`（浅/深两套）
- 新增 `Help.Tip` 附加属性 → **61 个按钮全覆盖**（59 个带具体说明），
  文案说清"点了会发生什么"
- 设置「界面选项」新增 2 个开关：**按钮悬停动画** / **悬停弹出说明**（持久化）

### 验证

| 项 | 结果 |
|---|---|
| Core 测试 | **81/81** ✅（**+25**） |
| 集成测试 | **68/68** ✅ |
| 合计 | **149/149** ✅ |
| AOT 发布 | ✅ **零 IL 警告**，31.06 MB exe |
| AOT 实跑 | ✅ 离线生成 2 张 + 4 个 JSON 文件全正确 |
| AOT 真实请求 | ✅ 请求发出、4xx 只重试 1 次 |
| UI 实测 | ✅ ToolTip 弹出 / 关闭后不弹 / 两个开关就位 |
| 用户真实数据 | ✅ 未触碰（`%TEMP%` 副本） |

---

## 五、如何运行与测试

```powershell
# 在仓库根目录执行（早期文档写的是 avalonia/，已重组到根）

# 全部单元测试（Core）
dotnet test tests\ImgHub.Core.Tests\ImgHub.Core.Tests.csproj

# 端到端集成测试（离线，不花钱）
dotnet test tests\ImgHub.Integration.Tests\ImgHub.Integration.Tests.csproj

# 运行桌面工作台
dotnet run --project src\ImgHub.Desktop

# ── 打包 ────────────────────────────────────────
# 框架依赖桌面版 + Android APK（build.ps1 的全部目标）
powershell -File build.ps1

# L3 Native AOT（30.6 MB exe + 3 原生 DLL，启动最快）
dotnet publish src\ImgHub.Desktop -c Release -r win-x64 `
  -p:PublishAot=true -p:DebugType=none -p:DebugSymbols=false -o release\desktop-aot

# L2 单文件 + 裁剪（约 41 MB 单 exe）
dotnet publish src\ImgHub.Desktop -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishTrimmed=true -p:TrimMode=partial -p:DebugType=none -p:DebugSymbols=false

# 离线跑（不联网不花钱）
$env:IMGHUB_HOME="$env:TEMP\imghub-demo"; dotnet run --project src\ImgHub.Desktop
```

**数据目录**：Windows 默认 `%LOCALAPPDATA%\imghub`；可用 `IMGHUB_HOME` 覆盖。

> 📛 **v0.5.28 改名兼容**：项目由 `Imgagent` 改名为 `ImgHub`。
> 为不丢用户数据，以下旧位置**仍会被读取**（新位置优先，旧位置回退）：
> · 数据目录 `%LOCALAPPDATA%\imgagent`（若新目录不存在则**直接沿用**，数据无需搬迁）
> · key 文件 `.imgagent_key` / `.imgagent_apimart_key` / `.imgagent_polish_key`
> · 环境变量 `IMGHUB_HOME` ← 也接受 `IMGAGENT_HOME`；`IMGHUB_*_API_KEY` ← 也接受 `IMGAGENT_*`
>
> ⚠️ 所以 `AppPaths.LegacyDirName*` 与 `Session.LegacyKeyFile` / `ReadKeyFileCompat`
> **不是残留代码** —— 删除会导致用户图库与 key"凭空消失"。已由
> `RenameCompatibilityTests`（Core）与 `RenamePathTests`（集成）锁定。

---

## 六、下一步建议优先级

1. **Android 真机验证**（中文渲染 + 软键盘/IME 交互）—— 需设备
2. 环境诊断面板（原版 `doctor.py`）／可考虑把日志查看做进 UI
3. 绑定真实快捷键（当前「快捷键」按钮只打印文案，无实际键位）
4. ~~`Session`/`HttpJsonClient` 迁移到 System.Text.Json **source generation**，
   彻底消除 AOT 的 `IL2026`/`IL3050` 警告~~ ✅ **v5.24.0 已完成**
5. 缩略图 LRU 缓存（列表 > 200 条时）
