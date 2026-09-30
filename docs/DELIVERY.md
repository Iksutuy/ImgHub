# ImgHub（Avalonia 版）— 交付文档

> 讲清「怎么打包、产物在哪、各平台注意事项」。
> 约束见 [CONSTRAINTS.md](CONSTRAINTS.md)，架构见 [ARCHITECTURE.md](ARCHITECTURE.md)。

---

## 一、交付产物一览

| 产物 | 路径 | 体积 | 适用 |
|---|---|---|---|
| **Windows 桌面（Native AOT）** | `release/desktop-aot/` | 30.6 MB exe + 18 MB DLL | 追求最小体积/最快启动 |
| **Windows 桌面（框架依赖）** | `release/desktop/` | 0.16 MB exe + 依赖 | 目标机已装 .NET 10 Desktop Runtime（`build.ps1 -Target desktop` 的产物） |
| **Android APK** | `release/android/imghub-*-android.apk` | ~51 MB | Android 7.0+ (API 23+) |

> 体积随内嵌 CJK 字体与版本变化，上表为 v5.22.0 实测值（v0.5.x 已重新 AOT 打包：exe 约 **31 MB**）。

---

## 二、桌面版打包

### 快捷方式：`build.ps1`

```powershell
powershell -File build.ps1 -Target desktop           # 框架依赖（release/desktop）
powershell -File build.ps1 -Target android           # Android APK（release/android）
powershell -File build.ps1                           # 两者都构建
```

`build.ps1` 的 `ValidateSet` 只接受 `all | desktop | android`，**不含 AOT**。
追求最小体积需按下面手动发布。

### L3：Native AOT（推荐，最小体积）

```powershell
# 在仓库根目录执行
dotnet publish src\ImgHub.Desktop -c Release -r win-x64 `
    -p:PublishAot=true `
    -p:DebugType=none -p:DebugSymbols=false `
    -o release\desktop-aot
```

**产物内容（必须先杀进程，见约束 E2）**：
```
release/desktop-aot/
  ImgHub.Desktop.exe     30.59 MB   ← AOT 编译的原生可执行
  libSkiaSharp.dll         11.09 MB   ← 必需
  av_libglesv2.dll          5.14 MB   ← 必需
  libHarfBuzzSharp.dll      1.73 MB   ← 必需
```

> ⚠️ **AOT exe 不可单独拷走**（约束 E1）：缺任一 DLL 会以 `0xC0000409` 秒崩。
> 分发时必须整个目录打包。

### L2：单文件 + 裁剪（备选）

```powershell
dotnet publish src\ImgHub.Desktop -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=true -p:TrimMode=partial `
    -p:DebugType=none -p:DebugSymbols=false
```

真·单个 exe，双击即用，无 DLL 伴随。

### 三级方案对比（实测）

| 级别 | 配置 | 体积 | 启动 | 备注 |
|---|---|---|---|---|
| L1 | 自包含单文件 | 99 MB | 正常 | pdb 默认生成导致偏大 |
| L2 | + 裁剪（TrimMode=partial） | **41 MB** | 正常 | 真单文件 |
| **L3** | **Native AOT** | **~31 MB exe** | **更快** | 需带 3 个原生 DLL |

### AOT 友好度（本工程已满足）

| 条件 | 状态 |
|---|---|
| `AvaloniaUseCompiledBindingsByDefault=true` | ✅ |
| 全 XAML 编译期绑定 + `x:DataType` | ✅ |
| 无动态 XAML / XamlReader.Load | ✅ |
| 无反射式 DI（手工组合根） | ✅ |
| 资源走 `AvaloniaResource` | ✅ |
| **JSON 全走 source-gen / `JsonObject`（无反射序列化）** | ✅ **v5.24.0 起** |
| **AOT 发布零 IL2026/IL3050 警告** | ✅ **v5.24.0 起** |

> ⚠️ **更正历史表述**：v5.23.0 曾把 `IL2026`/`IL3050` 记为
> 「残留警告（无害，功能实测正常）」—— **这是错的**。
> 该警告对应的反射序列化在 **AOT 产物下直接导致「生成」不可用**
> （`Reflection-based serialization has been disabled`）。
> v5.24.0 已全部清零，做法与踩坑记录见 [CONSTRAINTS.md](CONSTRAINTS.md) §H。

### AOT 产物**必须实跑**验证

框架依赖构建通过 ≠ AOT 可用。发布后至少验证：
**启动 → 离线生成 → 真实生成 → 润色 → 改参数重启恢复 → 累计金额不清零**。
（详见 CONSTRAINTS.md H3 的 7 项清单。）

---

## 三、Android 打包

### 前置条件

```powershell
# 1) 装 workload（需管理员，或用已装的用户目录 SDK）
dotnet workload install android --skip-manifest-update

# 2) 装 Android SDK + JDK 到**用户目录**（避免管理员权限）
dotnet build src\ImgHub.Android -t:InstallAndroidDependencies `
    -f net10.0-android -p:AcceptAndroidSDKLicenses=True `
    -p:AndroidSdkDirectory=$env:USERPROFILE\android-sdk-imghub `
    -p:JavaSdkDirectory=$env:USERPROFILE\jdk-imghub
```

### 打包

```powershell
# 在仓库根目录执行
powershell -File build.ps1 -Target android
```

等价于：
```powershell
$env:ANDROID_HOME="$env:USERPROFILE\android-sdk-imghub"
dotnet build src\ImgHub.Android -c Release `
    -p:AndroidSdkDirectory=$env:ANDROID_HOME `
    -p:JavaSdkDirectory=$env:USERPROFILE\jdk-imghub
