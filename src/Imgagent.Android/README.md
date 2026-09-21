# Imgagent.Android — Android head

> 极薄的平台适配层。界面与逻辑全部复用 `Imgagent.App`。

## 文件说明

| 文件 | 职责 |
|---|---|
| `MainActivity.cs` | Activity 宿主（`AvaloniaMainActivity`，非泛型 —— Avalonia 12 变更） |
| `AndroidApp.cs` | Application：`AvaloniaAndroidApplication<Imgagent.App.App>` 指定 App 类型 |
| `Properties/AndroidManifest.xml` | 权限（网络/读图/写外部存储）+ 应用标签与图标 |
| `Resources/` | 图标 mipmap（各密度）+ 主题样式 |
| `Imgagent.Android.csproj` | 目标框架 `net10.0-android`（API 36）+ 包名/版本 |

## 图标（Android 特有）

Android **不使用单个 PNG**，而是**多密度 mipmap + 自适应图标**：

```
Resources/
  mipmap-mdpi/ic_launcher.png        48×48
  mipmap-hdpi/ic_launcher.png        72×72
  mipmap-xhdpi/ic_launcher.png       96×96
  mipmap-xxhdpi/ic_launcher.png      144×144
  mipmap-xxxhdpi/ic_launcher.png     192×192
  mipmap-anydpi-v26/ic_launcher.xml  自适应图标（foreground + background）
  values/colors.xml                  ic_launcher_background 底色
```

清单引用：`android:icon="@mipmap/ic_launcher"`

> ⚠️ 旧版用 `drawable/Icon.png` 单资源 → 各密度设备拉伸模糊或显示默认图标（用户反馈的问题 1）。

生成脚本：`tools/make-android-icons.ps1`（从 `App/Assets/app-icon.png` 生成全套）。

## 数据目录

`AppPaths.ResolveHome()` → `/data/data/com.imgagent.app/files/imgagent`（应用私有，无需存储权限）。

## 构建前置

| 依赖 | 说明 |
|---|---|
| `dotnet workload install android` | 需要 API 36 pack |
| Android SDK + JDK | 建议装到**用户目录**（免管理员）：`%USERPROFILE%\android-sdk-imgagent`、`%USERPROFILE%\jdk-imgagent` |
| 首次补装 SDK | `dotnet build -t:InstallAndroidDependencies -p:AcceptAndroidSDKLicenses=True ...` |

详见 `docs/DELIVERY.md`。

## 已知问题（进行中）

| 问题 | 状态 |
|---|---|
| **中文显示为方框** | Avalonia Android CJK 回归（#19868/#20195）。缓解：显式字体链 + 内嵌 Noto Sans SC |
| **竖屏三栏挤压** | 待改为纵向堆叠响应式布局 |
| 保存到相册 | 走 SAF picker（用户选位置），非静默写相册 |
