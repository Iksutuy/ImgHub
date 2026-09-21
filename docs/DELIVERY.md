# imgagent（Avalonia 版）— 交付文档

> 讲清「怎么打包、产物在哪、各平台注意事项」。
> 约束见 [CONSTRAINTS.md](CONSTRAINTS.md)，架构见 [ARCHITECTURE.md](ARCHITECTURE.md)。

---

## 一、交付产物一览

| 产物 | 路径 | 体积 | 适用 |
|---|---|---|---|
| **Windows 桌面（Native AOT）** | `release/desktop-aot/` | 23.3 MB exe + 18 MB DLL | 追求最小体积/最快启动 |
| **Windows 桌面（框架依赖）** | `release/desktop/` | 0.16 MB exe + 依赖 | 目标机已装 .NET 10 Desktop Runtime |
| **Android APK** | `release/android/imgagent-*-android.apk` | ~42 MB | Android 7.0+ (API 23+) |

---

## 二、桌面版打包

### L3：Native AOT（推荐，23 MB）

```powershell
cd avalonia
powershell -File build.ps1 -Target desktop-aot
```

等价于：
```powershell
dotnet publish src\Imgagent.Desktop -c Release -r win-x64 `
    -p:PublishAot=true `
    -p:DebugType=none -p:DebugSymbols=false `
    -o ..\release\desktop-aot
```

**产物内容（必须先杀进程，见约束 E2）**：
```
release/desktop-aot/
  Imgagent.Desktop.exe     23.31 MB   ← AOT 编译的原生可执行
  libSkiaSharp.dll         11.09 MB   ← 必需
  av_libglesv2.dll          5.14 MB   ← 必需
  libHarfBuzzSharp.dll      1.73 MB   ← 必需
```

> ⚠️ **AOT exe 不可单独拷走**（约束 E1）：缺任一 DLL 会以 `0xC0000409` 秒崩。
> 分发时必须整个目录打包。

### L2：单文件 + 裁剪（41 MB，备选）

```powershell
dotnet publish src\Imgagent.Desktop -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=true -p:TrimMode=partial `
    -p:DebugType=none -p:DebugSymbols=false
```

真·单个 exe（41 MB），双击即用，无 DLL 伴随。

### 三级方案对比（实测）

| 级别 | 配置 | 体积 | 启动 | 备注 |
|---|---|---|---|---|
| L1 | 自包含单文件 | 99 MB | 正常 | pdb 默认生成导致偏大 |
| L2 | + 裁剪（TrimMode=partial） | **41 MB** | 正常 | 真单文件 |
| **L3** | **Native AOT** | **23 MB** | **更快** | 需带 3 个原生 DLL |

### AOT 友好度（本工程已满足）

| 条件 | 状态 |
|---|---|
| `AvaloniaUseCompiledBindingsByDefault=true` | ✅ |
| 全 XAML 编译期绑定 + `x:DataType` | ✅ |
| 无动态 XAML / XamlReader.Load | ✅ |
| 无反射式 DI（手工组合根） | ✅ |
| 资源走 `AvaloniaResource` | ✅ |

**残留警告**（无害，功能实测正常）：
`IL2026`/`IL3050` —— `Session`/`HttpJsonClient` 的 System.Text.Json 反射序列化。
彻底消除需迁移到 **source generation**（列为后续增强）。

---

## 三、Android 打包

### 前置条件

```powershell
# 1) 装 workload（需管理员，或用已装的用户目录 SDK）
dotnet workload install android --skip-manifest-update

# 2) 装 Android SDK + JDK 到**用户目录**（避免管理员权限）
dotnet build src\Imgagent.Android -t:InstallAndroidDependencies `
    -f net10.0-android -p:AcceptAndroidSDKLicenses=True `
    -p:AndroidSdkDirectory=$env:USERPROFILE\android-sdk-imgagent `
    -p:JavaSdkDirectory=$env:USERPROFILE\jdk-imgagent
```

### 打包

```powershell
cd avalonia
powershell -File build.ps1 -Target android
```

等价于：
```powershell
$env:ANDROID_HOME="$env:USERPROFILE\android-sdk-imgagent"
dotnet build src\Imgagent.Android -c Release `
    -p:AndroidSdkDirectory=$env:ANDROID_HOME `
    -p:JavaSdkDirectory=$env:USERPROFILE\jdk-imgagent
```

**产物**：`src/Imgagent.Android/bin/Release/net10.0-android/com.imgagent.app-Signed.apk`

### 图标资源（Android 特有）

Android 需要**多密度 mipmap**（不是单个 PNG）：

```
src/Imgagent.Android/Resources/
  ├─ mipmap-mdpi/ic_launcher.png       48×48
  ├─ mipmap-hdpi/ic_launcher.png       72×72
  ├─ mipmap-xhdpi/ic_launcher.png      96×96
  ├─ mipmap-xxhdpi/ic_launcher.png     144×144
  ├─ mipmap-xxxhdpi/ic_launcher.png    192×192
  ├─ mipmap-anydpi-v26/ic_launcher.xml 自适应图标（前景+背景）
  └─ values/ic_launcher_background.xml 背景色
```

清单里：`android:icon="@mipmap/ic_launcher"`

> ⚠️ 旧版用 `drawable/Icon.png` 单个资源 → Android 各密度设备会**拉伸模糊**或**显示默认图标**。

### Android 特有配置

**AndroidManifest.xml**：
```xml
<uses-permission android:name="android.permission.INTERNET" />
<uses-permission android:name="android.permission.READ_MEDIA_IMAGES" />
<uses-permission android:name="android.permission.WRITE_EXTERNAL_STORAGE"
                 android:maxSdkVersion="28" />
<application android:label="imgagent 工作台"
             android:icon="@mipmap/ic_launcher" ... />
```

**数据目录**（`AppPaths.ResolveHome`）：`/data/data/com.imgagent.app/files/imgagent`（应用私有，无需权限）

---

## 四、发布前检查清单

```
□ 跑全量测试：dotnet test tests\Imgagent.Core.Tests + Integration（应 92/92 通过）
□ 杀残留进程：Get-Process -Name "Imgagent*" | Stop-Process -Force
□ 桌面版：图标显示正确（资源管理器 + 任务栏 + 窗口标题栏）
□ 桌面版：中文无方框
□ Android：图标为应用图标（非默认安卓机器人）
□ Android：中文无方框
□ Android：横竖屏布局正常（竖屏纵向堆叠）
□ Android：真机验证（模拟器可能缺字体，与真机表现不同）
```

---

## 五、版本号约定

| 位置 | 字段 |
|---|---|
| `src/Imgagent.Android/Imgagent.Android.csproj` | `ApplicationDisplayVersion`（如 5.19.0） |
| `src/Imgagent.Core/*` | 无版本（由 head 决定） |
| 产物名 | `imgagent-<版本>-<平台>.<ext>` |

---

## 六、已知限制

| 项 | 说明 |
|---|---|
| Android `保存到相册` | 走 SAF picker（用户选位置），非静默写入相册 —— 后续可接 MediaStore |
| Android 中文渲染 | 已在 `App.axaml` 显式字体链 + 计划内嵌 Noto Sans SC；**需真机验证** |
| iOS | 未实现（架构支持，只需新增 head） |
| Linux/macOS | 理论可用（Avalonia 跨平台），未实测 |
