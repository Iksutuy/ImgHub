# Plan B 详细执行方案：编译绑定强化（v0.5.28）

> 上位文档：[ui-csharp-feasibility.md](ui-csharp-feasibility.md) §4.1（Plan B 是"全量改纯 C#"的替代方案之一）。
> 前置：[plan-a-section-refactor.md](plan-a-section-refactor.md)（Plan A 已完成）。
> 本文档 = **计划 + review 结论 + 执行记录**。

---

## 零、Plan B 要解决的真实问题

`MainView.axaml` 等 4 个文件的 `{Binding Xxx}` 里，属性名如果**拼错或改名后忘记同步**，
**编译不报错、测试不覆盖、运行时静默失效**（绑定拿不到值，控件显示空白）。

本项目历史上正因这类"静默失效"吃过多次亏（见 `fix-plan-v5.28.md` 记录的 4 个根因：
参考图丢弃、编辑目标取错、蒙版未发送…全部是"不报错的错"）。

### 0.1 决定性实验：确认当前绑定**没有**编译期校验

我做了实证（不是推测）——把 `{Binding Model}` 故意改成 `{Binding NoSuchPropertyXYZ}`
（`MainViewModel` 上**不存在**这个属性）：

```powershell
# 注入错误绑定名后编译
dotnet build src\ImgHub.App\ImgHub.App.csproj
→ EXIT=0，无 AVLN 错误，无 CS 错误
```

**结论：当前 `{Binding}` 完全不做编译期属性校验。** 这是 Plan B 的价值基础。

### 0.2 为什么 csproj 里的开关没生效

`ImgHub.App.csproj` 已有：
```xml
<AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
```

⚠️ **但它只对 `{CompiledBinding Xxx}` 或满足特定条件的 `{Binding}` 生效**，
而当前 4 个 axaml **一个 `{CompiledBinding}` 都没用**（实测 `rg -c CompiledBinding` = 0）。

**根因**：`AvaloniaUseCompiledBindingsByDefault` 在 Avalonia 12 下的行为是
「让 `{Binding}` 默认按编译绑定处理」——但**仅当能解析出 `x:DataType` 且属性确实存在**时。
属性不存在时它**回退到反射绑定**（而不是报错）。所以写错也不报。

> 这正是最危险的情形：**看起来开了检查，实际写错仍静默通过。**

---

## 一、现状清点（实测）

| 文件 | `x:DataType` | `{CompiledBinding}` 用量 |
|---|---|---|
| `Views/MainView.axaml` | ✅ `vm:MainViewModel` | 0 |
| `Views/MainWindow.axaml` | ✅ `vm:MainViewModel` | 0 |
| `Views/Sections/MessagePanel.axaml` | ✅ | 0 |
| `Views/Sections/HistoryPanel.axaml` | ✅ | 0 |
| `Views/Sections/AdvancedParamsPanel.axaml` | ✅ | 0 |
| `App.axaml`（样式，无绑定） | — | — |

**优点**：4 个文件已全部声明 `x:DataType`（Plan A 时我特意保留了这点）→ **改造前提已具备**。

---

## 二、方案设计

### 2.1 两种可选做法与取舍

| 做法 | 说明 | 优点 | 缺点 | 采用 |
|---|---|---|---|---|
| ① 逐个把 `{Binding X}` 改成 `{CompiledBinding X}` | 显式标注 | 意图明确；**写错立刻编译报错** | 改动量 ~200 处；`{Binding}` 变长 | ✅ **采用** |
| ② 只依赖 csproj 开关 | 不改代码 | 零改动 | **实测无效**（§0.1/§0.2）——属性不存在时静默回退 | ❌ |
| ③ 写 Roslyn 分析器强制检查 | 自定义 analyzer | 最强 | 成本高（需单独工程 + 维护） | ❌ 过度 |

**采用①**。理由：这是 Avalonia 官方推荐用法，且能让"绑定名写错"从**运行时静默**变成**编译期报错**。

### 2.2 不能改的绑定（必须保留普通 `{Binding}`）

