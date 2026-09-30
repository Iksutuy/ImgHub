# ImgHub vX.Y.Z

<!--
用法：GitHub → Releases → Draft a new release → 选 tag（如 v0.5.43）
→ 把本文件内容粘进说明框 → 删除本段注释 → 按需删掉不适用的小节。
要点：把「下载哪个」写清楚（AOT 是整目录 ZIP，不是裸 exe），
并如实写出已知限制 —— 这类项目最劝退新用户的就是"下下来跑不起来"。
-->

跨平台 AI 生图客户端：**Windows 桌面 + Android**，一套 UI 两端复用。

## 下载

| 平台 | 文件 | 说明 |
|---|---|---|
| **Windows 10/11 x64** | `imghub-X.Y.Z-win-x64.zip` | Native AOT，**免装 .NET 运行时**，解压即用 |
| **Android 7.0+ (API 23+)** | `imghub-X.Y.Z-android.apk` | arm64-v8a + x86_64 |

校验：`SHA256SUMS.txt`（`certutil -hashfile <文件> SHA256` 或
`Get-FileHash <文件> -Algorithm SHA256`）。

> ⚠️ **Windows 用户请注意**：AOT 版**必须整个解压后再运行**，
> 不要把 `ImgHub.Desktop.exe` 单独拷出来 —— 它需要同目录的
> `libSkiaSharp.dll` / `libHarfBuzzSharp.dll` / `av_libglesv2.dll`，
> 缺任一个会**秒崩**（`0xC0000409`，且不会有任何提示）。

## 首次使用

1. 启动后打开右下角 **⚙ 设置**；
2. 选 Provider（OpenRouter / APIMart / OpenAI 官方 / 千问 DashScope / 即梦）并填入你自己的 API key；
3. 想先不花钱试试：左栏勾 **离线** → 输提示词 → 点生成（用确定性占位图跑通全流程）。

> 本项目**不附带任何 API 凭据**，也不代付费用。

## 本版变更

<!-- 从 CHANGELOG.md 复制对应版本一节；建议保留「修复了什么根因」，而不只是「修了 bug」。 -->

### Added

### Fixed

### Changed

## 已知限制

<!-- 如实写出，别只写好消息。 -->

- Android 中文渲染已内嵌字体兜底，**桌面实测正常，Android 待真机验证**
- Android「保存到相册」走系统选择器（用户选位置），非静默写入相册
- 环境诊断面板（原版 `doctor.py`）未移植
- 消息面板的快捷键提示**仅为文案**，未绑定实际按键
- 平台：**Windows 与 Android 已实测**；Linux / macOS 理论可用（Avalonia 跨平台）但**未实测**

## 从源码构建

```powershell
git clone https://github.com/Iksutuy/ImgHub.git
cd ImgHub
dotnet test tests\ImgHub.Core.Tests           # 236 项，离线
dotnet test tests\ImgHub.Integration.Tests    # 213 项，离线
dotnet run --project src\ImgHub.Desktop
```

打包见 [`docs/DELIVERY.md`](../blob/master/docs/DELIVERY.md)。

---

**完整变更记录**：[`CHANGELOG.md`](../blob/master/CHANGELOG.md)
**文档索引**：[`docs/README.md`](../blob/master/docs/README.md)
