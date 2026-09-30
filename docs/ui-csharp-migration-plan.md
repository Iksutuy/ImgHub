# XAML → 纯 C# UI 迁移计划（若决定执行）

> 配套评估见 [ui-csharp-feasibility.md](ui-csharp-feasibility.md)。
> ⚠️ **本文档是"如果决定迁移"的执行方案，不是建议迁移** —— 评估结论是不建议（见该文档 §六）。
> 保留本文档的目的：若将来因其他原因（如需要完全动态生成 UI）必须迁移，可直接执行。

---

## 零、执行前必须确认的前提

| 检查项 | 要求 |
|---|---|
| 全量测试基线 | `dotnet test tests\ImgHub.Core.Tests`（132）+ `Integration`（85）= **217 全绿** |
| 桌面版可跑 | `dotnet run --project src\ImgHub.Desktop` 正常 |
| 有回滚点 | git 工作区干净（或先 commit/stash），迁移**必须可整批回滚** |
| 截图基线 | **迁移前先截宽屏 + 窄屏两张图**（否则无法验证"像素级一致"） |

> ⚠️ XAML 改动测试覆盖不到（AGENTS.md §3.4），迁移同样如此 ——
> **所有阶段都必须人工跑桌面版逐屏比对截图**。

---

## 一、迁移顺序（分批、可回滚、每批独立验证）

顺序原则：**从"风险低、可独立验证"到"风险高"**，每批结束时项目都能编译 + 跑通。

### 批次 0：骨架准备（0.2 人日）

**目标**：建立 C# 建 UI 的基础设施，不改任何现有行为。

1. 新建 `src/ImgHub.App/Ui/` 目录：
   ```
   Ui/
     UiFactory.cs        ← 控件构造快捷方法（见下）
     Bind.cs             ← 绑定辅助（强类型属性名，避免字符串拼错）
     UiTheme.cs          ← 主题资源访问
   ```
2. `Bind.cs` 核心：**用 `nameof` 消除绑定字符串拼写风险**
   ```csharp
   public static class Bind
   {
       /// <summary>把控件属性绑定到 VM 属性（属性名走 nameof → 改名时编译报错）。</summary>
       public static T To<T, TVm>(this T ctrl, AvaloniaProperty prop,
                                  Func<TVm, object?> path) where T : AvaloniaObject
       {
           // 用表达式树取出属性名，避免手写字符串
           var name = ExpressionPath(path);
           ctrl.Bind(prop, new Binding(name));
           return ctrl;
       }
   }
   ```
   > 这是**迁移唯一能带来净收益**的技术点：把绑定名从字符串变成编译期可验证的表达式。

**验证**：编译通过；不改变 UI。

### 批次 1：`App.axaml` → C#（0.5–1.0 人日）

**目标**：主题字典 + 31 个 Style 全部 C# 化。

1. 主题颜色（14 色 × Light/Dark）→ `UiTheme.cs`
   ```csharp
   public static class UiTheme
   {
       public static readonly (string Key, string Light, string Dark)[] Colors = {
           ("AppBg",        "#F7F8FA", "#171717"),
           ("AppSurface",   "#FFFFFF", "#232326"),
           // ... 14 项，逐项照抄 App.axaml（不要在迁移时"顺手调色"）
       };
       public static ResourceDictionary Build() { /* 构造 ThemeDictionaries */ }
   }
   ```
   ⚠️ **必须逐值照抄**，迁移期间禁止任何配色改动（否则无法判断差异来源）。

2. 31 个 Style → `Styles/*.cs`（按用途拆文件）
   ```csharp
   // 选择器翻译示例（注意模板内选择器最容易写错）
   new Style(x => x.OfType<Button>().Class("accent"))
       .Set(Button.BackgroundProperty, AppAccentBrush)
       .Set(Button.ForegroundProperty, Brushes.White);

   // ⚠️ 模板内选择器：原 XAML "Button:pointerover /template/ ContentPresenter"
   new Style(x => x.OfType<Button>().Class("accent").Template().OfType<ContentPresenter>()
                  .Name("PART_ContentPresenter").Class(":pointerover"))
       .Set(ContentPresenter.BackgroundProperty, AppAccentHoverBrush);
   ```
   > **这是本批次最大风险点**：`/template/` 选择器翻译错**不报错、静默失效**（按钮悬停效果消失）。
   > 缓解：每翻译 5 个就实跑一次，鼠标悬停验证。

3. 字体（CJK 字体链 + 内嵌静态字体）→ 保留 `FontFamily` 构造
   ```csharp
   new FontFamily("avares://ImgHub.App/Assets/Fonts/NotoSansSC-Regular.ttf#Noto Sans SC, Microsoft YaHei UI, ...")
   ```
   ⚠️ **绝不能改字体**：Android 中文渲染依赖内嵌静态字体（CONSTRAINTS D5/D6）。

**验证**：
- `dotnet test` 全绿
- 桌面版实跑：**逐一切换浅色/深色主题**，比对截图（颜色、悬停、圆角）
- 鼠标悬停每个按钮，确认 hover 效果仍在（验证 `/template/` 选择器翻译正确）

