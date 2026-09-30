<!--
ImgHub v0.5.43 Release notes —— 英中双语（English first, then 中文）。
用法：GitHub → Releases → Draft a new release → 选 tag v0.5.43
→ 把本文件正文（去掉本段注释）粘进说明框 → 上传 dist/ 里的 3 个文件 → Publish。
-->

> **A pure vibe-coding project. The desktop build is usable; the Android build is still rough.**
> **纯 Vibe Coding 产物。桌面端可用，安卓端未完善。**

ImgHub is an image-generation workbench for people who would rather click than curl — bring your own API key, then generate, edit, mask and iterate in one window, with a cost meter running the whole time.
ImgHub 是为「懒得手写请求」的人做的 AI 生图工作台：填上自己的 API key，就能在一个窗口里生成、编辑、蒙版重绘、反复迭代，并且时刻知道花了多少钱。

This is the **first public release**. It collects everything built so far — the project had no tags before, so the whole history lands here at once.
这是**首个公开版本**。此前没有打过 tag，因此到目前为止的所有工作一次性收进这一版。

---

## 📦 Download / 下载

| Platform | File | Size |
|---|---|---|
| Windows 10/11 x64 | `imghub-0.5.43-win-x64.zip` | 26.66 MB |

> The Android build is not attached to this release — the Android head is still being finished. See *Known limitations* below.
> 本次发布**不含安卓包** —— 安卓端仍在完善中，详见下方「已知限制」。

### Checksums / 校验和

| File | MD5 | SHA256 |
|---|---|---|
| `imghub-0.5.43-win-x64.zip` | `fedf499b1a61bcf69f8fdddfe2527a91` | `8a0bd7cf2b01bfd9e708410a6bdfb37f42a948dec7eb3fa52fd102c18da92e39` |

Verify / 校验：

```powershell
Get-FileHash .\imghub-0.5.43-win-x64.zip -Algorithm MD5      # Windows
Get-FileHash .\imghub-0.5.43-win-x64.zip -Algorithm SHA256
```
```bash
md5sum imghub-0.5.43-win-x64.zip                            # Linux / macOS
sha256sum imghub-0.5.43-win-x64.zip
```

> ⚠️ **Extract the Windows package as a whole — do not pull the .exe out on its own.**
> `ImgHub.Desktop.exe` loads `libSkiaSharp.dll`, `libHarfBuzzSharp.dll` and `av_libglesv2.dll` from its own folder. Copying the executable out on its own makes it exit immediately with code `0xC0000409` and no message.
>
> ⚠️ **Windows 包请整体解压，不要单独把 exe 拿出来。**
> `ImgHub.Desktop.exe` 会从自己所在目录加载 `libSkiaSharp.dll`、`libHarfBuzzSharp.dll` 与 `av_libglesv2.dll`；只拷 exe 单独运行会立刻退出（退出码 `0xC0000409`），且没有任何提示。

---

## 🚀 First run / 首次使用

1. Open **⚙ Settings** in the bottom-right corner.
   打开右下角 **⚙ 设置**。
2. Pick a provider and paste your API key.
   选一个 Provider，粘贴你的 API key。
3. Want to try it without spending anything? Set `IMGHUB_DEBUG=1` before launching, then tick **Offline** in Settings — it renders deterministic placeholder images and drives the whole pipeline for free.
   想先不花钱试？启动前设 `IMGHUB_DEBUG=1`，然后在设置里勾上 **离线** —— 它会用确定性占位图跑通整条链路，完全免费。

Five providers are supported: **OpenRouter** / **APIMart** / **OpenAI official** / **Qwen DashScope** / **Jimeng (Volcengine)**. Enterprise gateways and dedicated DashScope domains can be set as custom endpoints.
支持五个 Provider：**OpenRouter** / **APIMart** / **OpenAI 官方** / **千问 DashScope** / **即梦（火山引擎）**；企业网关与 DashScope 专属域名可另填端点。

> ImgHub ships with **no API credentials** and pays for nothing on your behalf. Every key is yours, stored locally.
> 本项目**不附带任何 API 凭据**，也不代付任何费用；每个 key 都由你自己提供，存储在本地。

---

## ✨ What's in this release / 本版包含

### Generation & editing / 生成与编辑

- **Text-to-image** across a broad model catalog (OpenAI, Google, Seedream, Flux, Grok and more).
  **文生图**，模型覆盖面广（OpenAI、Google、Seedream、Flux、Grok 等）。
- **Reference images**, up to 16 per request (Qwen DashScope caps at 3). The image being edited is always pinned to position 1, because models read reference images positionally.
  **参考图**，单次最多 16 张（千问 DashScope 上限 3 张）。待修改图恒排第 1 位 —— 模型是按位置理解参考图的。
