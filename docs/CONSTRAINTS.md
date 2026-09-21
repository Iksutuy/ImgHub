# imgagent（Avalonia 版）— 约束文档

> **写代码前必读**。这些是踩过坑后沉淀的硬约束，每条都有实战代价。
> 对应 Python 版的 `mobile/docs/CONSTRAINTS.md`（A–G 七类），本文件是 GUI 版平移 + 新增。

---

## A. 架构约束

### A1. Core 层绝不引用 UI / 平台

```csharp
// ❌ 禁止：Core 里出现这些
using Avalonia.*;        // UI 框架
using Android.App;       // 平台
using System.Windows;    // 平台

// ✅ 正确：Core 只依赖跨平台库
using SkiaSharp;         // 图像处理（非 UI 框架，允许）
```

**为什么**：这是分层的地基。实战验证 —— App 层曾被误删，
因 Core 无 UI 依赖，**仅凭测试契约就完整重建**，92 项测试全绿。

### A2. 缩略图等 UI 类型不能放进 Core

```csharp
// ❌ 禁止：在 Item（Core 的 Model）上加 Bitmap
public Bitmap? Thumb { get; }        // Core 不能引用 Avalonia

// ✅ 正确：App 层包一层
public sealed class HistoryRow      // App 层
{
    public Item Item { get; }
    public Bitmap? Thumb { get; }   // UI 关注点留在 UI 层
}
```

**代价**：首版误把 `Thumb` 加到 `Item` 上，编译失败（CS0246 找不到 Avalonia）。

### A3. 平台差异走接口注入

```csharp
// ✅ 正确：Core/App 定义接口，head 提供实现
public interface IPlatformStorage { ... }
public sealed class PlatformStorage : IPlatformStorage { ... }  // App 层默认实现
```

---

## B. 业务逻辑约束（从 Python 版平移）

### B1. 待修改图永远排参考图第 1 位

```csharp
// ✅ 正确：编辑时先加原图/标注图，再加用户导入的参考图
refs.Add((annotatedOrBase, "image/png"));     // 第 1 位 = 主体
foreach (var rf in RefImages) refs.Add(...);  // 后续 = 风格/人设参考
```

**为什么**：模型按位置权重理解参考图，第 1 位是"要改的那张"。

### B2. 提示词提交即记录，失败要放回输入框

```csharp
// ✅ 正确
_sess.PushPromptHistory(prompt, kind);   // 提交时立即记录（无论成败）
try { ... } catch { Prompt = prompt; }   // 失败把提示词放回，用户不用重打
```

### B3. 质量档不能给会 400 的选项

```csharp
// ✅ 正确：按 provider + 模型 过滤
public static string[] QualityChoices(ApiProvider p, string? model = "")
    => p == ApiProvider.Apimart && model.StartsWith("gpt-image-2.5")
        ? QualitiesApimart                          // 6 档
        : QualitiesApimart.Where(q => q is not ("xhigh" or "max")).ToArray();
```

**为什么**：`xhigh`/`max` 只有 2.5 系支持，传给 `gpt-image-2` 直接 400。

### B4. 非法配置启动时必须纠正

```csharp
// ✅ Sanitize() 在 Session.Load() 里自动跑
Config.Model ??= "";                                    // null → 空（防闪退）
if (!Catalog.QualitySupported(p, Config.Quality, ...)) Config.Quality = "low";
if (!Catalog.ModelMatchesProvider(Config.Model, p))     Config.Model = DefaultModel(p);
```

**代价**：曾有 `"model": null` 写进 config.json → 二次启动 `NullReferenceException` → **进程闪退**。

### B5. 参考图上传失败不静默降级

```csharp
// ❌ 错误：上传失败就跳过参考图（用户以为编辑生效了，其实没有）
try { url = await Upload(...); } catch { /* 忽略 */ }

// ✅ 正确：明确抛错
var url = await UploadImageAsync(...)   // 失败即抛 ApiError，让上层报给用户
```

---

## C. 网络与错误处理约束

### C1. 余额判断必须先于权限判断（**最贵的一条**）