```

**产物**：`src/ImgHub.Android/bin/Release/net10.0-android/com.imghub.app-Signed.apk`
（`build.ps1` 会把它复制为 `release/android/imghub-<版本>-android.apk`）

### 图标资源（Android 特有）

Android 需要**多密度 mipmap**（不是单个 PNG）：

```
src/ImgHub.Android/Resources/
  ├─ mipmap-mdpi/ic_launcher.png       48×48   (+ ic_launcher_round.png)
  ├─ mipmap-hdpi/ic_launcher.png       72×72   (+ round)
  ├─ mipmap-xhdpi/ic_launcher.png      96×96   (+ round)
  ├─ mipmap-xxhdpi/ic_launcher.png     144×144 (+ round)
  ├─ mipmap-xxxhdpi/ic_launcher.png    192×192 (+ round)
  │                                             (+ ic_launcher_foreground.png 432×432)
  ├─ mipmap-anydpi-v26/ic_launcher.xml 自适应图标（前景+背景）
  ├─ mipmap-anydpi-v26/ic_launcher_round.xml
  └─ values/colors.xml                 ic_launcher_background 底色
```

清单里：`android:icon="@mipmap/ic_launcher"` + `android:roundIcon="@mipmap/ic_launcher_round"`

由 `tools/make-android-icons.ps1` 从 `src/ImgHub.App/Assets/app-icon.png` 生成。

> ⚠️ 旧版用 `drawable/Icon.png` 单个资源 → Android 各密度设备会**拉伸模糊**或**显示默认图标**。

### Android 特有配置

**AndroidManifest.xml**：
```xml
<uses-permission android:name="android.permission.INTERNET" />
<uses-permission android:name="android.permission.READ_MEDIA_IMAGES" />
<uses-permission android:name="android.permission.WRITE_EXTERNAL_STORAGE"
                 android:maxSdkVersion="28" />
<application android:label="imghub 工作台"
             android:icon="@mipmap/ic_launcher" ... />
```

**数据目录**（`AppPaths.ResolveHome`）：`/data/data/com.imghub.app/files/imghub`（应用私有，无需权限）

---

## 四、发布前检查清单

```
□ 跑全量测试：dotnet test tests\ImgHub.Core.Tests + Integration（应 159/159 通过）
□ 杀残留进程：Get-Process -Name "ImgHub*" | Stop-Process -Force
□ 桌面版：图标显示正确（资源管理器 + 任务栏 + 窗口标题栏）
□ 桌面版：中文无方框
□ 桌面版：顶栏「设置」可打开浮层（填 Provider + API Key）
□ 桌面版：改窗口大小，宽窄两套布局切换正常、控件不丢
□ Android：图标为应用图标（非默认安卓机器人）
□ Android：中文无方框
□ Android：横竖屏布局正常（竖屏纵向堆叠）
□ Android：真机验证（模拟器可能缺字体，与真机表现不同）
```

> 前两项桌面检查对应约束 D4b/D4c —— 这两类是**测试无法覆盖**的 UI 可达性缺陷。

---

## 五、版本号约定

**唯一真源** = 仓库根 `Directory.Build.props` 的 `<Version>`（当前 `0.5.43`）。

| 位置 | 字段 | 是否需手动同步 |
|---|---|---|
| `Directory.Build.props` | `<Version>` | **改这里**（真源） |
| `src/ImgHub.Android/ImgHub.Android.csproj` | `ApplicationDisplayVersion` | ⚠️ **需同步**（Android head 不读 `<Version>`） |
| `build.ps1` | 从 `Directory.Build.props` **读取**版本生成产物名 | ✅ 无需改（v0.5.28 起自动读取） |
| `src/ImgHub.Core/*` | 无版本（由 head 决定） | — |
| 产物名 | `imghub-<版本>-<平台>.<ext>`（由 `tools/make-release.ps1` 生成） | — |

> ⚠️ 改版本号时**两处都要改**（`Directory.Build.props` 与 Android csproj），
> 否则 APK 内版本与产物名不一致。
> `tools/make-release.ps1` 会读取两处并**在不一致时给出警告**（不阻断打包）。

### `<RepositoryUrl>` 的现状（如实记录）

`Directory.Build.props` 里有 `<RepositoryUrl>`，但**目前没有任何代码读取它**。
UI 底栏显示的 `VersionText` 取自 `AssemblyInformationalVersion`，
渲染为一个**纯文本 `TextBlock`，不可点击**（`MainView.axaml`）。
若将来要做「点版本号跳转 GitHub」，才需要引入 `HyperlinkButton` / `LaunchUri` 并读该字段。

---

## 六、已知限制

| 项 | 说明 |
|---|---|
| Android `保存到相册` | 走 SAF picker（用户选位置），非静默写入相册 —— 后续可接 MediaStore |
| Android 中文渲染 | 已内嵌 `NotoSansSC-Regular.ttf`（静态子集，7.15 MB）为兜底；**桌面实测正常，Android 需真机验证** |
| Android 中文（可变字体坑） | 见约束 D6 —— 曾用 VF 字体导致笔画发虚 |
| 「离线」选项 | debug 功能，默认隐藏，需 `IMGHUB_DEBUG=1` |
| 快捷键 | 消息面板的快捷键提示目前**仅为文案**，未绑定实际按键 |
| 环境诊断面板 | 原版 `doctor.py` 未移植 |
| iOS | 未实现（架构支持，只需新增 head） |
| Linux/macOS | 理论可用（Avalonia 跨平台），未实测 |
