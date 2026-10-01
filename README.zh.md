<h1 align="center">
  <img src="src/ImgHub.App/Assets/app-icon.png" width="128" height="128" alt="ImgHub" />
</h1>

<p align="center">
  <a href="README.md">English</a> · <b>简体中文</b> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a>
</p>

<div align="center">

![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Android-blue)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![Avalonia](https://img.shields.io/badge/Avalonia-12.1.2-8B44AC)
![Version](https://img.shields.io/badge/version-0.5.43-blue)
![License](https://img.shields.io/badge/license-GPL--3.0-blue)
[![CI](https://github.com/Iksutuy/ImgHub/actions/workflows/ci.yml/badge.svg)](https://github.com/Iksutuy/ImgHub/actions/workflows/ci.yml)

</div>

<p align="center">
  <b>纯 Vibe Coding 产物。桌面端可用，安卓端未完善。</b>
</p>

# 🖼️ ImgHub

ImgHub 是一个给「懒得手写请求」的人用的 AI 生图工作台。填上任意一家 provider 的 API key，就能在一个窗口里生成、编辑、蒙版重绘、反复迭代——同时有个成本表一直在算，随时知道这一轮花了多少。

它对着的是最日常的那个循环：写提示词、看图、改掉不对的地方、留下满意的那张。这个循环需要的东西都在应用里，不必中途出去改文件名、算花费、或者手搓请求体。

# 🌠 界面截图

<img src="assets/screenshots/main-window.png" alt="ImgHub 主界面" width="100%">

**主界面** —— 左栏是生成参数，中间是预览与标注工具条，右栏是历史与按 provider 分别计算的累计花费。状态栏给出自检结论，消息面板把每一步都写清楚。

<img src="assets/screenshots/mask-edit.png" alt="蒙版局部重绘" width="100%">

**蒙版局部重绘** —— 用方框（或画笔/马克笔）圈出要改的区域，以带 Alpha 的蒙版送出。日志会完整记录这条链路：标注已保存 → 导出含 Alpha 的蒙版 → 上传参考图 → 上传蒙版 → 提交任务。

<img src="assets/screenshots/prompt-guide.png" alt="提示词工程指南" width="100%">

**提示词工程指南** —— 内置的提示词写法参考：一份通用总览，加上按 provider 分开的小节（OpenAI GPT Image / 千问 Qwen-Image），因为两者偏好的写法并不相同。

<img src="assets/screenshots/advanced-params.png" alt="高级参数" width="100%">

**高级参数** —— 按官方文档对齐的参数集：背景、压缩率、流式部分图（SSE）等，并且每个选项都会收窄到当前模型真正接受的范围。

# 🌟 核心功能

1. **生成**

   - 文生图，模型覆盖面广（OpenAI、Google、Seedream、Flux、Grok 等）
   - 批量出图，一次 1–10 张；上限随模型与 provider 收窄，即梦由模型自行决定张数
   - 离线模式：确定性占位图渲染器，不花钱跑通整条链路

2. **参考图与编辑**

   - 单次请求最多 16 张参考图（千问 DashScope 上限 3 张）
   - 待修改图恒排第 1 位——模型是按位置理解参考图的
   - 蒙版局部重绘：画笔、马克笔、方框或椭圆圈出区域，导出带 Alpha 的蒙版，`alpha=0` 即要改的地方

3. **Provider**

   - OpenRouter —— 同步，支持 SSE 流式部分图
   - APIMart —— 异步轮询
   - OpenAI 官方
   - 千问 DashScope —— 同步与异步两条链路
   - 即梦（火山引擎）—— AK/SK 签名
   - 可为千问业务空间专属域名、OpenAI 企业网关另填端点

4. **工作流**

   - 提示词润色：LLM 给 4 条候选，浮窗内挑一条
   - 历史记录，按 provider 与总计分别算钱；动手前先给预估
   - 区域标注画布，支持缩放、平移、撤销/重做，且每张图各自保存标注历史
   - 断点恢复：task_id 会落盘，提交后意外退出也还能把图找回来

5. **界面**

   - 中文、English、日本語，设置里切换，即时生效
   - 深浅两套主题
   - 一套共享 UI，宽屏渲染为三栏、窄屏渲染为堆叠

# 🚀 快速开始

## 前置

Windows 10/11（x64）。只有从源码构建才需要 .NET 10 SDK。

## 下载

到 [Releases](https://github.com/Iksutuy/ImgHub/releases) 取最新构建：

| 平台 | 文件 | 说明 |
|---|---|---|
| Windows 10/11 x64 | `imghub-<版本>-win-x64.zip` | Native AOT，免装 .NET 运行时，解压即用 |
| Android 7.0+（API 23+） | `imghub-<版本>-android.apk` | arm64-v8a + x86_64，尚未完善 |

Windows 包请整体解压。`ImgHub.Desktop.exe` 会从自己所在目录加载 `libSkiaSharp.dll`、`libHarfBuzzSharp.dll` 与 `av_libglesv2.dll`；只把 exe 拷出来单独运行会立刻退出，退出码 `0xC0000409`，且没有任何提示。

每个 release 同时附带 `SHA256SUMS.txt`。

## 配置

打开右下角 **⚙ 设置**，选 Provider，粘贴 API key，保存。

也可以用环境变量：

```powershell
$env:OPENROUTER_API_KEY = "sk-or-v1-..."
$env:IMGHUB_APIMART_API_KEY = "sk-..."
```

## 不花钱先试一次

离线模式用确定性占位图渲染，能跑通存储、历史、撤回、持久化整条链路，完全不碰 API。它藏在 debug 开关后面：启动前设 `IMGHUB_DEBUG=1`，设置浮层里就会出现 **离线** 复选框。勾上，输提示词，点 **生成**。

数据默认放在 `%LOCALAPPDATA%\imghub`，可用 `IMGHUB_HOME` 覆盖。API key 也存这个目录，并且在应用日志里做了脱敏。若改名前的目录（`%LOCALAPPDATA%\imgagent`）已存在，ImgHub 会继续沿用，历史图片和 key 都还在。

# 🧩 支持的 Provider

| Provider | 链路 | 说明 |
|---|---|---|
| OpenRouter | 同步 | SSE 流式部分图 |
| APIMart | 异步 | 通过 `mask_url` 真正支持蒙版 |
| OpenAI 官方 | 同步 | 两个端点；各模型参数不同 |
| 千问 DashScope | 同步 + 异步 | 走哪条链路取决于模型 |
| 即梦（火山引擎） | 异步 | AK/SK 签名 |

请求体按各家文档对齐到参数级——`background`、`output_compression`、`moderation`、精确像素 `size`、`seed`，以及 provider 路由（`only` / `order` / `ignore` / `sort` / `allow_fallbacks`）。各家的接入契约见 [docs/](docs/)。

不具备蒙版能力的 provider 会退回「原图 + 标注合成图」链路，界面上会如实说明，而不是假装蒙版已经发出去了。

# 🌈 界面语言

界面内置**中文、English、日本語**。在设置浮层底部切换语言，立即生效、无需重启，偏好持久化在 `config.json`。

# 🏗️ 架构

```
ImgHub.Core       平台无关业务层（无 UI 依赖）
    ↑
ImgHub.App        共享 UI（XAML + MVVM），两端复用
    ↑
ImgHub.Desktop / ImgHub.Android       薄平台 head
```

`ImgHub.Core` 绝不引用 Avalonia 或 Android。UI 需要的一切都以普通数据与服务的形式表达——这正是同一套 XAML 能同时驱动 Windows 与 Android、且测试无需 UI 脚手架的原因。

# 🛠️ 从源码构建

```powershell
git clone https://github.com/Iksutuy/ImgHub.git
cd ImgHub

# 测试：449 项，全部离线——不联网、不产生 API 费用
dotnet test tests\ImgHub.Core.Tests           # 236
dotnet test tests\ImgHub.Integration.Tests    # 213

# 跑桌面版
dotnet run --project src\ImgHub.Desktop

# 打一个发布包（AOT 桌面 zip + APK + SHA256SUMS.txt）
powershell -File tools\make-release.ps1
```

构建 Android head 还需 `dotnet workload install android`，以及 Android SDK 与 JDK。完整打包说明见 [docs/DELIVERY.md](docs/DELIVERY.md)。

# 📁 项目结构

```
ImgHub/
├── src/
│   ├── ImgHub.Core/                平台无关业务层
│   ├── ImgHub.App/                 共享 UI（XAML + MVVM）
│   ├── ImgHub.Desktop/             Windows / Linux / macOS head
│   └── ImgHub.Android/             Android head
├── tests/
│   ├── ImgHub.Core.Tests/          236 项单元测试
│   └── ImgHub.Integration.Tests/   213 项端到端测试
├── legacy/                         上一代 Python + curses 实现
├── docs/                           文档
├── tools/                          发布打包、图标与字体生成
├── build.ps1                       一键构建（桌面 + APK）
└── ImgHub.slnx                     解决方案文件
```

`legacy/` 是更早的 Python 终端客户端，作为 C# 重写的参考实现与行为契约保留——959 项 Python 测试仍然钉着预期的 API 请求体、成本算法与错误处理。

# 📖 文档

| 文档 | 内容 |
|---|---|
| [docs/HANDOVER.md](docs/HANDOVER.md) | 从这里开始——怎么跑、改哪、怎么排错 |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | 分层结构、数据流、provider 差异、扩展点 |
| [docs/CONSTRAINTS.md](docs/CONSTRAINTS.md) | 硬约束，以及它们背后的坑 |
| [docs/DELIVERY.md](docs/DELIVERY.md) | 打包与发布 |
| [docs/FEATURES.md](docs/FEATURES.md) | 完整功能清单与已知缺口 |
| [docs/i18n.md](docs/i18n.md) | 多语言是怎么实现的 |
| [docs/port-status.md](docs/port-status.md) | 迁移进度与历轮修复记录 |
| [CHANGELOG.md](CHANGELOG.md) | 版本变更历史 |
| [CONTRIBUTING.md](CONTRIBUTING.md) | 贡献指南 |
| [SECURITY.md](SECURITY.md) | 安全模型与私密报告渠道 |
| [NOTICE.md](NOTICE.md) | 第三方许可，含内嵌字体 |

# 📝 路线图

已知缺口，大致按优先级排列：

- **Android 完善** —— APK 能构建能跑，但中文渲染尚未在真机验证，「保存到相册」走的是系统文件选择器，而非写入 MediaStore。
- **平台覆盖** —— 目前只验证过 Windows。Avalonia 本身跨平台，Linux 与 macOS 理论上可用，但未实测。
- **环境诊断** —— Python 版的 `doctor.py` 面板尚未移植。
- **快捷键** —— 消息面板的快捷键提示只是文案，背后没有绑定键位。
- **文件体积** —— `ImageApi.cs` 与 `MainViewModel.cs` 已经偏大，拆分计划作为独立重构推进。

# 🤝 参与贡献

欢迎提 issue 与 PR。[CONTRIBUTING.md](CONTRIBUTING.md) 讲了最要紧的三条约束：分层红线、面对两套布局时 UI 该改哪、以及编译绑定。

有两件事最好先知道：

- 测试全部离线跑，用确定性占位图，跑测试永远不会花钱。
- XAML 改动不在测试覆盖范围内。动了视图，请跑一遍桌面版点一下。

# 📜 许可证

GPL-3.0 —— 见 [LICENSE](LICENSE)。

内嵌的第三方组件与字体列在 [NOTICE.md](NOTICE.md)。项目不附带任何 API 凭据；每个 key 都由用户自行提供并存储在本地。
