# ImgHub（Avalonia 版）— 约束文档

> **写代码前必读**。这些是踩过坑后沉淀的硬约束，每条都有实战代价。
> 对应 Python 版的 `legacy/docs/CONSTRAINTS.md`（A–G 七类），本文件是 GUI 版平移 + 新增。

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
因 Core 无 UI 依赖，**仅凭测试契约就完整重建**，全部测试一次跑绿。

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

### D4. 窗口图标用 PNG，exe 图标用 ICO（**且 ICO 必须是 BMP 帧**）

```xml
<!-- MainWindow.axaml -->
<Window Icon="avares://ImgHub.App/Assets/app-icon.png">   <!-- ✅ PNG -->
```

**代价**：窗口 Icon 用 PNG-in-ICO 会抛
`ArgumentException: Unable to load bitmap from provided data` → **启动即崩**
（Avalonia 的 `IconTypeConverter` 走单帧解码，不支持 PNG-in-ICO）。

#### D4-补充（v0.5.42）：exe 的 `.ico` 有两个**独立的**坑

**坑 A：`.ico` 内部必须是 BMP（DIB）帧，不能是 PNG-in-ICO**

用户报「**二进制文件没有图标**」（资源管理器显示通用程序图标）。根因：
`make-icon.ps1` 早先用 `Bitmap.Save(..., ImageFormat.Png)` 生成各尺寸的 **PNG** blob 塞进 `.ico`。
PNG-in-ICO 在**窗口**里会被 Avalonia 拒绝（上面那条），在 **exe** 里则表现为
**Explorer / `Shell32.ExtractIcon` 不渲染 → 显示通用图标**（不报错，只是没图标）。

⇒ 生成脚本必须写 **BMP/DIB 帧**（`BITMAPINFOHEADER` + BGRA 像素 + AND 掩码）。
现成脚本见仓库根 `make-icon.ps1`（已改好并带自检）。

**坑 B：目录表的 offset 基准写错 → 整个 ico 不可解析**

ICO 的文件布局是 `[ICONDIR(6B)][目录表(16B × N)][各帧数据]`。
每个目录项的 `dwImageOffset` 是**相对文件开头的绝对偏移**，
基线必须是 **`6 + 16×N`**（即目录表本身也算进去）。

写错的话（常见误写：从 0 开始累加）文件**字节数看起来正常**，
但**任何工具都读不出图标**。

**这个坑最阴险的地方：生成脚本自己"看不出"错**。
旧脚本用 `New-Object System.Drawing.Icon($outIco)` 自检，
但那一步抛异常被 PowerShell 的**非终止错误**吞掉 → 脚本照样打印 `DONE`，
于是「文件是坏的」却一直没人发现。

⇒ **自检必须显式验证两件事**（新脚本已做）：
1. `System.Drawing.Icon` 能构造成功（用 `try/catch`，**失败要 `exit 1`**）；
2. **逐条检查目录表的 offset ≥ `6+16×N` 且 < 文件长度**。

**验收 `exe` 图标的正确方式**（不是看 XAML，不是看文件大小）：

```powershell
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class SI {
  [DllImport("shell32.dll", CharSet=CharSet.Auto)] public static extern IntPtr ExtractIcon(IntPtr h, string exe, int idx);
  [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
}
'@
$h = [SI]::ExtractIcon([IntPtr]::Zero, (Resolve-Path release\desktop-aot\ImgHub.Desktop.exe).Path, 0)
if ($h -eq [IntPtr]::Zero) { "NO ICON" } else { "OK"; [void][SI]::DestroyIcon($h) }
```

**另注**：`.ps1` 一律 **ASCII-only**（见 §3.6）——
PS 5.1 会把 UTF-8 无 BOM 当 ANSI 读，中文注释会破坏解析。
`make-icon.ps1` 顶部的三个路径曾是**硬编码绝对路径**（换机器要改）；
现已改为 `Join-Path $PSScriptRoot ...`（脚本自身位置相对解析）。

### D4b. UI 是两套布局，改一处必须同步另一处

`MainView.axaml` 里宽屏三栏与窄屏堆叠是**两个独立子树**，
宽屏 `IsVisible="{Binding IsWideLayout}"`、窄屏 `IsVisible="{Binding !IsWideLayout}"`。

```xml
<!-- ❌ 错误：只在宽屏那套加了控件 → 手机竖屏看不到 -->
<!-- ✅ 正确：同一个控件（如「离线」「用标注编辑」）两套都要加 -->
```

**代价（真实）**：本轮核查发现「设置」按钮曾被放进**已隐藏的底栏**
（`IsVisible="False"`），导致设置浮层**在 UI 上完全无法打开**，
而 commit message 却记为「#6 设置按钮移到右上角」。**改完必须实际点一遍**。

#### D4b-补充（v0.5.28 / Plan A）：优先改 Section，不要改两份

已抽出 **3 个共用 Section**（`Views/Sections/`），改它们**一处生效、两套自动同步**：

| 区域 | 改哪里 |
|---|---|
| 消息面板 | `Sections/MessagePanel.axaml` ✅ 一处生效 |
| 历史 / 累计 | `Sections/HistoryPanel.axaml` ✅ 一处生效（差异用依赖属性 `ListHeight`/`ShowUnknownCost` 参数化） |
| 高级参数 | `Sections/AdvancedParamsPanel.axaml` ✅ 一处生效 |
| 预览卡片 / 主参数卡片 | `MainView.axaml` ❌ **仍需两处都改**（重复度低或含 `x:Name` 强依赖，抽了反而更差） |

> 依据（实测重复度）：消息 75% / 历史 49% / 高级参数逐行对应 → 抽；
> 预览 40%（含 4 个 `x:Name` + 变换矩阵共用约束）、主参数 19% → 不抽。
> 详见 [plan-a-section-refactor.md](plan-a-section-refactor.md) §1.1。

**新增控件时的判断顺序**：
1. 能否放进上述 3 个 Section？→ 能就放那里（一处生效）
2. 不能 → 才去 `MainView.axaml` 两套都改

### D4c. 命令必须有可达入口，且隐藏容器里的入口不算

```
❌ <Border IsVisible="False">            ← 底栏整体隐藏
     <Button Command="{Binding OpenSettingsCommand}"/>   ← 入口跟着失效
```

