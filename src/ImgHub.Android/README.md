# ImgHub.Android — Android head

> 极薄的平台适配层。界面与逻辑全部复用 `ImgHub.App`。

## 文件说明

| 文件 | 职责 |
|---|---|
| `MainActivity.cs` | Activity 宿主（`AvaloniaMainActivity`，非泛型 —— Avalonia 12 变更） |
| `AndroidApp.cs` | Application：`AvaloniaAndroidApplication<ImgHub.App.App>` 指定 App 类型 |
| `Properties/AndroidManifest.xml` | 权限（网络/读图/写外部存储）+ 应用标签与图标 |
| `Resources/` | 图标 mipmap（各密度）+ 主题样式 |
| `ImgHub.Android.csproj` | 目标框架 `net10.0-android`（API 36）+ 包名/版本 |

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

`AppPaths.ResolveHome()` → `/data/data/com.imghub.app/files/imghub`（应用私有，无需存储权限）。

## 构建前置

| 依赖 | 说明 |
|---|---|
| `dotnet workload install android` | 需要 API 36 pack |
| Android SDK + JDK | 建议装到**用户目录**（免管理员）：`%USERPROFILE%\android-sdk-imghub`、`%USERPROFILE%\jdk-imghub` |
| 首次补装 SDK | `dotnet build -t:InstallAndroidDependencies -p:AcceptAndroidSDKLicenses=True ...` |

详见 `docs/DELIVERY.md`。

## 已知问题（进行中）

| 问题 | 状态 |
|---|---|
| **中文显示为方框** | 已缓解：内嵌 `NotoSansSC-Regular.ttf`（静态子集 7.15 MB，见 `App.axaml`）。**桌面实测正常，Android 仍需真机验证** |
| **竖屏三栏挤压** | ✅ 已解决：`IsWideLayout` 断点（900px）切换纵向堆叠，446×859 实测通过 |
| 保存到相册 | 走 SAF picker（用户选位置），非静默写相册 |
| 中文输入法细节 | 软键盘未收起时点按钮的焦点行为未在真机验证（可行性报告风险 4） |
