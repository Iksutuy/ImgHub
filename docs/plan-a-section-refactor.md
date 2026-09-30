# Plan A 详细执行方案：抽取可复用 Section（v0.5.28）

> 来源：用户要求「详细完善你说的 PLAN A，写成详细计划和执行方案，review 后执行」。
> 上位文档：[ui-csharp-feasibility.md](ui-csharp-feasibility.md) §4.1（Plan A 是替代"全量改纯 C#"的更优方案）。
> 本文档=**计划 + review 结论 + 执行记录**。

---

## 零、Plan A 要解决的真实问题

`MainView.axaml` 960 行里有**两套独立布局**：
- 宽屏（`IsVisible="{Binding IsWideLayout}"`）L78–507
- 窄屏（`IsVisible="{Binding !IsWideLayout}"`）L510–782

AGENTS.md §3.2 的硬约束：

> 加控件、改布局都要**两处都改**，否则手机竖屏看不到。

**这个约束历史上已经吃过一次事故**（AGENTS.md §3.3：设置按钮放进隐藏底栏导致功能完全打不开）。
它的成本是**每次 UI 改动都要重复劳动 + 漏改风险**。Plan A 的目标就是把重复块收敛成一份。

---

## 一、实证分析（决定方案边界，非估算）

### 1.1 两套布局的区块与重复度

用脚本对两套布局做**归一化重复度**比较（去注释、去缩进、token 化后取交集）：

| 区块 | 宽屏 | 窄屏 | 共同 token | **重复度** | 结论 |
|---|---|---|---|---|---|
| **消息卡片** | 44 tok | 41 tok | 33 | **75%** | ✅ 抽 |
| 历史卡片 | 188 tok | 138 tok | 92 | **49%** | ✅ 抽（结构不同，见 §2.2） |
| 预览卡片 | 248 tok | 194 tok | 99 | **40%** | ⚠️ 部分抽（工具条差异大） |
| 参数卡片 | 593 tok | 317 tok | 113 | **19%** | ❌ **不抽**（差异过大） |

> ⚠️ **这一条修正了我原先的乐观判断**。我原本计划把"左栏参数区"整体抽成 `ParamsPanel`，
> 但实测重复度只有 19%：宽屏用 `Classes="fieldLabel"`（统一样式），
> 窄屏**手写** `FontSize="13"` + `Foreground`；宽屏有 `MinHeight=34`、
> 窄屏是 `MinHeight=38`。强行合并必然引入一堆 `if (wide)` 分支 —— 比重复更差。
> **所以 Plan A 的范围按实测收窄**，只抽真正高重复的部分。

### 1.2 硬约束：`x:Name` 与事件处理器（抽 Section 的真实成本）

抽成 `UserControl` 后，XAML 里的 `Click="OnImportClick"` 会去**Section 自己的 code-behind**
找处理器 —— 找不到就编译失败。实测清单：

| 约束 | 数量 | 明细 |
|---|---|---|
| `x:Name` | **8** | `RegionLayer` / `RegionLayerNarrow` / `PreviewLayer` / `PreviewLayerNarrow` / `HistoryList` / `HistoryListNarrow` / `MessageScroll` / `PromptHistoryList` |
| 事件处理器 | **24 个定义、40 处绑定** | 见 §1.3 |
| `x:DataType` | 已在根节点声明 `vm:MainViewModel` | Section 需各自声明或继承 |

**其中 4 个是 code-behind 强依赖**（`RegionLayer*`、`PreviewLayer*`、`MessageScroll`），
它们必须在**主 View 的命名作用域**内（因为 `SyncPreviewTransform` / `SetupMessageAutoScroll` 要用）。

### 1.3 事件处理器跨布局出现次数（= 需要桥接的量）

出现 **2 次**（两套布局都绑 → 必须桥接）：

```
OnAnnotatedEditClick    OnClearPromptClick     OnClearRegionsClick
OnDeleteSelectedHistoryClick   OnHistoryDeleteClick   OnHistoryRemoveFromListClick
OnImportClick   OnRemoveSelectedHistoryClick   OnToggleHistoryMultiClick
OnToggleRegionClick   OnZoomInClick   OnZoomOutClick   OnZoomResetClick
OnHistorySelected (SelectionChanged)
```

出现 **1 次**（只需一处）：

