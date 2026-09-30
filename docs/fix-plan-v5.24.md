# 修复计划（第九轮 · v5.24.0）

> 来源：用户实测「生成失败：Reflection-based serialization has been disabled」+ 追加要求。
> 方法：**先实证定位根因，再动手；每完成一项 → 编译 + 测试 + AOT 实跑 + UI 探针 check**。
> 目标版本：**v5.24.0**

---

## 零、根因诊断（决定性证据）

### 复现（最小 AOT 项目实证）

用户截图：**上传参考图成功**，但**生成失败**：

```
23:52:44  上传参考图 1/1 (566KB) …
23:52:48  参考图已上传 1 张
23:52:48  生成失败：Reflection-based serialization has been disabled for this application.
          Either use the source generator APIs or explicitly configure the
          'JsonSerializerOptions.TypeInfoResolver' property.
```

我建了一个最小 AOT 工程复现，结果**完全吻合**：

```
=== A) Dictionary<string, object?> + JsonSerializer.Serialize ===
FAIL-> System.InvalidOperationException: Reflection-based serialization has been
        disabled for this application. Either use the source generator APIs or
        explicitly configure the 'JsonSerializerOptions.TypeInfoResolver' property.
=== B) 强类型 POCO + JsonSerializer.Serialize ===
FAIL-> （同上）
=== C) JsonObject（System.Text.Json.Nodes，DOM）===
OK  -> {"model":"gpt-image-2.5-flare","prompt":"一只猫",...}
=== D) JsonSerializer.IsReflectionEnabledByDefault = False   ← 关键
```

### 根因

`Directory.Build.props` 无 `JsonSerializerIsReflectionEnabledByDefault=true`，
而 **Native AOT 默认禁用反射序列化**（`IsReflectionEnabledByDefault == false`）。

`HttpJsonClient.PostJsonAsync` 用的是：

```csharp
var body = JsonSerializer.Serialize(payload);   // payload 是 Dictionary<string,object?> → 反射 → AOT 崩
```

**为什么"上传成功但生成失败"**：`PostMultipartAsync`（上传）**不含** JSON 序列化，
而 `PostJsonAsync`（生成/润色）含 —— 所以恰好在生成那步炸。

### ⚠️ 更正上一轮的错误结论

v5.23.0 的 `docs/DELIVERY.md` 写着 IL2026/IL3050 是
「**残留警告（无害，功能实测正常）**」——**这是错的**。
它只在**框架依赖**构建下"看起来正常"；**AOT 产物下直接导致生成不可用**。
本计划一并修正该表述。

### 为什么只有这一处暴露

其余反射序列化点（`SaveConfig` / `LoadConfig` / `SaveState` / `Log` / `ModelStats`）
**都被 `catch { }` 静默吞掉**了 —— 所以只表现为「参数不保存/统计不更新」，
不报错、更难发现。**这本身就是必须修的问题**（见 L2）。

---

## 一、AOT 风险全量排查（用户要求「排查所有代码」）

扫描全部 `src/**/*.cs`，逐类核验：

| # | 风险模式 | 命中位置 | AOT 实测 | 处置 |
|---|---|---|---|---|
| 1 | `JsonSerializer.Serialize/Deserialize`（**反射**） | `HttpJsonClient:39`、`Session:151/249/271/297/322`、`ModelStatsService:35/53` | ❌ **崩** | **J1 全部迁移** |
| 2 | `Dictionary<string,object?>` 动态 payload | `ImageApi:100/114/117/186`、`PolishService:132` | ❌ **崩**（经 #1） | **J1 改 `JsonObject`** |
| 3 | `Assembly.GetCustomAttribute<>` | `MainViewModel:150`（版本号） | ✅ **正常**（实测 `value = 1.0.0`） | 保留 |
| 4 | `RegexOptions.Compiled` | `Session:430`、`PolishService:189/193/207` | ✅ **正常**（.NET 8+ 不再依赖动态代码生成） | 保留 |
| 5 | `Activator.CreateInstance` / `MakeGenericType` | **无** | — | — |
| 6 | `Reflection.Emit` / `dynamic` / `Expression.*` | **无** | — | — |
| 7 | `Enum.Parse` / `JsonStringEnumConverter` | **无** | — | — |
| 8 | 无参 `new JsonSerializerOptions`（隐式反射） | `ModelStatsService:54`、`HttpJsonClient` 默认 | ❌ **崩** | **J1 绑定 TypeInfoResolver** |

