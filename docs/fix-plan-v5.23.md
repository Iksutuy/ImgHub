# 修复计划（第八轮 · v5.23.0）

> 来源：用户截图标注 + 8 条文字要求。
> 方法：**先实证定位根因，再动手；每完成一项 → 编译 + 测试 + UI 实测 check**。
> 目标版本：**v5.23.0**

---

## 零、诊断方法（本轮的证据链）

不靠猜测。用三层实证：

| 层 | 手段 | 用途 |
|---|---|---|
| 静态 | 通读 `MainView.axaml` / `MainViewModel.cs` / `RegionCanvas.cs` + codegraph 索引 | 定位可疑绑定与逻辑 |
| **运行时** | `ImgHub.Desktop.exe`（Release）+ 临时数据目录副本 | 复现真实行为，不碰用户数据 |
| **UI 探针** | 截图 + `UIAutomation`（控件树 / `IsEnabled` / `FromPoint`）+ 合成鼠标事件 | **量化**验证：按钮是否可达、菜单项是否可用、指针命中到哪个控件 |

> 工具为临时脚本，放 `%TEMP%`，**不入库**（符合 AGENTS.md §3.5）。

### 关键实证结果（修复依据）

```
# 历史右键菜单 —— 复现用户「右键无法点击」
history item rect: 874,224 331x65
非文字区右键 -> MenuItem 数 = 3
   '删除（含文件）'              en=False   ← 禁用！
   '仅从列表移除（保留文件）'    en=False   ← 禁用！
文字区右键 -> MenuItem 数 = 3（同上，都 en=False）

# 画布命中测试 —— 复现「编辑图片功能区内按钮无反应」
FromPoint(预览区中心) -> ControlType.Image   ← 命中的是下层 Image，不是 RegionCanvas
```

---

## 一、问题清单与根因

### 🔴 P1. 历史右键菜单两项**禁用**（用户：「右键无法点击」）

**现象**：右键弹出菜单，但「删除（含文件）」「仅从列表移除」是灰色，点了没反应。

**根因（实证确认，两个叠加）**：

1. **命令参数类型不匹配 → `CanExecute` 恒为 false**

```xml
<!-- MainView.axaml:308-313（当前写法） -->
<MenuItem Header="删除（含文件）"
          Command="{Binding $parent[ListBox].DataContext.DeleteHistoryItemCommand}"
          CommandParameter="{Binding}"/>       <!-- ← 这里是 HistoryRow -->
```

```csharp
// MainViewModel.cs:525 —— 参数声明为 Item
private void DeleteHistoryItem(ImgHub.Core.Models.Item item)
```

`ListBox.ItemsSource` 是 `ObservableCollection<HistoryRow>`，`{Binding}` 传的是 **`HistoryRow`**。
CommunityToolkit 生成的 `RelayCommand<Item>.CanExecute(HistoryRow)` 类型检查失败 → **禁用**。

2. **`ContextMenu` 不在可视树，`$parent[ListBox]` 解析不到**
   `ContextMenu` 是弹出层，其 `$parent[]` 追溯不到 `ListBox` → `Command` 为 null → 同样禁用。

**修复**：放弃绑定路径 + 类型转换的组合，改用 **code-behind 事件**（`Click`），
`MenuItem.DataContext` 天然继承自挂载控件（→ `HistoryRow`），无需跨层查找。
`CommandParameter` 不再需要。

**验收**：探针中两个 `MenuItem` 的 `IsEnabled == true`；点击后历史项减少且文件按预期删除/保留。

---

### 🔴 P2. 右键只有「文字」能弹菜单（用户额外要求 ①）

**根因**：`ContextMenu` 挂在 `TextBlock`（提示词历史）/ 局部 `StackPanel`（图片历史）上，
而非整行容器。

**修复**：把 `ContextMenu` 挂到 **DataTemplate 的根容器**，并给根容器
`Background="Transparent"` 保证**整行空白区**都参与命中测试。

**验收**：在行的**缩略图 / 空白 / 费用列**上右键均能弹出同一菜单。

---

### 🔴 P3. 画布收不到指针 → 标注画不上（用户：「编辑图片功能区内，全部无法正常使用」）

**现象**：进入编辑模式后，在预览图上拖动**画不出任何标注**；「用标注编辑」因此报
「还没有画任何标注」。

**根因（实证确认）**：`RegionCanvas : Control` 的 `Render` 在**没有标记时不绘制任何内容**，
而 Avalonia 的命中测试基于**已绘制的几何**：无绘制 → 不命中 →
指针事件穿透到下层 `Image`（`FromPoint` 证实返回 `Image`）。
`Control` 基类**没有** `Background` 属性，无法靠设背景解决。