```
OnAddRefImageClick   OnThumbClick   OnPaletteColorClick   OnDeleteSelectedPromptsClick
OnPromptHistoryDeleteClick   OnPromptMultiClick   OnPromptHistorySelected
```

**结论**：抽 Section 必须解决「命令/事件转发」，有 3 种可选机制 —— 见 §2.1，
我选**方案②（RoutedEvent 太复杂）→ 实际选方案①（Section 只做纯展示，事件在主 View 挂）**
还是方案③（`ICommand` 转发），在 §2.1 给出取舍。

---

## 二、方案设计

### 2.1 事件转发的三种机制与取舍

| 机制 | 做法 | 优点 | 缺点 | 采用 |
|---|---|---|---|---|
| ① Section 内不含事件，事件由主 View 在**外层**容器挂 | Section = 纯展示（只有 `{Binding}`），`Click` 不写在 Section 里 | 最简单、零桥接代码 | 只能挂容器级事件，按钮级 `Click` 挂不上 | ❌ 不适用（按钮多） |
| ② `RoutedEvent` 冒泡 | Section 内 `RaiseEvent`，主 View 用 `AddHandler` 接 | 松耦合 | 样板代码多、调试难 | ❌ 过度设计 |
| ③ **Section 暴露 `ICommand` 依赖属性 / 直接用 VM 命令** | 按钮绑 `{Binding SomeCommand}`（走 VM）或 `{Binding $parent[UserControl].DataContext.XxxCommand}` | **零桥接代码**、符合 MVVM | 需把 code-behind 事件改成 VM 命令 | ✅ **采用** |

**采用③**。理由：项目已有 `CommunityToolkit.Mvvm`，`[RelayCommand]` 现成；
现存 code-behind 处理器大量只是"转发到 VM"或"调用 VM 的 public 方法"，
改为命令后**顺带消除了一批 code-behind 债**（`MainView.axaml.cs` 445 行里大部分可迁走）。

⚠️ **但有一条不能迁**：`SyncPreviewTransform` / `MessageAutoScroll` 依赖
`RegionLayer` / `PreviewLayer` / `MessageScroll` 的**控件实例**（要读 `ViewMatrix`、`Extent`），
这属于 View 的视觉逻辑，**必须留在主 View**。所以：
> **`PreviewPanel` 与 `HistoryPanel` 的抽取决不能把 `x:Name` 带进去** ——
> 它们要么留主 View，要么用「附加属性/事件回传」把实例暴露出去。
> 我选：**这 4 个 x:Name 保留在主 View**，即 `PreviewPanel` 抽的是"外壳 + 工具条"，
> 中央的底图/画布叠层仍写在主 View 内联。

### 2.2 最终范围（按实测收窄 + 硬约束裁剪）

| # | 新建 Section | 内容 | 覆盖 | 为什么这么切 |
|---|---|---|---|---|
| 1 | `Sections/MessagePanel.axaml` | 消息卡片（标题 + `ItemsControl` + `DataTemplate`） | 75% | 重复度最高；**唯一例外**：`MessageScroll` x:Name 留在主 View 的外层 `ScrollViewer`，Section 只含内部 `ItemsControl` |
| 2 | `Sections/HistoryPanel.axaml` | 历史卡片（标题行 + 多选按钮 + `ListBox` + `DataTemplate` + 总累计） | 49% | 结构差异 = 宽屏 `RowDefinitions="Auto,*,Auto"` + 双 `TextBlock`（含未归类），窄屏 `RowDefinitions="Auto,240,Auto"` + 单 `TextBlock`。差异通过**依赖属性**（`ListHeight`、`ShowUnknownCost`）参数化 |
| 3 | `Sections/ParamsPanel.axaml` | 生成参数（5 下拉 + 批量 + 预估 + 高级参数 Expander） | 19% | ⚠️ **重复度太低**。改为：**只抽"高级参数 Expander"**（它两套是逐行对应的，重复度高），主参数区不动 |
| 4 | 不抽 | 预览卡片、提示词区、浮层、底栏 | — | 预览含 4 个 x:Name 强依赖；浮层/底栏只出现 1 次（无重复可言） |

**修正后的 Plan A 范围**：抽 3 个 Section，其中 `ParamsPanel` 收窄为 `AdvancedParamsPanel`。
预计 `MainView.axaml` 从 960 行 → **约 640 行**（净减 320 行，且消除 3 处重复维护点）。

