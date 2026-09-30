# 架构审查报告（v5.27.0）— 衰退风险诊断与 Roslyn 工具链引入

> 来源：用户要求「安装 arch-optimize 系列 skill 并积极使用，用来优化目前项目」。
> 方法：arch-optimize v3.2 五阶段工作流（arch_scan → risk_diagnose → quality_metrics → regression_guard）
> → 本报告 → Roslyn 分析器与 MCP 工具链落地。
> 基线版本：v5.26.0（工作区含未提交改动）

---

## 零、执行摘要

| 项 | 结论 |
|---|---|
| 架构红线 | ✅ **健康**。`ImgHub.Core` 零 `ProjectReference`，无循环依赖，依赖方向单向 |
| 测试基线 | ✅ **全绿**。Core 91/91、集成 68/68（实测） |
| 主要风险 | ⚠️ `MainViewModel.cs` 单文件 1794 行 / 141 方法定义，God Object |
| 次要风险 | ⚠️ ViewModel 反向依赖 View 层 `Controls.RegionCanvas`（DIP 违规） |
| 工具偏差 | ❗ arch-optimize 扫描器**不支持 C#**，其"健康分 0/危险"读数对本项目无效（见 §四） |
| 已落地 | Roslyn 分析器（AnalysisMode=All）、`.editorconfig` 规则取舍、RoslynCodeLens MCP（67 工具） |

**核心提醒**：本文所有 `legacy/`（Python）相关数字**不是待办事项**。`legacy/` 是行为契约，见 §五。

---

## 一、扫描结果与工具偏差（重要）

### 1.1 arch-optimize 报告"健康分 0 / 危险"——该结论对本项目无效

用 arch-optimize 跑全仓库：

| 指标 | 工具报告值 |
|---|---|
| `risk_diagnose --target .` | 296 findings，health 0/危险（Critical 122 / Warning 174） |
| `quality_metrics --target .` | 102 files，MI 43.44，health 0/危险 |

**但这个读数几乎全部来自 Python 旧代码，且工具不支持 C#**：

```
findings 按目录分解：
  legacy/   294   ← Python 旧实现
  tools/      1   ← Python 辅助脚本
  _fix_t2.py  1   ← 根目录残留 Python 文件
```

**决定性证据 —— 两个扫描器的语言表都不含 `.cs`**：

```python
# scripts/risk_diagnose.py LANG_EXTENSIONS
".py": "python", ".go": "go", ".c": "c", ".h": "c", ".cpp": "cpp",
".cc": "cpp", ".cxx": "cpp", ".hpp": "cpp",
".rs": "rust", ".ts": "typescript", ".tsx": "typescript",
".js": "javascript", ".jsx": "javascript"
# ← 没有 .cs
```

```python
# scripts/quality_metrics.py LANG_EXTENSIONS
# 同上，另加 ".java": "java", ".kt": "kotlin" —— 同样没有 .cs
```

**实测 `--target src` 的陷阱**：

```
risk_diagnose --target src  →  files_analyzed: 0   （33 个 C# 文件一个都没扫到）
quality_metrics --target src →  files_analyzed: 71  （全是 obj/Release 下的 Android
                                                    Java 构建产物，非项目源码）
                                health_score: 100 "优秀"  ← 假象
```

即：`quality_metrics --target src` 报"健康分 100 优秀"是**假象**——那 71 个文件全是 `src/ImgHub.Android/obj/Release/**/androidx/*.java`（构建产物，已被 `.gitignore` 排除），而 33 个 C# 源文件覆盖数为 **0**。

**结论**：工具给出的分数**既不能证明项目健康，也不能证明项目糟糕**。C# 部分必须另找手段——这正是 §六 引入 Roslyn 的动因。

### 1.2 阶段一产物（arch_scan / dep_graph）

`dep_graph` 仅识别出 4 个模块，且 `edges: []`：

```
_fix11a (1 file) / _fix_t2 (1 file) / legacy (28 files) / tools (1 file)
circular_deps: []
```

