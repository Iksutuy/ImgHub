# imgagent（Avalonia 版）— 架构文档

> 讲清「系统怎么搭的、数据怎么流、两端怎么复用」。
> 读之前建议先看 [README.md](README.md) 了解文档分工。

---

## 一、四层结构

```
┌─────────────────────────────────────────────────────────────┐
│  Imgagent.Desktop (net10.0)     Imgagent.Android (net10.0-android)
│  ── 只做平台适配 ──                    ── 只做平台适配 ──
│  · Program.cs 启动              · MainActivity 宿主
│  · app.manifest DPI             · AndroidApp 生命周期
│  · app.ico 文件图标              · mipmap 各密度图标
│  · win-x64 RID / Native AOT     · APK 打包
└──────────────────────────┬──────────────────────────────────┘
                           │ 引用
┌──────────────────────────▼──────────────────────────────────┐
│  Imgagent.App (net10.0)  ── 共享 UI ──                       │
│  · App.axaml        主题/样式/字体/颜色字典（含深浅两套）      │
│  · Views/           MainView（三栏工作台）、MainWindow        │
│  · ViewModels/      MainViewModel（编排层）、HistoryRow 等    │
│  · Controls/        RegionCanvas（区域标注画布）              │
│  · Services/        平台抽象接口 + AppPaths + 组合根容器      │
│                                                              │
│  ⚠️ 不引用任何平台特定类型，只依赖 Avalonia 抽象层            │
└──────────────────────────┬──────────────────────────────────┘
                           │ 引用
┌──────────────────────────▼──────────────────────────────────┐
│  Imgagent.Core (net10.0)  ── 平台无关业务 ──                  │
│  · Catalog.cs        模型/质量/画幅/成本常量（唯一真源）      │
│  · Models/           Item / GenResult / AppConfig / ApiProvider
│  · Http/             HttpJsonClient / ApiError / ErrorHints   │
│  · Services/         ImageApi（双 provider）/ PolishService   │
│  · Storage/          Session（配置/历史/提示词/key）           │
│  · Imaging/          ImageCodec / Placeholder（SkiaSharp）    │
│                                                              │
│  ⚠️ 架构红线：不引用 Avalonia、不引用 Android、不引用 UI      │
│  ✅ 只依赖 SkiaSharp（跨平台图像库，非 UI 框架）              │
└─────────────────────────────────────────────────────────────┘
```

### 为什么这么分？

| 分层 | 收益 | 实战验证 |
|---|---|---|
| **Core 零 UI 依赖** | 可单元测试、可换 UI 框架 | App 层被误删时，**Core + 测试无损**，凭测试契约完整重建 |
| **App 共享 UI** | 一套 XAML 两端跑 | Android 竖屏只需改 `MainView.axaml` 一处 |
| **Head 极薄** | 平台差异隔离 | Desktop 43 行、Android 约 60 行 |

---

## 二、数据流

### 生成一张图（完整链路）

```
用户点「生成」
   ↓
MainViewModel.GenerateCommand
   ├─ 校验：Busy? CanRun? 提示词非空?
   ├─ PushPromptHistory（提交即记录 —— 铁律 D3）
   ├─ 组装 refs：编辑模式下 [待修改图(第1位), ...RefImages]
   ├─ FlushConfig()（确保参数落盘）
   ↓
ImageApi.GenerateAsync(prompt, key, model, quality, aspect, n, refs, offline, ...)
   ├─ offline? → Placeholder.Png（确定性占位图，尊重 n）
   ├─ OpenRouter → POST /images（同步返回 b64）
   └─ APIMart   → POST /images/generations → 并发轮询 /tasks/{id}
                  （参考图先 POST /uploads/images 换公网 URL）
   ↓
GenResult { Images: [(bytes, media)], Cost, Tokens }
   ↓
MainViewModel.LandResultsAsync
   ├─ SaveImageToHome()：按输出格式定扩展名 + SkiaSharp 转码
   ├─ Session.Push(item) + Log(item)（写 state.json + history.jsonl）
   ├─ TotalCost / ProviderCost 累加
   ├─ RefreshHistory() / PublishBatchResults()
   └─ ShowPreviewAsync()（Dispatcher.Post，不阻塞）
```

### 配置持久化

```
用户改参数（模型/质量/画幅/分辨率/格式/批量/离线/润色）
   ↓
On*Changed → PersistConfig()
   ├─ 同步到 _sess.Config（内存态，立即）
   └─ 尾触发节流 Timer（120ms 安静后写一次，**最后一次必落盘**）
   ↓
Session.SaveConfig() → 原子写 config.json（.tmp → File.Move）
   ↑
   └─ Session._writeLock 全局串行化（防 UI 线程与 Timer 回调并发写）
```

**config.json 字段名是 snake_case**（`batch_n`/`output_format`），与 Python 版互通 ——
靠 `AppConfig` 上的 `[JsonPropertyName]` 映射。

