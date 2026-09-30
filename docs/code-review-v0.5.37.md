# 代码审查：隐藏错误与技术债（v0.5.37）

> 本轮（图标化 + 均匀排布）附带的多维度审查结果。
> **方法说明**：配置的 review / security-review / arch-optimize 子代理在当前环境每次
> 只允许 8 轮工具调用就中断（`max_steps`），无法完成完整审查 —— 因此本轮审查
> **由主线程直接读代码 + 写验证脚本完成**，每条结论都附证据。

---

## 一、本轮修复的缺陷

### 1. 【已修·高】按钮的 `Content` 属性覆盖子元素 → 图标不显示

**现象**：给 46 个按钮加了「图标 TextBlock + 文字 TextBlock」子元素，但界面上**只有文字没有图标**。

**根因**：Avalonia 的 `ContentControl` 在同时有 `Content` 属性与子元素时，**优先渲染 `Content`**。
批量脚本保留了 `Content="生成"` 又追加了子元素 → 子元素被忽略。

**证据**：首次截图（图标应为 ▶+生成）与像素扫描；删掉 `Content` 后同一位置出现图标。

**修法**：删除这 46 个按钮的 `Content` 属性，文字改由子元素 `TextBlock` 提供。
**防护**：`MainViewLayoutContractTests.EveryButton_HasAnIcon`（新增契约测试）。

### 2. 【已修·高】绑定式 `Content` 被脚本误删 → 按钮变空

**现象**：脚本的正则 `Content="([^"]*)"` 也匹配了**绑定**写法，导致：
- `Content="{CompiledBinding PromptMultiButtonText}"` → 整个属性被删 → 提示词多选按钮**变空**；
- `Content="{CompiledBinding ConfirmOkText}"` → 确认对话框的按钮**变空**；
- `HistoryPanel` 的 `Content="{CompiledBinding HistoryMultiButtonText}"` 同病；
- `HistoryPanel` 的 `Content="导入"` 也丢了文字。

**证据**：与改造前的备份逐按钮对比（`PromptMultiButtonText` / `ConfirmOkText` 在备份里存在，改后消失）。

**修法**：4 处全部恢复，并把内容改为「图标 + 绑定文字」结构（与其它按钮风格一致）。

### 3. 【已修·中】`TryOwnerOnly` 的注释与实现不符（安全误导）

**现象**：注释写「尽力限制为'仅所有者可读'（**Windows ACL** / Unix 0600）」，
但实现是 `if (!OperatingSystem.IsWindows())` —— **Windows 上什么都不做**。

**为什么是问题**：本项目主力平台是 Windows，读到注释的人会以为 key 文件已加固，
而实际只依赖 `%LOCALAPPDATA%` 的**默认 ACL**。

**修法**：把注释改成如实描述（明确写出 Windows 边界 + 若真要加固需要什么），
实现行为不变（不引入 `System.Security.AccessControl` 这类新依赖 —— 那是独立决策）。
**未修**：Windows 侧的真实加固（见"待决"）。

---

## 二、审查通过、**无问题**的项

| 检查项 | 结论 | 证据 |
|---|---|---|
| 分层红线 A1（Core 不得引用 UI/平台） | ✅ 干净 | `rg "using Avalonia\|using Android\|System.Windows" src/ImgHub.Core` → 0 命中 |
| 命令注入（`explorer.exe /select,"{path}"`） | ✅ 不可能 | 路径经 `Session.SafeFilename` 的 `Unsafe` 正则（只留 `\w`/中文/`.-`）—— 实测 `"`、`;`、`&`、`\|`、`\n`、`\0` 全部被替换为 `_` |
| 图标字体是否含所有 XAML 用到的码位 | ✅ 无缺失 | `IconFontContractTests` 解析 TTF cmap 逐一比对 |
| 图标字体是否为静态（非可变） | ✅ 是 | 生成脚本实例化 `FILL/GRAD/opsz/wght` 后校验无 `fvar` 表 |
| 字体许可声明是否随文件保留 | ✅ 已写 | 实例化会丢 nameID 13/14，脚本显式补回（NOTICE 承诺"许可随文件保留"） |