对 C# 部分**完全无覆盖**，故对架构判断无参考价值。

---

## 二、风险诊断（R1–R6 四段式）

> 按 SKILL.md 铁律：**完成诊断前不提修复建议**。以下为诊断，修复方案见 §七。

### R1 认知过载／R2 变更传播 —— `MainViewModel.cs`（Critical）

**Symptom**

| 指标 | 实测值 | 阈值 |
|---|---|---|
| 物理行数 | **1794** | — |
| 方法定义数 | **141** | — |
| `[RelayCommand]` | 39 | — |
| `[ObservableProperty]` | 52 | — |
| 文件内 `// ====` 职责分区 | **22** | — |
| 引用 `ImgHub.Core.*` 命名空间 | 7（Core/Diagnostics/Http/Imaging/Models/Services/Storage） | R2 判据 >5 |

> 注：HEAD 版本为 992 行，工作区未提交改动使其增至 1794 行（`git diff --stat`：+714/−18）。

22 个分区覆盖：可绑定 / 状态 / 启动自检 / 响应式布局 / 节流持久化 / 生成 / 导入 / 撤回 / 确认浮层 / 多选批量 / 预览 / 设置(×2) / key / 润色 / 区域标注 / 多图 / 主题 / 状态栏 / 消息。

**Source**：单一 ViewModel 同时承担 UI 编排、业务调用、持久化节流、文件系统操作、主题管理、状态栏聚合。缺少中间层（未按职责拆分为多个协作对象）。

**Consequence**：任何一处改动都可能波及无关功能区；22 个分区意味着 22 个潜在的合并冲突面；新增功能只能继续往此文件堆，退化会自我强化。

**Remedy**：见 §七 T1。

### R3 知识重复（Warning）

48 条 R3 findings，全部位于 `legacy/`（Python 旧实现内部跨模块重复的常量/决策）。**不处理**，理由见 §五。

### R5 依赖失序（Critical @ legacy）

工具报 8 组循环依赖，**全部位于 `legacy/impydroid/`**：

```
app.py ↔ console.py / httpclient.py / settings.py / store.py
store.py ↔ ui.py
store.py → pngcodec.py → app.py
store.py → ui.py → android.py → app.py
```

**不处理**，理由见 §五。

### R4 偶发复杂性（Critical @ legacy）

`legacy/impydroid/` 中 CC>20 的函数：`app.py:main` CC=69、`ui.py:do_settings` CC=39、`api.py:explain_error` CC=29、`photos.py:pick` CC=26 等。**不处理**，理由见 §五。

### R6 领域模型扭曲（Warning）

`tools` 模块名属通用技术词汇（`legacy/tools/*`、`tools/make-static-font.py`）。`tools/` 作为辅助脚本目录是通行惯例，**判定为误报，不处理**。

### 架构红线核查（人工，工具无法覆盖）

✅ **通过**。`ImgHub.Core.csproj` 内**零 `ProjectReference`**：

```
ImgHub.Core       → （无引用）              ← 红线成立
ImgHub.App        → ImgHub.Core
ImgHub.Desktop    → ImgHub.App
ImgHub.Android    → ImgHub.App
*Tests              → Core（集成测试另加 App）
```

无环、方向单向，与 `docs/README.md` 声明一致。

### DIP 违规 —— ViewModel 反向依赖 View 层（Warning，非假阳性）

```
src/ImgHub.App/ViewModels/MainViewModel.cs:110
  private readonly Dictionary<string, Controls.RegionCanvas.RegionSnapshot> _regionCache
:113  public void SaveRegionsFor(string?, Controls.RegionCanvas.RegionSnapshot)
:121  public Controls.RegionCanvas.RegionSnapshot? GetRegionsFor(string?)
:182  public Controls.RegionCanvas.RegionTool CanvasTool
```

**Symptom**：ViewModels 层类型把 View 层控件的 `RegionSnapshot`/`RegionTool` 嵌入字段类型与公开 API。

**Source**：区域标注状态需要跨 `RegionCanvas`（View）与 `MainViewModel` 传递，直接复用了控件的嵌套类型作为载体。

