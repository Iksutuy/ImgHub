# Imgagent.Desktop — Windows/Linux/macOS head

> 极薄的平台适配层（约 50 行）。所有界面与逻辑都在 `Imgagent.App`。

## 文件说明

| 文件 | 职责 |
|---|---|
| `Program.cs` | 入口：`BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)` |
| `app.manifest` | Windows 清单：Per-Monitor V2 DPI 感知 + 支持的 OS 版本 |
| `app.ico` | **exe 文件图标**（多尺寸 16/24/32/48/64/128/256，PNG-in-ICO） |
| `Imgagent.Desktop.csproj` | 目标框架 `net10.0` + 图标 + RID 发布配置 |

## 图标

- **exe 图标**：`app.ico`，由 `../../make-icon.ps1` 从 `../Imgagent.App/Assets/app-icon.png` 生成
- **窗口图标**：在 `Imgagent.App/Views/MainWindow.axaml` 用
  `Icon="avares://Imgagent.App/Assets/app-icon.png"`（**必须 PNG**，见约束 D4）

重新生成 ico：
```powershell
cd avalonia
powershell -File make-icon.ps1
```

## 打包

见 `docs/DELIVERY.md`。三种方式：

| 方式 | 体积 | 命令要点 |
|---|---|---|
| Native AOT | 23 MB exe + 18 MB DLL | `-p:PublishAot=true` |
| 单文件裁剪 | 41 MB 单 exe | `-p:PublishSingleFile=true -p:PublishTrimmed=true -p:TrimMode=partial` |
| 框架依赖 | 0.16 MB exe | 目标机需装 .NET 10 Desktop Runtime |

> ⚠️ AOT 产物**必须整目录分发**（含 `libSkiaSharp.dll`/`av_libglesv2.dll`/`libHarfBuzzSharp.dll`）。

## 平台能力实现

`IPlatformStorage` 由 `Imgagent.App/Services/PlatformStorage.cs` 统一实现
（用 Avalonia 的 `StorageProvider`，Windows/Android 通用）：
- `SaveToGalleryAsync` → `SaveFilePickerAsync`（默认定位到「图片」）
- `OpenInExternalViewerAsync` → Windows Shell 打开
- `RevealInFileManagerAsync` → `explorer /select`