---

## 三、待决 / 已知债务（v0.5.38 更新）

> 状态：**#2 与 #3 已在 v0.5.38 修复**（见下方 ✅）。#1 / #4 / #5 仍待决。

| # | 项 | 影响 | 状态 / 建议 |
|---|---|---|---|
| 1 | **Windows 上 key 文件无主动权限加固** | 依赖 `%LOCALAPPDATA%` 默认 ACL（仅当前用户 + Administrators）。风险有限，但与"仅所有者可读"的意图不完全一致 | ⚠️ **待决**。注释已在 v0.5.37 改为如实描述。要加固需引入 `System.Security.AccessControl`，属独立决策 |
| 2 | ~~`PlatformStorage.RevealInFileManagerAsync` 无调用者~~ | 功能完整但无 UI 入口（"在文件管理器中显示"） | ✅ **v0.5.38 已修**：底栏新增「定位文件」按钮 + `RevealInFileManagerCommand`（非 Windows 会如实提示"不支持"） |
| 3 | ~~`RegionCanvas.Render` 每帧创建 `RenderTargetBitmap`~~ | 拖动时每帧分配与图片同尺寸的位图（4K 约 33MB/张） | ✅ **v0.5.38 已修**：改为**缓存**离屏扁平层，键 =（形状版本号, 画布宽, 画布高）；缩放/平移不触发重建。`_current`（拖动中那条）不进缓存 |
| 4 | **`ImageApi.cs`（约 2100 行）/ `MainViewModel.cs`（约 3000 行）偏大** | 单文件职责多、定位改动慢 | ⚠️ **待决**。属大型重构（P1 批次），**不应与 UI bug 修复混做** —— 独立轮次推进 |
| 5 | **`UseIcons` 字段已无 UI 入口** | 保留仅为 config.json 与 legacy 的 1:1 契约 | ⚠️ **保持现状**（有意保留，非缺陷） |

### 本轮（v0.5.40）新增：多语言（中 / 英 / 日）

**范围**：213 键 × 3 语 = 639 条文案；XAML **238 处**接入；设置浮层可切换，**即时生效**。

**完整实现记录与三个"不报错"的坑 → [docs/i18n.md](i18n.md)**

新增的硬约束（已进 CONSTRAINTS H2 与 AGENTS §3.4c）：

| 坑 | 症状 | 原因 |
|---|---|---|
| 文案用 `{DynamicResource}` | 切语言后**界面不更新**，不报错 | DynamicResource 只在附加资源树时解析一次 |
| `Language` setter 发 `string.Empty` 或 `Item[key]` | 代码取值对、**界面不变**，不报错 | 索引器绑定**只认 `Item`**（`Item[key]` 看起来最像却是错的，v0.5.41 才查准） |
| `{CompiledBinding [k], Source={x:Static ...}}` | **编译期** AVLN2000 "does not have an indexer" | CompiledBinding 忽略 `Source=`，按 `x:DataType` 推断源 → 须写 `{CompiledBinding L[k]}` |
| `ContextMenu` 内用 `L[k]` | AVLN2000 | ContextMenu 是**独立视觉树** → 须 `{Binding [k], Source={x:Static ui:Localizer.Instance}}` |

**教训（可复用的诊断套路）**：遇到"代码对、界面不对、还不报错"，
按 ① 配置读到了吗 → ② 单例值对吗（**打日志打印你以为对的东西**）→ ③ 通知格式对吗 → ④ 绑定路径对吗 逐层排查。
本轮坑 3 就是靠在 setter 里打一行日志定位的（`探针(app.title)=[ImgHub Workbench]` 证明取值无误）。

### 本轮（v0.5.42）修：exe 没有图标（用户报「二进制文件没有图标」）