```csharp
// ❌ 错误：APIMart 余额不足返回 403，被误诊为「key 无效」
if (status is 401 or 403) return "key 无效或没权限";

// ✅ 正确：先判余额
if (status == 402 || low.Contains("insufficient") || low.Contains("quota") || ...)
    return "账户余额不足 —— 去充值；编辑比文生图贵（要传参考图）";
if (status is 401 or 403) return "key 无效或没权限";
```

**代价**：真机上 APIMart 余额不足报 403，用户按提示去换 key，白折腾半小时。

### C2. 重试只针对 429 / 5xx

```csharp
// ✅ 正确
bool retryable = status == 429 || (status >= 500 && status < 600);
if (!retryable || attempt == maxAttempts) throw last;
```

**为什么**：4xx 是配置/请求问题，重试 3 次只是浪费流量并掩盖真实原因。

### C3. 2xx 但 body 非法 JSON 不重试

```csharp
// ✅ 正确：协议错误重试无意义
try { return JsonDocument.Parse(text); }
catch (JsonException jex) { throw new ApiError($"响应不是合法 JSON：...", status); }
```

### C4. 写盘必须串行化

```csharp
// ✅ Session._writeLock 包住所有 AtomicWrite
lock (_writeLock) { File.WriteAllText(tmp, text); File.Move(tmp, path, true); }
```

**为什么**：`FlushConfig`（UI 线程）与节流 Timer 回调（线程池）可能**同时**写同一文件，
Windows 上 `File.Move` 并发会抛 `IOException`。

### C5. 并发防重复扣费

```csharp
// ✅ 所有会调 API 的入口先查 Busy
private async Task RunGenerationAsync(bool editMode)
{
    if (Busy) return;          // 防双击 → 两次 API 调用 → 重复扣费
    if (!CanRun) { ... }
```

---

## D. UI 约束

### D1. 未配置时禁用所有功能按钮

```xml
<Button Content="生成" Command="{Binding GenerateCommand}"
        IsEnabled="{Binding CanRun}"/>   <!-- CanRun = IsConfigured -->
```

并显示引导条（`NeedsSetup` → `SetupHint`）。**「就绪」徽标仅在已配置后显示**。

### D2. async void 事件处理器必须 try/catch

```csharp
// ❌ 危险：异常直接崩进程
private async void OnImportClick(...) { await DoAsync(); }

// ✅ 正确
private async void OnImportClick(object? s, RoutedEventArgs e)
{
    try { await OnImportClickCoreAsync(); }
    catch (Exception ex) { Vm?.LogPublic($"导入失败：{ex.Message}", MessageLevel.Err); }
}
```

### D3. 不用 InvokeAsync 等待（测试/无消息泵环境会死锁）

```csharp
// ❌ 错误：测试环境永久阻塞
await Dispatcher.UIThread.InvokeAsync(() => PreviewImage = new Bitmap(...));

// ✅ 正确：Post 不等待
Dispatcher.UIThread.Post(() => { try { PreviewImage = ...; } catch { } });
```

### D4. 窗口图标用 PNG，exe 图标用 ICO

```xml
<!-- MainWindow.axaml -->
<Window Icon="avares://Imgagent.App/Assets/app-icon.png">   <!-- ✅ PNG -->
```

**代价**：窗口 Icon 用 PNG-in-ICO 会抛
`ArgumentException: Unable to load bitmap from provided data` → **启动即崩**
（Avalonia 的 `IconTypeConverter` 走单帧解码，不支持 PNG-in-ICO）。

### D5. 中文显示必须显式配置字体链 + 内嵌兜底

```xml
<!-- Android 上系统字体名不可靠，必须内嵌 CJK 字体 -->
<FontFamily x:Key="AppFont">Microsoft YaHei UI, Noto Sans CJK SC, sans-serif</FontFamily>
```

**代价**：Android 上中文渲染为**方框（豆腐块）** —— Avalonia Android CJK 回归
（issue #19868 / #20195）。缓解：内嵌 Noto Sans SC 为 `AvaloniaResource`。

---

## E. 交付约束

### E1. AOT exe 必须与原生 DLL 同目录

