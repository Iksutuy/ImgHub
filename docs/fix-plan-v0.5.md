# 修复计划（第十二轮 · v0.5.x）— UI 微调 + 马克笔/缩放修复 + 消息自动滚动

> 来源：用户截图标注（3 张）+ 6 条要求。
> 方法：先读码定位根因 → 写 plan → 执行 → 实测截图核对。

---

## 零、根因定位（读码所得，非猜测）

### 🔴 D1. 马克笔「重复点击叠加、加深直至不透明」

**根因**：预览用 **`DrawLine` 逐段绘制**，每段都带 `alpha=110`：

```csharp
var brush = new SolidColorBrush(Color.FromArgb(110, R, G, B));
var pen = new Pen(brush, radiusPx * 2, lineCap: Round, lineJoin: Round);
for (int i = 1; i < Points.Count; i++)
    ctx.DrawLine(pen, pt(i-1), pt(i));     // ← 段与段在拐角/重合处 alpha 累加
```

- 拖动时相邻段**重叠** → alpha 叠加 → 越描越深
- 反复点击同一处 → 多个 `FreeStroke` 各自 alpha 110 → 叠加
- **导出走 mask 是对的**（`RenderToMask` 用 alpha=255 画到独立 mask，再统一替换为固定 alpha）→ 所以**只有预览错**

**修复**：预览也走"蒙版式"——用 `Opacity` 在一个**图层**上绘制，而不是让每段各自半透明。
Avalonia 方案：把整条 `FreeStroke` 放进 `using (ctx.PushOpacity(110/255.0))`，
内部用**不透明**色绘制 → 同一条笔画内部不再叠加；多条笔画仍会叠加（符合"涂两层更深"的直觉，
与导出 mask 语义一致）。

> 但用户明确要"重复点击不加深"→ 需与导出语义完全一致：
> **同一条内不叠加 + 跨条也不加深**。后者只能在导出时保证（mask 统一 alpha）。
> 预览侧折中：**单条内不叠加**（解决"按住拖动变不透明"），
> 并用**同色同 alpha 的相邻条合并**减少视觉差异。

### 🔴 D2. 缩放后「标注与原图分层、位置错乱」

**根因**：底图 `Image` 与 `RegionCanvas` 用了**两套不同基准**的变换矩阵：

| 控件 | 变换 | 基准 |
|---|---|---|
| `RegionCanvas` | `BuildMatrix(w,h)` = 平移→缩放→平移，**绕 Bounds 中心** | 画布自身 Bounds |
| `Image` | `RenderTransform = ViewMatrix` + **`RenderTransformOrigin=50%,50%`** | 又叠加了一次中心偏移 |

`RenderTransformOrigin=50%,50%` 会让矩阵**再绕中心变换一次** → 双重偏移 → 分层错位。

**修复**：统一基准。让 `Image` 与画布**共用同一个 Bounds**（都撑满 Grid），
且 `Image.RenderTransformOrigin = 0,0`（默认），矩阵只应用一次。
**实测判据**：缩放后底图与标注的相对位置不变（像素比对）。

### 🟡 D3. 工具顺序：圆圈/方框置顶

用户要求：`圆圈` 和 `方框` 放最上方。
现枚举顺序 = `Marker, Brush, Rectangle, Ellipse, Eraser`（VM `ToolNames` 与之对齐）。

**修复**：枚举改为 `Rectangle, Ellipse, Marker, Brush, Eraser`，
`ToolNames` 同步改为 `["方框","圆圈","马克笔","画笔","橡皮"]`。
（`CanvasTool` 的 `Math.Clamp(_toolIndex, 0, 4)` 不用改。）

### 🟡 D4. 粗细默认 3px

`_brushSize = 24` → **3**。（`BrushSizeMin=2`，3 在范围内。）

### 🟡 D5. 消息区：贴底才自动滚动

**现状**：`MessageScroll` 是 `ScrollViewer`，无任何自动滚动 —— 新消息来了不动。