**检查法**：搜 `Command="{Binding ...Command}"` 的所有出现位置，
确认至少有一个落在 `IsVisible` 恒为真的容器里。

**实际受害者**：
- `OpenSettingsCommand` —— 设置浮层一度完全打不开（已补顶栏入口）
- `OnImportClick` —— 导入按钮被删后未加回（已补历史面板标题行）
- `ShowShortcutsCommand` / `ShowDataDirCommand` —— 仍只在隐藏底栏

> **当前底栏整体 `IsVisible="False"`。新功能不要往底栏加按钮 —— 加了也不可达。**

### D4f. Section / 子控件内不要用 `$parent[UserControl].Xxx`（AVLN2000）

抽成 `UserControl` 的子面板里，**不能用 `$parent[UserControl]` 访问派生类上定义的属性**：

```xml
<!-- ❌ AVLN2000: Unable to resolve property or method of name 'ShowUnknownCost'
     on type 'Avalonia.Controls.UserControl'
     → $parent[UserControl] 做**类型匹配**，命中的是基类，看不到派生类属性 -->
<TextBlock IsVisible="{Binding $parent[UserControl].ShowUnknownCost}"/>

<!-- ✅ 方案 1（最简）：DataContext 会向下继承到 Section 内部，直接用即可 -->
<TextBlock IsVisible="{Binding ShowUnknownCost}"/>
<!-- 注：$parent[UserControl].DataContext.Xxx 也等价于 Xxx，但写起来啰嗦 -->

<!-- ✅ 方案 2：确实要引用 Section 自身属性时，根节点 x:Name + ElementName -->
<UserControl x:Name="Root">
  <TextBlock IsVisible="{Binding ShowUnknownCost, ElementName=Root}"/>
```

⚠️ **这个坑的症状极具误导性**，务必记住：
任何一个 `.axaml` 编译失败（AVLNxxxx）都会让**整个 XAML 编译阶段**失效，
运行时表现为：
```
No precompiled XAML found for ImgHub.App.App, make sure to specify x:Class
and include your XAML file as AvaloniaResource
```
**报错指向的是没改过的 `App.axaml`**，会把人引到错误方向（清 obj、查 x:Class…）。

**正确的排查法**：直接看 XAML 编译日志里的真错误码，不要只看运行时堆栈：
```powershell
dotnet msbuild src\ImgHub.App\ImgHub.App.csproj -t:Rebuild -v:d -nologo 2>&1 |
    Select-String -Pattern "AVLN|XAMLIL"
```

**同源坑（也一并记住）**：
- 增量构建可能报 `正在跳过目标 GenerateAvaloniaResources…已最新` → 删 `obj/<cfg>/Avalonia` 后重建
- `x:Name="X"` 会由 NameGenerator 生成同名字段，**手写属性不能叫 X**（CS0102）→ 改名（如 `InnerList`）
- 构建前先停应用：`Get-Process -Name "ImgHub*" | Stop-Process -Force`（否则 dll 被锁，MSB3021）

### D4c. 命令必须有可达入口，且隐藏容器里的入口不算

```
❌ <Border IsVisible="False">            ← 底栏整体隐藏
     <Button Command="{Binding OpenSettingsCommand}"/>   ← 入口跟着失效
```

**检查法**：搜 `Command="{Binding ...Command}"` 的所有出现位置，
确认至少有一个落在 `IsVisible` 恒为真的容器里。

**实际受害者**：
- `OpenSettingsCommand` —— 设置浮层一度完全打不开（已补顶栏入口）
- `OnImportClick` —— 导入按钮被删后未加回（已补历史面板标题行）
- `ShowShortcutsCommand` / `ShowDataDirCommand` —— 仍只在隐藏底栏

> **当前底栏整体 `IsVisible="False"`。新功能不要往底栏加按钮 —— 加了也不可达。**

### D4d. `ContextMenu` / 弹出层里不要用 `$parent[...]` 查找命令

```xml
<!-- ❌ 双重坑：菜单项恒为禁用（用户报"右键无法点击"） -->
<ContextMenu>
  <MenuItem Command="{Binding $parent[ListBox].DataContext.DeleteCommand}"
            CommandParameter="{Binding}"/>   <!-- ① $parent 追溯不到（ContextMenu 是弹出层） -->
</ContextMenu>                                <!-- ② 参数类型与命令声明不符 → CanExecute=false -->
```

```xml
<!-- ✅ 正确：用 Click 事件；Control.DataContext 天然继承宿主行的数据 -->
<Border Background="Transparent">     <!-- 根容器 + Transparent → 整行可命中 -->
  <Border.ContextMenu>
    <ContextMenu>
      <MenuItem Header="…" Click="OnHistoryDeleteClick"/>   <!-- code-behind 取 DataContext -->
    </ContextMenu>
  </Border.ContextMenu>
  …
</Border>
```

**代价（实测）**：v5.22.0 的历史右键菜单两项 `IsEnabled` 恒为 `false`，
菜单弹出但点了没反应。用 UIAutomation 探针实测确认（见 `docs/fix-plan-v5.23.md`）。

### D4e. 自绘 `Control` 必须填充透明矩形才可命中

```csharp
// ❌ 错误：Control 基类没有 Background；不绘制就没有命中区域
//    → 指针事件穿透到下层控件 → 标注/拖拽全部失效
public override void Render(DrawingContext ctx) { /* 只画笔迹 */ }

// ✅ 正确：先铺一层透明矩形建立命中区域
public override void Render(DrawingContext ctx)
{
    base.Render(ctx);
    ctx.FillRectangle(Brushes.Transparent, new Rect(0, 0, Bounds.Width, Bounds.Height));
    /* …再画内容 */
}
```

**代价（实测）**：`RegionCanvas` 无标注时不绘制任何几何 → `FromPoint` 命中到下层
`Image` → **在编辑模式下怎么画都画不出标注**（用户报「编辑图片功能区内全部无法使用」）。

### D4g. 抽 UI 组件时的 4 条硬约束（v0.5.29 血泪）

把区域抽成 `UserControl` / Section 时，以下 4 条**每条都真实踩过**：

#### ① 先看目标区域里有没有「本应固定」的元素

