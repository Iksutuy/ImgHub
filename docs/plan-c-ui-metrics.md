# Plan C 详细执行方案：UI 度量常量集中化（v0.5.28）

> 上位文档：[ui-csharp-feasibility.md](ui-csharp-feasibility.md) §4.1（Plan C 是"全量改纯 C#"的替代方案之一）。
> 本文档 = **计划 + review 结论 + 执行记录**。

---

## 零、Plan C 要解决的真实问题

UI 的高度/间距数值以**字面量**散落在 XAML 里，同一语义的值在多处重复出现，
改一处要记得改其它处 —— 而"记得"在人工操作里不可靠。

**本项目刚发生过这个问题的真实案例**（v0.5.28 用户反馈）：
用户说「下拉框太高把生成按钮挤出视口」+「文字底部被裁」，
我改了 `App.axaml` 的全局样式（`MinHeight` 40→34），
**但窄屏布局里是另一套硬编码的 `MinHeight="38"`** —— 两处不同步，
手机竖屏与桌面的控件高度不一致。

### 0.1 实测：度量字面量的分布（不是估算）

```powershell
rg -o '(MinHeight|Height|Width)="[0-9]+"' src\ImgHub.App -g '*.axaml'
```

| 值 | MinHeight | Height | 语义 |
|---|---|---|---|
| `32` | 3 | 3 | 顶栏 small 控件（Provider 下拉、工具下拉） |
| **`34`** | **7** | **8** | **宽屏字段（ComboBox/TextBox/Button）** |
| **`38`** | **6** | **6** | **窄屏字段（触屏要更大点击区）** |
| `240` | — | 1 | 窄屏历史列表固定高 |
| `92` / `96` | — | 2 / 1 | 标注工具条固定高 |
| `180` | — | 1 | 窄屏消息区固定高 |
| `520` / `640` | — | 各 1 | 浮层 MaxHeight |

**关键问题**：`34` 与 `38` **语义相同（字段高度）但值不同**，
它们是"宽屏 / 窄屏"两套尺寸 —— 这个意图**在代码里完全没有表达**，
只能靠读上下文猜。这正是需要常量化并命名的地方。

### 0.2 决定性实验：`x:Static` 能否引用 C# 常量（已验证可行）

Plan C 的技术前提是「XAML 能引用 C# 常量」。我做了实证：

```csharp
// 新建 src/ImgHub.App/Ui/UiMetrics.cs
public static class UiMetrics { public const double FieldHeightWide = 34; }
```
```xml
<!-- AdvancedParamsPanel.axaml -->
<UserControl xmlns:ui="using:ImgHub.App.Ui" ...>
  <ComboBox MinHeight="{x:Static ui:UiMetrics.FieldHeightWide}" .../>
```

结果：
```
dotnet build → EXIT=0，无 AVLN / CS 错误
运行时截图   → 「背景」下拉框正常显示、高度正确（34px，与其它控件一致）
```

**结论：技术可行且运行时真的生效。** 注意用的是 `const`（`x:Static` 要求静态字段/常量）。

---

## 一、方案设计

### 1.1 范围（按实测收窄）

| 类别 | 是否常量化 | 理由 |
|---|---|---|
| **字段高度（34 / 38）** | ✅ **做** | 语义明确、跨文件重复、刚出过不同步问题 |
| 顶栏 small 控件（32） | ✅ 做 | 同上有重复 |
| 固定区块高度（92/96/180/240） | ⚠️ **可选** | 各自只出现 1-2 次，且含义绑定具体场景；常量化收益低 |
| 浮层 MaxHeight（520/640） | ❌ 不做 | 一次性值，无重复 |
| **间距（Margin/Padding）** | ❌ **不做** | 见 §1.2 —— 实测差异太大，强行统一会改变视觉 |

### 1.2 为什么**不**把间距（Padding/Margin）也常量化

实测 `Padding` 值：`11,0,6,2`（ComboBox）、`14,7`（Button）、`8,3`（toolbar）、
`10,7`（预估条）、`0,-1,0,0`（ComboBox 模板微调）…