- **Mask-based inpainting.** Paint, marker, rectangle or ellipse a region; the app exports an alpha mask where `alpha=0` marks the area to change, and sends it as `mask_url` on APIMart. Providers that cannot take a mask fall back to compositing the original image with the annotation, and the UI says so rather than pretending.
  **蒙版局部重绘**。画笔、马克笔、方框或椭圆圈出区域，导出带 Alpha 的蒙版（`alpha=0` 即要改的地方），在 APIMart 上以 `mask_url` 真正限定改动范围。不具备蒙版能力的 provider 退回「原图 + 标注合成图」，且界面上如实说明，不假装蒙版已发出。
- **Batch generation**, 1–10 per run; the cap narrows per model and provider, and Jimeng decides the count itself.
  **批量出图**，一次 1–10 张；上限随模型与 provider 收窄，即梦则由模型自行决定张数。
- **Prompt polishing** — four LLM candidates in a popup, pick one.
  **提示词润色** —— LLM 给 4 条候选，浮窗内挑一条。

### Workflow / 工作流

- **Cost tracking** — live estimates before you spend, plus per-provider and total accumulation.
  **成本管理** —— 动手前先给预估，另有按 provider 与总计的累计。
- **Region markup canvas** with zoom (anchored at the pointer), pan, undo/redo, and per-image annotation history. Zoom and pan are coordinate-normalised, so marks stay bound to the image.
  **区域标注画布**，支持以鼠标点为锚的缩放、平移、撤销/重做，且每张图各自保存标注历史；坐标归一化存储，标记天然绑定在图片上。
- **Crash recovery** — the task id is persisted, so a generation submitted before an unexpected exit can still be retrieved.
  **断点恢复** —— task_id 会落盘，提交后意外退出也还能把图找回来。
- **Offline mode** — a deterministic placeholder renderer that exercises the full pipeline (storage, history, undo, persistence) without contacting any API.
  **离线模式** —— 确定性占位图渲染器，不碰 API 就能跑通存储、历史、撤回、持久化整条链路。

### Interface / 界面

- **Chinese, English and Japanese**, switchable from Settings with immediate effect — no restart.
  **中文、English、日本語**，设置里切换，即时生效、无需重启。
- **Light and dark themes.**
  **深浅两套主题。**
- **One shared UI** that renders as a wide three-column layout or a narrow stacked layout, driving both Windows and Android.
  **一套共享 UI**，宽屏渲染为三栏、窄屏渲染为堆叠，同时驱动 Windows 与 Android。
- **Icon-consistent buttons** backed by an embedded Material Symbols subset — no dependency on fonts that cannot legally ship or do not exist on Android.
  **按钮图标统一**，基于内嵌的 Material Symbols 子集 —— 不依赖那些不能合法分发、或 Android 上根本不存在的字体。

### Under the hood / 底层

- **Native AOT desktop build** — no .NET runtime required on the target machine, fast startup.
  **桌面 Native AOT 构建** —— 目标机免装 .NET 运行时，启动快。
- **Architecture** — `ImgHub.Core` (platform-agnostic business logic) ← `ImgHub.App` (shared UI, XAML + MVVM) ← thin `ImgHub.Desktop` / `ImgHub.Android` heads. Core never references Avalonia or Android.
  **分层结构** —— `ImgHub.Core`（平台无关业务层）← `ImgHub.App`（共享 UI，XAML + MVVM）← 薄 head `ImgHub.Desktop` / `ImgHub.Android`。Core 绝不引用 Avalonia 或 Android。
- **449 offline tests** (236 unit + 213 end-to-end) run with deterministic placeholder images, so they never cost money and never need the network.
  **449 项离线测试**（236 单元 + 213 端到端），用确定性占位图，不花钱、不联网。

---

## 🐛 Notable fixes behind this release / 本版修复的几桩关键问题

These are worth calling out because they were all **silent failures** — the kind that look fine until you use the feature.
这几条值得单独说，因为它们**都不报错** —— 属于「看着正常、一用就废」的那类。