原结构常是「固定标题 + 滚动列表」：
```
Border
└ Grid RowDefinitions="Auto,*"
  ├ TextBlock "消息"     ← 在滚动区**外**（固定）
  └ ScrollViewer         ← 只有列表滚动
```
抽 Section 时若把两层都包进去 → **标题会跟着滚**（用户报「消息二字随日志滚动」）。
**做法**：固定元素留在主 View，Section 只含可滚动部分。

#### ② `Auto` 行会吃掉兄弟行 —— 可变高控件所在行必须用 `*`

```xml
<!-- ❌ ListBox 高度自适应(NaN)时会按全部条目撑高，把后面的「总累计」顶出可视区 -->
<Grid RowDefinitions="Auto,Auto,Auto">

<!-- ✅ ListBox 只占剩余空间，汇总行固定可见 -->
<Grid RowDefinitions="Auto,*,Auto">
```
**代价（实测）**：用户报「历史累计花费没了」。

#### ③ `Expander` 的内容区宽度**不可控** —— 不要用它承载需要对齐的内容

实测数据：
```
Expander 标题栏      w=314   ← 正常
Expander 展开内容区  w=304   ← 窄 10px
内部 StackPanel      w=270   ← 再窄 34px（模板内边距左右各 17）
```
**三种补偿实测全部无效**：`Expander.Padding=0`、`Style` 覆盖
`Expander /template/ ContentPresenter`、内容 `Margin` 负值
（Avalonia **不支持负 Margin**，内容还会被裁）。

✅ **正确做法**：自绘折叠（宽度由外层决定，天然对齐）
```xml
<StackPanel>
  <ToggleButton IsChecked="{CompiledBinding AdvancedOpen}" .../>            <!-- 标题栏 -->
  <StackPanel IsVisible="{CompiledBinding AdvancedOpen}">...</StackPanel>   <!-- 内容 -->
</StackPanel>
```

#### ④ 构造函数里读 `DataContext` 一定是 `null`

父级（`MainWindow`）的 `DataContext` 注入**晚于**子控件构造：
```csharp
// ❌ 构造函数里：DataContext 还是 null → 判空失败 → 事件订阅从未生效
public MainView()
{
    InitializeComponent();
    if (DataContext is MainViewModel vm)          // ← 永远 false
        vm.Messages.CollectionChanged += OnMessagesChanged;
}

// ✅ 放 DataContextChanged，并先解绑避免重复订阅
DataContextChanged += (_, _) =>
{
    if (Vm is { } vm)
    {
        vm.Messages.CollectionChanged -= OnMessagesChanged;
        vm.Messages.CollectionChanged += OnMessagesChanged;
    }
};
```
**代价（实测）**：用户报「处在底部时新增日志没有自动滚动到底部」。

> **配套**：改完必须清 Avalonia 缓存再验证，否则改动不生效（见 H1c 旁的说明）：
> ```powershell
> Get-ChildItem src\ImgHub.App\obj -Recurse -Directory -Filter "Avalonia" |
>     ForEach-Object { cmd /c "rmdir /s /q `"$($_.FullName)`"" }
> ```

### D4h. 下拉框（ComboBox）一律绑 `SelectedIndex`，不要绑 `SelectedItem`（v0.5.31）

```xml
<!-- ❌ 会回写 null → VM 收到空串 → 快照存空值 + 下拉显示空白 -->
<ComboBox ItemsSource="{CompiledBinding AspectOptions}"
          SelectedItem="{CompiledBinding Aspect}"/>

<!-- ✅ 索引 setter 收到 -1 时可以安全忽略 -->
<ComboBox ItemsSource="{CompiledBinding AspectOptions}"
          SelectedIndex="{CompiledBinding AspectIndex, Mode=TwoWay}"/>
```

**为什么**：ComboBox 在 `ItemsSource` **被替换**时（切 provider/模型 → 选项集合变化）
会先把 `SelectedItem` 置 null 并**回写绑定源**。于是：

1. VM 的字符串属性被写成空串 → `SaveParamPreset` 把 `aspect=''` 存进 `config.json`
   → 用户看到「配置没随模型保存」（**真实回归**，用户在 config.json 里肉眼抓到）；
2. 空值虽会被后续收窄回退成合法值，但下拉的选中态已丢 → 界面「画幅」**空白**
   （同一根因的第二个症状）。

**做法**：
- VM 侧提供 `XxxIndex`（getter 用 `Array.FindIndex` 找当前值，**找不到返回 -1**）；
- setter 收到 `-1`（或越界）**一律忽略，绝不写空值**；
- 选项集合或当前值变化后调 `NotifyDropdownIndices()` 刷新选中态。

**相关**：`ComboBox` 的 `SelectedItem` 还要求项**引用相等**或实现相等比较；
字符串列表里"值相同但实例不同"时也会选不中 —— 索引绑定顺带绕开了这个坑。

### D4i. 代码里给控件上色**必须走 `DynamicResource` 绑定**，绝不能 `Foreground = null`（v0.5.31）

```csharp
// ❌ 此刻控件还没挂树 → TryFindResource 查不到主题字典 → 得到 null
tb.Foreground = TryFindResource(key) as IBrush;   // = null → **Avalonia 完全不绘制该文本**

// ✅ 延迟求值：挂树后自然解析，还会跟随主题切换
tb.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AppTextBrush"));
```

**为什么查不到**：画刷定义在 `App.axaml` 的 `ResourceDictionary.ThemeDictionaries`
（Light / Dark 两套）。在 **`DataContextChanged` 阶段**（控件尚未挂到窗口可视树）
调 `TryFindResource` 会返回 false。

**为什么 null 是"不绘制"而不是"用默认色"**：
Avalonia 里**显式赋 null 会覆盖**全局 `Style Selector="TextBlock"` 设的前景色，
而 null 前景色 = 不画。

**判据性特征**（下次见到可立刻对上）：**只有硬编码颜色的元素可见，其余全部消失**。
那次唯一可见的恰好是硬编码了 `Brushes.OrangeRed` 的行内代码（v0.5.31 截图即此形态）。

**验证别靠目测** —— 用离屏渲染数像素：
```csharp
c.Measure(size); c.Arrange(rect);
using var bmp = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
bmp.Render(c);
// bmp.CopyPixels 统计"与左上角背景色不同"的像素数；为 0 即不可见
```
实测对照：显式 null → **0 像素**；DynamicResource → **1430 像素**。

### D5. 中文显示必须显式配置字体链 + 内嵌兜底

```xml
<!-- 内嵌 CJK 字体优先，系统字体仅作回退 -->
<FontFamily x:Key="AppFont">avares://ImgHub.App/Assets/Fonts/NotoSansSC-Regular.ttf#Noto Sans SC,
    Microsoft YaHei UI, Noto Sans CJK SC, Segoe UI, sans-serif</FontFamily>