**修复**：在 `Render` 起始处填充**透明矩形**覆盖整个控件，使其成为可命中区域：

```csharp
public override void Render(DrawingContext ctx)
{
    base.Render(ctx);
    // 使控件参与命中测试（否则指针穿透到下层 Image，标注画不上）
    ctx.FillRectangle(Brushes.Transparent, new Rect(0, 0, Bounds.Width, Bounds.Height));
    ...
}
```

**验收**：探针拖拽后 `RegionCountChanged` 触发（消息面板出现「已…」）；
截图可见半透明笔迹；「用标注编辑」不再报"没有画任何标注"。

---

### 🟡 P4. 点「编辑图片」时预览被压缩上移（用户额外要求 ④）

**根因**：宽屏中栏

```xml
<Grid RowDefinitions="Auto,2*,Auto,Auto,Auto">
  <!-- Row1 = 预览（2*，吸收所有剩余高度） -->
  <!-- Row3 = 编辑工具栏，IsVisible="{Binding RegionMode}" → 高度 0 ⇄ ~78 -->
```

`Row1` 是 `2*`，`Row3` 从 0 变 78 → `Row1` 减少 78 → **图片上移**。

**修复**：把 `Row3` 的高度**恒定化** —— 容器固定高度，内容按 `RegionMode` 显隐。
非编辑模式显示同一高度的引导文案（不留突兀空白）。

**验收**：探针记录点「编辑图片」**前后**「系统看图器」按钮的 `Y` 与预览 `Image` 的
`BoundingRectangle` 完全一致。

---

### 🟡 P5. 顶栏「就绪」名不副实（用户：「这么多功能都用不了，这里写个就绪？就绪了什么？」）

**根因**：`Status` 默认值硬编码 `"就绪"`，且徽标 `IsVisible="{Binding IsConfigured}"`
—— 只要**有 key 文件**就显示「就绪」，与「全功能是否真的可用」无关。

**修复**：新增**启动自检** `SelfCheck()`，逐项检查并在顶栏显示**真实的**就绪状态与原因：

| 检查项 | 不通过时的提示 |
|---|---|
| 生图 provider / API key | 「未配置 API key」 |
| 端点可达性（可选，不阻塞） | — |
| 数据目录可写 | 「数据目录不可写」 |
| 润色配置（可选） | 「润色未配置（可选）」 |

徽标文案：全通过 → 「就绪」；否则 → 「未就绪：<首个原因>」，
并给出「修复」入口（打开设置）。**不再无条件显示「就绪」**。

**验收**：探针检查徽标文本；清空 key 后应显示「未就绪：未配置 API key」而非「就绪」。

---

### 🟡 P6. 左栏下拉框字体底部被遮挡（用户额外要求 ⑤）

**根因**：`ComboBox MinHeight="32"`（宽屏 5 个 + 窄屏 5 个），
中文字体（内嵌 Noto Sans SC）行高较大，32px 下基线偏下、下缘被裁。

**修复**：左栏/窄屏「生成参数」区所有 `ComboBox`（以及 `NumericUpDown`）
`MinHeight` 32 → **38**，并同步 `Padding`。

**验收**：截图放大检查「模型/质量/画幅/分辨率/格式」文字下缘完整无裁切。

---

### 🟡 P7. 预览路径未对齐（用户额外要求 ⑥）

**现象**：路径紧贴「预览」标签，尾部被「编辑图片」按钮截断，视觉凌乱。

**修复**：路径列 `TextAlignment="Right"`、去掉左侧 `Margin`，
使路径**右端对齐**到「编辑图片」按钮左侧（即预览卡片内容右边界）。

**验收**：截图确认路径右端与按钮左缘对齐、无溢出。

---

### 🟡 P8. 「离线」复选框残留（用户额外要求 ⑦）

**根因**：v5.22.0 的 #3 声称「离线移入设置 → 开发者选项」，
但**左栏宽屏（L109）与窄屏（L465）两处复选框仍在**。

**修复**：删除两处复选框；`Offline` 保留为**内部状态**（设置里 `IMGHUB_DEBUG=1` 时可见），
测试仍可设 `Config.Offline`。

**验收**：截图中左栏不再出现「离线」；探针控件树中无该 `CheckBox`。

---

### 🟡 P9. 左栏滚动条过挤（用户：「这边滚动条UI太挤了」）