**结论**：AOT 风险集中在 **#1/#2/#8 三类，全部与 `System.Text.Json` 反射序列化有关**。
其余（正则、特性反射、无动态代码）实测安全。

---

## 二、修复项

### 🔴 J1. JSON 序列化全部迁移到 AOT 安全方案

**两条路线**（按数据结构选择）：

| 场景 | 方案 | 理由 |
|---|---|---|
| **动态 payload**（HTTP 请求体、state.json、jsonl 记录） | `System.Text.Json.Nodes`（`JsonObject`/`JsonArray`） | DOM 天生 AOT 安全，无需为每个形状定义类型 |
| **固定结构**（`AppConfig`、`ModelStatsFile`） | **source generation**（`JsonSerializerContext` + `[JsonSerializable]`） | 编译期生成读写器，零反射 |

**改点清单**：

```
HttpJsonClient.cs:39    JsonSerializer.Serialize(payload)
                     → (payload as JsonNode)?.ToJsonString() ?? JsonObject 包装
Session.cs:151          Deserialize<AppConfig>            → AppJson.Default.AppConfig（source gen）
Session.cs:249          Serialize(Config, JsonOpts)       → source gen
Session.cs:271          Serialize(d, JsonOpts)            → JsonObject.ToJsonString(opts)
Session.cs:297          Serialize(rec)                    → JsonObject.ToJsonString()
Session.cs:322          Serialize(new Dictionary<..>)     → JsonObject
ModelStatsService:35    Deserialize<ModelStatsFile>       → source gen
ModelStatsService:53    Serialize(_cache, new Options{})  → source gen（不再用裸 Options）
ImageApi.cs:100/186     Dictionary<string,object?>        → JsonObject
PolishService.cs:132    Dictionary<string,object?>        → JsonObject
```

**关键约束（必须保持的既有契约）**：

- `config.json` / `state.json` 字段名是 **snake_case**（与 Python 版互通）
  → source gen 用 `PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower`（**已实测可用**）
- 宽松读取（未知字段/注释/尾逗号）→ 保留 `JsonOpts` 的这些选项
- `history.jsonl` / `prompt_history.jsonl` 保持**逐行可读**（`JsonObject.ToJsonString()`）
- 只读计算属性（如 `AvgCost`）**要被写出**（实测 source gen 会写只读属性，✅）

**验收**：AOT 产物下 **生成 / 编辑 / 润色 / 落盘 / 统计** 全部可用（不再出现该异常）。

---

### 🟡 J2. AOT 产物实测（本次必须实跑，不能只看编译）

```
dotnet publish src\ImgHub.Desktop -c Release -r win-x64 `
    -p:PublishAot=true -p:DebugType=none -p:DebugSymbols=false -o release\desktop-aot
```

必测清单（AOT exe，用 `IMGHUB_HOME` 指向临时副本）：

| # | 动作 | 期望 |
|---|---|---|
| 1 | 启动 | 正常，顶栏自检显示 |
| 2 | **离线**生成 | 出图 + 落盘 + 历史 + 配置写入 config.json |
| 3 | **真实**生成（有 key 时） | 不再报 reflection 异常 |
| 4 | 润色 | 不再报 reflection 异常 |
| 5 | 改参数 → 重启 | 参数恢复（验证 config.json 真的写了） |
| 6 | 重启后累计金额 | 不被清零（验证 state.json/total_cost） |
| 7 | 统计/预估 | model_stats.json 正常读写 |

---

### 🟡 N1. 网络健壮性加固

现状问题：`HttpJsonClient` 把各种异常笼统塞进 `ApiError`，`ErrorHints` 只覆盖少数关键字；
`SocketException`/DNS 失败/无网络/代理错误没有专门分支。

**改动**：

| 项 | 内容 |
|---|---|
| 错误分类 | 新增 `NetworkKind` 判定：`NoNetwork` / `DnsFailure` / `ConnectionRefused` / `TlsError` / `Timeout` / `ProxyError` / `ServerError` / `AuthError` / `RateLimited` |
| 可读提示 | `ErrorHints` 为每类给**具体动作**（如 DNS：检查网络/DNS/代理；TLS：换网络或关代理） |
| 内层异常穿透 | 解包 `HttpRequestException.InnerException`（`SocketException.SocketErrorCode`），据此判别 |
| 重试复查 | 保留 429/5xx；**新增**：连接类错误（DNS/拒绝/超时）**也可重试**，但退避时间更长；4xx（业务）不重试 |
| 超时分层 | 连接超时 vs 读超时分开（`SocketsHttpHandler.ConnectTimeout`），避免"卡 300 秒" |
| 请求前预检 | 生成前检查：网络可用性（可选）、key 非空、prompt 非空 → 早失败、早提示 |
| 取消支持 | 用户取消（`CancellationToken`）不当作网络错误上报 |

---

### 🟡 L1. 分级日志 + 落盘（用户追加要求）

**要求**：`info` / `warn` / `error` 三级；每次日志保存在**同目录 `log/` 文件夹**；供诊断。

**设计**：

```
数据目录/
  ├─ log/
  │   ├─ imghub-2026-09-22.log     ← 按天分文件（追加写）
  │   └─ ...
  ├─ config.json
  ├─ state.json
  └─ history.jsonl