```

**代价**：Android 上中文渲染为**方框（豆腐块）** —— Avalonia Android CJK 回归
（issue #19868 / #20195）。`Microsoft YaHei` / `Noto Sans CJK SC` 在 Android 上都不存在，
唯一可靠方案是**内嵌字体**（`AvaloniaResource`）。

### D6. 内嵌字体必须是**静态**字体，不能用可变字体（VF）

```xml
<!-- ❌ 错误：VF 的 wght 轴默认值可能是 100(Thin) → 笔画极细、发虚模糊 -->
<FontFamily x:Key="AppFont">avares://.../NotoSansSC-VF.ttf#Noto Sans SC</FontFamily>

<!-- ✅ 正确：静态 Regular，字重确定 -->
<FontFamily x:Key="AppFont">avares://.../NotoSansSC-Regular.ttf#Noto Sans SC</FontFamily>
```

**代价**：`NotoSansSC-VF.ttf` 的 `fvar` 表里 `wght` 默认值是 **100**，
Avalonia 按该默认值渲染 → 中文能显示但**笔画细弱发虚**。
修复：用 fontTools 实例化到 wght=400 并子集化 —— `tools/make-static-font.py`
（16.95 MB → 7.15 MB）。当前内嵌的 `NotoSansSC-Regular.ttf` 即该脚本产物。

### D7. Avalonia 12 移除了 ExtendClientAreaChromeHints

自绘标题栏只需 `ExtendClientAreaToDecorationsHint` + `SystemDecorations="None"`；
照抄旧版写法加 `ExtendClientAreaChromeHints` 会编译报 **AVLN2000**。

---

## E. 交付约束

### E1. AOT exe 必须与原生 DLL 同目录

```
release/desktop-aot/
  ├─ ImgHub.Desktop.exe      ← 单独拷走会秒崩（0xC0000409）
  ├─ libSkiaSharp.dll          ← 必需
  ├─ av_libglesv2.dll          ← 必需
  └─ libHarfBuzzSharp.dll      ← 必需
```

### E2. 构建前先杀进程（Windows 文件锁）

```powershell
Get-Process -Name "ImgHub*" -ErrorAction SilentlyContinue | Stop-Process -Force
```

**代价**：验证时启动的 exe 未杀，publish 报 `MSB3027: 文件被 ImgHub.Desktop 锁定`。

### E3. 清理时绝不对项目目录用 -Recurse -Force

```powershell
# ❌ 灾难：误删整个 App 层
Remove-Item src\ImgHub.App -Recurse -Force

# ✅ 正确：精确到文件
Remove-Item src\ImgHub.App\ViewModels\MainViewModel.cs
```

**代价**：一次清理误删 App 层全部文件（含 1099 行 ViewModel）。

---

## F. 测试约束

### F1. 测试一律离线跑（不花钱）

```csharp
session.Config.Offline = true;   // 所有集成测试都开离线
```

离线用 `Placeholder.Png`（确定性占位图，相同 prompt+step 恒同图）。

> 注意：UI 上的「离线」是 **debug 功能**（`IMGHUB_DEBUG=1` 才显示），
> 但 `Config.Offline` 字段本身仍是公开可设的 —— 测试靠它跑离线，不受 UI 影响。

### F2. mock 环境要关掉 PDCurses 语义（Python 版遗留）

Python 版的列冲突测试假定 ncurses 坐标语义；GUI 版测试用真实 Avalonia，
无需此处理。但测试里的 `_IS_PDCURSES = false` 逻辑保留在 Python 版。

### F3. 每条铁律都要有回归测试

| 铁律 | 测试 |
|---|---|
| B1 待修改图第 1 位 | `Edit_WithImage_UsesCurrentAsReferenceFirst` / `RegionEdit_UsesAnnotatedImageAsReference` |
| B2 提交即记录 | `OfflineGenerate_ClearsPromptOnSuccess` |
| B3 质量档过滤 | `Quality_RoutesPerProvider_OpenRouterHasNoXhigh` / `QualityOptions_FollowProvider` |
| B4 配置纠正 | `Config_NullModel_FallsBackToDefault` / `ModelChoices_NeverLeaveModelBlank` |
| B5 上传失败不静默 | `ImageApi.GenerateApimartAsync` 抛错（集成测试间接覆盖） |
| C1 余额先于权限 | `Hint_403InsufficientQuota_IsBalanceNotPermission` |
| C4 写盘串行 | （集成测试间接覆盖） |
| D2 async void | `StatusBarCommands_AreExecutableAndSafe` |
| D1 未配置禁用按钮 | `Unconfigured_DisablesActions` |
| 成本口径一致 | `TotalCost_EqualsItemsSum_Always` / `TotalCost_UnknownProvider_DoesNotDrift` |
| 参考图生命周期 | `RefImages_ClearedAfterUse_ByDefault` / `RefImages_KeptWhenOptedIn` |
| 标注随图切换 | `RegionCache_PerImage` |
| 布局切换不丢标注 | `RegionCanvas_ShapeTransferPreservesData` |
| 价格预估语义 | `ModelStats_UnusedModel_ReturnsNull` / `Estimate_ShowsAvgAfterFirstUse` |
| 分隔符容错 | `PolishService_NormalizeDividers_FixesTypos` |

> **注意**：`Command` 只证明命令本身可执行，**不证明 UI 上有入口**。
> D4b/D4c 类缺陷需要人工检查 XAML（见 D4c 的检查法）。

---

## H. AOT / 裁剪约束（v5.24.0 新增 —— 代价最大的一类）

> **背景**：v5.23.0 的 AOT 产物下，用户点「生成」直接失败：
> `Reflection-based serialization has been disabled for this application.`
> 而**框架依赖构建完全正常** —— 这个差异让问题排查多花了很多时间。
> 以下每条都是踩过的坑。

### H1. AOT 下**禁止**反射式 JSON 序列化

Native AOT **默认关闭反射序列化**：

```csharp
JsonSerializer.IsReflectionEnabledByDefault == false   // AOT 下
```

任何走反射的重载在 AOT 产物里都会抛：

```
System.InvalidOperationException: Reflection-based serialization has been disabled
for this application. Either use the source generator APIs or explicitly configure
the 'JsonSerializerOptions.TypeInfoResolver' property.
```

```csharp
// ❌ 全部会在 AOT 下崩（实测复现）
JsonSerializer.Serialize(new Dictionary<string, object?> { ... });  // 动态 payload
JsonSerializer.Serialize(poco);                                    // 强类型也崩
JsonSerializer.Deserialize<AppConfig>(json);
new JsonSerializerOptions { WriteIndented = true }                 // 隐含反射解析器
```

```csharp
// ✅ 正确：按数据结构选方案
// ① 固定结构 → source generation（编译期生成读写器，零反射）
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(AppConfig))]
internal sealed partial class AppJson : JsonSerializerContext { }
var cfg = JsonSerializer.Deserialize(json, AppJson.Default.AppConfig);