| Problem / 问题 | Root cause / 根因 |
|---|---|
| Generated images came back unrelated to the reference or edit source<br>参考图 / 编辑历史图出来的图完全不相关 | Four independent defects stacked: the reference images were dropped entirely on plain "Generate"; the edit target was not pinned to position 1; the mask path was actually dead (exported a greyscale image with no alpha, and never sent `mask_url`); six documented parameters were missing<br>四个独立缺陷叠加：普通「生成」时参考图被完全丢弃；编辑目标图没放第 1 位；蒙版链路**实际是死的**（导出无 Alpha 的黑白图，也从未发送 `mask_url`）；缺 6 项文档参数 |
| "Reflection-based serialization has been disabled" on generate<br>生成时报 `Reflection-based serialization has been disabled` | Native AOT disables reflection serialization by default; the HTTP layer serialised a dynamic dictionary reflectively. Fixed with source generation for fixed shapes and `JsonObject` DOM for dynamic ones<br>Native AOT 默认禁用反射序列化，而 HTTP 层用反射序列化动态字典。改为固定结构走 source generation、动态结构走 `JsonObject` DOM |
| 4xx responses were retried 3 times<br>4xx 被误重试 3 次 | No explicit retryability marker; a 401 got retried, wasting traffic and hiding the real cause<br>缺显式可重试标记，401 也被重试，浪费流量且掩盖真因 |
| The exe showed a generic icon<br>exe 显示通用图标 | Two independent causes: PNG blobs embedded in the `.ico` (Explorer will not render them), and a wrong offset baseline in the ICO directory table<br>两个独立根因：`.ico` 内嵌 PNG blob（资源管理器不渲染）、ICO 目录表 offset 基准算错 |
| Language switching left part of the UI unchanged<br>切语言后部分界面不变 | Text assembled in the ViewModel was hard-coded, and switching did not raise change notifications for those properties<br>VM 里拼装的文案硬编码，且切语言时没给这些属性发通知 |
| Tools and scroll wheel stopped working on the canvas<br>标注工具与滚轮全部失效 | A layout change to `HorizontalAlignment="Center"` gave an unsized control a `DesiredSize` of 0 — a 0×0 canvas has no hit area<br>把控件改成 `Center` 对齐后，未设尺寸的 `DesiredSize` 为 0 → 画布变成 0×0，没有任何命中区域 |
| Drawing stayed opaque while dragging<br>拖动绘制时不透明，松手才变半透明 | An offscreen-layer cache moved the in-progress stroke outside the opacity scope<br>离屏层缓存把拖动中那条挪出了 opacity 作用域 |
| Silent `catch { }` blocks everywhere<br>到处是静默 `catch { }` | 29 such blocks hid real failures; audited down to 3 legitimate ones, everything else now logs with a location<br>29 处静默 catch 掩盖了真实故障；审计后降到 3 处合法例外，其余改为带位置的日志 |

Also in this release / 本版另含：

- **Network diagnostics** — nine classes of failure (offline, DNS, refused, reset, unreachable, timeout, TLS, proxy, cancelled), each with a concrete suggested action.
  **网络诊断** —— 无网络 / DNS / 拒绝 / 重置 / 不可达 / 超时 / TLS / 代理 / 取消九类分类，每类给出具体处置建议。
- **Graded logging** with credential masking — keys never reach disk.
  **分级日志**，并对凭据脱敏 —— key 绝不落盘。
- **Crash hardening** for settings save and key loading.
  **崩溃加固** —— 设置保存与 key 读取。

---

## ⚠️ Known limitations / 已知限制

Stated plainly, so nothing is a surprise.
如实写出，免得装完才发现。

- **Android is not finished.** The APK builds and runs, but Chinese text rendering has not been verified on a real device, and "save to gallery" goes through the system file picker rather than writing to MediaStore.
  **安卓端未完善。** APK 能构建能跑，但中文渲染尚未在真机验证；「保存到相册」走的是系统文件选择器，而非写入 MediaStore。
- **Only Windows is verified.** Linux and macOS should work, since Avalonia is cross-platform, but they have not been tested.
  **只验证过 Windows。** Avalonia 本身跨平台，Linux 与 macOS 理论上可用，但未实测。
- **Environment diagnostics panel** from the earlier Python version has not been ported.
  早期 Python 版的**环境诊断面板**尚未移植。
- **Keyboard shortcuts** — the hints in the message panel are text only; no key bindings behind them.
  **快捷键** —— 消息面板的提示只是文案，背后没有绑定键位。
- **Large source files** — `ImageApi.cs` and `MainViewModel.cs` have grown; splitting them is planned as a separate refactor.
  **大文件** —— `ImageApi.cs` 与 `MainViewModel.cs` 已偏大，拆分计划作为独立重构推进。

---

## 📜 License / 许可

**GPL-3.0** — see [LICENSE](https://github.com/Iksutuy/ImgHub/blob/master/LICENSE).

Bundled third-party components and embedded fonts are listed in [NOTICE.md](https://github.com/Iksutuy/ImgHub/blob/master/NOTICE.md) (includes SIL OFL-1.1 font notices).
内嵌的第三方组件与字体列在 [NOTICE.md](https://github.com/Iksutuy/ImgHub/blob/master/NOTICE.md)（含 SIL OFL-1.1 字体声明）。

---

**Full changelog / 完整变更记录**: [CHANGELOG.md](https://github.com/Iksutuy/ImgHub/blob/master/CHANGELOG.md)
**Documentation / 文档索引**: [docs/README.md](https://github.com/Iksutuy/ImgHub/blob/master/docs/README.md)
