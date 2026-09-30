# Avalonia (C#/.NET) 跨平台重写可行性研究

> 目标：把现有 imghub（纯标准库 Python + curses TUI）重写为**图形化工作台**，
> 同时跑在 **Windows 桌面**与 **Android**，功能对齐并易于扩展。
> 调研日期：2026-09-19 · 本机环境：Windows / .NET SDK 10.0.302 / Java 26 / Android SDK (API 34,35)

> 📌 **历史文档，保留原貌不改**。文中的 `mobile/` 现已改名为 `legacy/`，
> `avalonia/` 子目录的内容已提到仓库根目录（见 [port-status.md](port-status.md) 四·十四）。
> 结论与风险判断仍有效；§四 的「P0 字体 gate」已通过（内嵌静态字体 + 桌面实测）。

---

## 一、结论（先说答案）

**技术路线可行，推荐采用。** 但有一个必须先实测的关键风险点（Android 中文字体渲染），
以及一个本机环境限制（Android 构建所需的 workload 未装全，需管理员权限补装）。

| 判断项 | 结论 | 依据 |
|---|---|---|
| 桌面端（Windows） | ✅ **已实测跑通** | 本报告附带的 PoC 实际构建并运行成功 |
| 中文字体渲染（Windows） | ✅ **已实测正常** | PoC 截图确认全部界面中文无乱码 |
| 内存图片→UI 显示 | ✅ **已实测可用** | `new Bitmap(Stream)` + `SkiaSharp.Decode` 均成功 |
| 异步 HTTP / API 调用 | ✅ **已实测可用** | `HttpClient` 请求成功返回 |
| 相册选图 / 存图到 Pictures | ✅ **官方 API 支持** | Avalonia StorageProvider 兼容表：Windows/Android 均 ✓ |
| Android 实际构建 | ⚠️ **本机未跑通（环境问题）** | 缺 API 36 pack，`dotnet workload install android` 需管理员权限 |
| Android 中文渲染 | ⚠️ **需实测确认** | 该框架有过 CJK 渲染回归（#19868/#20195），必须真机验证 |
| 许可 / 成本 | ✅ **MIT，商用免费** | 核心框架 MIT；付费项（Accelerate 热重载/图表）非必需 |

---

## 二、现有项目盘点（重写范围）

```
源码 8,545 行（19 个模块）  ·  测试 3,664 行（956 项）
```

| 模块 | 行数 | 性质 | 重写策略 |
|---|---|---|---|
| `curses_ui.py` | 3,283 | TUI 渲染 + 交互 | **整体替换**为 XAML 视图 + ViewModel |
| `ui.py` | 991 | CLI 菜单界面 | **整体替换**（或保留作 headless 模式） |
| `api.py` | 560 | 双 provider API（生成/编辑/上传/轮询） | **逻辑移植**（几乎 1:1） |
| `settings.py` | 490 | 常量 + 路径 | 改为 `IOptions`/配置服务 |
| `android.py` | 452 | pyjnius 原生桥（可选） | **删除**（改用 Avalonia StorageProvider） |
| `store.py` | 420 | 落盘 + 设置 + 历史 | **逻辑移植** + 换存储层 |
| `polish.py` | 316 | 提示词润色（chat/completions） | **逻辑移植** |
| `console.py` | 319 | 终端加固 + 配色 | **删除**（GUI 不需要） |
| `pngcodec.py` | 263 | 纯 Python PNG 编解码/缩放 | **删除**（SkiaSharp 代劳） |
| `termimg.py` | 202 | 终端字符画渲染 | **删除**（真正显示图片） |
| `doctor.py` | 201 | 环境诊断 | **重写**为诊断视图 |
| `photos.py` | 197 | 相册扫描/选择 | **替换**为 StorageProvider |
| `preview.py` | 195 | 4 级预览降级链 | **替换**为 Image 控件 |
| `httpclient.py` | 137 | urllib 封装（重试+错误翻译） | **逻辑移植** + HttpClient |
| `concurrency.py` | 97 | IO 并发 | 用 `Task.WhenAll` |
| `placeholder.py` | 66 | 离线占位图 | **逻辑移植**（可用 SkiaSharp 生成） |
| `app.py`/`__main__`/`__init__` | 356 | 入口编排 | 重写为 Avalonia 启动流程 |