// ② 动态结构（HTTP 请求体、state.json 行）→ JSON DOM（天生 AOT 安全）
var payload = new JsonObject { ["model"] = model, ["n"] = 1 };
var body = payload.ToReadableJson();
```

**为什么"上传成功但生成失败"**：`PostMultipartAsync`（上传，multipart）不含 JSON 序列化，
而 `PostJsonAsync`（生成/润色）含 —— 所以恰好在生成那步炸。

### H2：多语言文案必须走 `{CompiledBinding L[key]}`，切语言必须发 `Item[key]` 通知（v0.5.40）

**代价（三条都不报错）**：

| 写法 | 症状 |
|---|---|
| `{DynamicResource key}` 取文案 | 切语言后界面**不更新**（DynamicResource 只在附加资源树时解析一次） |
| `Localizer.Language` setter 发 `string.Empty` 或 `Item[key]` | 代码取值正确、**界面不变** —— 索引器绑定只认 **`Item`**（C# 索引器元数据名）。`string.Empty`（经典绑定约定）与 `Item[key]`（**看起来最像却是错的**）都**不触发刷新** |
| `{CompiledBinding [k], Source={x:Static ...}}` | **编译期** AVLN2000 `does not have an indexer`（CompiledBinding 忽略 `Source=`，按 `x:DataType` 推断） |
| `ContextMenu` 内用 `CompiledBinding L[k]` | AVLN2000 —— ContextMenu 是**独立视觉树**，须显式 `{Binding [k], Source={x:Static ui:Localizer.Instance}}` |
| **VM 里拼装文案的属性**仍是硬编码中文（如 `ToolNames` / `RegionCountText`） | 切到英/日时**这些地方不变**（界面"部分翻译"）。必须走 `Localizer.Instance[...]` |
| VM 文案属性改了 Localizer 但**没发通知** | 仍是旧语言 —— 计算属性需 `OnPropertyChanged(nameof(X))`，Avalonia 不会因为它读了 Localizer 就重取。统一放 `MainViewModel.NotifyLocalizedStrings()` |

**⚠️ 测"通知"必须测订阅侧的效果**：只断言"发过某个通知名"会**假通过**（v0.5.40 就这么被骗过一次）。要用假源穷举通知名、看哪种真能让绑定重取。

**诊断顺序**（"代码对、界面不对、不报错"类问题的通用套路）：
① 配置读到了吗 → ② 单例值对吗（打日志打印**你以为对的东西**）→ ③ **通知名对吗**（索引器 = `Item`） → ④ 绑定路径对吗。

完整记录：**[docs/i18n.md](i18n.md)**。

### H1b. `JsonArray.Add&lt;T&gt;(T)` 是泛型重载，会刷 IL2026/IL3050

`JsonArray` 有泛型 `Add<T>(T)`（内部**反射**把 T 包成 `JsonValue`）：

```
IL2026 RequiresUnreferencedCode —— 裁剪下可能丢成员
IL3050 RequiresDynamicCode      —— AOT 下需要运行时生成代码
```

```csharp
// ❌ AOT 发布刷出成片的 IL2026/IL3050
arr.Add(new JsonObject { ... });
arr.Add("https://x/1.png");

// ✅ 用显式接口实现（IList<JsonNode?>.Add）→ 零反射，警告彻底消除（已实测）
// 见 src/ImgHub.Core/JsonSafe.cs
arr.AddNode(new JsonObject { ... });
arr.AddString("https://x/1.png");
```

> 实测：这两条警告**功能上当时可用**，但它们会淹没真正的 AOT 问题
> （我们正是被"无害警告"误导过一次，见 H2）。且泛型重载对**非基元类型**确实不可靠。

### H1c. 不要在 C# 里手写 `new Binding{...}`（AOT 会失效）

**Plan B/C 的实战发现（v0.5.28）**：在 `HistoryPanel.axaml.cs` 里为了把
`ListHeight` 依赖属性绑到内部 `ListBox.Height`，写了：

```csharp
// ❌ 反射绑定 —— AOT publish 时报 IL2026 / IL3050
InnerList.Bind(HeightProperty,
    new Binding { Source = this, Path = nameof(ListHeight) });
```

AOT 输出（实测）：
```
warning IL2026: Using member 'Avalonia.Data.Binding.Binding()' which has
  'RequiresUnreferencedCodeAttribute' ... Consider using CompiledBindings instead.
warning IL3050: ... 'RequiresDynamicCodeAttribute' ... (同上)
```

**修法**：改到 XAML 里用**编译绑定**（`ElementName` 指向 Section 自身）：
```xml
<UserControl x:Name="Root" ...>
  <ListBox Height="{CompiledBinding ListHeight, ElementName=Root}"/>