**Consequence**：ViewModel 无法脱离 Avalonia 控件独立测试；控件类型变动会强制改动 ViewModel 公开签名。

**假阳性排除**：SKILL.md 提示「组合根装配 ≠ DIP 违规」。此处**不是**装配——是公开 API 与字段类型直接依赖 View 层类型，属真实越层。

**Remedy**：见 §七 T2。

---

## 三、质量度量（工具可覆盖部分）

| 指标 | 全仓库 | 说明 |
|---|---|---|
| files_analyzed | 102 | 31 Python + 71 Java(构建产物) |
| total_logical_loc | 17898 | — |
| average_MI | 43.44 | — |
| average_CC | 2.74 | — |
| health_score | 0 / 危险 | **无效**，见 §1.1 |

C# 部分**无工具数据**。手工补充（`src/` 精确行数）：

| 文件 | 行数 | 备注 |
|---|---|---|
| `ViewModels/MainViewModel.cs` | **1794** | 见 R1 |
| `Core/Services/ImageApi.cs` | 574 | — |
| `App/Controls/RegionCanvas.cs` | 567 | 538 行 / 24 public 成员 |
| `Core/Storage/Session.cs` | 498 | — |
| `App/Views/MainView.axaml.cs` | 372 | 含 3 处 `async void` |
| `Core/Services/PolishService.cs` | 349 | — |
| `Core/Storage/PendingTaskStore.cs` | 327 | v5.26 新增 |
| `Core/Http/HttpJsonClient.cs` | 316 | — |

---

## 四、回归防护（阶段五基线）

实测建立基线（Roslyn 分析器变更**前后**均为）：

| 测试项目 | 结果 |
|---|---|
| `tests/ImgHub.Core.Tests` | **91/91 通过** |
| `tests/ImgHub.Integration.Tests` | **68/68 通过** |
| 合计 | **159/159** |

`legacy/` 自身测试（Python，959 项）本次未运行——未改动 `legacy/`。

**基线文件**：`regression_guard.py record` 需在 CI 中固化，本次为手工记录。

---

## 五、`legacy/` 判读：不是技术债（重要）

`AGENTS.md` 明确定义：

> `legacy/` 是上一代的 Python + curses 实现，**保留作参考实现与行为契约**，不是死代码 —— **不要清理它**。

依据：
- `README.md`：`legacy/` 为「参考 + 行为契约」来源
- `docs/port-status.md`：`legacy` 测试 **959/959** 通过，作为 C# 移植的行为基准
- `docs/HANDOVER.md`：「上一代 ... 保留作参考实现」

**因此**：§二 中所有 `legacy/` 的 R3/R4/R5 findings（占全部 296 条中的 294 条）**全部不构成优化任务**。对 `legacy/` 做重构会**破坏 C# 移植的对照基准**，属负收益。

> 按 SKILL.md 的假阳性防护原则，这类「按当前职责本不该被评判」的代码应被排除，而非列入待办。

---

## 六、Roslyn 工具链引入（本次已落地）

### 6.1 澄清：「使用 Roslyn 编译器」的实际情况

**.NET SDK 的 C# 编译器本身就是 Roslyn**，项目**早已在用**。构建日志实测：

```
CoreCompile:
  C:\Program Files\dotnet\sdk\10.0.302\Roslyn\bincore\csc.exe
Compilation request ImgHub.Core (net10.0),
  PathToTool=C:\Program Files\dotnet\sdk\10.0.302\Roslyn\bincore\csc.exe
[analyzer: ...Microsoft.CodeAnalysis.CSharp.NetAnalyzers.dll]
```

| 项 | 值 |
|---|---|
| SDK | 10.0.302 |
| Roslyn (csc) 版本 | 5.600.26.33009 |
| `Microsoft.CodeAnalysis.CSharp.dll` | 5.600.26.33009 |

即：**不存在「需要替换成 Roslyn」的事**。可做的是**用上 Roslyn 的分析能力**（此前只跑了默认档）。