**根因**：左栏 `Grid ColumnDefinitions="*,14"`，滚动条仅 14px 且紧贴内容，
内容 `Margin="0,0,6,0"` 仍显拥挤（截图中滚动条压在 ComboBox 右缘）。

**修复**：滚动条列 14 → **18**，内容右边距 6 → **10**，
并给滚动条加内边距样式，使其与控件有呼吸间距。

**验收**：截图确认滚动条与下拉框之间有可见间隙。

---

### 🟡 P10. 「用标注编辑」按钮超出边界（用户：「图标超出边界」）

**根因**：编辑工具栏第二行按钮多（缩放 −/＋/复位 + 提示文字 + 清除 + 用标注编辑），
在约 375px 宽的中栏里**溢出**到卡片外（截图中「用标注编辑」被右边界裁切）。

**修复**：工具栏改为 `WrapPanel` 自动换行；缩短提示文字；
「用标注编辑」设为**醒目且不换行**（`MinWidth`），确保完整可见。

**验收**：截图确认「用标注编辑」四字完整、未越界。

---

### 🟡 P11. 历史删除缺确认 / 选项顺序（用户额外要求 ②）

**要求**：
- 「仅从列表移除」放在**前面**（破坏性小的在前）
- 「删除（含文件）」需**弹窗确认**

**修复**：
- 菜单顺序调整：**仅从列表移除（保留文件）** → **删除（含文件）**
- 新增确认浮层（VM `ConfirmOpen/ConfirmTitle/ConfirmMessage/ConfirmAction`），
  删除文件前弹出：「确认删除 N 个文件？此操作不可恢复（文件将从磁盘删除）」

**验收**：点击「删除（含文件）」→ 出现确认浮层；取消则不删；确认才删且文件消失。

---

### 🟡 P12. 历史 / 提示词历史**多选批量**（用户额外要求 ③）

**要求**：加入多选按钮，选择后批量「移除」或「删除」，并确认「是否完全删除 N 个项目」。

**修复**：
- 两个列表加「多选」ToggleButton（默认关，防误触）；开启后
  `ListBox.SelectionMode="Multiple"`，`SelectedItems` 绑定 VM 集合
- 出现批量操作条：`移除选中(N)`（仅列表）、`删除选中(N)`（含文件，走 P11 确认浮层）
- 提示词历史：`删除选中(N)`（确认「删除 N 条提示词记录？」）
- 不破坏原有单选行为（点击项仍切换预览）

**验收**：选 3 项 → 按钮显示 `(3)` → 确认浮层写「3」→ 执行后列表减少 3 项。

---

### 🟢 P13. 底栏加版本号（用户：「这里加上版本，未来可加点击GitHub链接」）

**修复**：
- `Directory.Build.props` 加统一 `<Version>`（5.23.0），
  VM 通过 `AssemblyInformationalVersion` 读取并暴露 `AppVersion`
- 底栏（或设置浮层底部）显示 `imghub v5.23.0`
- 预留 `RepositoryUrl` 常量（当前为空 → 纯文本；填值后可点击跳转）

**验收**：截图中可见 `v5.23.0`；版本与 `Directory.Build.props` 一致。

---

## 二、执行顺序与验收

| 序 | 项 | 改动文件 | 验收方式 |
|---|---|---|---|
| 1 | P1 右键命令可用 | `MainView.axaml` + `MainView.axaml.cs` | 探针 `IsEnabled==true` |
| 2 | P2 整行可右键 | 同上 | 探针在多处右键 |
| 3 | P11 顺序 + 确认浮层 | `MainView.axaml` + VM | 探针点菜单看浮层 |
| 4 | P12 多选批量 | `MainView.axaml` + VM + code-behind | 探针选 3 项执行 |
| 5 | P3 画布可命中 | `RegionCanvas.cs` | 探针拖拽出笔迹 |
| 6 | P4 预览不位移 | `MainView.axaml` | 探针比对前后 rect |
| 7 | P5 启动自检 | VM + `MainView.axaml` | 探针读徽标文本 |
| 8 | P6 下拉框调高 | `MainView.axaml` | 截图 |
| 9 | P7 路径右对齐 | `MainView.axaml` | 截图 |
| 10 | P9 滚动条间距 | `MainView.axaml` + `App.axaml` | 截图 |
| 11 | P8 移除离线框 | `MainView.axaml` | 探针控件树 |
| 12 | P10 工具栏不越界 | `MainView.axaml` | 截图 |
| 13 | P13 版本号 | `Directory.Build.props` + VM + XAML | 截图 |
| 14 | 全量回归 | — | 114 测试 + 手工过一遍 |
| 15 | 文档 + 提交 | `docs/` | — |