**回滚点**：`git revert` 本批次。

### 批次 2：`MainWindow` → C#（0.3 人日）

**目标**：窗口壳 + 自绘标题栏（56 行 XAML）。

1. 窗口属性（`Width/Height/MinWidth/MinHeight`、`ExtendClientArea*`）→ 构造函数
2. 标题栏（拖拽、最小化/最大化/关闭）→ `MainWindow.axaml.cs` 已有逻辑，保留
3. ⚠️ 窗口图标必须 PNG、exe 用 ICO（CONSTRAINTS D4）—— 迁移时别改错

**验证**：窗口能拖动、三个标题栏按钮都有效、图标正常。

### 批次 3：窄屏布局 → C#（1.0–1.5 人日）

**为什么先迁移窄屏**：它是**风险较低**的那套（结构更简单，纵向堆叠），且能验证整套
C# 构建手法是否可行 —— 若这里就出问题，可低成本放弃。

`MainView.axaml` 第 ~431–700 行（`IsVisible="{Binding !IsWideLayout}"` 的 `ScrollViewer`）：

1. 逐段翻译，**保持与 XAML 完全相同的层次结构**（不要在迁移时重构布局）
2. 18 个 `DataTemplate` → `FuncDataTemplate<T>`：
   ```csharp
   // XAML: <DataTemplate><Grid ColumnDefinitions="28,*,Auto">...</Grid></DataTemplate>
   new FuncDataTemplate<PreviewThumb>((item, _) =>
   {
       var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*,Auto") };
       grid.Children.Add(new Border { Width = 24, Height = 24 }.Also(b =>
           b.Bind(Border.BackgroundProperty, ThemeBrush("AppSurfaceBrush"))));
       // ...
       return grid;
   })
   ```
   > ⚠️ 嵌套 DataTemplate（历史行：缩略图 + 提示词 + 费用）会变成**深层嵌套 lambda**。
   > 建议为每个模板抽独立方法：`static Control BuildHistoryRow(HistoryRow row)`，
   > **否则可读性劣化会比 XAML 更严重**。

3. 事件处理器（`Click="OnImportClick"` 等）→ 保留在 `MainView.axaml.cs`，改为
   `button.Click += OnImportClick;` 订阅

**验证**：
- 把窗口**拉窄到 <900px**（触发窄屏断点）实跑
- 比对迁移前的窄屏截图
- 重点检查：批量缩略图条、历史列表右键菜单、消息面板自动滚动

### 批次 4：宽屏布局 → C#（1.0–1.5 人日）

**风险最高的一批**（三栏 + 更多交互）。

`MainView.axaml` 第 78–410 行（`IsVisible="{Binding IsWideLayout}"`）：

1. 三栏 Grid（`ColumnDefinitions="1.02*,1.18*,1"`）
2. 左栏：参数区 5 下拉 + 高级参数 Expander + 提示词 + 4 按钮 + 参考图列表
3. 中栏：预览 + RegionCanvas 叠层 + 缩放工具条 + 标注工具条
4. 右栏：历史列表 + 消息面板

⚠️ **中栏是本批次的关键风险**：`RegionCanvas` 与底图 `Image` **必须共用同一 Bounds 与变换矩阵**
（CONSTRAINTS 关于"缩放分层错位"的坑）。C# 里要确保：
- 两者放在同一 `Grid` 中（撑满同一格）
- `ViewChanged` → `SyncPreviewTransform` 的事件订阅不被遗漏

**验证**：
- 宽屏截图比对（与批次 0 的基线逐像素看）
- **缩放/平移测试**：滚轮缩放、右键拖拽、`0` 复位 → 确认底图与标注**同步**（这是历史踩坑点）
- 标注流程：圈画 → 「用标注编辑」→ 确认按钮 `IsEnabled` 正确

### 批次 5：清理与文档（0.5 人日）

1. 确认无 `.axaml` 引用残留：`rg -n "axaml" src/ImgHub.App`（`App.axaml` 可作为应用资源保留）
2. 更新文档：
   - `AGENTS.md` §4「改代码去哪」的表格（`.axaml` → `.cs`）
   - `docs/ARCHITECTURE.md` 四层结构描述
   - `docs/CONSTRAINTS.md` 里涉及 XAML 的条目（D4b/D4c「两套布局同步」的表述要改）
3. ⚠️ **`AGENTS.md` §3.2「改 UI 必须两套布局同步改」的约束依然成立**，
   但"并排可见"的优势消失 → 需补充缓解措施（见 §二）

---

## 二、迁移后必须新增的保障（否则是净损失）

迁移会失去两个现有保障，必须补回来：

### 2.1 补偿「编译期绑定检查」的损失

当前 `AvaloniaUseCompiledBindingsByDefault=true` 能在编译时发现 `{Binding 拼错}`。
C# 手写 `new Binding("ModelChoices")` 拼错 → **运行时静默失效**。