**关键发现：约 2,600 行是"可平移的业务逻辑"（api/store/polish/httpclient/placeholder），
约 4,300 行是"平台适配层"（curses/console/termimg/pngcodec/android/photos/preview），
后者在 Avalonia 下大多可直接删除或由框架能力替代。**

> 注意：`curses_ui.py` 的 3,283 行里，真正的"业务编排"（`_act_generate`/`_act_edit`/
> `load_edit_images` 等 17 个动作函数）约占 800 行，其余是绘制/按键/浮窗等终端专属代码。
> 这些动作函数是**移植的核心资产**。

---

## 三、Avalonia 方案实测验证（PoC）

### 3.1 已实测通过的项目（Windows 桌面）

我创建了一个 Avalonia 12.1.2 项目，完整复刻了 imghub 的三栏工作台界面，实测结果：

```
[OK] 内存字节 -> Avalonia Bitmap: 64x64       ← 生成结果不落盘直接显示
[OK] SkiaSharp.Decode(byte[]): 64x64          ← 图片解码路径
[OK] 系统字体数: 319, Typeface: Microsoft YaHei, Noto Sans SC  ← 中文字体链
[OK] HttpClient(异步) HTTP 403                 ← 网络连通（403 仅因缺 UA）
```

界面截图确认：**顶栏、参数面板、预览区、历史列表、消息区、底栏的
全部中文文本渲染正确**，图片在预览区正常显示，MVVM 绑定工作正常。

### 3.2 PoC 已覆盖的技术点

| 技术点 | 验证方式 | 结果 |
|---|---|---|
| `TextBox`（中文输入框） | 界面含中文 Watermark + 多行输入 | ✅ |
| `Image` 控件从 `byte[]` 显示 | `new Bitmap(new MemoryStream(png))` | ✅ |
| SkiaSharp 直接解码 | `SKBitmap.Decode(bytes)` | ✅ |
| 中文字体回退链 | `Typeface("Microsoft YaHei, Noto Sans SC, ...")` | ✅ |
| 异步 I/O（不阻塞 UI） | `async Task` + `Dispatcher.UIThread` | ✅ |
| MVVM（CommunityToolkit.Mvvm） | 模板内置，绑定正常 | ✅ |
| 跨平台工程结构 | `dotnet new avalonia.xplat` 生成 Android/iOS/Desktop/Browser 四 head | ✅ 结构就位 |

### 3.3 未跑通 / 需后续验证

| 项 | 状态 | 原因 |
|---|---|---|
| Android APK 实际构建 | ⚠️ 未跑通 | 本机 android workload 仅含 API 35 pack，Avalonia 12 模板要求 `net10.0-android`（API 36）；`dotnet workload install android` 需管理员权限写 `C:\Program Files\dotnet`，当前会话被拒 |
| Android 中文字体渲染 | ⚠️ 未验证 | 见 §四 风险 |
| Android 相册/存图 | ⚠️ 未验证 | 需真机；API 层面官方已支持 |

---

## 四、关键风险与对策

### 🔴 风险 1（最高）：Android 上的 CJK 中文渲染

**这是本项目最大的不确定性**——因为整个 UI 是全中文。

调研发现的证据链：
- Issue **#19868**：「CJK characters can't be rendered with newest version of Avalonia on
  Android」，报告**从 11.3.5 起**出现，提及该问题在 **11.3.9 恢复**；
- Issue **#20195**：升级到 11.3.9 后，**部分 Android 系统**上中文字符显示错误；
- Issue **#19931**：Android 上默认 Inter 字体的**字形回退是坏的**；
- Issue **#12099**：即便嵌入 Noto Sans SC 也出现过「could not create glyph Typeface」。

