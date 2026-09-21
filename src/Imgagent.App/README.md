# Imgagent.App — 共享 UI 层

> 被 `Imgagent.Desktop` 与 `Imgagent.Android` 共同引用。
> **一套 XAML 两端跑** —— 平台差异通过接口注入。

## 职责

界面（XAML）+ 视图模型（MVVM）+ 自定义控件 + 平台抽象接口。

## 目录结构

```
App.axaml / App.axaml.cs        应用级：主题、样式、字体、颜色字典、组合根
Views/
  MainView.axaml                三栏工作台（唯一主界面）
  MainWindow.axaml              桌面窗口壳（含窗口图标）
  *.axaml.cs                    事件处理器（import/标注/调色板等）
ViewModels/
  MainViewModel.cs              编排层（对应 Python 版 17 个 _act_* 函数）
  ProviderOption.cs             provider 下拉选项
  HistoryRow.cs                 历史行（含缩略图 —— UI 类型不进 Core）
  PreviewThumb.cs               缩略图条/参考图项
Controls/
  RegionCanvas.cs               区域标注画布（画笔/马克笔/方框/圆圈/橡皮）
Services/
  PlatformAbstractions.cs       IPlatformStorage / IPlatformInfo / AppServices / AppPaths
  PlatformStorage.cs            Avalonia StorageProvider 实现
Assets/
  app-icon.png                  应用图标（窗口图标 + 生成 ico 的源）
  Fonts/                        （预留）内嵌 CJK 字体目录
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

## 响应式布局

`MainView.axaml` 按宽度断点切换：
- **宽屏（≥900px）**：三栏并排（参数 | 预览 | 历史）
- **窄屏（<900px）**：纵向堆叠（预览优先 → 操作 → 参数）

## 改 UI 从哪下手

1. 布局/间距 → `Views/MainView.axaml`
2. 配色/字体 → `App.axaml`
3. 行为逻辑 → `ViewModels/MainViewModel.cs`
4. 画布交互 → `Controls/RegionCanvas.cs`