**每项完成即 check**：编译 0 错误 → 相关测试通过 → UI 探针/截图确认 → 才进入下一项。

---

## 三、风险与注意事项

| 风险 | 对策 |
|---|---|
| 改右键为 code-behind 会丢测试覆盖 | 命令**保留**为 `[RelayCommand]`，只把 XAML 的 `Command` 换成 `Click`；命令仍可被测试直接调用 |
| 多选改动可能破坏「点击切换预览」 | 多选默认关闭；单选模式行为不变，并加回归测试 |
| 确认浮层与现有浮层（设置/调色板/润色）互相干扰 | 复用同一叠加层模式，用独立 `IsVisible`，互斥打开 |
| P5 自检误报「未就绪」 | 端点可达**不做阻塞检查**（离线可用）；只校验本地条件 |
| P4 固定工具栏高度影响窄屏 | 窄屏预览行本就是固定 320，不受影响；只改宽屏 |
| 删除文件不可逆 | 确认浮层 + 文案明确「不可恢复」；不提供静默删除入口 |
| 触碰用户真实数据 | 实测一律用 `%TEMP%` 下的**数据目录副本**（`IMGHUB_HOME`） |

---

## 四、计划 review（写盘后复查，已定死的技术细节）

### R1. P1 改用 `Click` 后，命令与测试怎么办

`[RelayCommand]` **保留不动**（测试直接调用 `DeleteHistoryItemCommand.Execute(row.Item)`）。
只把 XAML 的 `Command=` 换成 `Click=`，code-behind 里取
`(sender as MenuItem)?.DataContext as HistoryRow → row.Item`。
`MenuItem` 的 `DataContext` 继承自挂载它的 `ContextMenu`（→ 宿主控件的 `DataContext`），
所以事件壳里能拿到 `HistoryRow`。**需实测确认**，不能想当然。

### R2. P3 的命中测试方案：首选 + 备选

- **首选**：`ctx.FillRectangle(Brushes.Transparent, fullRect)`。
  Avalonia 对**非 null 画刷**的绘制会建立命中区域（与 WPF 的 `Background=Transparent` 同理）。
- **备选（首选实测无效时）**：`RegionCanvas` 基类由 `Control` 改为
  `Panel`/`Decorator`（自带 `Background`，天然可命中）。
- **判据**：探针 `FromPoint(画布中心)` 必须返回 `RegionCanvas`（或其内部类型），
  且拖拽后消息面板出现标注相关日志。**两条都要满足**。

### R3. P4 非编辑模式下 Row3 放什么

固定 78px 高度后，非编辑模式不能留空洞。方案：同一容器内按 `RegionMode` 切换
① 编辑工具栏 / ② 一行淡色引导文案（「点「编辑图片」开始圈画要修改的区域」）。
这样高度恒定、信息有用。

### R4. P5 自检的判定口径（关键决策）

严格按用户原话「**全功能可用、端点全部配置才写就绪**」：

| 情形 | 徽标 |
|---|---|
| 全部检查通过 | 就绪 |
| 任一项未通过 | 未就绪：<首个未通过原因> |

其中「润色未配置」**也算**未就绪（因为不是"全功能可用"），文案注明「（可选）」。
**绝不**在功能不可用时显示「就绪」——这是用户抱怨的核心。
端点可达性**不做阻塞判定**（离线/无网也应能用本地功能）。

### R5. P12 多选用 code-behind 读 `SelectedItems`，不做绑定

Avalonia 的 `ListBox.SelectedItems` 双向绑定到 VM 集合容易出坑（引用替换、类型不符）。
改为 code-behind 直接读 `listBox.SelectedItems` 传给 VM 方法，最稳且可测。

### R6. P13 版本号读取要防脏

`AssemblyInformationalVersion` 可能带 `+<git-sha>` 后缀，读取时按 `+` 截断。
统一版本号来源为 `Directory.Build.props` 的 `<Version>`，
并同步 Android 的 `ApplicationDisplayVersion`（否则产物名与 APK 内版本不一致）。

### R7. 补充验收项（原计划遗漏）

- 编辑工具栏**每个按钮**都实测一次（调色板 → 浮层；清除 → 日志；缩放/复位 → 无异常）
- 「用标注编辑」在成功画标注后**不再**报"还没有画任何标注"
- 窄屏（<900px）下逐项复测（本轮改动涉及两套布局）

---

## 五、不在本轮范围（如实记录）