```

- 新增 `ImgHub.Core/Diagnostics/AppLog.cs`：
  - `AppLog.Info/Warn/Error(string msg, string? where = null, Exception? ex = null)`
  - 行格式：`2026-09-22 01:23:45.678 [WARN ] (MainViewModel.GenerateAsync:512) 消息 | 异常：类型: 详细消息`
  - **`where` 写清「文件:方法:行号」**（用户要求「写清楚错误位置，方便排查」）
  - 写入走 `lock` + 追加（`File.AppendAllText`），失败不抛（降级为只写内存）
  - 单文件上限（如 5 MB）触发滚动；保留最近 N 个文件
  - 内存环形缓冲（最近 500 条）供 UI「诊断」查看
- `AppLog` 初始化在组合根（`App.axaml.cs`），路径 = `AppPaths.ResolveHome()/log`
- UI 消息面板与 `AppLog` **双向对接**：`MessageLevel.Info/Ok → Info`、`Warn → Warn`、`Err → Error`
- 新增命令：**「打开日志目录」**（并在设置里显示日志路径）
- 日志**不含** API key（脱敏：`sk-xxx…` → `sk-***`）

---

### 🟡 L2. 所有操作函数 try/catch + 写明原因与位置（用户追加要求）

**现状问题**：大量 `catch { }` 静默吞异常 —— 正是本次 bug 难以定位的元凶。

**改动**：

| 原则 | 做法 |
|---|---|
| 不再静默 | 把 `catch { }` 改为 `catch (Exception ex) { AppLog.Warn("…", where, ex); }`（写日志但仍容错） |
| 写清位置 | 每个 catch 用 `AppLog.Error(msg, where: nameof(方法)+":"+行号, ex)` |
| 保留容错语义 | 存储写失败**仍不阻断**主流程（既有契约），但**必须留痕** |
| 覆盖范围 | `Session` 全部 IO、`ModelStatsService` 读写、`ImageApi` 各 provider 分支、`PolishService`、`MainViewModel` 各 `Safe` 包装、`PlatformStorage` |
| 异常不丢信息 | 记录 `ex.ToString()`（含 `InnerException` 链）到日志文件；UI 只显示简短消息 |

**验收**：故意制造失败（只读目录 / 损坏 JSON），日志里能看到**原因 + 位置**。

---

### 🟢 U1. 按钮悬停动画（用户追加要求）

**做法**（统一在 `App.axaml` 里定义样式，避免逐个按钮改）：

```xml
<Style Selector="Button">
  <Setter Property="Transitions">
    <Transitions><BrushTransition Property="Background" Duration="0:0:0.12"/></Transitions>
  </Setter>
</Style>
<Style Selector="Button:pointerover /template/ ContentPresenter">
  <Setter Property="Background" Value="{DynamicResource AppHoverBrush}"/>