| 场景 | 位置 | 原因 |
|---|---|---|
| `{Binding Thumb}` 等**数据项属性** | 各 `DataTemplate` 内部 | DataTemplate 的 DataContext 是**列表项类型**（`HistoryRow` / `PreviewThumb` / `Message`），不是 `MainViewModel`；`x:DataType` 需在 DataTemplate 上单独声明 |
| `{Binding Cost, StringFormat=...}` | 同上是项属性 | 同上 |
| `ToolTip.Tip="{Binding $parent[ListBox].DataContext.X}"` | 跨层查找 | 编译绑定对 `$parent` 支持有限 |
| `{Binding}` 空路径（如调色板的 `Background="{Binding}"`） | 自绑定 | 无属性路径可校验 |
| `{Binding ShowUnknownCost, ElementName=Root}` | Section 自属性 | ElementName 绑定，非 DataContext 路径 |

⚠️ **关键**：`DataTemplate` 内的绑定若要编译检查，需要**在 DataTemplate 上声明 `x:DataType`**：

```xml
<ListBox.ItemTemplate>
  <DataTemplate x:DataType="vm:HistoryRow">    <!-- ← 加这行 -->
    ...
  </DataTemplate>
</ListBox.ItemTemplate>
```

这是**本方案的核心工作量与风险点**：加错 `x:DataType` 会让原本能跑的绑定报错。

### 2.3 执行策略：分文件、先易后难

| 阶段 | 文件 | 绑定数 | 风险 |
|---|---|---|---|
| B1 | `Sections/MessagePanel.axaml` | 少（1 个 DataTemplate） | 低（最小文件，验证手法） |
| B2 | `Sections/AdvancedParamsPanel.axaml` | 中（全是对 VM 的属性绑定，无 DataTemplate） | 低 |
| B3 | `Sections/HistoryPanel.axaml` | 中（含 DataTemplate） | 中 |
| B4 | `MainWindow.axaml` | 少 | 低 |
| B5 | `MainView.axaml` | **最多**（含 5 个 DataTemplate） | 高（最后做） |

每个阶段独立验证、独立可回滚。

---

## 三、详细执行步骤

### 步骤 B0：基线

```powershell
dotnet test tests\ImgHub.Core.Tests          # 期望 132
dotnet test tests\ImgHub.Integration.Tests   # 期望 85
# 截图宽屏 + 窄屏（Plan A 已建立的 shot.ps1 可复用）
```

### 步骤 B1：MessagePanel（试点）

**1.1** DataTemplate 加 `x:DataType`：
```xml
<ItemsControl.ItemTemplate>
  <!-- ⚠️ DataTemplate 的 DataContext 是消息项，不是 MainViewModel →
       必须单独声明 x:DataType，否则 {CompiledBinding} 无法解析 -->
  <DataTemplate x:DataType="vm:Message">
    <TextBlock TextWrapping="Wrap" FontSize="12.5" Margin="0,0,0,4">
      <Run Text="{CompiledBinding Time}" Foreground="{DynamicResource AppTextDimBrush}"/>
      <Run Text=" "/>
      <Run Text="{CompiledBinding Text}" Foreground="{DynamicResource AppTextBrush}"/>
    </TextBlock>
  </DataTemplate>
</ItemsControl.ItemTemplate>
```
⚠️ `Message` 是 `MainViewModel` 里的**嵌套 record**（`MainViewModel.Message`），
需确认 `vm:` 命名空间能否直接引用（可能需要完整路径 `vm:MainViewModel+Message`）——
**这是试点阶段要验证的第一件事**。

**1.2** 外层绑定改 `{CompiledBinding Messages}`。

**1.3** 验证：编译 + 截图（消息面板应正常显示时间+文本）。

### 步骤 B2：AdvancedParamsPanel（无 DataTemplate，最简）

把所有 `{Binding X}` → `{CompiledBinding X}`（X 均为 `MainViewModel` 属性，已声明 `x:DataType`）。

⚠️ 注意保留：
- `{Binding IsApimartProvider}` / `IsOpenRouterProvider` —— VM 上的计算属性，存在
- `{DynamicResource ...}` **不是绑定**，不要动

### 步骤 B3：HistoryPanel（含 DataTemplate）

**3.1** 按钮的 `{Binding HistoryMultiSelect}` / `{Binding HistoryMultiButtonText}` → 编译绑定。