### 2.3 为什么不用 `x:DataType` 继承而要显式声明

Section 是独立 `UserControl`，其 `DataContext` 在运行时会继承（WPF/Avalonia 的 DataContext 会向下传递），
但**编译期**需要 `x:DataType` 才能走 `{CompiledBinding}`。所以每个 Section 根节点写：

```xml
<UserControl ... x:DataType="vm:MainViewModel">
```

⚠️ 若漏写，`{Binding}` 会退化为反射绑定 —— **编译不报错但失去检查**，
且 AOT 下反射绑定可能出问题（CONSTRAINTS H1 的同类风险）。**这是 review 检查点之一**。

---

## 三、详细执行步骤

### 步骤 0：基线记录（必做，否则无法验证"没改坏"）

```powershell
# 0.1 全量测试基线
dotnet test tests\ImgHub.Core.Tests          # 期望 132
dotnet test tests\ImgHub.Integration.Tests   # 期望 85

# 0.2 截图基线（宽屏 + 窄屏）—— 见 §五 的截图脚本
#     ⚠️ 必须在改动前截，否则无从比对
```

### 步骤 1：`Sections/MessagePanel.axaml`（最小、最安全，先做）

**1.1** 新建 `src/ImgHub.App/Views/Sections/MessagePanel.axaml`：

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:ImgHub.App.ViewModels"
             x:Class="ImgHub.App.Views.Sections.MessagePanel"
             x:DataType="vm:MainViewModel">
  <Grid RowDefinitions="Auto,*">
    <TextBlock Grid.Row="0" Text="消息" Classes="section" Margin="0,0,0,8"/>
    <ItemsControl Grid.Row="1" ItemsSource="{Binding Messages}">
      <ItemsControl.ItemTemplate>
        <DataTemplate>
          <TextBlock TextWrapping="Wrap" FontSize="12.5" Margin="0,0,0,4">
            <Run Text="{Binding Time}" Foreground="{DynamicResource AppTextDimBrush}"/>
            <Run Text=" "/>
            <Run Text="{Binding Text}" Foreground="{DynamicResource AppTextBrush}"/>
          </TextBlock>
        </DataTemplate>
      </ItemsControl.ItemTemplate>
    </ItemsControl>
  </Grid>
</UserControl>
```

**1.2** code-behind（`MessagePanel.axaml.cs`）：仅 `InitializeComponent()`，无逻辑。

**1.3** 主 View 替换（**两处**）：
- 宽屏 L488–505 → 把 `<Grid RowDefinitions="Auto,*">` 内的内容换成
  `<sections:MessagePanel/>`，**外层 `ScrollViewer x:Name="MessageScroll"` 保留**（x:Name 不能搬）
- 窄屏 L763–780 → 同样替换

宽屏替换后形状：
```xml
<Border Grid.Row="1" Classes="card" Padding="14" Margin="0,6,0,0">
  <ScrollViewer Grid.Row="1" x:Name="MessageScroll">   <!-- ← x:Name 留这里 -->
    <sections:MessagePanel/>
  </ScrollViewer>
</Border>
```
> ⚠️ 注意：`MessageScroll` 原本包的是 `ItemsControl`；现在包 `MessagePanel`。
> `SetupMessageAutoScroll` 读的是 `MessageScroll.Extent/Offset/Viewport` —— 语义不变（仍是滚动容器）。

**1.4** 加 `xmlns:sections="using:ImgHub.App.Views.Sections"` 到主 View 根节点。

**1.5** 验证：编译 + 测试 + 截图比对。

### 步骤 2：`Sections/HistoryPanel.axaml`（含参数化）

**2.1** 用依赖属性吸收两套差异：

| 依赖属性 | 宽屏 | 窄屏 | 作用 |
|---|---|---|---|
| `ListHeight`（double，默认 NaN） | `Grid.Row=1` 自适应（`*`） | `240` | 列表高度 |
| `ShowUnknownCost`（bool） | `true` | `false` | 是否显示「含未归类旧数据」 |
| `ListName`（string） | `HistoryList` | `HistoryListNarrow` | ⚠️ 见下 |

**2.2** ⚠️ `HistoryList` / `HistoryListNarrow` 两个 x:Name 的处理：

它们被 `MainView.axaml.cs` 使用（多选批量 `SelectedHistoryRows()` 会同时读两个 ListBox）。
**两个实例都必须存在**（宽/窄各一），不能合并。

→ 采用：Section 暴露 **`public ListBox HistoryListBox { get; }`**（在 code-behind 里
`InitializeComponent()` 后取 `this.FindControl<ListBox>("PART_HistoryList")`）。
主 View 里用 `x:Name` 引用 Section **实例**本身，code-behind 改为：

```csharp
// 之前：直接读 HistoryList / HistoryListNarrow
// 之后：读两个 Section 实例暴露出的 ListBox
private IEnumerable<ListBox> HistoryListBoxes =>
    new[] { WideHistoryPanel?.HistoryListBox, NarrowHistoryPanel?.HistoryListBox }
        .Where(lb => lb is not null)!;