**两个独立根因**（都**不报错**，只能靠工具验证）：

| 根因 | 现象 | 修法 |
|---|---|---|
| **A. PNG-in-ICO** | `.ico` 里嵌的是 PNG blob → Explorer / `ExtractIcon` **不渲染** → 显示通用图标 | 生成脚本改为写 **BMP/DIB 帧**（`BITMAPINFOHEADER` + BGRA + AND 掩码） |
| **B. 目录表 offset 基准错** | 每项 `dwImageOffset` 从 **0** 起（应为 `6+16×N`）→ 指向文件头 → 整个 ico 不可解析 | 基线改为 `6 + 16×条目数` |

**为什么长期没被发现**：`make-icon.ps1` 的自检用
`New-Object System.Drawing.Icon($outIco)`，但那一步**抛异常被 PowerShell 的非终止错误吞掉**
→ 脚本照常打印 `DONE` + `ICON LOAD OK`（其实没加载成功）。

⇒ **教训**：`*.ps1` 的自检必须 `try/catch` 且**失败 `exit 1`**；
并且要有一条**独立于生成脚本**的检查 —— 本轮加了 `ExeIconContractTests`（4 条），
已用**注入法**验证「注入旧 bug → 正确失败」。

**顺带修的**：`make-icon.ps1` 顶部三个**硬编码绝对路径** → `Join-Path $PSScriptRoot ...`
（换机器不用改；AGENTS「已知缺口」里那条也随之划掉）。

**验收方式**（不是看文件大小！）：
`Shell32.ExtractIcon($exe)` 返回非 0 句柄，且**把图标提取成 PNG 目视确认**
（本轮已看图确认是蓝底机器人图标，非空白）。

### 本轮（v0.5.41）修：语言切换"部分界面不变"（坑 5 + 坑 6）

用户报"**切换语言完全没变化**"。定位到**两个独立根因**（先修了通知名，才发现这两个）：

| # | 根因 | 症状 | 修法 |
|---|---|---|---|
| 5 | **VM 里拼装文案的属性**硬编码中文（`ToolNames` / `RegionCountText` / `SetupHint` / `HistoryMultiButtonText` … 共 13 个） | XAML 字面量变了，但这些**生成文案的属性**不变 | 改走 `Localizer.Instance[...]`；已补 21 条新键 |
| 6 | 改走 Localizer 后**没发通知** | 「多选」按钮等**仍保持旧语言** | 新增 `NotifyLocalizedStrings()`，统一对 13 个属性 `OnPropertyChanged` |

**为什么上一轮漏了**：我只把 **XAML 字面量**当成"界面文案"，没想到 VM 里拼装的也是。
⇒ **教训**：i18n 的范围 =「所有用户可见文本」，**包括代码里生成的**，不只是 XAML 里的。

**新增两条防护测试**（都是**源码扫描**，能主动发现遗漏而非等人报）：
- `ViewModelBoundStrings_AreLocalized_NotHardcodedChinese` — 那些属性不得有中文字面量
- `EveryLocalizedVmProperty_IsNotifiedWhenLanguageChanges` — 切语言必须通知它们

> 这两条测试在编写过程中**各自真的抓到了一个遗漏**（`PolishStyleLabel` 未本地化、
> `PolishStyleNames` 的扫描窗口越界误报）—— 说明"用测试表达约束"比"写完靠眼睛看"可靠。

### 本轮（v0.5.39）修的 5 项用户反馈