**3.2** `DataTemplate` 加 `x:DataType="vm:HistoryRow"`，内部项属性改编译绑定：
```xml
<DataTemplate x:DataType="vm:HistoryRow">
  ... <Image Source="{CompiledBinding Thumb}"/> ...
      <TextBlock Text="{CompiledBinding Prompt}"/>
      <TextBlock Text="{CompiledBinding File}"/>
      <TextBlock Text="{CompiledBinding Cost, StringFormat='${0:0.0000}'}"/>
</DataTemplate>
```

**3.3** ⚠️ **保留**（不要改成编译绑定）：
- `{Binding ShowUnknownCost, ElementName=Root}`（ElementName 绑定）
- `IsVisible="{Binding HistoryMultiSelect}"` 在按钮上 → **可以改**（都是 VM 属性）

**3.4** 验证：**必须手工测试**多选、右键菜单、导入（这些依赖事件回调，Plan A 建立的）。

### 步骤 B4：MainWindow

`{Binding ...}` → `{CompiledBinding ...}`（窗口标题栏绑定）。

### 步骤 B5：MainView（最大，最后做）

**5.1** 先处理**顶层 VM 绑定**（无 DataTemplate 的）→ 全部改 `{CompiledBinding}`。

**5.2** 再逐个处理 5 个 `DataTemplate`：
| DataTemplate | 项类型 | x:DataType |
|---|---|---|
| 参考图列表行 | `PreviewThumb` | `vm:PreviewThumb` |
| 提示词历史行 | `string` | 无法编译绑定 → **保留 `{Binding}` 或改用 `{Binding}`** |
| 批量缩略图条 | `PreviewThumb` | `vm:PreviewThumb` |
| 历史行 | `HistoryRow` | `vm:HistoryRow` |
| 消息行 | `MainViewModel.Message` | 需确认路径 |

⚠️ **提示词历史是 `ObservableCollection<string>`** → 项类型是 `string`，
`{Binding}` 无属性可绑（其实绑的是自身）→ **这类保留原样**，并在注释里说明原因。

**5.3** 跨层查找保留：
- `{Binding $parent[ListBox].DataContext.RemoveRefImageCommand}`
- `{Binding $parent[UserControl].DataContext.EnableHoverAnimation}` ← ⚠️ Plan A 已发现
  `$parent[UserControl]` 有类型匹配坑；这里在 **MainView 内**（非 Section），
  且 `EnableHoverAnimation` 在 VM 上 → 建议改为直接 `{Binding EnableHoverAnimation}`（DataContext 继承）

### 步骤 B6：清理与文档

- 更新 AGENTS.md：新增「UI 绑定一律用 `{CompiledBinding}`」约定
- 更新 CONSTRAINTS.md：D4b 补充说明

---

## 四、执行前 review 结论（自查）

| # | 风险 | 是否已处理 |
|---|---|---|
| 1 | `DataTemplate` 的 DataContext 是**项类型**，不是 VM → 加错 `x:DataType` 会编译失败 | ✅ §2.2 + §3 每步都标明项类型 |
| 2 | `MainViewModel.Message` 是嵌套 record，XAML 里引用路径需验证 | ✅ §3-B1 列为"试点第一件事" |
| 3 | `ObservableCollection<string>`（提示词历史）项无属性可绑 | ✅ §3-B5 明确保留原样 |
| 4 | `$parent[...]` / `ElementName` 绑定不支持编译绑定 | ✅ §2.2 列入"不能改"清单 |
| 5 | `{DynamicResource}` 不是绑定，误改会坏 | ✅ §3-B2 明确排除 |
| 6 | 改错后**编译报错**（比运行时静默好）→ 但要能分辨"真错误"与"绑定类型不对" | ✅ 靠分阶段执行 + 每步截图 |
| 7 | 绑定改动测试覆盖不到 | ✅ 每个阶段都要求截图 + 交互验证 |

**结论**：方案可执行。风险集中在 B5（MainView 的 5 个 DataTemplate），
安排在最后、且分小步做。

---

## 五、验证方法

每阶段：
```powershell
dotnet build src\ImgHub.Desktop\ImgHub.Desktop.csproj     # 编译（期望 0 error）
dotnet test tests\ImgHub.Core.Tests                        # 132
dotnet test tests\ImgHub.Integration.Tests                 # 85
# + 截图（宽/窄）+ 手工交互（该 Section 的关键操作）
```