</Style>
```

- 悬停：背景色过渡（120ms）+ 轻微不透明度/边框变化
- 按下：`Button:pressed` 再深一档
- **不动画尺寸**（避免布局抖动，遵守上一轮 P6 的教训）
- 主题色新增 `AppHoverBrush`（Light/Dark 两套）

---

### 🟢 U2. 悬停说明 ToolTip 全覆盖（用户追加要求）

- 逐个检查 `MainView.axaml` 里**所有** `Button`/`ToggleButton`/`ComboBox`/`CheckBox`/`Slider`，
  确保都有 `ToolTip.Tip`
- 文案要求：**说清"点了会发生什么"**（如「生成：用当前参数出图，会真实计费」）
- 新增按钮（多选/删除选中/移除选中/打开日志目录）一并补齐

---

### 🟢 U3. 设置里加开关（用户追加要求）

设置浮层「界面选项」新增：

| 开关 | 默认 | 作用 |
|---|---|---|
| `EnableHoverAnimation` | **开** | 关闭后按钮无悬停/按下动画（低配机或不喜欢动效） |
| `EnableToolTips` | **开** | 关闭后不显示悬停说明 |

持久化进 `AppConfig`（`use_hover_animation` / `use_tooltips`，snake_case），
XAML 用 `IsVisible`/样式类绑定控制。

---

## 三、执行顺序与验收

| 序 | 项 | 主要文件 | 验收 |
|---|---|---|---|
| 1 | J1 JSON 迁移 | `HttpJsonClient` / `Session` / `ModelStatsService` / `ImageApi` / `PolishService` + 新增 `AppJsonContext` | AOT 实跑无异常 |
| 2 | L1 日志 | 新增 `AppLog.cs` + 组合根接线 | 日志文件出现、分级正确、含位置 |
| 3 | L2 try/catch | Core 全部 IO 与 App VM | 制造失败能看到日志 |
| 4 | N1 网络加固 | `HttpJsonClient` / `ApiError` / `ErrorHints` | 断网/DNS/超时各有明确提示 |
| 5 | J2 AOT 全项实测 | — | 7 项全通过 |
| 6 | U1/U2/U3 悬停与开关 | `App.axaml` / `MainView.axaml` / VM | UI 探针 + 截图 |
| 7 | 回归测试 | `tests/` | 124 → 全绿（+新增） |
| 8 | 文档与踩坑记录 | `docs/` | 见 §四 |

---

## 四、踩坑记录（用户要求「记录到文档」）

写入 `docs/CONSTRAINTS.md` 新增 **H 类：AOT / 裁剪约束**：

- **H1**：**AOT 下禁止反射序列化** —— 完整复现、错误原文、正确做法（`JsonObject` / source gen）
- **H2**：**不要把 IL2026/IL3050 当作"无害警告"** —— 修正 v5.23.0 DELIVERY 的错误表述
- **H3**：**框架依赖构建通过 ≠ AOT 可用** —— 必须实跑 AOT 产物
- **H4**：**`catch { }` 会掩盖 AOT 问题** —— 静默吞异常让 bug 变成"功能悄悄失效"
- **H5**：`RegexOptions.Compiled` 在 .NET 8+ AOT 下**是安全的**（避免后人误改）
- **H6**：source gen 的 `PropertyNamingPolicy` 必须显式设为 `SnakeCaseLower`（保持 Python 互通）

并在 `docs/DELIVERY.md` 修正「残留警告（无害）」那句。

---

## 五、风险与注意

| 风险 | 对策 |
|---|---|
| 迁移 JSON 后 config.json 格式变化 → 用户配置读不出 | **保持 snake_case 与既有字段完全一致**；加回归测试对比字段 |
| `JsonObject` 的数值类型（int/double/bool）序列化与原来不同 | 实测确认（`n:1` 不是 `n:1.0`）；加断言 |
| 日志写入过频拖慢 UI | 缓冲 + 阈值落盘；单文件 5MB 滚动 |
| 日志泄露 key | 统一脱敏；key 永不入日志 |
| 悬停动画影响性能 | 只动 `Background`/`Opacity`，不动布局；可开关关闭 |
| 破坏上一轮 13 项修复 | 每项改动后重跑上一轮回归测试（10 项）+ UI 探针 |
| AOT publish 耗时长 | 只在最后做一次完整 publish；中间用 `-p:PublishAot=false` 快速验证逻辑 |

---

## 六、不在本轮范围

| 项 | 原因 |
|---|---|
| System.Text.Json → 其他序列化库 | 无必要，source gen 已足够 |
| Android AOT 验证 | 无设备；但改动对 Android 同样有利 |
| 真实快捷键绑定 | 用户未要求 |

---

## 七、执行结果（全部完成 · 逐项实测）

**版本**：v5.23.0 → **v5.24.0**

### J1 JSON 迁移（AOT 修复）

| 改动 | 文件 |
|---|---|
| 新增 source-gen 上下文（`SnakeCaseLower`） | `AppJsonContext.cs` |
| 新增 AOT 安全 JSON 辅助（`AddNode`/`AddString`/`ToReadableJson`/`ToJsonLine`） | `JsonSafe.cs` |
| HTTP 请求体 → `JsonObject` | `HttpJsonClient` / `ImageApi` / `PolishService` |
| config/统计 → source-gen | `Session` / `ModelStatsService` |
| state/JSONL → `JsonObject`（单行） | `Session` |

**实测证据**：

```
A) Dictionary<string,object?> + 反射  -> FAIL（与用户截图错误一致）
B) 强类型 POCO + 反射                -> FAIL
C) JsonObject（DOM）                 -> OK
D) IsReflectionEnabledByDefault      = False
```

修复后 AOT 产物：请求**真的发出**（`POST https://openrouter.ai/api/v1/images`），
不再出现 reflection 异常。