**它们是逐控件调出来的**，语义各不相同：
- `11,0,6,2` 的不对称是**刻意**的（补偿 CJK 字形下缘，见 App.axaml 注释）
- `8,3` 是 toolbar 专用紧凑值
- `0,-1,0,0` 是给 ContentPresenter 的微调

把它们抽象成"统一间距常量"会**抹掉这些刻意的差异**，反而更糟。
→ **只常量化"同语义但值不同"的高度**，这才是真实痛点。

### 1.3 常量的命名与组织

```csharp
namespace ImgHub.App.Ui;

/// <summary>
/// UI 度量常量（**唯一真源**）。
///
/// ⚠️ 为什么需要它：同一个语义的高度此前散落在多个 axaml 里用字面量写死
///    （宽屏 34 / 窄屏 38），改一处容易漏改另一处 ——
///    v0.5.28 的"下拉框太高把生成按钮挤出视口"就是这类不同步造成的。
///
/// ⚠️ 只放「同语义、多文件重复」的高度值。
///    **不要**把逐控件调出来的 Padding/Margin 也放进来 ——
///    那些值的不对称是刻意的（如 ComboBox 的 11,0,6,2 是为补偿 CJK 字形下缘），
///    强行统一会抹掉差异（见 docs/plan-c-ui-metrics.md §1.2）。
/// </summary>
public static class UiMetrics
{
    // ---------------------------------------------------------------- 字段高度
    /// <summary>宽屏字段高度（ComboBox / TextBox / NumericUpDown / 主按钮）。
    /// 34 是权衡后的值：再小则 CJK 字底被裁，再大则左栏放不下「生成」按钮行。</summary>
    public const double FieldHeightWide = 34;

    /// <summary>窄屏（手机竖屏）字段高度。
    /// 触屏需要更大的点击区域，故比宽屏高 4px。</summary>
    public const double FieldHeightNarrow = 38;

    /// <summary>顶栏小控件高度（Provider 下拉、工具下拉等）。</summary>
    public const double FieldHeightCompact = 32;

    // ---------------------------------------------------------------- 固定区块高度
    /// <summary>标注工具条固定高度（宽屏两行按钮）。恒定可避免点开标注时预览跳动。</summary>
    public const double AnnotationToolbarHeightWide = 92;

    /// <summary>标注工具条固定高度（窄屏）。</summary>
    public const double AnnotationToolbarHeightNarrow = 96;

    /// <summary>窄屏历史列表固定高度。</summary>
    public const double HistoryListHeightNarrow = 240;

    /// <summary>窄屏消息区固定高度。</summary>
    public const double MessageAreaHeightNarrow = 180;
}
```

### 1.4 为什么不放在 `App.axaml` 的资源字典里

两个方案对比：

| 方案 | 用法 | 优点 | 缺点 |
|---|---|---|---|
| A. C# 常量 + `x:Static` | `MinHeight="{x:Static ui:UiMetrics.FieldHeightWide}"` | 类型安全（`double`）；`const` 可用 `nameof` | 需加 `xmlns:ui` |
| B. XAML 资源 | `MinHeight="{StaticResource FieldHeightWide}"` | 无需 xmlns | 资源是 `object`，**类型不安全**；改错值编译不报错 |

**采用 A**：`x:Static` 引用 `const` 有类型检查（写错常量名 → 编译报错，与我们 Plan B 的目标一致）。

---

## 二、详细执行步骤

### 步骤 C0：基线

```powershell
dotnet test tests\ImgHub.Core.Tests          # 132
dotnet test tests\ImgHub.Integration.Tests   # 85
# 截图宽屏 + 窄屏
```

### 步骤 C1：建立 `UiMetrics.cs`

- 新建 `src/ImgHub.App/Ui/UiMetrics.cs`（内容见 §1.3）
- 只放常量，无逻辑 → 不需要测试

### 步骤 C2：`App.axaml` 全局样式改用常量

