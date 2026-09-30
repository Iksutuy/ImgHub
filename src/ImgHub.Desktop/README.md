# ImgHub.Desktop — Windows/Linux/macOS head

> 极薄的平台适配层（约 50 行）。所有界面与逻辑都在 `ImgHub.App`。

## 文件说明

| 文件 | 职责 |
|---|---|
| `Program.cs` | 入口：`BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)` |
| `app.manifest` | Windows 清单：Per-Monitor V2 DPI 感知 + 支持的 OS 版本 |
| `app.ico` | **exe 文件图标**（多尺寸 16/24/32/48/64/128/256，PNG-in-ICO） |
| `ImgHub.Desktop.csproj` | 目标框架 `net10.0` + 图标 + RID 发布配置 |

## 图标

- **exe 图标**：`app.ico`，由 `../../make-icon.ps1` 从 `../ImgHub.App/Assets/app-icon.png` 生成
- **窗口图标**：在 `ImgHub.App/Views/MainWindow.axaml` 用
  `Icon="avares://ImgHub.App/Assets/app-icon.png"`（**必须 PNG**，见约束 D4）

重新生成 ico：
```powershell
# 在仓库根目录执行
powershell -File make-icon.ps1
```

> ⚠️ `make-icon.ps1` 内部写的是**绝对路径**（历史上仓库曾在 `avalonia/` 子目录下），
> 换机器或移动目录后需先改脚本顶部三个路径变量。

## 打包

见 `docs/DELIVERY.md`。三种方式：

| 方式 | 体积 | 命令要点 |
|---|---|---|
| Native AOT | ~31 MB exe + 3 原生 DLL | `-p:PublishAot=true`（需手动 `dotnet publish`） |
| 单文件裁剪 | ~41 MB 单 exe | `-p:PublishSingleFile=true -p:PublishTrimmed=true -p:TrimMode=partial` |
| 框架依赖 | 0.16 MB exe | 目标机需装 .NET 10 Desktop Runtime（`build.ps1 -Target desktop`） |

> ⚠️ AOT 产物**必须整目录分发**（含 `libSkiaSharp.dll`/`av_libglesv2.dll`/`libHarfBuzzSharp.dll`）。

## 平台能力实现

`IPlatformStorage` 由 `ImgHub.App/Services/PlatformStorage.cs` 统一实现
（用 Avalonia 的 `StorageProvider`，Windows/Android 通用）：
- `SaveToGalleryAsync` → `SaveFilePickerAsync`（默认定位到「图片」）
- `OpenInExternalViewerAsync` → Windows Shell 打开
- `RevealInFileManagerAsync` → `explorer /select`
