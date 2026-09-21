# imgagent Avalonia 跨平台重写 —— 施工状态

> 目标：把原 Python + curses 的 imgagent 重写为 **Avalonia (C#/.NET 10)** 图形化工作台，
> 一套 UI 同时跑 **Windows 桌面**与 **Android**。
> 施工起点：2026-09-19 · 前置研究见 `../docs/avalonia-migration-feasibility.md`

---

## 一、当前进度总览

| 阶段 | 内容 | 状态 |
|---|---|---|
| P0 | 可行性验证（字体/图片/HTTP PoC） | ✅ 完成 |
| **P1** | **Core 层移植**（业务逻辑，平台无关） | ✅ **完成 · 53/53 测试通过** |
| **P2** | **主界面 + MVVM**（三栏工作台） | ✅ **完成 · 已实测运行** |
| **P3** | **端到端集成测试**（离线生成闭环） | ✅ **完成 · 13/13 测试通过** |
| **P4** | **Desktop head** | ✅ **Release 版已发布并实测运行** |
| **P5** | **Android head** | ✅ **APK 构建成功（42 MB，arm64+x86_64）** |
| **P6** | **高级功能（设置浮层/状态栏按钮/深浅主题）** | ✅ **完成 · 已实测** |
| P7 | 响应式布局（手机竖屏）打磨 | ⏳ 待做 |
| **P8** | **区域标注重绘 / 多图预览 / 配置引导** | ✅ **完成 · 已实测** |
| P9 | 润色候选选择 UI（4 条候选的图形化挑选） | ⏳ 待做 |
| P10 | Android 真机中文渲染验证 | ⏳ **需真机** |

**测试合计：92/92 通过**（Core 55 + 集成 37）

**构建产物**（`../release/`）：
```
release/desktop/                      Windows 桌面版（自包含框架依赖）
release/android/imgagent-5.19.0-android.apk   42.12 MB（arm64-v8a + x86_64，含 AOT）
```

**已实测验证（截图 + 日志）**：
```
[OK] 内存字节 -> Avalonia Bitmap      ← 生成结果不落盘直接显示
[OK] SkiaSharp.Decode(byte[])         ← 图片解码
[OK] 系统字体数: 319, 中文正常渲染      ← 全界面零乱码
[OK] HttpClient(异步)                  ← 网络连通
[OK] 离线生成 -> 落盘 -> 历史 -> 撤回 -> 持久化 闭环
[OK] Android APK 构建成功（含 Imgagent.App/Core/Android 的 AOT 原生库）
```


---

## 二、已完成：代码结构

```
avalonia/
├── Imgagent.slnx
├── Directory.Build.props            Avalonia 12.1.2 统一版本
├── src/
│   ├── Imgagent.Core/               ★ 平台无关（net10.0，无 UI 依赖）
│   │   ├── Catalog.cs               模型/质量/画幅/成本（对应 settings.py）
│   │   ├── Models/                  Item / GenResult / AppConfig / ApiProvider
│   │   ├── Http/                    ApiError + ErrorBody + ErrorHints + HttpJsonClient
│   │   ├── Imaging/                 ImageCodec + Placeholder（SkiaSharp）
│   │   ├── Services/                ImageApi（双 provider）+ PolishService
│   │   └── Storage/                 Session（配置/历史/提示词/key）
│   ├── Imgagent.App/                ★ 共享 UI（XAML + ViewModel）
│   │   ├── App.axaml(.cs)           深色主题 + CJK 字体回退链 + 组合根
│   │   ├── Services/                平台抽象（IPlatformStorage 等）
│   │   ├── ViewModels/              MainViewModel（编排层）
│   │   └── Views/                   MainView（三栏）+ MainWindow
│   ├── Imgagent.Desktop/            net10.0（Windows/Linux/macOS）
│   └── Imgagent.Android/            net10.0-android（结构就位）
└── tests/
    ├── Imgagent.Core.Tests/         53 项（常量/错误/文件名/图像/存储/润色）
    └── Imgagent.Integration.Tests/  13 项（端到端离线生成闭环）
```

**架构原则（延续原项目工程约束）**：
- `Imgagent.Core` **不引用任何 UI/平台类型** → 可单元测试、可换 UI 框架；
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
| 3 | 报 `XA5207: 找不到 API 36 的 android.jar` | 系统 Android SDK 在 `C:\Program Files (x86)\...`（**不可写**） | 用官方目标把 SDK/JDK 装到**用户目录**：<br>`dotnet build src\Imgagent.Android -t:InstallAndroidDependencies -f net10.0-android -p:AcceptAndroidSDKLicenses=True -p:AndroidSdkDirectory=%USERPROFILE%\android-sdk-imgagent -p:JavaSdkDirectory=%USERPROFILE%\jdk-imgagent` |
| 4 | `MainActivity` 编译错误 | Avalonia 12 的 API 变更：`AvaloniaMainActivity` 改为**非泛型**，App 类型由 `AvaloniaAndroidApplication<TApp>` 指定 | 见 `src/Imgagent.Android/MainActivity.cs` 与 `AndroidApp.cs` |
| 5 | `APT2260: drawable/icon not found` | 缺应用图标资源 | 复制 `Icon.png` 并加入 `<AndroidResource>` |

**最终结果**：
```
src/Imgagent.Android/bin/Release/net10.0-android/
  com.imgagent.app-Signed.apk   42.12 MB   ← arm64-v8a + x86_64，含 AOT 原生库
```

**复现命令**（已封装进 `build.ps1`）：
```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Target android
```

> ⚠️ 注：Android 工具链装在**用户目录**（`%USERPROFILE%\android-sdk-imgagent`、
> `%USERPROFILE%\jdk-imgagent`），不污染系统 SDK，也不需要管理员权限。
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

实测：用用户真实 `%LOCALAPPDATA%\imgagent` 配置（含 model:null）二次启动，**正常打开**。

### 其余修复

| 问题 | 修复 |
|---|---|
| 切 provider 后左上模型框空白、不自动选 | 见上：`OnProviderChanged` 现在保证 `Model` 总是合法默认模型并同步下拉 |
| 设置「校验」点了无反应 | 新增 `KeyChecking/KeyCheckStatus`，设置浮层内**实时显示**「正在校验… / 校验通过 · 可用模型 N 个 / 校验失败：原因」 |
| 润色端点/模型重启后丢失 | `SaveSettings` 同时落 `config.json`（polish_base_url/polish_model）与 `.imgagent_polish_key`；启动时组合根回填 `PolishService`（已验证用户配置里三项均正确持久化） |
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

缩略图**没有**加在 `Imgagent.Core.Models.Item` 上 —— Core 层是平台无关层，
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
| **窗口标题栏图标** | `MainWindow.axaml` 用 `Icon="avares://Imgagent.App/Assets/app-icon.png"` |

> ⚠️ 踩坑记录：窗口 Icon **不能用 PNG-in-ICO** —— Avalonia 的 `IconTypeConverter`
> 走 `Bitmap(Stream)` 单帧解码，遇到 PNG-in-ICO 会抛
> `ArgumentException: Unable to load bitmap from provided data` 而**直接崩溃**。
> 正确做法：exe 用 ICO，窗口用 PNG。两者分开。

### App 层重建（意外删除后）

一次清理操作误删了 `src/Imgagent.App` 整目录（含 `MainViewModel.cs` 899 行、
`MainView.axaml`、`Controls/RegionCanvas.cs` 等）。恢复方式：

1. `Imgagent.Core` + 两个测试工程**未受影响**（55 + 37 项测试仍全绿）——
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
| `src/Imgagent.Core/README.md` | 43 | 业务层职责 + 关键不变量 |
| `src/Imgagent.App/README.md` | 55 | UI 层结构与设计决策 |
| `src/Imgagent.Desktop/README.md` | 44 | 桌面 head + 图标 + 打包 |
| `src/Imgagent.Android/README.md` | 56 | Android head + mipmap/自适应图标 |
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
src/Imgagent.App/Assets/Fonts/NotoSansSC.ttf   16.95 MB（从系统 NotoSansSC-VF.ttf 复制）
```
`App.axaml` 字体链改为**内嵌字体优先**：
```xml
<FontFamily x:Key="AppFont">avares://Imgagent.App/Assets/Fonts/NotoSansSC.ttf#Noto Sans SC,
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
imgagent/                          原 avalonia/ 的内容提到根目录
├── src/Imgagent.Core|App|Desktop|Android
├── tests/Imgagent.Core.Tests|Integration.Tests
├── legacy/                        原 mobile/（Python + curses 版）
├── docs/                          （含原 avalonia/docs）
├── tools/make-android-icons.ps1
├── build.ps1 / make-icon.ps1 / Directory.Build.props / Imgagent.slnx
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
| 4 | 🟡 | **`TotalCost` 与 `Items` 求和可能偏差**（Undo 减的是均摊值，TotalCost 是 API 实际值） | `Session.ReconcileCost()`：偏差 > 1e-6 时以 Items 求和为准；`RefreshHistory` 时调用 |
| 5 | 🟡 | **生成失败后 `BatchResults` 不清理** → 旧缩略图条残留，误导用户 | 生成开始前 + 失败分支都清空 |
| 6 | 🟢 | 全部任务失败时错误信息不含原因 | 汇总 `failureReasons` 到异常消息 |

### 死代码 / 假实现检查

| 检查项 | 结果 |
|---|---|
| TODO / FIXME / HACK / NotImplemented | **0 处** |
| 空 `catch` | 全部为**有意的容错**（存储写失败不阻断、缩略图解码失败返回 null），且均有注释说明 |
| 未使用的公开成员 | 无（`ReconcileCost` 等新增成员均已被调用） |
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

## 五、如何运行与测试

```powershell
cd D:\Project\imagagent\Imagagent\avalonia

# 全部单元测试（Core）
dotnet test tests\Imgagent.Core.Tests\Imgagent.Core.Tests.csproj

# 端到端集成测试（离线，不花钱）
dotnet test tests\Imgagent.Integration.Tests\Imgagent.Integration.Tests.csproj

# 运行桌面工作台
dotnet run --project src\Imgagent.Desktop

# ── 打包（三级，按需选择）────────────────────────
# L2 推荐：单文件 + 裁剪（41 MB 单 exe）
dotnet publish src\Imgagent.Desktop -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:PublishTrimmed=true -p:TrimMode=partial -p:DebugType=none -p:DebugSymbols=false

# L3 Native AOT（22.9 MB exe + 3 原生 DLL，启动最快）
dotnet publish src\Imgagent.Desktop -c Release -r win-x64 ^
  -p:PublishAot=true -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false

# 离线跑（不联网不花钱）
$env:IMGAGENT_HOME="$env:TEMP\imgagent-demo"; dotnet run --project src\Imgagent.Desktop
```

**数据目录**：Windows 默认 `%LOCALAPPDATA%\imgagent`；可用 `IMGAGENT_HOME` 覆盖。

---

## 六、下一步建议优先级

1. **解除 Android 构建**（管理员装 workload）→ 跑真机中文验证（1–2 天）
2. 设置页（provider / key / 润色配置）—— 原版的 `do_settings` 平移
3. 润色候选选择 UI（原版 `_pick_pages` 的图形化）
4. 响应式布局：宽屏三栏 / 手机竖屏纵向堆叠（原版 `_WIDE_COLS` 逻辑）
5. 环境诊断面板（原版 `doctor.py`）