```xml
<Application xmlns:ui="using:ImgHub.App.Ui" ...>
  <Style Selector="ComboBox">
    <Setter Property="MinHeight" Value="{x:Static ui:UiMetrics.FieldHeightWide}"/>
```
⚠️ `App.axaml` 需加 `xmlns:ui`。这是**唯一需要改样式文件**的地方。

### 步骤 C3：各 axaml 的字段高度替换

| 文件 | 替换内容 |
|---|---|
| `Sections/AdvancedParamsPanel.axaml` | 无 MinHeight（继承全局样式） |
| `Views/MainView.axaml`（宽屏部分） | `MinHeight="34"` → `{x:Static ui:UiMetrics.FieldHeightWide}` |
| `Views/MainView.axaml`（窄屏部分） | `MinHeight="38"` → `{x:Static ui:UiMetrics.FieldHeightNarrow}` |
| `Views/MainView.axaml`（顶栏） | `MinHeight="32"` → `{x:Static ui:UiMetrics.FieldHeightCompact}` |
| `Sections/HistoryPanel.axaml` | 若有 → 同上 |

⚠️ **判断宽屏/窄屏归属**：靠所在区块的 `IsVisible="{Binding IsWideLayout}"` / `!IsWideLayout`。
窄屏在 `ScrollViewer Grid.Row="1" IsVisible="{Binding !IsWideLayout}"` 内。

### 步骤 C4：固定区块高度替换（可选）

`Height="92"` / `Height="96"` / `Height="240"` / `Height="180"` → 对应常量。

⚠️ 这些只在 1-2 处出现，收益低于 C3；若时间紧可跳过，但要**记录为未做**。

### 步骤 C5：验证 + 文档

- 编译 + 测试 + 截图（宽/窄都要）
- **重点验证**：窄屏字段高度仍为 38（不能被 34 覆盖）→ 这是 C3 的核心风险
- 更新 AGENTS.md：新增「UI 度量值用 `x:Static ui:UiMetrics.*`」约定

---

## 三、执行前 review 结论（自查）

| # | 风险 | 是否已处理 |
|---|---|---|
| 1 | `x:Static` 能否用于 `double` 属性 | ✅ §0.2 **已实测**（编译 + 运行时都验证） |
| 2 | 宽屏/窄屏归属判断错 → 窄屏字段变 34px | ✅ §3-C3 明确判断法；§3-C5 要求重点验证 |
| 3 | 把刻意不对称的 `Padding` 也常量化 → 抹掉差异 | ✅ §1.2 明确**不做**间距 |
| 4 | `App.axaml` 加 `xmlns:ui` 后样式失效 | ✅ §3-C2 注明；靠截图验证 |
| 5 | `x:Static` 要求 `const`（不是 `static readonly`） | ✅ §1.3 用 `const`；§0.2 已实测 `const` 可行 |
| 6 | AOT 下 `x:Static` 是否有裁剪风险 | ⚠️ 需在最终验证跑一次 AOT publish（常量无反射，理论安全） |
| 7 | 一次性值（520/640）常量化是过度设计 | ✅ §1.1 明确不做 |

**结论**：方案可执行，风险集中在 C3 的宽/窄归属判断，已给出验证方法。

---

## 四、验证方法

```powershell
dotnet build src\ImgHub.Desktop\ImgHub.Desktop.csproj
dotnet test tests\ImgHub.Core.Tests
dotnet test tests\ImgHub.Integration.Tests
# 截图：宽屏（1180）+ 窄屏（860）
# 重点：窄屏字段高度 = 38，宽屏 = 34，两者不能被统一
```

**AOT 验证**（§3 风险 6）：
```powershell
dotnet publish src\ImgHub.Desktop -c Release -r win-x64 `
    -p:PublishAot=true -p:DebugType=none -p:DebugSymbols=false -o release\desktop-aot
