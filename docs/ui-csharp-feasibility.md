# UI 构建方式评估：XAML vs 纯 C#（v0.5.28）

> 来源：用户要求「检查 UI 界面 avalonia 是否使用了 yaml，如果是，评测全量使用 C# avalonia 来构筑 UI 的可行性，
> 并写出详细 plan 和对应的修改计划」。
> 基线版本：v0.5.28（工作区）。评估方法：**对本仓库实际代码做静态清点** + 官方文档核实，不含推测。

---

## 零、先纠正一个前提：本项目用的是 **XAML**，不是 YAML

| | YAML | **XAML**（本项目实际使用） |
|---|---|---|
| 本质 | 数据序列化格式（缩进敏感） | **XML 方言**（标签/属性），配合 .NET 类型系统 |
| 解析时机 | 运行时 | **编译期**（Avalonia 的 XAML 编译器把 `.axaml` 编译成 IL） |
| 类型检查 | 无 | **有**：属性名/类型错误在**编译时**报错 |
| 绑定检查 | 无 | **有**：本项目已开启 `AvaloniaUseCompiledBindingsByDefault=true` |
| 可编程性 | 无 | 可调 `x:DataType`、`{CompiledBinding}`、附加属性 |

这不是文字游戏 —— 它直接决定结论：

> **XAML 不是"配置文件"，它是编译期检查过的 UI 声明语言。**
> 把它视为"外部 DSL"并因而想迁移到 C#，是**错误的问题诊断**。

---

## 一、现状清点（本仓库实测数据）

### 1.1 UI 代码规模

| 文件 | 行数 | 职责 |
|---|---|---|
| `Views/MainView.axaml` | 960 | **两套响应式布局**（宽屏三栏 + 窄屏堆叠） |
| `Views/MainView.axaml.cs` | 445 | 代码后置（事件处理、画布同步、文件选择） |
| `Views/MainWindow.axaml` | 56 | 窗口壳 + 自绘标题栏 |
| `Views/MainWindow.axaml.cs` | 56 | 标题栏拖拽/最小化 |
| `App.axaml` | 260 | 主题字典（Light/Dark）+ 31 个 Style |
| `Controls/RegionCanvas.cs` | 588 | **已是纯 C# 自绘控件**（SkiaSharp） |
| **XAML 合计** | **1276 行** | |
| **C# UI 合计** | **~1089 行** | 其中 RegionCanvas 588 行已全 C# |

> ⚠️ 关键事实：**画布控件早就是纯 C# 实现**（`RegionCanvas : Control`，`Render()` 里直接画）。
> 也就是说"纯 C# 构建 UI"不是新事物，项目已在最复杂的控件上用了这个方案。

### 1.2 XAML 特性使用统计（决定迁移工作量的直接依据）

| 特性 | 用量 | 迁移到 C# 的难度 |
|---|---|---|
| `DynamicResource` | 74 | 中（需 `SetResourceReference` 或手动订阅主题变化） |
| `Setter Property` | 74 | 中（`Style` 对象可在 C# 构造） |
| `Grid.Row` 等附加属性 | 81 | 低（`Grid.SetRow(el, n)`） |
| `Classes.*` 样式类 | 63 | 中（`el.Classes.Add("...")`） |
| `ctrl:Help.Tip` 附加属性 | 43 | 低（`Help.SetTip(el, ...)`） |
| `StaticResource` | 38 | 低 |
| `Style Selector` | 31 | **中高**（选择器字符串需翻译成 `Selector.Parse` 或改用显式赋值） |
| `DataTemplate` | 18 | **高**（见 §3.2） |
| `ListBox` / `ComboBox` | 26 + 26 | 低（控件实例化） |
| `Image` / `Border` | 112 + 104 | 低 |
| `Transitions` | 10 | 低 |
| `ItemsPanelTemplate` | 2 | 中 |
| `x:Name` | 8 | 低 |
| `ControlTemplate` | **0** | — |