```
release/desktop-aot/
  ├─ Imgagent.Desktop.exe      ← 单独拷走会秒崩（0xC0000409）
  ├─ libSkiaSharp.dll          ← 必需
  ├─ av_libglesv2.dll          ← 必需
  └─ libHarfBuzzSharp.dll      ← 必需
```

### E2. 构建前先杀进程（Windows 文件锁）

```powershell
Get-Process -Name "Imgagent*" -ErrorAction SilentlyContinue | Stop-Process -Force
```

**代价**：验证时启动的 exe 未杀，publish 报 `MSB3027: 文件被 Imgagent.Desktop 锁定`。

### E3. 清理时绝不对项目目录用 -Recurse -Force

```powershell
# ❌ 灾难：误删整个 App 层
Remove-Item src\Imgagent.App -Recurse -Force

# ✅ 正确：精确到文件
Remove-Item src\Imgagent.App\ViewModels\MainViewModel.cs
```

**代价**：一次清理误删 App 层全部文件（含 899 行 ViewModel）。

---

## F. 测试约束

### F1. 测试一律离线跑（不花钱）

```csharp
session.Config.Offline = true;   // 所有集成测试都开离线
```

离线用 `Placeholder.Png`（确定性占位图，相同 prompt+step 恒同图）。

### F2. mock 环境要关掉 PDCurses 语义（Python 版遗留）

Python 版的列冲突测试假定 ncurses 坐标语义；GUI 版测试用真实 Avalonia，
无需此处理。但测试里的 `_IS_PDCURSES = false` 逻辑保留在 Python 版。

### F3. 每条铁律都要有回归测试

| 铁律 | 测试 |
|---|---|
| B1 待修改图第 1 位 | `Edit_WithImage_UsesCurrentAsReferenceFirst` |
| B3 质量档过滤 | `Quality_RoutesPerProvider_OpenRouterHasNoXhigh` |
| B4 配置纠正 | `Config_NullModel_FallsBackToDefault` |
| C1 余额先于权限 | `Hint_403InsufficientQuota_IsBalanceNotPermission` |
| C4 写盘串行 | （集成测试间接覆盖） |
| D2 async void | `StatusBarCommands_AreExecutableAndSafe` |

---

## G. 常见陷阱速查

| 症状 | 根因 | 对策 |
|---|---|---|
| 启动闪退（0xE0434352） | 窗口图标 PNG-in-ICO | D4：窗口用 PNG |
| 启动闪退（NullReference） | config 里 `model: null` | B4：Sanitize 归一化 |
| 中文显示方框 | Android 缺 CJK 字体 | D5：内嵌字体 |
| 中文发虚模糊 | 内嵌了**可变字体**（默认字重 100） | D6：改用静态字体 |
| Avalonia 12 编译报 ExtendClientAreaChromeHints | 该属性已移除 | 只用 ExtendClientAreaToDecorationsHint |

### D6. 内嵌字体必须是**静态**字体，不能用可变字体（VF）

```xml
<!-- ❌ 错误：VF 的 wght 轴默认值可能是 100(Thin) → 笔画极细、发虚模糊 -->
<FontFamily x:Key="AppFont">avares://.../NotoSansSC-VF.ttf#Noto Sans SC</FontFamily>

<!-- ✅ 正确：静态 Regular，字重确定 -->
<FontFamily x:Key="AppFont">avares://.../NotoSansSC-Regular.ttf#Noto Sans SC</FontFamily>
```

**代价**：`NotoSansSC-VF.ttf` 的 `fvar` 表里 `wght` 默认值是 **100**，
Avalonia 按该默认值渲染 → 中文能显示但**笔画细弱发虚**。
修复：用 fontTools 实例化到 wght=400 并子集化 —— `tools/make-static-font.py`（17 MB → 7.15 MB）。
| publish 报文件锁定 | 旧进程未杀 | E2：先 Stop-Process |
| 参数改完重启就丢 | 节流失效 / snake_case 未映射 | 尾触发节流 + `[JsonPropertyName]` |
| AOT exe 单独拷走崩溃 | 原生 DLL 未随行 | E1：整目录分发 |
| 缩略图重新解码卡顿 | 每次 RefreshHistory 重建 | 列表 ≤200 条可接受；如需优化加 LRU |
