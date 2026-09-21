# imgagent（Avalonia 版）— 交接文档

> **新接手先读这份**。约 40 分钟能上手。
> 详细架构见 [ARCHITECTURE.md](ARCHITECTURE.md)，约束见 [CONSTRAINTS.md](CONSTRAINTS.md)。

---

## 一、这是什么

跨平台图形化 AI 生图工作台。**一套 UI 同时跑 Windows 桌面和 Android**。

- 上一代：`mobile/`（Python + curses 终端界面），保留作参考实现
- 这一代：`avalonia/`（C# + Avalonia GUI）
- 功能：文生图 / 图生图（多参考图）/ 区域标注重绘 / 批量出图 / 提示词润色 / 历史与成本管理 / 离线模式

---

## 二、5 分钟跑起来

```powershell
cd D:\Project\imagagent\Imagagent\avalonia

# 1) 跑测试（确认环境 OK）
dotnet test tests\Imgagent.Core.Tests           # 应 55/55
dotnet test tests\Imgagent.Integration.Tests    # 应 37/37

# 2) 跑桌面版（会弹窗口）
dotnet run --project src\Imgagent.Desktop

# 3) 配置 API key
#    窗口右下角「⚙ 设置」→ 填 Provider + API Key → 保存
#    或直接写文件：%LOCALAPPDATA%\imgagent\.imgagent_key

# 4) 先在「离线」模式试（不花钱）
#    勾选左栏「离线」→ 输入提示词 → 点「生成」
```

**数据目录**：默认 `%LOCALAPPDATA%\imgagent`，可用环境变量 `IMGAGENT_HOME` 覆盖。

---

## 三、代码地图（改功能去哪）

| 我要改… | 文件 |
|---|---|
| **界面布局/样式** | `src/Imgagent.App/Views/MainView.axaml` |
| **主题颜色/字体** | `src/Imgagent.App/App.axaml` |
| **业务编排（生成/编辑/撤回）** | `src/Imgagent.App/ViewModels/MainViewModel.cs` |
| **区域标注画布** | `src/Imgagent.App/Controls/RegionCanvas.cs` |
| **API 调用（双 provider）** | `src/Imgagent.Core/Services/ImageApi.cs` |
| **错误提示文案** | `src/Imgagent.Core/Http/ErrorHints.cs` |
| **模型清单/成本估算** | `src/Imgagent.Core/Catalog.cs` |
| **配置/历史存储** | `src/Imgagent.Core/Storage/Session.cs` |
| **图像编解码/占位图** | `src/Imgagent.Core/Imaging/` |
| **Android 宿主** | `src/Imgagent.Android/MainActivity.cs` |

---

## 四、常见任务

### 加一个新模型

1. `src/Imgagent.Core/Catalog.cs` → 加进 `ModelChoicesOpenRouter` 或 `ModelChoicesApimart`
2. 若是 2.5 系以外的 APIMart 模型，确认 `QualityChoices` 的过滤逻辑正确
3. 跑测试（`Quality_RoutesPerProvider_*` 会校验）

### 加一个新参数（如 seed）

1. `Catalog` 加常量
2. `AppConfig` 加字段（记得 `[JsonPropertyName("seed")]` 保持 snake_case）
3. `MainViewModel` 加 `[ObservableProperty]` + `OnSeedChanged → PersistConfig()`
4. `MainView.axaml` 加控件
5. `ImageApi.GenerateAsync` 加参数并传给两个 provider
6. 加回归测试

### 加一个新绘制工具

1. `RegionCanvas.RegionTool` 枚举加值
2. 继承 `Shape` 写新子类（实现 `DrawPreview` + `DrawExport`）
3. `OnPointerPressed` 的 `switch` 加分支
4. `MainViewModel.ToolNames` 加显示名（顺序必须对齐枚举）

### 调 Android 竖屏布局

`MainView.axaml` 里的响应式断点（窄屏改纵向堆叠）。

---

## 五、出问题查什么

| 症状 | 先查 |
|---|---|
| 启动闪退 | 事件查看器 → Windows 日志 → 应用程序；或命令行跑看 stderr |
| 中文显示方框 | 见 [CONSTRAINTS.md](CONSTRAINTS.md) D5（字体链 + 内嵌字体） |
| 参数改完重启就丢 | config.json 的 snake_case 映射；尾触发节流 |
| 累计金额显示 0 | `Session.LoadState` 是否恢复 `total_cost` |
| 生成失败 | 消息面板有详细提示；`ErrorHints` 给可操作建议 |
| AOT exe 崩溃 | 是否 3 个原生 DLL 都在同目录（约束 E1） |
| publish 文件锁定 | 先杀进程（约束 E2） |

### 诊断命令

```powershell
# 看数据目录内容
Get-ChildItem $env:LOCALAPPDATA\imgagent

# 看 config
Get-Content $env:LOCALAPPDATA\imgagent\config.json

# 看历史流水
Get-Content $env:LOCALAPPDATA\imgagent\history.jsonl -Tail 10

# 看崩溃事件
Get-WinEvent -FilterHashtable @{LogName='Application'; Level=2} -MaxEvents 20 |
    Where-Object { $_.Message -match "Imgagent" }
```

---

## 六、测试怎么跑

```powershell
# 全部（92 项，约 40 秒）
dotnet test tests\Imgagent.Core.Tests
dotnet test tests\Imgagent.Integration.Tests

# 只跑某个
dotnet test tests\Imgagent.Integration.Tests --filter "FullyQualifiedName~RegionEdit"
```

**测试哲学**：全部离线跑（`Offline=true`），用确定性占位图，**不花钱**。
每条约束都有对应回归测试（见 [CONSTRAINTS.md](CONSTRAINTS.md) F3）。

---

## 七、构建发布

见 [DELIVERY.md](DELIVERY.md)。一句话：

```powershell
powershell -File build.ps1 -Target desktop-aot   # Windows（23 MB）
powershell -File build.ps1 -Target android       # Android APK（42 MB）
```

---

## 八、迁移进度与历史

见 [port-status.md](port-status.md) —— 含历次修复的根因分析与验证记录。

**当前状态**：
- Core 层：✅ 完成（55 项单测）
- 桌面版：✅ 完成并实测（含 Native AOT 发布）
- Android：⚠️ 构建通过，**中文渲染与横竖屏布局待完善**
- 应用图标：✅ 桌面已生效；Android 待同步多密度 mipmap