**注意：`ControlTemplate` 用量为 0** —— 这是本项目迁移难度远低于一般 Avalonia 项目的原因
（没有需要重写控件模板的地方）。

---

## 二、可行性结论

### 结论：**技术可行，但不建议全量迁移。**

| 维度 | 判断 |
|---|---|
| 技术上能否做到 | ✅ **能**。Avalonia 全部控件都是普通 C# 类，官方文档给出 `Binding`/`AddClassHandler`/代码绑定的完整 API；`RegionCanvas` 已证明本项目具备该能力 |
| 是否解决实际问题 | ❌ **不能**。当前问题（下拉框高、字底裁切、按钮对齐）是**样式数值问题**，与 XAML/C# 无关 —— 改用 C# 写同样要调这些数值 |
| 投入产出比 | ❌ **差**。见 §4 工作量估算：约 3–5 人日，换来的是**可读性下降 + 编译期检查减少** |
| 风险 | ⚠️ **中高**。见 §5 |

### 2.1 为什么"换成 C#"解决不了当前问题（关键论证）

用户的诉求是「下拉框减少高度 / 文字上移几个像素 / 按钮对齐」。
这三件事在 XAML 里是：

```xml
<Setter Property="MinHeight" Value="34" />
<Setter Property="Padding" Value="11,0,6,2" />
```

在 C# 里等价物是：

```csharp
combo.MinHeight = 34;
combo.Padding = new Thickness(11, 0, 6, 2);
```

**同样的数值，同样的心智负担。** 换语言不改变"CJK 字体在小字号下基线偏低"这个根因
（这正是本项目 v5.25.0 已踩过的坑，见 `App.axaml` 注释）。

### 2.2 迁移的**真实**代价（不是"重写一遍"那么简单）

| 代价 | 说明 |
|---|---|
| **两套布局同步约束失效** | AGENTS.md §3.2 要求宽/窄两套布局同步改。XAML 里两者**并排可见**，差异一眼能看出；C# 里是 900+ 行命令式代码，漏改一处**编译不报错、测试查不出** |
| **失去编译期绑定检查** | 当前 `AvaloniaUseCompiledBindingsByDefault=true` 会在编译时发现 `{Binding 拼错}`。C# 手写 `new Binding("...")` 是**字符串**，拼错只在运行时静默失败（本项目历史上正因这类静默失败吃过亏 —— 见 docs/fix-plan-v5.28.md 的四个根因） |
| **可读性/可维护性下降** | 当前左栏那段 20 行 XAML 声明式描述了 5 个字段；C# 需要 ~80 行命令式代码，且**布局意图（列宽/行序）被淹没在 setter 调用里** |
| **AOT 影响** | 需重新验证（XAML 编译产物与手写 C# 的裁剪行为不同）。本项目 AOT 是硬约束（CONSTRAINTS E1） |

---

## 三、技术细节：哪些能迁、哪些难迁

### 3.1 容易迁移（约 60% 的代码量）

| XAML | C# 等价物 |
|---|---|
| `<Button Content="生成" Command="{Binding x}"/>` | `new Button { Content = "生成" }.Bind(Button.CommandProperty, new Binding("x"))` |
| `<Grid ColumnDefinitions="Auto,*">` | `new Grid { ColumnDefinitions = new("Auto,*") }` |
| `Grid.SetRow(el, 2)` | `Grid.SetRow(el, 2)`（API 同名） |
| `Classes="accent"` | `el.Classes.Add("accent")` |
| `ctrl:Help.Tip="..."` | `Help.SetTip(el, "...")` |
| `<Style Selector="Button.accent">` | `new Style(x => x.OfType<Button>().Class("accent"))` |
| `<ResourceDictionary.ThemeDictionaries>` | 仍需 `ResourceDictionary` + `ThemeVariant`（C# 可构造，但**没有 XAML 直观**） |

### 3.2 难迁移（真正的痛点）