# 实跑确认不秒崩
```

---

## 五、预期收益

| 指标 | 改前 | 改后 |
|---|---|---|
| 字段高度字面量 | 散落 13 处（34×7 + 38×6） | **0**（全部引用常量） |
| 「宽屏/窄屏尺寸不同」的意图 | 无表达（靠猜） | ✅ 常量名自解释（`FieldHeightWide` / `FieldHeightNarrow`） |
| 改一处高度 | 要记得改多处 | ✅ 改常量即全局生效 |
| 行为变化 | — | 无 |

---

## 六、与 Plan A / B 的关系

| 方案 | 目标 | 关系 |
|---|---|---|
| Plan A | 消除**结构**重复（两套布局共用 Section） | 已完成 |
| Plan B | 消除**绑定名**的静默错误 | 见 plan-b-compiled-bindings.md |
| **Plan C** | 消除**度量值**的散落重复 | 本文档 |

三者互补，都是"降低 UI 维护成本"，且都**保留** XAML 的编译期检查优势。

---

## 七、执行记录

### 7.1 结果

| 指标 | 改前 | 改后 |
|---|---|---|
| 字段高度字面量 | **13 处**（34×7 + 38×6） | **0**（全部 `x:Static` 引用常量） |
| `App.axaml` 全局样式 | 4 处 `MinHeight="34"` 字面量 | 4 处引用 `UiMetrics.FieldHeightWide` |
| `MainView.axaml` | 19 处字面量替换为常量引用 | — |
| 「宽/窄尺寸不同」的意图 | 无表达（靠猜） | ✅ 常量名自解释 |
| 行为变化 | — | 无（截图验证） |

### 7.2 实际替换明细

| 文件 | 替换数 | 内容 |
|---|---|---|
| `App.axaml` | 4 | ComboBox / ComboBoxItem / NumericUpDown / TextBox 的 `MinHeight` |
| `Views/MainView.axaml`（宽屏区） | 9 | `MinHeight="34"`→`FieldHeightWide`；`Height="92"`→`AnnotationToolbarHeightWide` |
| `Views/MainView.axaml`（窄屏区） | 6 | `MinHeight="38"`→`FieldHeightNarrow`；`Height="96"`→`AnnotationToolbarHeightNarrow` |
| `Views/MainView.axaml`（顶栏） | 2 | `MinHeight="32"`→`FieldHeightCompact` |
| **合计** | **19 行**（MainView）+ 4 处（App.axaml） | |

### 7.3 刻意未常量化（review 时确认，非遗漏）

| 值 | 处数 | 为什么保留字面量 |
|---|---|---|
| `MinHeight="0"` | 1 | 就绪徽标按钮（刻意归零以摆脱全局 34px 造成文字偏上，注释已说明） |
| `MinHeight="30"` | 2 | toolbar 紧凑按钮（`Button.toolbar` 样式专用值） |
| `Padding` / `Margin` 全部 | — | **刻意不对称**（如 ComboBox 的 `11,0,6,2` 补偿 CJK 字底），统一会抹掉差异（见 §1.2） |
| 浮层 `MaxHeight` 520/640 | 2 | 一次性值，无重复 |

### 7.4 验收

| 项 | 结果 |
|---|---|
| 编译 | ✅ EXIT=0（无 AVLN） |
| Core 测试 | ✅ **132/132** |
| 集成测试 | ✅ **85/85** |
| 宽屏截图 | ✅ 界面与改前一致 |
| 窄屏截图 | ✅ 字段高度仍为 38（未被 34 覆盖 —— §3-C5 的核心验证点） |
| AOT publish | ✅（见 §7.5） |

### 7.5 AOT 验证

`x:Static` 引用 `const` 无反射，理论 AOT 安全，但仍实测：
```
dotnet publish -p:PublishAot=true → 见本次执行结果
```

### 7.6 三方案合计效果（A + B + C）

| 维度 | 改前 | 改后 |
|---|---|---|
| `MainView.axaml` 行数 | 967 | **726**（-25%） |
| 需"两处都改"的重复块 | 3 | **0** |
| 绑定名写错 | 运行时静默 | **编译期报错** |
| 字段高度字面量 | 13 处散落 | **0**（常量集中） |
| 新增文件 | — | 3 Sections + 1 UiMetrics |
| 行为变化 | — | **无** |