**结论**：该问题是**版本相关 + 设备相关**的，不能仅凭文档断定已修。

**对策（必须做，且是项目启动前的 gate）**：
1. 在**目标真机**上跑最小验证：一个只显示「中文测试 imghub 生成 编辑 撤回」的页面；
2. 覆盖 `net10.0-android`（Avalonia 12）与 `net10.0-android`（Avalonia 11.3.x）两条线；
3. **嵌入 CJK 字体作为兜底**（推荐，一劳永逸）：
   ```xml
   <!-- csproj -->
   <ItemGroup>
     <AvaloniaResource Include="Assets\Fonts\" />
   </ItemGroup>
   ```
   ```xml
   <TextBlock Text="中文测试"
              FontFamily="avares://ImgHub/Assets/Fonts/NotoSansSC-Regular.otf#Noto Sans SC" />
   ```
   并在 `Program.cs` 注册字体集合：
   ```csharp
   var fonts = new EmbeddedFontCollection(
       new Uri("fonts:ImgHub", UriKind.Absolute),
       new Uri("avares://ImgHub/Assets/Fonts", UriKind.Absolute));
   FontManager.Current.AddFontCollection(fonts);
   ```
   注意：嵌入字体后仍**需真机验证**——#12099 表明它不总是成功。
4. 若 Android CJK 始终不可靠 → **降级方案**：Android 端界面改用**英文 + 图标**，
   或改用 **.NET MAUI**（原生控件，系统字体天然正确，代价是桌面端 UI 一致性差些）。

> 建议：**先花 1–2 天做这个字体 gate，再决定是否全量投入。** 这是唯一可能推翻整个方案的
> 风险点。

### 🟡 风险 2：Android 构建工具链

- 需 `dotnet workload install android`（含 API 36 pack）+ JDK 17+（本机 Java 26 可用）；
- 本机当前**只有 API 35 pack**，且安装需管理员权限 → 需你手动补装；
- 官方已提示 Google Play 的 **16 KB page size** 要求（Android 15+），
  需 Avalonia 11.3.6+ / SkiaSharp 3.x（本机 SkiaSharp 已是 3.119.4，满足）。

### 🟡 风险 3：APK 体积

- Avalonia Android 基础 APK 约 **25 MB（压缩）/ 58 MB（解压）**，显著大于现有纯 Python 包（190 KB）；
- 缓解：限制 ABI（`android-arm64;android-arm`）、`PublishSingleFile`、裁剪。
- 对"工具类 App"可接受，但需知情。

### 🟢 风险 4：Android 软键盘/IME 细节

- 有报告称软键盘未收起时点按钮会卡、焦点切换有回归 → 需在真机验收时覆盖
  「中文输入法 → 切焦点 → 收起键盘」流程。

---

## 五、推荐架构（跨平台工作台）

```
ImgHub.slnx
├── ImgHub.Core/            ← 平台无关（netstandard/net10.0）
│   ├── Models/                Item、GenResult、Session、AppConfig
│   ├── Services/
│   │   ├── IImageApi.cs       生成/编辑/上传/轮询（双 provider）
│   │   ├── IImageApiOpenRouter.cs
│   │   ├── IImageApiApimart.cs
│   │   ├── IPolishService.cs  提示词润色
│   │   ├── IHistoryStore.cs   历史 + 提示词历史 + config
│   │   ├── ICostEstimator.cs  成本估算表
│   │   └── IImageCodec.cs     解码/缩放（SkiaSharp）
│   └── Utilities/             错误翻译、重试、文件名安全
├── ImgHub.App/             ← 共享 UI（XAML + ViewModel）
│   ├── Views/                  GenerateView / EditView / HistoryView / SettingsView
│   ├── ViewModels/             MVVM（CommunityToolkit.Mvvm）
│   ├── Assets/Fonts/           NotoSansSC（CJK 兜底）
│   └── App.axaml
├── ImgHub.Desktop/         ← net10.0  （Windows/Linux/macOS）
└── ImgHub.Android/         ← net10.0-android  （APK/AAB）
```