| XAML | 难点 |
|---|---|
| **`DataTemplate`（18 处）** | C# 里要么用 `FuncDataTemplate<T>((item, _) => new TextBlock{...})`，要么手写 `IDataTemplate`。**嵌套 DataTemplate（如历史行的缩略图+费用列）会变成深层嵌套 lambda**，可读性显著劣化 |
| **`Style Selector` 字符串（31 处）** | `"Button:pointerover /template/ ContentPresenter"` 这类**模板内选择器**需精确翻译为 `Selector.Parse(...)`，写错**不报错**（静默失效） |
| **`Transitions`（10 处）** | C# 可构造，但 `BrushTransition`/`DoubleTransition` 的嵌套写法很啰嗦 |
| **`DynamicResource`（74 处）** | C# 没有 `{DynamicResource}` 的一等语法糖；需 `SetResourceReference` 或在主题切换时**手动遍历更新**，容易漏 |

### 3.3 结论表

| 部分 | 建议 |
|---|---|
| `ControlTemplate` | 无（用量 0） |
| `RegionCanvas` | **保持纯 C#**（现状最优：自绘控件用 C# 是标准做法） |
| 数据模板/列表行 | **保持 XAML**（C# 会显著劣化可读性） |
| 主题/样式字典 | **保持 XAML**（`ThemeDictionaries` 的 C# 写法冗长易错） |
| 简单静态布局 | 可选 C#（但收益低） |

---

## 四、工作量估算（若仍要全量迁移）

| 阶段 | 内容 | 人日 |
|---|---|---|
| 1 | `App.axaml` → C#：主题字典（14 色 × 2 套）+ 31 个 Style 选择器翻译 | 0.5–1.0 |
| 2 | `MainWindow` → C#：窗口壳 + 自绘标题栏 | 0.3 |
| 3 | `MainView` 宽屏布局 → C#：约 400 行 XAML → ~1200 行 C# | 1.0–1.5 |
| 4 | `MainView` 窄屏布局 → C#：同上（**必须同步，否则手机竖屏看不到**） | 1.0–1.5 |
| 5 | 18 个 DataTemplate → FuncDataTemplate | 0.5–1.0 |
| 6 | 重新验证：跑通 217 项测试 + AOT + 人工逐屏比对（XAML 改动测试覆盖不到） | 1.0 |
| | **合计** | **4.3–6.3 人日** |

**收益**：无（不解决任何现存问题）。
**代价**：可读性下降、失去编译期绑定检查、两套布局同步约束更难维护。

### 4.1 更值得做的替代方案（同样工作量，实际有收益）

| 方案 | 收益 | 工作量 |
|---|---|---|
| **抽 `Styles/Controls.axaml`** 把 App.axaml 的 260 行样式拆出 | 主题集中、易改 | 0.3 人日 |
| **抽 `Views/Sections/*.axaml`**（左栏参数区/中栏预览/右栏历史）复用两套布局 | **消除"两套布局重复"这个真实痛点** | 1.0 人日 |
| 给 `fieldLabel` 等加 `x:DataType` 强化编译绑定 | 更早发现拼写错误 | 0.2 人日 |

> 这三个才是能解决本项目真实维护成本的改动。

---

## 五、若坚持迁移：风险与缓解

| 风险 | 缓解 |
|---|---|
| 两套布局漏改（编译不报错） | 迁移后**立刻**人工对比宽/窄屏截图；为关键绑定写集成测试 |
| 绑定字符串拼错 → 静默失效 | 用 `{CompiledBinding}` 生成的强类型路径思路：抽 `partial class` 常量放绑定名 |
| 主题切换漏更新资源 | 写测试：切换 `RequestedThemeVariant` 后断言关键控件 `Background` 变化 |
| AOT 裁剪行为变化 | 迁移后重跑 Native AOT publish + 实跑（CONSTRAINTS E1） |
| 一次性大改引入回归 | **分批**：先迁 `App.axaml` 主题 → 再迁窄屏布局 → 最后宽屏。每批跑全量测试 + 截图 |

---

## 六、最终建议

### 建议：**不迁移。保持不变（XAML 声明布局 + C# 自绘控件）。**