| 项 | 原因 |
|---|---|
| 真实快捷键绑定 | 用户未要求；属"未来"项，本轮不加 |
| GitHub 链接可点击 | 用户明确说"**未来**可加"；本轮只加版本号 + 预留常量 |
| Android 真机验证 | 无设备 |

---

## 六、执行结果（全部完成 · 逐项实测）

**版本**：v5.22.0 → **v5.23.0**（`Directory.Build.props` 的 `<Version>` 为唯一真源）

| 项 | 改动 | 实测证据 |
|---|---|---|
| P1 右键命令禁用 | `ContextMenu` 内 `Command/CommandParameter` → `Click` 事件 | 探针：两个 `MenuItem` 的 `IsEnabled` 由 **false → true** |
| P2 整行可右键 | `ContextMenu` 移到 DataTemplate 根 `Border`（`Background=Transparent`） | 探针：在**缩略图区**与**文字区**右键均弹同一菜单 |
| P3 顺序调整 | 「仅从列表移除」置前，「删除（含文件，需确认）」置后 | 探针输出顺序确认 |
| P4 删除确认 | 新增确认浮层（`ConfirmOpen/Title/Message/OkText`） | 截图：「将完全删除 **2** 个图片文件（不可恢复）」「完全删除 2 项」 |
| P5 多选批量 | 「多选」开关 + `SelectionMode` 切换 + 批量移除/删除 | 探针：点「多选」后出现「删除选中/移除选中/完成多选」 |
| P3′ **画布可命中** | `RegionCanvas.Render` 先 `FillRectangle(Brushes.Transparent)` | `FromPoint(预览区)` 由 **`Image` → `RegionCanvas`**；拖动后**画出红色笔迹**（截图） |
| P4′ 预览不动 | 编辑工具栏区域**高度恒定**（112px），非编辑态显示引导文案 | 探针：点「编辑图片」前后预览 `Image` rect **完全一致**（`610,460 374x210`） |
| P5′ 启动自检 | 顶栏徽标改为自检结论；未就绪时显示原因并可点击去设置 | 日志：`启动自检：全部通过 → 就绪` + 逐项 `✓`；无 key 时显示「未就绪」 |
| P6 下拉框调高 | 左栏/窄屏 5 个 `ComboBox` + `NumericUpDown`：`MinHeight` 32 → **38** | 探针：5 个 ComboBox 均为 `246x38` |
| P7 路径右对齐 | `HorizontalAlignment=Right` + `TextAlignment=Right` | 截图：路径右端对齐到「编辑图片」左缘 |
| P8 移除离线框 | 删除宽/窄屏两处「离线」复选框（保留 debug 设置项） | 探针：`离线CheckBox 存在? False`（宽屏）；窄屏 `离线 不存在` |
| P9 滚动条间距 | 滚动条列 14 → **18px**，内容右边距 6 → 10px | 截图：滚动条与下拉框有可见间隙 |
| P10 工具栏越界 | 改 `WrapPanel` 自动换行；「用标注编辑」`MinWidth=96` | 探针：与「系统看图器」**无重叠**；右缘 701 < 中栏 800 |
| P11 版本号 | `Directory.Build.props` 加 `<Version>`；VM 读 `AssemblyInformationalVersion`（剥离 `+sha`） | 探针：底栏文本 `imghub v5.23.0` |

### 验证

| 项 | 结果 |
|---|---|
| Core 测试 | **56/56** ✅ |
| 集成测试 | **68/68** ✅（本轮 **+10** 项回归） |
| 合计 | **124/124** ✅ |
| `ImgHub.App` 编译 | ✅ 0 错误 |
| UI 实测 | ✅ 上述 13 项逐项用截图 / UIAutomation 探针确认 |
| 窄屏（860px） | ✅ 离线框已移除、多选/导入按钮就位 |
| 用户真实数据 | ✅ **未被触碰**（实测一律用 `%TEMP%` 下的数据目录副本） |

### 新增回归测试（10 项）

`SelfCheck_ReportsNotReady_WhenKeyMissing` · `SelfCheck_FlagsMissingPolish_WhenEnabled` ·
`DeleteWithFiles_RequiresConfirmation` · `RemoveFromList_KeepsFile_AndNoConfirm` ·
`BatchDelete_ConfirmsCount_AndDeletesAll` · `BatchRemoveFromList_KeepsFiles` ·
`BatchDeletePrompts_ConfirmsCount` · `MultiSelect_TogglesSelectionMode_AndButtonText` ·
`VersionText_ComesFromAssembly` · `RegionCanvas_NoShapes_ExportsNull`