| # | 反馈 | 根因 | 修法 |
|---|---|---|---|
| 1 | 拖动绘制时**不透明**，松手才变半透明 | v0.5.38 给离屏层加缓存时，把拖动中那条（`_current`）挪到了 `PushOpacity` 的 **using 块外** | 移回块内（共享同一 alpha）。回归测试用**大括号净增**做结构断言（见下"教训 3"） |
| 2 | 设置图标仍比文字低一点 | 顶栏那处**手写了 `LineHeight="17"`**（我上一轮漏改的唯一一处），17 < 图标自然行盒 20.40px → baseline 被压到底 | 改用 `Classes="icon"`（交给样式：无 LineHeight，只靠 Center）。**实测图标中心 81.0 vs 文字中心 81** |
| 3 | 马克笔提示标记要简洁、极小粗细时显示异常 | 马克笔画的是**虚线圆**（半径 = 笔宽/2）→ 笔宽最小时半径几像素，虚线糊成一团 | 马克笔改**十字准星**（尺寸固定为屏幕可见大小，与笔宽无关）；橡皮保留圆圈 + 最小半径保护（<6px 时退化实线） |
| 4 | 切马克笔时图标越界、挤成一团 | 出现「粗细」控件后行 1 变宽 → `WrapPanel` 换行 → 与行 2 挤在一起 | 工具条由 2 行改 **3 行**（工具/粗细 → 调色板/撤销/重做 → 缩放/居中/清除/编辑）；`AnnotationToolbarHeightWide` 118 → 160 |
| 5 | 工作台窗口不能**对角拖拽缩放** | `SystemDecorations="None"` 自绘标题栏 → 系统边框 resize 热区**不存在** | 最上层叠 8 个透明热区（四边 6px + 四角 12px，带对角光标）→ code-behind 接 `BeginResizeDrag`。布局是 Grid 自适应 → 缩放时控件位置关系不变 |

### 教训 3：**离屏 `RenderTargetBitmap` 里的 `PushOpacity` 测不出效果**（重要）

为回归 #1，我先写了"渲染两状态比像素亮度"的测试 —— 它**假通过**了：
把旧 bug 注入回去（`_current` 移出 `PushOpacity`）后，测试**仍然通过**。

原因：`RenderTargetBitmap.CreateDrawingContext()` 的离屏上下文里，
`PushOpacity` 不产生可测量的像素差异（两状态都一样）。

**结论**：这类"透明度是否套上"的回归，**不能用离屏渲染测试**，
要用**源码结构断言**（该测试用 `{`/`}` 净增判断 `_current` 是否还在 using 块内 ——
已用注入法验证过：注入旧 bug 时净增 0 → 正确失败）。


| 教训 | 说明 |
|---|---|
| **给 `.icon` 加 `LineHeight` 反而会错位** | `LineHeight=17 < 图标字体自然行盒 20.40px` → Avalonia 把 baseline 压到底部（`L − descent`）→ 图标字形中心上移 1.70px，而文字只偏 0.55px → **中心差 2.25px**（就是用户看到的"不在一条水平线上"）。去掉 `LineHeight`、只靠 `VerticalAlignment=Center` 后差值降到 **0.63px**。数学推导见 `docs/code-review-v0.5.37.md` 附注 |
| **`ToggleButton` 也是按钮** | 「深色」按钮是 `ToggleButton`，而 `EveryButton_HasAnIcon` 起初只匹配 `<Button>` → 漏检（用户报"深色按钮没有图标"）。现已同时匹配 `Button|ToggleButton`；「高级参数」折叠标题（同为 ToggleButton）也一并加了图标 |


---

## 四、给后续维护者的两条硬约束（本轮新增）

1. **新增图标必须重跑字体脚本**：把码位加进 `tools/make-icon-font.py` 的 `ICON_CODEPOINTS`，
   再执行 `python tools\make-icon-font.py`。否则字形不在子集里 → **界面显示空白**（不报错）。
   `IconFontContractTests.EveryIconUsedInXaml_ExistsInFont` 会在 CI 里拦住这种情况。

2. **按钮加图标时不要保留 `Content`**：一旦用了「图标 + 文字」子元素，
   必须删掉 `Content` 属性，否则 Avalonia 渲染 `Content`、子元素被忽略。
   `EveryButton_HasAnIcon` 会检查（但它看的是子元素，仍需人注意别写 `Content`）。