---

## 三、双 provider 差异（关键）

| 维度 | OpenRouter | APIMart |
|---|---|---|
| 调用模式 | **同步**：POST 后直接返回图像 | **异步**：提交任务 → 轮询 `/tasks/{id}` |
| 模型命名 | `provider/model`（带斜杠） | **裸名**（如 `gpt-image-2.5-flare`） |
| 画幅参数 | `aspect_ratio` | `size` |
| 参考图 | `input_references`（**base64 内联**） | `image_urls`（**需先上传换公网 URL**） |
| 质量档 | auto/low/medium/high | auto/low/medium/high/**xhigh/max**（仅 2.5 系） |
| 批量 | `n` 参数 | 返回多个 task_id，**并发轮询** |

**成本估算差异**：`Catalog.EstimateCost` 按 quality × resolution 倍数 × n。
`auto` 档实测恒落 `low`（output_tokens 恒 196），故按 low 估。

---

## 四、UI 结构

### 三栏布局（桌面横屏）

```
┌──────────────┬────────────────────┬──────────────┐
│  左栏         │  中栏               │  右栏         │
│  · 生成参数   │  · 预览（大图）      │  · 历史/累计  │
│  · 提示词     │  · 多图缩略图条      │  · 消息流     │
│  · 主操作     │  · 编辑图片工具栏    │              │
│  · 参考图     │  · 系统看图器/保存   │              │
│  · 提示词历史  │                    │              │
└──────────────┴────────────────────┴──────────────┘
│  底栏：撤回/预览/保存/润色/快捷键/数据目录 + ⚙设置   │
└──────────────────────────────────────────────────┘
```

### 响应式（竖屏）

窄屏（< 900px）时**改为纵向堆叠**：预览优先 → 操作 → 参数。
见 `MainView.axaml` 的 `LayoutTransform`/`IsVisible` 断点切换。

### 浮层

| 浮层 | 触发 | 内容 |
|---|---|---|
| 设置 | 底栏 ⚙ | Provider / API Key（含校验）/ 润色配置 / 数据目录 |
| 调色板 | 编辑工具栏 | 16 色网格，点选即用 |
| 润色候选 | 润色按钮 | 4 选 1，←→ 翻页 + 重新生成 + 采用 |

---

## 五、区域标注（RegionCanvas）

```
预览图（Image 控件）
   ↑ 叠加
RegionCanvas（自定义 Control）
   ├─ 五种工具：马克笔 / 画笔 / 方框 / 圆圈 / 橡皮
   ├─ 两种语义：要修改（红 #FF3B30）/ 要保留（绿 #32D74B）
   ├─ 坐标**归一化**（0~1）→ 与控件尺寸无关，导出到原分辨率对齐
   └─ 导出 ExportComposite()
        ├─ 自由笔迹 → 先画到不透明 mask → mask 像素统一 alpha=110 → 贴回
        │             （**蒙版式非叠加**：重复涂色不加深）
        └─ 方框/圆圈 → 直接描边
```

标注图作为**参考图第 1 位**（铁律 D1：第 1 位=主体）传给模型，
并自动追加提示词说明「高亮区域即需修改部分」。

---

## 六、扩展点（加功能去哪）

| 想加什么 | 改哪里 |
|---|---|
| 新 provider | `Catalog.Providers` + `ImageApi.GenerateAsync` 分支 + `AppConfig.Provider` |
| 新参数（如 seed） | `Catalog` 常量 + `AppConfig` + `MainViewModel` 属性 + `MainView.axaml` 控件 |
| 新工具（如箭头） | `RegionCanvas.RegionTool` 枚举 + `Shape` 子类 + 工具栏下拉 |
| 新平台（如 iOS） | 新建 head 工程引用 `Imgagent.App` + 实现 `IPlatformStorage` |
| 新测试 | `tests/Imgagent.Integration.Tests/WorkbenchFlowTests.cs`（离线，不花钱） |

---

## 七、与 Python 版的关系

```
mobile/（Python + curses TUI）          avalonia/（C# + Avalonia GUI）
─────────────────────────────────       ─────────────────────────────
api.py            ──── 契约一致 ────→   Services/ImageApi.cs
store.py          ──── 契约一致 ────→   Storage/Session.cs
settings.py       ──── 契约一致 ────→   Catalog.cs
polish.py         ──── 契约一致 ────→   Services/PolishService.cs
curses_ui.py      ──── 重写为 ─────→   Views/MainView.axaml + MainViewModel
                                        （去掉终端绘制，保留 17 个 _act_* 业务逻辑）

CONSTRAINTS.md（A–G 七类铁律）────→   avalonia/docs/CONSTRAINTS.md（逐条平移）
```

**Python 版保留为**：参考实现 + 行为契约基准（959 项测试）。