**修复**：加入"是否已在底部"检测：
- 记录用户是否在底部（`Offset.Y + Viewport.Height >= Extent.Height - 阈值`）
- 在底部 → 新消息插入后 `ScrollToEnd()`
- 不在底部 → **不滚动**（用户正在回看历史）
- 用户手动滚到底 → 恢复自动跟随

### 🟡 D6. 版本号改 0.5.x

`5.26.0` → **`0.5.26`**（demo 阶段不该用高版本号）。
同步 `Directory.Build.props` / Android `ApplicationDisplayVersion` / `build.ps1`。

### 🟢 D7. 图 1 的 UI 标注（对齐微调）

| 标注 | 现状 | 修复 |
|---|---|---|
| 红框：参数区「对齐到左边」 | 标签右对齐 + ComboBox 左边缘在 152px | 标签列改**左对齐**（`TextAlignment=Left`），与卡片左内边距对齐 |
| 红箭头：参数区整体偏右 | 左栏 `StackPanel Margin="0,0,12,0"` + 滚动条列 20 | 参数区左对齐到卡片内容起点 |
| 虚线框：提示词输入框「文本对齐到左上角」 | `TextBox` 垂直居中（v5.25 全局样式） | 提示词框改 `VerticalContentAlignment=Top` |
| 绿框：清空按钮「放在同一排」 | 「清空」被 `WrapPanel` 换到第二行 | 4 个按钮收窄（去图标/减 padding）使其同排 |
| 绿箭头：清空右侧留白过多 | 同上 | 同上 |

---

## 一、执行顺序

| 序 | 项 | 验收 |
|---|---|---|
| 1 | D2 缩放分层（最影响可用性） | 像素比对：缩放后相对位置不变 |
| 2 | D1 马克笔不叠加 | 截图：拖动/重复点击颜色不再加深 |
| 3 | D3 工具顺序 + D4 粗细 3px | 探针：下拉首项=方框；粗细显示 3px |
| 4 | D5 消息自动滚动 | 探针：贴底时新消息可见；离底不跳 |
| 5 | D6 版本 0.5.26 | 一致性 grep |
| 6 | D7 对齐微调 | 截图核对 4 处标注 |
| 7 | 全量测试 + AOT + 编译成品 | 159+ 测试全绿 |

---

## 二、Review（写盘后复查）

### V1. D1 用 `PushOpacity` 会不会影响导出？

**不会** —— 导出走 `RenderToMask`（独立路径，alpha=255 画到 mask），
预览的 `DrawPreview` 只影响屏幕显示。两条路径本就分离（这是既有设计）。

### V2. D2 改 `Image` 基准会破坏上一轮的缩放修复吗？

不会，是**加强**：上一轮把矩阵同步给 `Image` 但**基准不一致**（所以错位）。
这一轮统一基准。判据改为"相对位置不变"而非"有变化"。

### V3. D5 的"底部检测"阈值取多少？

取 **24px**（约一行高度）。太小会因行高误差误判，太大则"差一点到底"时不跟随。
滚动事件里维护 `_stickToBottom` 标志，**用户手动滚动时更新**（不只在插入时判断）。

### V4. D3 改枚举顺序会影响既有测试吗？

`RegionTools_MappedAndNoIntent` 断言工具枚举 —— 需同步更新断言。
`ToolNames` 顺序必须与枚举**严格对齐**（HANDOVER 已写明）。

### V5. D6 版本号 0.5.26 会不会与 Android `ApplicationVersion`（整数）冲突？

不冲突：`ApplicationVersion` 是整数（构建号），`ApplicationDisplayVersion` 是显示串。
本次把显示串改为 `0.5.26`，构建号 +1。

### V6. D7 的标签左对齐会不会破坏"对齐统一"？

会**改进**：左对齐后标签起点与卡片内容左边界一致，
`TextBlock` 宽度固定 → ComboBox 左边缘仍然对齐成一条竖线。

---

## 三、不在本轮范围

| 项 | 原因 |
|---|---|
| 跨笔画也不加深（预览侧完全等价 mask） | 需把全部笔画渲染到离屏层再统一 alpha，性能开销大；导出已正确，预览用单条内不叠加缓解 |
| Android 真机 | 无设备 |