</UserControl>
```

> ⚠️ 与 H1（JSON 反射序列化）**同源**：AOT 下**任何反射机制**都可能失效/被裁剪掉。
> 判断法：见到 `new Binding(...)` / `JsonSerializer.Serialize(object)` /
> `Activator.CreateInstance` / `Enum.Parse` 等就警觉。
> **改完必须真跑一次 AOT publish**，确认没有 IL2026/IL3050（H2）。

### H1d. `{Binding}` 默认**不做**编译期检查（Plan B 的出发点）

实测（v0.5.28）：即使 `csproj` 里开了
`<AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>`，
把绑定路径故意写成不存在的属性名，**编译仍然通过**（属性不存在时会**静默回退反射绑定**）。

```xml
<!-- ❌ 编译通过、运行时静默拿不到值 -->
<ItemsControl ItemsSource="{Binding NoSuchPropertyXYZ}"/>

<!-- ✅ 编译期报错，指明类型与行号 -->
<ItemsControl ItemsSource="{CompiledBinding NoSuchPropertyXYZ}"/>
```
```
AVLN2000: Unable to resolve property or method of name 'NoSuchPropXYZ'
          on type 'ImgHub.App.ViewModels.MainViewModel'. Line 22, position 32.
```

**约定（v0.5.28 起）**：UI 绑定一律用 `{CompiledBinding X}`。
- `DataTemplate` 内需先加 `x:DataType`（项类型**不是** VM！）
- 嵌套类型用 `+` 引用：`x:DataType="vm:MainViewModel+Message"`
- **例外**（保留普通 `{Binding}`）：空路径 `{Binding}`、`$parent[...]` 跨层查找、
  `ObservableCollection<string>` 这类**项类型无属性可绑**的场景

### H2. **不要**把 `IL2026`/`IL3050` 当作"无害警告"

v5.23.0 的 `docs/DELIVERY.md` 曾写「残留警告（无害，功能实测正常）」——
**这是错的**。它在框架依赖构建下"看起来正常"，在 **AOT 产物下直接导致功能不可用**。
**正确做法**：AOT 相关警告一律清零，而不是解释成"无害"。

### H3. **框架依赖构建通过 ≠ AOT 可用** —— 必须实跑 AOT 产物

```powershell
dotnet publish src\ImgHub.Desktop -c Release -r win-x64 `
    -p:PublishAot=true -p:DebugType=none -p:DebugSymbols=false -o release\desktop-aot
# 然后**真的运行它**（至少覆盖：启动 / 离线生成 / 真实请求 / 落盘 / 重启恢复）
```

**必测清单**（v5.24.0 实测项）：

| # | 动作 | 检查点 |
|---|---|---|
| 1 | 启动 | 窗口出现、自检显示 |
| 2 | 离线生成 | 出图 + 落盘 + 历史 + config 写入 |
| 3 | 真实生成 | 请求真的发出（不报 reflection 异常） |
| 4 | 润色 | 同上 |
| 5 | 改参数 → 重启 | 参数恢复（证明 config.json 真写了） |
| 6 | 重启后累计 | 不清零（证明 state.json 生效） |
| 7 | 统计/预估 | model_stats.json 读写正常 |

### H4. `catch { }` 会**掩盖** AOT 问题

其余反射序列化点（`SaveConfig`/`LoadConfig`/`SaveState`/`Log`/`ModelStats`）
原本都被 `catch { }` 静默吞掉 → 只表现为「参数不保存 / 统计不更新」，
**不报错、更难发现**。修 AOT 问题时**必须先把静默 catch 改成写日志**（见 L2 约束）。

### H5. `RegexOptions.Compiled` 在 .NET 8+ AOT 下**是安全的**

无需为 AOT 移除 `RegexOptions.Compiled`（实测正常）。
记在这里是为了**避免后人误改**（早期 .NET 版本它依赖动态代码生成）。

### H6. source-gen 必须显式声明 `SnakeCaseLower`

```csharp
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
```

漏了它 → 写出 PascalCase（`BatchN`）→ **与 Python 版 config.json 不再互通**，
用户既有配置读不出来。

### H7. JSONL 必须用**单行**序列化

```
history.jsonl / prompt_history.jsonl 是「一行一条记录」格式。
```

用带 `WriteIndented = true` 的选项写 → 一条记录被拆成多行 → **解析全失败**
（实测：历史项数直接变 0）。`JsonSafe` 因此提供两个入口：

- `ToReadableJson()` —— 缩进 + 中文可读（用于 `state.json` 这类"整体文件"）
- `ToJsonLine()` —— **单行** + 中文可读（用于 JSONL 与 HTTP 请求体）

### H8. XAML 编译错误**不会**让 `dotnet build` 失败，但会让应用**启动即崩**

v5.24.0 实测：`MainView.axaml` 里 DataTemplate 内误用
`Classes.hoverfx="{Binding EnableHoverAnimation}"`（模板上下文是 `PreviewThumb`/`string`，
解析不到 VM 属性）→ build 报 `AVLN2000` 但**退出码为 0**，
运行时直接 `XamlLoadException` + `No precompiled XAML found` 秒崩。

**对策**：
1. 每次改 XAML **必须真的启动一次**（不能只看 build 成功）；
2. 模板内要回指 VM 时用 `$parent[UserControl].DataContext.XXX`；
3. 关注输出里的 `AVLN` 警告/错误（易被 45 条 MVVMTK0034 淹没）。

---

## I. 日志与容错约束（v5.24.0 新增；v0.5.43 补 Debug 级别与全量审计）

### I1. **禁止**静默 `catch { }` —— 必须留痕（含原因与位置）

```csharp
// ❌ 错误：出了问题只表现为"功能悄悄失效"，无法排查
try { File.WriteAllText(path, json); } catch { }

// ✅ 正确：容错语义不变，但写清"原因 + 位置"
try { File.WriteAllText(path, json); }
catch (Exception ex)
{
    AppLog.Error($"写入失败：{path}", where: "Session.SaveConfig", ex: ex);
}
```

**代价（实测）**：v5.23.0 的 AOT 反射序列化 bug 之所以难定位，
正是因为 `SaveConfig`/`SaveState`/`Log`/`ModelStats` 的失败全被 `catch { }` 吞掉 ——
只表现为「参数不保存」，不报错。

**判定原则**：可以不阻断主流程，但**必须能事后从日志查到**。