### 6.2 分析器增强（已启用）

`Directory.Build.props` 新增：

```xml
<AnalysisMode>All</AnalysisMode>
<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
```

实测影响：

| 项 | 默认档 | AnalysisMode=All |
|---|---|---|
| App 警告数 | 114 | 716（未调规则时） |
| App 构建耗时 | 11.7s | 12.2s（**+4%**） |

**+4% 构建时间换全量 CA 规则，可接受**；且 `TreatWarningsAsErrors=false` 保持，**不阻断构建**。

### 6.3 规则取舍（`.editorconfig`，新增）

716 条原样保留会让真问题淹没在噪音里。逐条核实后降级 —— **每条都写明为何不是缺陷**：

| 规则 | 处置 | 核实结论 |
|---|---|---|
| CA1031 捕获通用异常 | none | App 层顶层兜底（`Safe()` 包装、`async void` 处理器）是**有意设计**，否则一个异常即崩溃 |
| CA1307/1310/1305 | none | 中文单语客户端；绑定路径/配置键均固定 ASCII |
| CA2007 ConfigureAwait | none | **UI 线程必须回归**，加 `ConfigureAwait(false)` 会破坏绑定更新 |
| CA1062 参数 null 校验 | none | `<Nullable>enable</Nullable>` 已提供更强的流分析契约 |
| CA5394 不安全随机数 | none | **已核实 2 处**：`ImageApi.cs:444`、`HttpJsonClient.cs:163`，均为退避/轮询**抖动**，不涉令牌密钥 |
| CA5350 弱加密 | none | **已核实 1 处**：`ImageApi.UploadKey()` 用 SHA1 做**上传去重缓存键**，非签名/认证 |
| CA2000 Dispose | suggestion | **已核实 3 处**：均属所有权转移（handler→HttpClient、content→HttpRequestMessage） |
| CA1308 ToUpper | none | 该规则仅适用于往返可逆规范化；此处是 provider 名不区分大小写比较 |
| CA1054/1056 URI 字符串 | suggestion | `baseUrl` 参与 AOT JSON 序列化，改 `System.Uri` 破坏契约 |
| CA1822／CA1002／CA1819／CA2227 | suggestion | MVVM 绑定需要实例成员与可变集合属性 |
| CA1707 下划线命名 | none（仅 `tests/**`） | xUnit 通行命名惯例 |
| CA1861／CA1034／CA1063／CA1816／CA1849／CA1866 | suggestion | 微优化或测试惯例 |

**刻意保留为 warning**（已抽查确认是真问题）：

| 规则 | 理由 |
|---|---|
| CA1032 | 异常缺标准构造函数 → 无法被通用 catch 正确还原 |
| CA1001 | 拥有 `IDisposable` 字段却未实现 `IDisposable` → 真实泄漏风险 |
| CA1508 | 恒真/恒假死条件 → 可能逻辑错误（已核实 `ImageCodec.cs:57`：`SKCodec.Create` 标注非空，`if (codec is null)` 被判死条件；实际 SkiaSharp 失败仍返回 null，故保留警告，但**不构成当前缺陷**） |
| CA1068 | `CancellationToken` 应为末位参数 |
| CS8618 | 不可空字段未初始化 |
| MVVMTK0034 | 直接引用 `[ObservableProperty]` 背后字段 → 绕过变更通知（**存量 110 条，建议专项清理**） |

收敛结果：

| 项目 | 规则调整前 | 调整后 |
|---|---|---|
| `src/ImgHub.Core` | — | **18** |
| `src/ImgHub.App` | 716 | **138** |
| `src/ImgHub.Desktop` | — | **138** |
| `tests/ImgHub.Core.Tests` | 190 | **18** |
| `tests/ImgHub.Integration.Tests` | 282 | **138** |

### 6.4 RoslynCodeLens MCP（已注册）

```
dotnet tool install -g RoslynCodeLens.Mcp    # 2.18.1
```

实测 stdio 握手通过，加载 `ImgHub.slnx`，暴露 **67 个工具**：