### J1b 消除 IL2026/IL3050

`JsonArray.Add<T>` 泛型重载 → 改 `IList<JsonNode?>.Add`（`JsonSafe.AddNode`）。
**AOT 发布从 20+ 条 IL 警告降到 0**。

### N1/N2 网络健壮性

- 新增 `NetworkDiagnostics`：9 类故障分类（无网络/DNS/拒绝/重置/不可达/超时/TLS/代理/取消），
  每类给**具体动作**的中文提示；解包 `InnerException` 找真正原因。
- **修复实测缺陷**：4xx 曾被重试 3 次（日志说"不重试"却重试了）——
  改为 `ApiError.Retryable` 显式标记，401 **只请求 1 次**（有回归测试锁死）。
- 连接超时与整体超时分离（`ConnectTimeout = 20s`），避免"卡满 300 秒"。
- 用户取消不当作网络错误。

### L1/L2 日志与容错

- 新增 `AppLog`：**info/warn/error 三级**，落盘 `<数据目录>/log/imghub-yyyy-MM-dd.log`
  （按天、5 MB 滚动、保留 10 个、内存 500 条），**每条带位置**（`where`），
  **凭据自动脱敏**（`sk-or-v1-abc***`）。
- **消除全部静默 `catch { }`**（`Session`/`ModelStatsService`/VM 各处）→ 改为 `AppLog.*` + 位置。
- 设置里新增「打开日志目录」按钮。

### U1/U2/U3 悬停动画与说明

- `App.axaml` 新增 `hoverfx` 样式（只过渡 Background/Opacity，**不动尺寸**）+
  `AppHoverBrush`（浅/深两套）。
- 新增 `Help.Tip` 附加属性 → **61 个按钮全部覆盖**，59 个有具体说明；
  设置里可全局关闭。
- 设置「界面选项」新增 2 个开关：**按钮悬停动画** / **悬停弹出说明**（持久化到 config）。

### 🔴 执行中发现并修复的**额外崩溃**

XAML 里 DataTemplate 内误用 `Classes.hoverfx="{Binding EnableHoverAnimation}"`
（模板上下文是 `PreviewThumb`/`string`）→ build 报 `AVLN2000` 但**退出码为 0**，
运行时 `XamlLoadException` **启动即崩**。
修法：模板内用 `$parent[UserControl].DataContext.EnableHoverAnimation`。
已记为约束 **H8**。

### 验证

| 项 | 结果 |
|---|---|
| Core 测试 | **81/81** ✅（本轮 **+25** 项） |
| 集成测试 | **68/68** ✅ |
| 合计 | **149/149** ✅ |
| AOT 发布 | ✅ **零 IL2026/IL3050 警告**，31.06 MB exe |
| AOT 实跑 | ✅ 离线生成 2 张 + 落盘 + `state.json`/`history.jsonl`/`prompt_history.jsonl`/`model_stats.json` 全正确 |
| AOT 真实请求 | ✅ 请求发出、**4xx 只重试 1 次**、日志分级正确 |
| UI 实测 | ✅ ToolTip 悬停弹出、关闭后不弹、设置开关就位 |
| 用户真实数据 | ✅ 未触碰（实测用 `%TEMP%` 副本） |

### 新增回归测试（25 项）

`AotAndNetworkTests` 覆盖：JsonObject/JsonSafe 序列化契约、`AppConfig` snake_case 往返、
旧配置宽松读取、`state.json`/JSONL 可读性、网络故障 9 类分类、可操作提示、
`ApiError.Retryable` 默认值、**4xx 不重试 / 5xx 重试 / 429 重试**（用假 Handler 计次）、
日志分级/位置/脱敏/内存兜底。