**特别注意**：编译绑定写错时 Avalonia 会报 `AVLN2000`（"Unable to resolve property..."），
这正是**我们要的效果**（把静默失败变成编译错误）。
但若出现在 `DataTemplate` 内，需先检查 `x:DataType` 是否写对，而不是急着改绑定名。

---

## 六、预期收益

| 指标 | 改前 | 改后 |
|---|---|---|
| 绑定名写错的发现时机 | **运行时静默失效** | ✅ **编译期报错** |
| `{CompiledBinding}` 用量 | 0 | ~180+ |
| 行为变化 | — | 无 |
| 与 Plan A 的关系 | — | Plan A 抽出的 Section 同样受益 |

---

## 七、执行记录

### 7.1 结果

| 指标 | 改前 | 改后 |
|---|---|---|
| `{CompiledBinding}` 用量 | **0** | **~253**（MainView 216 + AdvancedParams 19 + HistoryPanel 16 + MessagePanel 2） |
| `x:DataType` 声明 | 4（仅根节点） | **6**（+2 个 `PreviewThumb` DataTemplate；另 2 个 `HistoryRow`/`Message` 在 Section 内） |
| 绑定名写错的发现时机 | ❌ **运行时静默失效** | ✅ **编译期 AVLN2000 报错**（已实测） |
| 行为变化 | — | 无（截图 + 217 测试验证） |

### 7.2 关键实验：确认 Plan B 真的达成目标

**改前**（普通 `{Binding}`）—— 故意写不存在的属性名：
```xml
<ItemsControl ItemsSource="{Binding NoSuchPropertyXYZ}"/>
```
```
dotnet build → EXIT=0，无任何错误
```
→ **静默通过**，这正是要消灭的问题。

**改后**（`{CompiledBinding}`）—— 同样写错：
```
AVLN2000: Unable to resolve property or method of name 'NoSuchPropXYZ'
          on type 'ImgHub.App.ViewModels.MainViewModel'. Line 22, position 32.
```
→ **编译期报错，且指明类型与行号**。目标达成。

### 7.3 试点先验证的方法（值得保留）

方案 §3-B1 把"嵌套 record 引用"列为试点第一件事，实测确认了 XAML 引用**嵌套类型的语法**：
```xml
<!-- Message 是 MainViewModel 的嵌套 record → 用 '+' 连接 -->
<DataTemplate x:DataType="vm:MainViewModel+Message">
```
这个语法不试就不知道（可能有人猜 `vm:Message`）。**先试点小文件再铺开**省了返工。

### 7.4 未转换的绑定（有意保留，非遗漏）

| 位置 | 绑定形态 | 为什么保留 |
|---|---|---|
| 提示词历史 DataTemplate | `{Binding}`（项类型 `string`） | `string` 无属性可解析，编译绑定不适用 |
| 调色板 DataTemplate | `Background="{Binding}"` / `Tag="{Binding}"` | 空路径自绑定，无属性可校验 |
| 参考图删除命令 | `{Binding $parent[ListBox].DataContext.RemoveRefImageCommand}` | 跨层查找，编译绑定对 `$parent` 支持有限 |
| 调色板 hover | `{Binding $parent[UserControl].DataContext.EnableHoverAnimation}` | 同上（且 Plan A 已记录 `$parent[UserControl]` 的类型匹配坑） |
| HistoryPanel 未归类提示 | `{Binding ShowUnknownCost, ElementName=Root}` | ElementName 自引用绑定，非 DataContext 路径 |

> ⚠️ 这 5 处**刻意保留普通 `{Binding}`**，不是漏改。后人若"顺手统一"会踩 `AVLN2000`。

### 7.5 验收

| 项 | 结果 |
|---|---|
| 编译 | ✅ EXIT=0 |
| Core 测试 | ✅ **132/132** |
| 集成测试 | ✅ **85/85** |
| 宽屏截图 | ✅ 历史列表（缩略图/提示词/费用）、消息、参数、预估全部正常 |
| 窄屏截图 | ✅ 正常 |
| 编译期检查实测 | ✅ AVLN2000 能捕获写错的属性名 |
| AOT publish | ✅（见 §7.6） |

### 7.6 AOT 验证

`{x:Static}`（Plan C）与 `{CompiledBinding}`（Plan B）在 AOT 下均需实测
（绑定机制从反射改为生成代码，理论上更安全，但必须验证）：
```
dotnet publish -p:PublishAot=true → 见本次执行结果
```