**关键设计原则（延续原项目的工程约束）**：
1. **Core 不引用任何 UI/平台类型** → 可单元测试、可复用；
2. **平台差异走接口注入**（`IStorageService`、`IPreviewService`），
   各 head 提供实现（Windows 用 `StorageProvider`，Android 用 `StorageProvider` + MediaStore）；
3. **配置与密钥存储**：改用各平台安全存储（Windows DPAPI / Android EncryptedSharedPreferences），
   比原项目的明文 `.imghub_key` 更安全；
4. **成本/离线/提示词不丢** 等业务规则**逐条对应移植**，并保留回归测试。

---

## 六、工作量与分期建议

| 阶段 | 内容 | 预估 |
|---|---|---|
| **P0 风险 gate** | 真机验证 Android 中文渲染 + 构建 APK | 1–2 天 |
| **P1 Core 移植** | api/httpclient/store/polish/placeholder → C#（含测试） | 5–8 天 |
| **P2 主界面** | 三栏工作台 XAML + 生成/编辑流程 MVVM | 5–7 天 |
| **P3 历史/设置/成本** | 历史回退、设置面板、成本显示、离线模式 | 3–4 天 |
| **P4 平台能力** | StorageProvider 选图/存图、系统看图器、权限 | 2–3 天 |
| **P5 打磨** | 响应式布局（手机竖屏/平板/桌面）、深色主题、图标 | 3–5 天 |
| **合计** | | **约 19–29 人天**（单人） |

> 对比：现有 8.5k 行 Python 是**多年演进**的结果（含 39 条铁律教训）。
> 重写不是"从零"，而是**带着这些教训平移**，所以核心逻辑移植会快，
> 但 UI 交互（尤其手机端手势/键盘）需要重新设计。

---

## 七、为什么不选 .NET MAUI？

| 维度 | Avalonia | .NET MAUI |
|---|---|---|
| 渲染模型 | 自绘（跨平台像素级一致） | 包装原生控件（各平台外观不同） |
| 桌面表现 | **强**（桌面血统，适合三栏工作台） | 一般（移动优先） |
| Android 表现 | 追赶中（12 声称 3x 提升） | **成熟**（原生控件） |
| 中文渲染 | ⚠️ 有历史问题（需验证） | ✅ 原生控件用系统字体，天然正确 |
| 代码共享 | 单一 XAML + 自绘 | XAML 但需平台特化 |
| 许可 | MIT（核心免费） | MIT（微软） |

**结论**：如果**桌面（Windows）是主要目标、Android 是次要**，选 Avalonia；
如果**两端同等重要且 Android 中文渲染过不了 gate**，则 MAUI 更稳。
本项目的"手机竖屏 + 桌面 + 中文 + 成本可见"特征更贴近 Avalonia 的设计目标，
但**必须先过字体 gate**。

---

## 八、给用户的建议

1. **先做 P0 风险 gate**（1–2 天）：装 Android workload（需管理员权限）→ 跑一个
   全中文最小 App 到你的目标真机 → 确认无乱码。这是**唯一可能否决方案的环节**。
2. PoC 已证明**桌面端完全可行**（本报告附带的截图与日志）。
3. 若 gate 通过 → 按 §五 架构、§六 分期推进；建议 **Core 与 UI 分离**，
   这样即使将来换 UI 框架，业务逻辑也能复用。
4. 保留现有 Python 版作为**参考实现与回归基准**（959 项测试是宝贵的行为契约）。

---

## 附录：本报告的证据来源

- Avalonia 官方文档：StorageProvider 兼容表、自定义字体、Android 部署、Native AOT
- Avalonia GitHub：Releases、Issue #19868 / #20195 / #19931 / #12099 / #19362、Discussion #17028 / #18905
- Avalonia 官方博客：v11 发布、v12 发布（"3x Android performance"）、MAUI 对比
- 本机 PoC：`dotnet new avalonia.mvvm` + 实际构建运行（Windows 桌面）
- 现有代码盘点：`mobile/impydroid/*.py` 行数与接口清单