#### I1-补充（v0.5.43）：全量审计结果 —— 29 处 → 3 处

对全仓库做了静默 `catch { }` 审计，**29 处**全部处理：

| 类别 | 数量 | 处理 |
|---|---|---|
| `ImageApi` 的**进度钩子**样板（`try { OnSubmitProgress?... } catch { }`） | 18 | 收拢为 `Notify(...)` 一个方法，内部记 **Debug**（异常仍不影响生成） |
| `MainViewModel`（清理/持久化/UI 辅助） | 8 | 各补 `AppLog.Debug(..., ex, where:)` |
| `MainView`（消息自动滚动） | 1 | 补 `AppLog.Debug` |
| **`AppLog.RollIfNeeded` 自己** | 2 | **保持静默**（日志系统不能反过来记日志 → 递归） |

**允许保留静默的唯一情形**：日志系统自身的清理动作。此时必须写注释说明原因。

```csharp
// ✅ 唯一可接受的静默（AppLog 内部）
catch { /* 滚动失败不影响写日志（日志系统不自我记录） */ }
```

### I2. 日志分级 + 落盘 + 位置

- 分级：`AppLog.Debug` / `AppLog.Info` / `AppLog.Warn` / `AppLog.Error`
- 落盘：`<数据目录>/log/imghub-yyyy-MM-dd.log`（按天、追加、单文件 5 MB 滚动、保留 10 个）
- **每条都带位置**：`where: "类名.方法名"`（用户要求「写清错误位置方便排查」）
- 异常记 `ToString()` 链（含 `InnerException` —— 网络错误常包在内层）
- **凭据脱敏**：`sk-or-v1-abc***`，**key 绝不落盘**
- 写盘失败**绝不抛**（降级为内存日志，内存保留 500 条）

#### I2-补充（v0.5.43）：**Debug 级别**（默认关闭）

`LogLevel.Debug = 0`（数值最小 → 默认被 `MinLevel = Info` 过滤掉）。

**打开方式**：环境变量 `IMGHUB_LOG_DEBUG=1`（兼容旧名 `IMGAGENT_LOG_DEBUG`），
或代码里 `AppLog.DebugEnabled = true`。

**用在哪**（不是随便加）：

| 场景 | 例子 |
|---|---|
| **被吞掉的非致命异常** ← 最值得记 | `ImageApi.Notify` 里钩子抛异常；VM 里清理/持久化失败 |
| 状态流转 | 浮层开合、主题切换、标注模式进出、工具切换 |
| 边界判定 | 钳制、回退、空值短路 |

**⚠️ 别把 Debug 当 Info 用**：高频循环里逐帧/逐像素打日志会拖慢界面。
判断标准：**这条日志在"正常情况下"有诊断价值吗？** 没有就用 Debug。

**验收方式**（不是看代码里有没有 `AppLog.Debug`）：

```powershell
$env:IMGHUB_LOG_DEBUG = "1"; dotnet run --project src\ImgHub.Desktop
# 点几下界面，然后看 <数据目录>/log/imghub-<日期>.log 里有没有 [DEBUG] 行
```

### I3. 网络错误必须**分类**并给可操作提示

```csharp
// ❌ 笼统：用户无从下手
throw new ApiError($"网络错误：{ex.Message}");

// ✅ 分类 + 具体建议（NetworkDiagnostics）
var kind = NetworkDiagnostics.Classify(ex);       // DNS / 无网络 / 拒绝 / TLS / 超时 …
throw new ApiError(NetworkDiagnostics.Describe(kind, ex));
// → "网络失败：域名解析失败（No such host is known）—— 检查网络、DNS 或被代理拦截"
```

要点：`HttpRequestException` 的**真正原因在 `InnerException`**（常是 `SocketException`），
只看外层消息会得到「An error occurred while sending the request」这类无用提示。

### I4. 重试语义：用**显式标记**，别靠 catch 兜

```csharp
// ❌ 错误：ApiError 被 catch 吞掉后继续循环 → 4xx 也会重试（实测 401 重试了 3 次）
catch (ApiError) when (attempt == _maxAttempts) { throw; }
catch (ApiError) { /* 继续 */ }

// ✅ 正确：ApiError.Retryable 显式标记（默认 false）
catch (ApiError ae) when (!ae.Retryable || attempt == _maxAttempts) { throw; }
catch (ApiError) { /* 只有可重试的才继续 */ }
```

分类：**429 / 5xx / 连接类（DNS·拒绝·不可达·超时）可重试**；
**业务 4xx（401/402/403/404/422）不重试**（浪费流量且掩盖真因 —— 铁律 C2）。

---

### I5. **不要重复写同一条日志**（v0.5.43）

`MainViewModel.Log(text, level, where)` **自己就会**调 `AppLog.*` 落盘。
所以下面这种"两行"写法会让**一次失败记两条日志**，其中一条还丢了异常详情：

```csharp
// ❌ 一次失败 → 两条日志（第二行丢 ex 详情）
AppLog.Error(ex.Message, where: "X", ex: ex);
Log($"落盘失败：{ex.Message}", MessageLevel.Err, where: "X");

// ✅ 一行搞定（Log 有 ex 重载，会带着堆栈一起落盘）
Log($"落盘失败：{ex.Message}", MessageLevel.Err, where: "X", ex: ex);
```

v0.5.43 已把 **10 处**这种双行合并为单行。

## J. 生图流程断点与恢复（v5.26.0 新增）

> **背景**：用户问「如果提交后意外退出了程序，图片去哪里找回」。
> 答案分两种情形 —— **异步接口能找回，同步接口只能如实告知**。
> 不要把后者说成能找回（诚实性要求）。

### J1. 核心机制：提交即落盘 task_id

```
APIMart 提交成功拿到 task_id
   ↓ 立即写 <数据目录>/pending_tasks.jsonl   ← 关键：不等轮询结束
重启 → ResumePendingAsync()
   ↓ 对每条 pending：GET /tasks/{id}        ← 只查询，**不重新提交**
   ↓ completed → 下载 → 落盘 → 入历史 → 标记 done
   ↓ 仍 pending/网络失败 → 保留，下次启动再试
```

> ⚠️ **恢复绝不重新提交** → **不会重复扣费**。代码注释与 UI 提示都写明。