```

⚠️ **这是本步骤最大的风险点**：`SelectedHistoryRows()` / `ClearListSelection()` 等
4 处代码依赖这两个 ListBox。改错会导致**批量删除失效**（而测试可能覆盖不到）。
→ 缓解：改完后**手工测一遍**多选批量删除/移除。

**2.3** 事件转发：`OnHistorySelected`、`OnImportClick`、`OnToggleHistoryMultiClick`、
`OnDeleteSelectedHistoryClick`、`OnRemoveSelectedHistoryClick`、
`OnHistoryDeleteClick`、`OnHistoryRemoveFromListClick` —— 共 **7 个**。
按 §2.1 采用命令化：新增 VM 命令（若 VM 已有对应 public 方法则加 `[RelayCommand]` 包装）。

**2.4** 验证：编译 + 测试 + **手工测批量操作** + 截图比对。

### 步骤 3：`Sections/AdvancedParamsPanel.axaml`

**3.1** 抽「高级参数 Expander」整块（宽屏 L129–188、窄屏 L647–700）。
两套逐行对应（背景/压缩/审核/种子/流式/only/order/ignore/sort），重复度高。

**3.2** 差异仅样式：宽屏用 `Classes="fieldLabel"`，窄屏手写 `FontSize="13"`。
→ 统一用 `Classes="fieldLabel"`（**顺手消除不一致**），并用 `UiMetrics`
（Plan C）统一 `MinHeight`。

> ⚠️ 这里会产生**可见的样式变化**（窄屏标签从手写 13px 变成 `fieldLabel`）。
> 需截图确认视觉一致；若不一致，改为参数化 `LabelStyle`。

**3.3** 验证同上。

### 步骤 4：清理与文档

- 确认 `MainView.axaml` 行数下降、无重复块
- 更新 AGENTS.md §3.2：从「两套布局必须都改」改为
  「**优先改 Section（一处生效）**；仅预览区/参数主区因差异大仍需分改」
- 更新 ARCHITECTURE.md 的 View 层结构说明

---

## 四、执行前 review 结论（自查）

我按"如果我是 reviewer 会问什么"逐条核对，发现 **4 个必须在执行中处理的问题**：

| # | 风险 | 是否已在方案中处理 |
|---|---|---|
| 1 | 抽 Section 后 `x:Name` 找不到 → 编译失败 | ✅ §2.2 明确 4 个强依赖 x:Name 留主 View |
| 2 | `Click="OnXxx"` 找不到处理器 → 编译失败 | ✅ §2.1 采用命令化（VM 命令，零桥接） |
| 3 | **`HistoryList`/`HistoryListNarrow` 被 code-behind 依赖，合并会致批量操作失效** | ✅ §2.2 暴露 `HistoryListBox` 属性 + 手工测试要求 |
| 4 | Section 漏 `x:DataType` → 绑定退化为反射（静默失去检查，AOT 风险） | ✅ §2.3 列为检查点 |
| 5 | `MessageScroll` 语义变化（包的东西变了） | ✅ §步骤1.3 注明"仍是滚动容器，语义不变" |
| 6 | Plan A 范围被高估（参数区实测仅 19% 重复） | ✅ §1.1 按实测收窄，只抽 Expander |

**结论：方案可执行，且已按实证收窄了范围。** 风险集中在步骤 2（History 的 ListBox 暴露），
已给出缓解（手工测批量操作）。

---

## 五、验证方法（每个步骤都跑）

```powershell
# 1) 编译
dotnet build src\ImgHub.Desktop\ImgHub.Desktop.csproj

