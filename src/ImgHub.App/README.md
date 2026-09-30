# ImgHub.App — 共享 UI 层

> 被 `ImgHub.Desktop` 与 `ImgHub.Android` 共同引用。
> **一套 XAML 两端跑** —— 平台差异通过接口注入。

## 职责

界面（XAML）+ 视图模型（MVVM）+ 自定义控件 + 平台抽象接口。

## 目录结构

```
App.axaml / App.axaml.cs        应用级：主题、样式、字体、颜色字典、组合根
Views/
  MainView.axaml                三栏工作台（**宽屏 + 窄屏两套布局**）
  MainWindow.axaml              桌面窗口壳（自绘标题栏 + 窗口图标）
  *.axaml.cs                    事件处理器（import/标注/调色板/缩放等）
ViewModels/
  MainViewModel.cs              编排层（约 1,100 行，对应 Python 版 _act_* 函数）
  ProviderOption.cs             provider 下拉选项
  HistoryRow.cs                 历史行（含缩略图 —— UI 类型不进 Core）
  PreviewThumb.cs               缩略图条/参考图项
Controls/
  RegionCanvas.cs               区域标注画布（马克笔/画笔/方框/圆圈/橡皮）
Services/
  PlatformAbstractions.cs       IPlatformStorage / IPlatformInfo / AppServices / AppPaths
  PlatformStorage.cs            Avalonia StorageProvider 实现
Assets/
  app-icon.png                  应用图标（窗口图标 + 生成 ico 的源）
  Fonts/NotoSansSC-Regular.ttf  内嵌 CJK 字体（静态子集，7.15 MB）
```

## 关键设计

| 决策 | 原因 |
|---|---|
| 颜色收敛到 `App.axaml` 的 `ThemeDictionaries` | 深浅主题一键切换，视图只引 `{DynamicResource}` |
| `AvaloniaUseCompiledBindingsByDefault=true` | 编译期绑定校验 + Native AOT 友好 |
| 缩略图放 `HistoryRow`/`PreviewThumb` | 遵守 Core 零 UI 依赖（约束 A2） |
| `Dispatcher.Post` 而非 `InvokeAsync` | 避免测试/无消息泵环境死锁（约束 D3） |
| `async void` 全 try/catch | 防异常崩进程（约束 D2） |
| 窗口图标用 PNG | PNG-in-ICO 会崩（约束 D4） |
| 内嵌**静态** CJK 字体 | VF 默认字重 100 → 中文发虚（约束 D5/D6） |
| 自绘标题栏 | 仿 Reasonix；Avalonia 12 已移除 `ExtendClientAreaChromeHints`（约束 D7） |

## 响应式布局

`MainView.axaml` 按宽度断点切换（阈值 900px，由 `MainView.axaml.cs` 的
`OnSizeChanged` 设 `Vm.IsWideLayout`）：

- **宽屏（≥900px）**：三栏并排（参数 | 预览 | 历史）
- **窄屏（<900px）**：纵向堆叠（预览+工具 → 提示词+操作 → 参数 → 历史+消息）

> ⚠️ 两套布局是**独立子树**，各持一个 `RegionCanvas` 实例。切换时靠
> `ExportShapes`/`ImportShapes` 搬运标注；加控件务必**两处都加**（约束 D4b）。

## 改 UI 从哪下手

1. 布局/间距 → `Views/MainView.axaml`（注意两套布局）
2. 配色/字体 → `App.axaml`
3. 行为逻辑 → `ViewModels/MainViewModel.cs`
4. 画布交互 → `Controls/RegionCanvas.cs`