**缓解（必做）**：
```csharp
// 用 nameof 表达式，改名/拼写错都会编译报错
combo.BindTo(ComboBox.ItemsSourceProperty, (MainViewModel vm) => vm.ModelChoices);
```
新增测试：对关键绑定做"非空断言"（如启动后 `ModelChoices.Count > 0`、
`EstimatedCostText` 非空），覆盖历史踩过的静默失败场景。

### 2.2 补偿「两套布局并排可见」的损失

XAML 里宽/窄两套布局上下相邻，漏改一眼可见。C# 里是两段相距数百行的方法。

**缓解（必做）**：
```
Ui/Layout.Wide.cs     ← 宽屏布局（一个方法一个区块）
Ui/Layout.Narrow.cs   ← 窄屏布局（结构镜像，便于对照）
```
+ 在两个文件顶部各写注释指向对方，列出"必须同步的控件清单"。
+ 新增集成测试：断言两套布局都创建了关键控件（如各有 `RegionCanvas` 实例）。

---

## 三、每批次的验收清单（模板）

```powershell
# 1) 编译
dotnet build src\ImgHub.App\ImgHub.App.csproj

# 2) 全量测试
dotnet test tests\ImgHub.Core.Tests
dotnet test tests\ImgHub.Integration.Tests

# 3) AOT 必须仍可发布（改动 UI 后裁剪行为可能变）
dotnet publish src\ImgHub.Desktop -c Release -r win-x64 `
    -p:PublishAot=true -p:DebugType=none -p:DebugSymbols=false -o release\desktop-aot
# 并实跑 release\desktop-aot\ImgHub.Desktop.exe（确认不秒崩）

# 4) 桌面版人工逐屏比对
dotnet run --project src\ImgHub.Desktop
#   - 宽屏：三栏布局、缩放下标注同步、标注编辑按钮状态
#   - 窄屏：拉窄到 <900px，检查堆叠布局
#   - 主题：浅色/深色切换
#   - 悬停：每个按钮 hover 效果（验证 /template/ 选择器翻译）
```

**任一项不过 → 回滚该批次，不进入下一批。**

---

## 四、风险与放弃判据

| 风险 | 触发信号 | 应对 |
|---|---|---|
| `/template/` 选择器翻译错误 | 按钮悬停/选中态效果消失 | 回滚批次 1，改用保留 XAML 样式的折中方案 |
| `DynamicResource` 漏更新 | 切换深色主题后部分控件仍是浅色 | 主题切换测试逐控件断言 |
| 两套布局漏改 | 窄屏看不到某控件 | 批次 3/4 分别截图比对 |
| AOT 裁剪异常 | AOT exe 秒崩或功能缺失 | 回滚，保留 XAML（AOT 是硬约束，不可让步） |
| 可读性劣化不可接受 | 代码 review 时宽/窄布局对照困难 | **接受评估结论 → 不迁移** |

### 明确的放弃判据

出现以下任一情况，**立即停止迁移并回滚**：

1. 批次 1 的 `/template/` 选择器在同一处失败 2 次以上 → 说明 C# 表达样式的能力确实更差
2. 批次 3 完成后，窄屏截图与基线**存在无法解释的差异**（非数值微调）
3. AOT 无法发布或产物运行异常，且 1 小时内定位不到原因
4. 任何批次耗时超过估算的 2 倍

---

## 五、推荐：先做这个（替代方案的真实计划）

若目标是**降低 UI 维护成本**（而不是"必须用 C#"），同样工作量做以下三项收益更大：

### Plan A：拆分 XAML（推荐，1.5 人日）

| 步骤 | 内容 | 收益 |
|---|---|---|
| A1 | `App.axaml` 的 31 个 Style → `Styles/Controls.axaml` | 样式集中，改一处全局生效 |
| A2 | 抽出 `Views/Sections/ParamsPanel.axaml`（左栏生成参数） | **宽/窄两套布局复用同一份**，彻底消除重复 |
| A3 | 同样抽出 `PreviewPanel.axaml` / `HistoryPanel.axaml` | 同上 |
| A4 | 两套布局改为"引用同一 Section + 仅外层容器不同" | **AGENTS.md §3.2 的痛点从根上消失** |

> Plan A 直接解决本项目**唯一真实存在**的 UI 维护痛点：两套布局重复。
> 而且它**保留**编译期绑定检查。

### Plan B：强化编译绑定（0.2 人日）

给 `MainView.axaml` 根节点加 `x:DataType="vm:MainViewModel"`，
把 `{Binding X}` 逐步改为 `{CompiledBinding X}` → 拼错立即编译报错。

### Plan C：抽 UI 常量（0.3 人日）

把散落的魔数（`MinHeight=34`、`Padding=11,0,6,2`、行距 4）提到
`Ui/UiMetrics.cs` 常量，XAML 用 `{x:Static}` 引用 → 改一处全局一致。

**Plan A+B+C 合计 2.0 人日，收益 > 全量迁移（4–6 人日，收益 0）。**
