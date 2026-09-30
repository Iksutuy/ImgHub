# ImgHub（Avalonia 版）— 交接文档

> **新接手先读这份**。约 40 分钟能上手。
> 详细架构见 [ARCHITECTURE.md](ARCHITECTURE.md)，约束见 [CONSTRAINTS.md](CONSTRAINTS.md)。

---

## 一、这是什么

跨平台图形化 AI 生图工作台。**一套 UI 同时跑 Windows 桌面和 Android**。

- 上一代：`legacy/`（Python + curses 终端界面），保留作参考实现
- 这一代：`src/`（C# + Avalonia GUI）
- 功能：文生图 / 图生图（多参考图）/ 区域标注重绘 / 批量出图 / 提示词润色 / 历史与成本管理 / 离线模式（调试）

---

## 二、5 分钟跑起来

```powershell
cd D:\Project\imagagent\Imagagent      # 仓库根目录

# 1) 跑测试（确认环境 OK）
dotnet test tests\ImgHub.Core.Tests           # 应 91/91
dotnet test tests\ImgHub.Integration.Tests    # 应 68/68

# 2) 跑桌面版（会弹窗口）
dotnet run --project src\ImgHub.Desktop

# 3) 配置 API key
#    顶栏右上角「设置」→ 填 Provider + API Key → 保存
#    或直接写文件：%LOCALAPPDATA%\imghub\.imghub_key

# 4) 先在「离线」模式试（不花钱）
#    需 IMGHUB_DEBUG=1 才在 设置 → 界面选项 看到「离线模式（调试）」
#    勾选后输入提示词 → 点「生成」
```

**数据目录**：默认 `%LOCALAPPDATA%\imghub`，可用环境变量 `IMGHUB_HOME` 覆盖。

---

## 三、代码地图（改功能去哪）

| 我要改… | 文件 |
|---|---|
| **界面布局/样式** | `src/ImgHub.App/Views/MainView.axaml` |
| **主题颜色/字体** | `src/ImgHub.App/App.axaml` |
| **业务编排（生成/编辑/撤回）** | `src/ImgHub.App/ViewModels/MainViewModel.cs` |
| **区域标注画布** | `src/ImgHub.App/Controls/RegionCanvas.cs` |
| **API 调用（五个 provider）** | `src/ImgHub.Core/Services/ImageApi.cs` |
| **OpenAI 官方接入细节** | `docs/provider-openai-image-api.md` |
| **千问 DashScope 接入细节** | `docs/provider-qwen-dashscope-api.md` |
| **错误提示文案** | `src/ImgHub.Core/Http/ErrorHints.cs` |
| **模型清单/成本估算** | `src/ImgHub.Core/Catalog.cs` |
| **价格预估（按模型统计）** | `src/ImgHub.Core/Services/ModelStatsService.cs` |
| **配置/历史存储** | `src/ImgHub.Core/Storage/Session.cs` |
| **图像编解码/占位图** | `src/ImgHub.Core/Imaging/` |
| **Android 宿主** | `src/ImgHub.Android/MainActivity.cs` |

---

## 四、常见任务

### 加一个新模型

1. `src/ImgHub.Core/Catalog.cs` → 加进 `ModelChoicesOpenRouter` 或 `ModelChoicesApimart`
2. 若是 2.5 系以外的 APIMart 模型，确认 `QualityChoices` 的过滤逻辑正确
3. 跑测试（`Quality_RoutesPerProvider_*` 会校验）

### 加一个新参数（如 seed）

1. `Catalog` 加常量
2. `AppConfig` 加字段（记得 `[JsonPropertyName("seed")]` 保持 snake_case）
3. `MainViewModel` 加 `[ObservableProperty]` + `OnSeedChanged → PersistConfig()`
4. `MainView.axaml` 加控件（宽屏 + 窄屏**两套布局都要加**）
5. `ImageApi.GenerateAsync` 加参数并传给两个 provider
6. 加回归测试

### 加一个新绘制工具

1. `RegionCanvas.RegionTool` 枚举加值
2. 继承 `Shape` 写新子类（实现 `DrawPreview` + `DrawExport`）
3. `OnPointerPressed` 的 `switch` 加分支
4. `MainViewModel.ToolNames` 加显示名（顺序必须对齐枚举）

### 加/改一条界面文案（多语言）

界面支持**中 / 英 / 日**（设置浮层底部切换，即时生效）。改文案的正确姿势：

1. 改 `src/ImgHub.App/Ui/Localizer.cs` 的**三个字典**（键必须一致）；
2. XAML 用 `{CompiledBinding L[键]}`；
3. 若文案在 **VM 里拼装**（如 `ToolNames`）→ 走 `Localizer.Instance[...]`
   **并加进 `MainViewModel.NotifyLocalizedStrings()`**。

⚠️ 有 **6 个坑**（其中 3 个"不报错"）—— 完整清单与排查顺序见 **[i18n.md](i18n.md)**。

### 调 Android 竖屏布局

`MainView.axaml` 里的响应式断点（`IsWideLayout`，阈值 900px，由
`MainView.axaml.cs` 的 `OnSizeChanged` 驱动）。**注意**：宽窄屏是两套独立
XAML 子树，改布局要同步改两处，且两套各有自己的 `RegionCanvas` 实例。

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
Get-ChildItem $env:LOCALAPPDATA\imghub

# 看 config
Get-Content $env:LOCALAPPDATA\imghub\config.json

# 看历史流水
Get-Content $env:LOCALAPPDATA\imghub\history.jsonl -Tail 10

# 看崩溃事件
Get-WinEvent -FilterHashtable @{LogName='Application'; Level=2} -MaxEvents 20 |
    Where-Object { $_.Message -match "ImgHub" }
```

---

## 六、测试怎么跑

```powershell
# 全部（449 项，约 90 秒：Core 快，集成约 1.5 分钟）
dotnet test tests\ImgHub.Core.Tests
dotnet test tests\ImgHub.Integration.Tests

# 只跑某个
dotnet test tests\ImgHub.Integration.Tests --filter "FullyQualifiedName~RegionEdit"
```

**测试哲学**：全部离线跑（`Offline=true`），用确定性占位图，**不花钱**。
每条约束都有对应回归测试（见 [CONSTRAINTS.md](CONSTRAINTS.md) F3）。

---

## 七、构建发布

见 [DELIVERY.md](DELIVERY.md)。一句话：

```powershell
powershell -File build.ps1 -Target desktop   # 桌面（框架依赖）
powershell -File build.ps1 -Target android   # Android APK

# 体积最小（Native AOT）需手动 dotnet publish，见 DELIVERY.md
```

---

## 八、迁移进度与历史

见 [port-status.md](port-status.md) —— 含历次修复的根因分析与验证记录。

**当前状态**（截至 v0.5.43，测试合计 449 项全绿）：
- Core 层：✅ 完成（236 项单测）
- 集成测试：✅ 完成（213 项，离线闭环）
- 桌面版：✅ 完成并实测（含 Native AOT 发布 + 启动验收）
- Android：⚠️ APK 构建通过，**中文渲染待真机验证**；横竖屏响应式布局已完成（446×859 实测）
- 应用图标：✅ 桌面 ICO + Android 多密度 mipmap 均已生效
- 多语言（中/英/日）即时切换 / 设置浮层 / 润色候选 4 选 1 / 响应式布局 / 按模型价格预估：✅ 已完成
- 已知缺口：环境诊断面板（原版 `doctor.py`）未实现；快捷键提示为文案（无实际键位绑定）