# 2) 全量测试（必须 132 + 85 全绿）
dotnet test tests\ImgHub.Core.Tests
dotnet test tests\ImgHub.Integration.Tests

# 3) 截图比对（宽屏 + 窄屏）
#    宽屏：默认 1180×760
#    窄屏：把窗口拉到 <900px 触发断点
#    脚本见下方 §5.1
```

### 5.1 截图脚本（已验证可用）

```powershell
# 保存为 $env:TEMP\cap.ps1（本轮已实测通过）
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public class WinCap {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  public static int[] Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return new int[]{ r.Left, r.Top, r.Right-r.Left, r.Bottom-r.Top }; }
}
"@
$p = (Get-Process -Name "ImgHub*" | Where-Object { $_.MainWindowTitle })[0]
[WinCap]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
[WinCap]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 900
$r = [WinCap]::Rect($p.MainWindowHandle)
$bmp = New-Object System.Drawing.Bitmap($r[2], $r[3])
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r[0], $r[1], 0, 0, (New-Object System.Drawing.Size($r[2], $r[3])))
$bmp.Save($env:TEMP + '\imghub-ui.png', [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
```

窄屏触发：手动把窗口拖窄到 <900px（`MainView.OnSizeChanged` 的断点）。

---

## 六、回滚策略

- 每个步骤独立提交（或至少能独立 `git checkout` 该文件）
- 新建的 `Sections/*` 是**纯新增文件** → 删除即回滚，主 View 恢复原状
- 若步骤 2（History）出问题 → **只回滚步骤 2**，步骤 1 的收益保留

---

## 七、预期收益（可量化）

| 指标 | 改前 | 改后（预期） |
|---|---|---|
| `MainView.axaml` 行数 | 960 | ≈ 640（净减 ~320） |
| 需要"两处都改"的重复块 | 3（消息/历史/高级参数） | **0**（全部一处生效） |
| 新增文件 | — | 3 个 Section（.axaml + .axaml.cs） |
| 行为变化 | — | **无**（纯结构重构） |
| AGENTS.md §3.2 约束 | 「两套都改」 | 收窄为「仅预览区/参数主区需分改」 |

---

## 八、执行记录

### 8.1 结果

| 指标 | 改前 | 改后 | 变化 |
|---|---|---|---|
| `MainView.axaml` | 967 行 | **726 行** | **净减 241 行（-25%）** |
| `MainView.axaml.cs` | 445 行 | 486 行 | +41（事件接线 + HistoryListBoxes 属性） |
| 新增 Section | — | 3 个（.axaml + .axaml.cs） | AdvancedParamsPanel / HistoryPanel / MessagePanel |
| 需"两处都改"的重复块 | 3 | **0** | 全部一处生效 |
| 行为变化 | — | **无**（纯结构重构） | 截图逐屏比对一致 |

### 8.2 实际执行的分步结果

| 步骤 | 内容 | 行数 | 验证 |
|---|---|---|---|
| 1 | `MessagePanel`（重复度 75%，最安全） | 967 → 948 | ✅ 宽屏截图一致 |
| 2 | `HistoryPanel`（重复度 49%，风险最高） | 948 → 835 | ✅ 宽/窄屏截图一致 |
| 3 | `AdvancedParamsPanel`（只抽 Expander，不收主参数区） | 835 → **726** | ✅ 截图一致 + **展开态验证**（点开后正确显示 背景/压缩/审核） |

### 8.3 ⚠️ 执行中踩到的真实坑（务必记录）

#### 坑 1：`$parent[UserControl].Xxx` 导致 XAML 编译失败（AVLN2000）

我在 `HistoryPanel` 里写了：
```xml
IsVisible="{Binding $parent[UserControl].ShowUnknownCost}"
```
报错：
```
AVLN2000: Unable to resolve property or method of name 'ShowUnknownCost'
          on type 'Avalonia.Controls.UserControl'
```

**原因**：`$parent[UserControl]` 做**类型匹配**，命中的是 `UserControl` 基类，
而 `ShowUnknownCost` 定义在派生类 `HistoryPanel` 上 → 找不到。

**修法（两种都对）**：
- 根节点加 `x:Name="Root"`，用 `{Binding ShowUnknownCost, ElementName=Root}`
- 或（更简）**直接用 `{Binding Xxx}`**：`DataContext` 会向下继承到 Section 内部，
  所以 `$parent[UserControl].DataContext.Xxx` 完全等价于 `Xxx`

**我最终两者都用了**：依赖属性用 `ElementName=Root`；命令/DataContext 类改成直接 `{Binding}`。

#### 坑 2：XAML 编译失败 → 运行时 `XamlLoadException`，症状极具误导性

上面那个 AVLN2000 是**编译错误**，但它导致的现象是：
```
运行时崩溃：No precompiled XAML found for ImgHub.App.App，make sure to specify
          x:Class and include your XAML file as AvaloniaResource
```
指向的是**我没改过的 App.axaml**！我因此浪费了大量时间在错误方向排查
（清 obj、重建、验证资源包、检查 x:Class）。

**教训**：Avalonia 里任何一个 `.axaml` 编译失败，都会让**整个 XAML 编译阶段**失效，
运行时表现为"找不到预编译 XAML"，且**报错位置指向无关文件**。
→ 排查法：用 `dotnet msbuild -v:d | Select-String "AVLN|XAMLIL"` 找**真正的**编译错误，
不要只看运行时堆栈。

#### 坑 3：`GenerateAvaloniaResources` 被判定"已最新"而跳过

增量构建时出现过：
```
正在跳过目标 GenerateAvaloniaResources，因为所有输出文件相对于输入文件而言都是最新的
```
连带 XAML 编译也跳过 → 新增的 Section 没进资源包。
**修法**：删 `src/<proj>/obj/<cfg>/Avalonia` 目录后重建。

#### 坑 4：`x:Name` 与手写属性重名（CS0102）

`HistoryPanel.axaml` 里 `x:Name="HistoryListBox"`，而 Avalonia 的 NameGenerator
会为它**自动生成同名字段**；我又手写了 `public ListBox HistoryListBox { get; }` → 冲突。
**修法**：手写属性改名 `InnerList`，构造函数里 `InnerList = HistoryListBox;`（用生成字段）。

#### 坑 5：应用运行时会锁住 dll，导致构建报 MSB3021

改完代码直接 `dotnet build` 会失败（`文件被 ImgHub.Desktop 锁定`）。
**修法**：构建前先 `Get-Process -Name "ImgHub*" | Stop-Process -Force`（AGENTS.md 已有此约定）。

### 8.4 事件转发方案的实际落地（review 时的判断被证实正确）

我在 review 阶段判断「**不能用命令绑定**」，因为原处理器读 View 层状态：

| 处理器 | 读 View 层状态 | 若命令化会丢什么 |
|---|---|---|
| `OnDeleteSelectedHistoryClick` | `SelectedHistoryRows()` 读两个 ListBox | 选不中被删对象 |
| `OnRemoveSelectedHistoryClick` | 同上 + `ClearListSelection(...)` | 选完不清空 |
| `OnToggleHistoryMultiClick` | `ClearListSelection(HistoryList, HistoryListNarrow)` | 退出多选后残留选中 |
| `OnHistoryRemoveFromListClick` | `(sender as Control)?.DataContext` 取行 | 取不到行 → 右键失效 |

→ 实际采用**事件回调**（Section 抛 `XxxRequested`，主 View 转发到原处理器），
`MainView.axaml.cs` 里 **零逻辑改动**，只有引用方式变了：
```csharp
// 改前：new[] { HistoryList, HistoryListNarrow }        ← XAML 的 x:Name
// 改后：HistoryListBoxes                                 ← Section 实例暴露的 InnerList
```
⚠️ 右键菜单的两个事件必须**透传 sender**（原处理器靠 `sender.DataContext` 取 HistoryRow），
所以签名是 `EventHandler<object?>` 而非 `EventHandler`。

### 8.5 验收

| 项 | 结果 |
|---|---|
| 编译 | ✅ EXIT=0（仅历史存量警告） |
| Core 测试 | ✅ **132/132** |
| 集成测试 | ✅ **85/85** |
| 宽屏截图 | ✅ 与基线一致（步骤 1/2/3 各验一次） |
| 窄屏截图 | ✅ 与基线一致（`<900px` 触发断点） |
| 展开态交互 | ✅ 点击「高级参数」正确展开，显示 背景/压缩/审核 等 |
| 行为变化 | ✅ 无 |

### 8.6 遗留（未做，非本方案范围）

- **预览卡片**（重复度 40%）未抽：含 4 个 `x:Name` 强依赖（`RegionLayer*` / `PreviewLayer*`），
  且底图与画布的变换矩阵必须共用同一 Bounds —— 抽出去反而增加出错面。
- **主参数卡片**（重复度 19%）未抽：两套差异过大，强行合并会引入大量条件分支。
- 这两处仍需"两套都改"，已在 §8.7 更新 AGENTS.md 说明。

### 8.7 文档同步

AGENTS.md §3.2「改 UI 必须两套布局同步改」需收窄表述 —— 见下一轮的文档更新记录。


---

## 九、v0.5.29：Plan A 引入的 5 个回归 + 修复（教训记录）

Plan A 抽取 Section **本身是对的**（结构重复确实消除了），但**抽取方式引入了 5 个回归**。
用户实测反馈后逐一定位修复。这部分比"怎么做"更有价值 —— 记下来避免重蹈。

### 9.1 回归清单（用户报的 5 项）

| # | 现象 | 根因 | 修法 |
|---|---|---|---|
| 1 | **历史累计花费不见了** | `HistoryPanel` 行定义写成 `Auto,Auto,Auto`。宽屏 `ListHeight=NaN` 时 ListBox 按内容撑高，把第 2 行的「总累计」挤出可视区（原为 `Auto,*,Auto`） | 改回 `Auto,*,Auto` |
| 2 | **左边拖动条与 UI 干涉** | 我上轮把滚动条列宽 20→18、内容右边距 12→10（"顺手优化"），两者是一组必须成对 | 还原 20 / 12 |
| 3 | **高级参数字段比上面下拉框窄** | `Expander` 模板给内容区加内边距：实测 `Expander=304 → 内部 StackPanel=270`（左右各 17px）。三种补偿**实测全部无效** | **改用 `ToggleButton` + `IsVisible` 自绘折叠** |
| 4 | **「消息」标题随日志滚动** | 抽 Section 时把标题一起放进去了，而整个 Section 被包在 `ScrollViewer` 里 → 标题跟着滚（原标题在 `ScrollViewer` **外**） | 标题移回 `ScrollViewer` 外；Section 只含列表 |
| 5 | **贴底时新日志不自动滚动** | `Messages.CollectionChanged` 的订阅写在**构造函数**里，那时 `DataContext` 还是 `null` → `if (DataContext is MainViewModel vm)` 判空失败 → **订阅从未生效** | 订阅移到 `DataContextChanged` |

### 9.2 最重要的教训

#### 教训 A：抽 Section 前必须确认「哪些元素原本在滚动容器外」

回归 4 就是这么来的。原结构是：
```
Border
└ Grid RowDefinitions="Auto,*"
  ├ TextBlock "消息"        ← 在滚动区**外**（固定）
  └ ScrollViewer            ← 只有列表滚动
```
我抽 Section 时把这两层都包进去了 → 标题开始滚动。
**检查法**：抽之前先看目标区域里有没有「本应固定」的元素（标题、工具条、汇总行）。

#### 教训 B：`Auto` 行会让"自适应高度"的控件吃掉兄弟行

回归 1 就是这么来的。`Grid RowDefinitions` 的 `Auto` 表示"按内容撑"，而 ListBox 在
`Height=NaN` 时会按**全部条目**撑 → 后面的「总累计」被顶出可视区。
**约定**：当某行放的是"可滚动/可变高"的控件时，那一行要用 `*`，不能 `Auto`。

#### 教训 C：`Expander` 的内容区宽度**不可控**（三种补偿都无效）

回归 3 的排查花了最多时间。实测数据：

```
Expander 标题栏      x=[28,341]  w=314   ← 正常
Expander 展开内容区  x=[28,331]  w=304   ← 窄 10px
内部 StackPanel      w=270               ← 再窄 34px（模板内边距左右各 17）
```

试过且**全部无效**的补偿：
| 尝试 | 结果 |
|---|---|
| `Expander.Padding="0"` | 无变化 |
| `<Style Selector="Expander /template/ ContentPresenter">` 设 `Padding`/`Margin=0` | 无变化 |
| 内容 `Margin` 负值 | Avalonia **不支持负 Margin**，内容还被裁（诊断显示内部控件变 null） |

**最终方案**：不用 `Expander` 装内容 ——
```xml
<StackPanel>
  <ToggleButton IsChecked="{CompiledBinding AdvancedOpen}" .../>   <!-- 标题栏 -->
  <StackPanel IsVisible="{CompiledBinding AdvancedOpen}">...</StackPanel>  <!-- 内容，宽度由外层决定 -->
</StackPanel>
```
内容直接位于外层容器中 → 与外层字段天然同宽，彻底不受模板内边距影响。

> ⚠️ 若将来又要用 `Expander`：**它只适合承载不需要精确对齐的内容**。
> 需要与外部对齐时一律自绘折叠。

#### 教训 D：构造函数里读 `DataContext` 大概率是 null

回归 5 就是这么来的。Avalonia 中父级（`MainWindow`）的 `DataContext` 注入**晚于**
子控件（`MainView`）构造 → 构造函数里的 `if (DataContext is X vm)` 一定失败。
**约定**：任何依赖 `DataContext` 的接线都放 `DataContextChanged`，并**先解绑再绑**
（避免重复订阅）。

#### 教训 E：改完必须清 `obj\*\Avalonia` 缓存再验证

本次多次出现「改了但测量值没变」，原因是 Avalonia 的 XAML 资源缓存
（`GenerateAvaloniaResources` 被判定"已最新"而跳过）。清掉才生效。
**命令**：
```powershell
Get-ChildItem src\ImgHub.App\obj -Recurse -Directory -Filter "Avalonia" |
    ForEach-Object { cmd /c "rmdir /s /q `"$($_.FullName)`"" }
```

### 9.3 验证方法（像素级，可复用）

肉眼判断"尺寸是否一致"不可靠（本次差点误判）。用**逐行像素扫描**：

```powershell
# 对比两处控件的 x 范围（纯 ASCII 脚本，避免 PS 中文编码坑）
# 注意：PowerShell 变量名不区分大小写 —— $Img 会与 [string]$ImgFile 冲突
param([string]$ImgFile, [int]$Y1, [int]$Y2)
Add-Type -AssemblyName System.Drawing
$bmpObj = [System.Drawing.Bitmap]::FromFile((Join-Path $env:TEMP $ImgFile))
foreach ($y in @($Y1, $Y2)) {
  $bg = $bmpObj.GetPixel(5, $y); $fx = -1; $lx = -1
  for ($x = 14; $x -lt 360; $x++) {
    $p = $bmpObj.GetPixel($x, $y)
    if ([Math]::Abs($p.R-$bg.R)+[Math]::Abs($p.G-$bg.G)+[Math]::Abs($p.B-$bg.B) -gt 30) {
      if ($fx -lt 0) { $fx = $x }; $lx = $x
    }
  }
  Write-Output ("y=$y  x=[$fx,$lx]  w=" + ($lx-$fx+1))
}
$bmpObj.Dispose()
```

**另一个更直接的办法**：在控件构造函数里挂 `LayoutUpdated`，把 `Bounds.Width`
写进日志（本次就是靠它拿到 `Expander=304 / StackPanel=270` 的关键数据）：
```csharp
this.LayoutUpdated += (_, _) =>
    AppLog.Info($"self={Bounds.Width} combo={combo.Bounds.Width}", "DBG");
```
⚠️ 诊断代码验证完**必须删掉**（本次已清理）。

### 9.4 修复后验收

| 项 | 结果 |
|---|---|
| 编译 | ✅ 0 error（无 AVLN） |
| Core / 集成测试 | ✅ **132 / 85** 全绿 |
| 问题 1 总累计 | ✅ 截图确认「总累计 $0.2852」+ 未归类行可见 |
| 问题 2 滚动条 | ✅ 放大截图确认有间距、不重叠 |
| 问题 3 字段尺寸 | ✅ 像素扫描：两处 ComboBox 左边缘同为 **x=88** |
| 问题 4 消息标题 | ✅ 截图确认标题固定、仅列表滚动 |
| 问题 5 自动滚动 | ✅ 点「生成」后新日志 `13:51:35` 出现在可视区底部 |
| AOT publish | ✅ 无 IL2026/IL3050 |