### J2. 问题清单：现象 → 能否找回 → 怎么办

| # | 问题 | 现象 | 能否找回 | 解决方法 |
|---|---|---|---|---|
| **J2.1** | 提交后程序被关闭/崩溃（APIMart） | 图没落盘 | ✅ **能** | task_id 已落盘 → **重启自动取回** |
| **J2.2** | 轮询超时（>300s） | 提示超时 | ✅ **能** | 保留为 `timeout` → 稍后/重启重试 |
| **J2.3** | 轮询期间网络中断 | 网络错误 | ✅ **能** | 自动重试；耗尽后保留记录 |
| **J2.4** | DNS 失败/断网/代理 | 连不上 | ✅ **能** | `NetworkDiagnostics` 分类提示；记录保留 |
| **J2.5** | 服务端 5xx | 失败 | ✅ **能** | 退避重试 3 次；仍失败则保留 |
| **J2.6** | 余额不足（402/403） | 「余额不足」 | ⚠️ 任务未提交 | 余额先于权限（铁律 C1）；充值后重生成 |
| **J2.7** | 批量部分失败 | 少给图 | ⚠️ 部分 | 提示「N 个任务失败 / M 张下载失败」+ 原因 |
| **J2.8** | **同步接口（OpenRouter）退出** | 响应没收到 | ❌ **不能** | 同步接口**无任务号** → 重启时**如实提示**去 provider 后台核对 |
| **J2.9** | 任务已完成但图片 URL 失效 | 完成却没图 | ⚠️ 有限 | 保留记录 + 已获 URL；重试仍失败则记 `failed` |
| **J2.10** | 恢复时 key 已换/失效 | 401 | ✅ 换回 key 后能 | 提示「key 无效，换回后可重试」；**不静默丢弃** |

### J3. `pending_tasks.jsonl`（用户可直接编辑）

位置：`<数据目录>/pending_tasks.jsonl`，**一行一条、人类可读**：

```json
{"task_id":"tsk_abc","provider":"apimart","model":"gpt-image-2.5-flare",
 "prompt":"一只猫","quality":"low","aspect":"1:1","resolution":"1k",
 "output_format":"png","n":1,"created_at":1790038146.1,"status":"pending","note":""}
```

| `status` | 含义 |
|---|---|
| `pending` | 轮询中 / 待恢复 |
| `timeout` | 超时，**仍可重试** |
| `sent` | 同步接口已发出（**无法找回**，仅告知） |
| `done` / `failed` | 终态（`done` 自动清理；`failed` 保留供查） |

- **绝不写 API key**（只记 `provider`，key 恢复时现读）—— 有回归测试锁死
- 手动重试：把 `status` 改回 `pending`，重启即可

### J4. 为什么不能"什么都找回"

| 接口 | 有任务号？ | 能否找回 |
|---|---|---|
| **APIMart**（异步） | ✅ `task_id` | ✅ 能（本文机制） |
| **OpenRouter**（同步） | ❌ 无 | ❌ **不能** —— 协议上无任何句柄可查 |

对同步接口**如实告知**，不假装能恢复。这是诚实性要求，不是技术缺陷。

---

## G. 常见陷阱速查

| 症状 | 根因 | 对策 |
|---|---|---|
| 启动闪退（0xE0434352） | 窗口图标 PNG-in-ICO | D4：窗口用 PNG |
| 启动闪退（NullReference） | config 里 `model: null` | B4：Sanitize 归一化 |
| 中文显示方框 | Android 缺 CJK 字体 | D5：内嵌字体 |
| 中文发虚模糊 | 内嵌了**可变字体**（默认字重 100） | D6：改用静态字体 |
| Avalonia 12 编译报 ExtendClientAreaChromeHints | 该属性已移除 | 只用 ExtendClientAreaToDecorationsHint |
| publish 报文件锁定 | 旧进程未杀 | E2：先 Stop-Process |
| 参数改完重启就丢 | 节流失效 / snake_case 未映射 | 尾触发节流 + `[JsonPropertyName]` |
| AOT exe 单独拷走崩溃 | 原生 DLL 未随行 | E1：整目录分发 |
| 缩略图重新解码卡顿 | 每次 RefreshHistory 重建 | 列表 ≤200 条可接受；如需优化加 LRU |
| UI 上找不到某功能入口 | 按钮被放进了隐藏容器 | D4c：确认入口在可见容器内 |
| 转屏 / 改窗口大小后控件消失 | 只改了宽屏或窄屏其中一套 | D4b：两套布局同步改 |
| **右键菜单弹出但点不动** | `ContextMenu` 里用 `$parent[]` + 参数类型不符 | D4d：改用 `Click` 事件 |
| **自绘画布上画不出标注** | `Control` 不绘制则无命中区域，指针穿透 | D4e：填充透明矩形 |
| **点按钮后预览图上下跳** | 工具栏从 0 高度变出来，挤压 `2*` 的预览行 | 工具栏区域**高度恒定**（P6） |
| **AOT 下「生成失败：Reflection-based serialization…」** | 用了 `JsonSerializer` 反射重载 | H1：改 `JsonObject` / source-gen |
| **提交后程序退出，图找不到** | task_id 只在内存 | J1：提交即落盘 → 重启自动取回 |
| **轮询超时后任务丢失** | 超时即弃 | J2.2：保留 `timeout` 记录 |
| **同步接口（OpenRouter）退出后无法找回** | 协议上没有任务号 | J2.8：**如实告知**，勿承诺能恢复 |
| **AOT 下参数不保存 / 统计不更新（且不报错）** | 反射序列化被 `catch { }` 吞掉 | H1 + H4：写日志 |
| **AOT 发布刷出成片 IL2026/IL3050** | `JsonArray.Add<T>` 泛型重载 | H1b：`AddNode`/`AddString` |
| **历史项数变 0** | JSONL 用了缩进序列化（一条拆成多行） | H7：`ToJsonLine()` |
| **改完 XAML，build 成功但启动即崩** | XAML 编译错误不阻断 build | H8：必须真启动一次 |
| 「离线」选项找不到 | 它是 debug 功能，默认隐藏 | 设 `IMGHUB_DEBUG=1` 后在设置里出现 |