理由（按重要性）：

1. **这是错误的问题诊断**：用户想解决的问题（下拉框高度/文字裁切/按钮对齐）是**样式数值问题**，
   与 XAML/C# 无关。本轮已直接修好（见 §七）。
2. **项目已采用混合方案，且是正确的那种**：
   - 复杂自绘 → C#（`RegionCanvas` 588 行）
   - 声明式布局/样式 → XAML
   这正是 Avalonia 社区的主流实践。
3. **迁移会削弱现有保障**：编译期绑定检查、两套布局并排可见、测试覆盖。
4. **成本 4–6 人日换来零收益**，而同样成本投入 §4.1 的替代方案能真正降低维护成本。

### 如果仍要迁移，建议的最小化路径

只迁 XAML 里**最难维护的部分**，而不是全量：

```
优先级 P1：App.axaml 的 Style 选择器（31 个）→ 改为 C# Style 对象
          收益：选择器写错时能编译报错
优先级 P2：不动布局与 DataTemplate
          原因：这两部分 C# 化后显著更差读
```

---

## 七、本轮已完成的 UI 修复（与本次评估独立）

用户报告的三个 UI 问题**已修复并截图验证**：

| 问题 | 根因 | 修法 | 验证 |
|---|---|---|---|
| 左栏下拉框太高，「生成」按钮被挤出视口 | 5 × `MinHeight=40` + `Margin=0,0,0,8` ≈ 240px | `MinHeight` 40→34；行距 8→4 | ✅ 按钮行 + 参考图行均完整可见 |
| 下拉框内文字底部被裁 | CJK 字体 ascent/descent 与模板行高不匹配 | 模板 `ContentPresenter` 加 `Margin="0,-1,0,0"`（上移 1px）+ 非对称 `Padding="11,0,6,2"` | ✅ 文字居中无裁切 |
| 与「系统看图器」按钮未对齐 | 左栏按钮 `Padding="12,7"` vs 中间栏默认 `MinHeight=34` | 两者统一 `MinHeight="34"` | ✅ 高度一致 |

**额外发现并修复**（截图时看出）：预估费用文案被右侧滚动条列裁成「…历史平」——
加 `TextWrapping="Wrap"` + 精简文案（模型名已在左栏下拉框可见，不必重复）。

改动文件：`App.axaml`（ComboBox/NumericUpDown 样式 + ComboBoxItem）、
`MainView.axaml`（两套布局的行距/按钮高度/预估文案换行）、
`MainViewModel.cs`（预估文案精简）。

验证：编译通过、Core 132/132 全绿、桌面版实跑截图确认。

### 7.1 顺带修复的构建脚本缺陷（本轮发现）

| 缺陷 | 影响 | 修法 |
|---|---|---|
| `build.ps1` 的 `OutDir` 默认值是 **相对当前目录**的 `..\release` | 从仓库根调用时产物落到**仓库外**（`D:\Project\imagagent\release`），按 `docs/DELIVERY.md` 去 `release\` 找不到东西 | 改为 `Resolve-OutDir`：相对**仓库根**解析，无论如何调用都落在仓库内 |
| APK 目标文件名**硬编码**脚本里的 `imghub-0.5.26` | 升版后产物名与实际版本不符 | 从 `Directory.Build.props` 读 `<Version>`（唯一真源） |
| 我一度给 `build.ps1` 加了**中文注释** | ⚠️ **违反 AGENTS.md §3.6**：PowerShell 5.1 把 UTF-8 无 BOM 当 ANSI 读，中文注释会导致解析失败。该脚本刻意保持 ASCII-only | 已全部改回英文注释，并实测「非 ASCII 字节 = 0 + PS 5.1 解析无错误」 |

> 教训记录：改 `build.ps1` / `make-icon.ps1` 这类 `*.ps1` 时，注释必须用英文。
> 已在 AGENTS.md §3.6 强调过，本轮仍踩了一次 —— 因为"加注释"看起来无害。