```
Loading solution: ImgHub.slnx
Background compilation starting...
Compiling project 1/6: ImgHub.Core
OK tools=67 :: find_unused_symbols, find_event_subscribers, list_running_tasks,
              find_circular_dependencies, find_tests_for_symbol, analyze_data_flow,
              get_public_api_surface, get_extension_methods
```

注册到项目级 `reasonix.toml`（**相对路径**，换机器不失效）：

```toml
[[plugins]]
name    = "roslyn-codelens"
command = "roslyn-codelens-mcp"
args    = ["ImgHub.slnx"]
```

> 说明：`reasonix.toml` 由安装器生成时含绝对路径与 `default_model` 覆盖，已改写为
> **仅声明 MCP**，避免在本仓库里悄悄换掉全局模型与计费口径。该文件**未被 `.gitignore` 排除**。

---

## 七、待办（按优先级，本次未执行）

### T1 `MainViewModel.cs` 拆分（R1+R2，最高优先）

**目标**：1794 行 → 多个按职责划分的协作对象，主 VM 只做编排。

**建议拆分边界**（对齐现有 22 个分区）：

| 抽出对象 | 覆盖分区 | 依据 |
|---|---|---|
| `GenerationCoordinator` | 生成 / 多图 / 撤回 | `RunGenerationAsync`、`LandResultsAsync` |
| `HistoryController` | 多选批量 / 历史 / 提示词历史 | `BatchDelete*`、`RefreshHistory*` |
| `RegionAnnotationController` | 区域标注 | 同时解掉 §二 的 DIP 违规 |
| `SettingsController` | 设置 / key / 主题 / 状态栏 | `PersistConfig`、`FlushConfig`、`RefreshKeyStatus` |
| `PreviewController` | 预览 / 缩略图 | `ShowPreviewAsync`、`OpenInViewerAsync` |

**风险**：高。这是核心 UI 编排文件，**必须先补测试再动**。

**验收**：Core 91/91 + 集成 68/68 保持全绿；主 VM < 400 行。

### T2 解除 ViewModel → View 层依赖（DIP）

把 `RegionSnapshot` / `RegionTool` 提升为 **App 层的独立契约类型**（如 `ImgHub.App.Models`），`RegionCanvas` 与 `MainViewModel` 共同依赖它，而非 VM 依赖控件。

**验收**：`MainViewModel.cs` 中不再出现 `Controls.` 前缀的类型引用。

### T3 清理 MVVMTK0034（110 条）

`[ObservableProperty]` 背后字段被直接引用会**绕过变更通知**。这是存量的真问题，建议逐个改为属性访问。

**附带收益**：同时消除 §6.3 表中最大的一类警告。

### T4 CI 固化质量门禁

按 SKILL.md 门槛接入 CI：

| 门禁 | 阈值 |
|---|---|
| 零退化率 | = 100%（`regression_guard.py compare`） |
| 健康分 | ≥ 70（**需先解决 C# 无工具覆盖**，见 §1.1） |
| 新增循环依赖 | 0 |

> **前置问题**：arch-optimize 不支持 C#，健康分门禁对 `src/` 无意义。可行方案是利用 §6.4 的 RoslynCodeLens（`find_circular_dependencies`、`get_public_api_surface` 等）构建 C# 版门禁。

---

## 八、附：本次未改动 `legacy/` 的确认

| 检查 | 结果 |
|---|---|
| `git status legacy/` | 无改动 |
| `legacy/` 测试 | 未运行（未改动，无需） |

---

## 九、变更清单

| 文件 | 变更 |
|---|---|
| `Directory.Build.props` | +`AnalysisMode=All`、+`EnforceCodeStyleInBuild`、+说明注释 |
| `.editorconfig` | **新增**。含逐条降级理由与保留规则说明 |
| `reasonix.toml` | **新增**（安装器生成后改写）。仅声明 roslyn-codelens MCP，相对路径 |
| `docs/fix-plan-v5.27.md` | **新增**。本报告 |

**未改动**：所有 `src/`、`tests/`、`legacy/` 源文件。
