<h1 align="center">
  <img src="src/ImgHub.App/Assets/app-icon.png" width="128" height="128" alt="ImgHub" />
</h1>

<p align="center">
  <b>English</b> · <a href="README.zh.md">简体中文</a> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a>
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
  <b>A pure vibe-coding project. The desktop build is usable; the Android build is still rough.</b>
</p>

# 🖼️ ImgHub

ImgHub is an image-generation workbench for people who would rather click than curl. Bring your own API key for any of five providers, then generate, edit, mask and iterate on images from one window — with a cost meter running the whole time so you always know what a session has spent.

It is aimed at the everyday loop: write a prompt, look at the result, fix the part that is wrong, keep the one you like. Everything that loop needs is in the app, so you never have to leave it to rename a file, count your spending, or hand-assemble a request body.

# 🌠 Screenshot

<!-- TODO: drop screenshots into assets/screenshots/ and reference them here, e.g.
![](assets/screenshots/main-window.png)
-->

# 🌟 Key Features

1. **Generation**

   - Text-to-image across a broad model catalog (OpenAI, Google, Seedream, Flux, Grok and more)
   - Batch generation, 1–10 images per run; the cap narrows per model and provider, and Jimeng decides the count itself
   - Offline mode: a deterministic placeholder renderer that drives the whole pipeline without spending anything
2. **Reference Images and Editing**

   - Up to 16 reference images per request (Qwen DashScope caps at 3)
   - The image being edited is always pinned to position 1, because models read reference images positionally
   - Mask-based inpainting: paint, marker, rectangle or ellipse a region, and the app exports an alpha mask where `alpha=0` marks the area to change

3. **Providers**

   - OpenRouter — synchronous, with SSE partial-image streaming
   - APIMart — asynchronous polling
   - OpenAI official
   - Qwen DashScope — both the synchronous and asynchronous routes
   - Jimeng (Volcengine) — AK/SK request signing
   - Custom endpoints for enterprise gateways and dedicated DashScope domains

4. **Workflow**

   - Prompt polishing: four LLM candidates in a popup, pick one
   - History with per-provider and total cost tracking, plus live estimates before you spend
   - A region markup canvas with zoom, pan, undo/redo and per-image annotation history
   - Crash recovery: the task id is persisted, so a generation submitted before an unexpected exit can still be retrieved

5. **Interface**

   - Chinese, English and Japanese, switchable from Settings with immediate effect
   - Light and dark themes
   - One shared UI that renders as a wide three-column layout or a narrow stacked layout

# 🚀 Getting Started

## Prerequisites

Windows 10/11 (x64). The .NET 10 SDK is only needed if you build from source.

## Download

Grab the latest build from [Releases](https://github.com/Iksutuy/ImgHub/releases):

| Platform | File | Notes |
|---|---|---|
| Windows 10/11 x64 | `imghub-<version>-win-x64.zip` | Native AOT — no .NET runtime required. Unzip and run. |
| Android 7.0+ (API 23+) | `imghub-<version>-android.apk` | arm64-v8a + x86_64. Still rough. |

Extract the Windows package as a whole. `ImgHub.Desktop.exe` loads `libSkiaSharp.dll`, `libHarfBuzzSharp.dll` and `av_libglesv2.dll` from its own folder — copying the executable out on its own makes it exit immediately with code `0xC0000409` and no message.

Each release also ships `SHA256SUMS.txt`.

## Configure

Open **⚙ Settings** in the bottom-right corner, pick a provider, paste your API key and save.

You can use environment variables instead:

```powershell
$env:OPENROUTER_API_KEY = "sk-or-v1-..."
$env:IMGHUB_APIMART_API_KEY = "sk-..."
```

## Try it without spending anything

Offline mode renders deterministic placeholder images and drives the full pipeline — storage, history, undo, persistence — without contacting any API. It is hidden behind a debug flag: set `IMGHUB_DEBUG=1` before launching, and an **Offline** checkbox appears in the Settings overlay. Tick it, type a prompt and hit **Generate**.

Your data lives in `%LOCALAPPDATA%\imghub` by default; override it with `IMGHUB_HOME`. API keys are stored in that folder as well, and are masked in the application log. If a folder from the pre-rename layout (`%LOCALAPPDATA%\imgagent`) already exists, ImgHub keeps using it so existing images and keys stay reachable.

# 🧩 Supported Providers

| Provider | Transport | Notes |
|---|---|---|
| OpenRouter | Synchronous | SSE partial-image streaming |
| APIMart | Asynchronous | True mask support via `mask_url` |
| OpenAI official | Synchronous | Two endpoints; separate parameters per model |
| Qwen DashScope | Sync + async | Route depends on the model |
| Jimeng (Volcengine) | Async | Signed with AK/SK |

Requests are aligned with each provider's documentation down to the parameter level — `background`, `output_compression`, `moderation`, exact-pixel `size`, `seed`, and provider routing (`only` / `order` / `ignore` / `sort` / `allow_fallbacks`). Per-provider contracts live in [docs/](docs/).

Providers that cannot take a mask fall back to compositing the original image with the annotated overlay, and the UI says so rather than pretending the mask was sent.

# 🌈 Interface Languages

The UI ships in **Chinese, English and Japanese**. Switch language at the bottom of the Settings overlay; it applies immediately, with no restart, and the preference is persisted in `config.json`.

# 🏗️ Architecture

```
ImgHub.Core       platform-agnostic business logic (no UI dependency)
    ↑
ImgHub.App        shared UI (XAML + MVVM), reused by both heads
    ↑
ImgHub.Desktop / ImgHub.Android       thin platform heads
```

`ImgHub.Core` never references Avalonia or Android. Everything the UI needs is expressed as plain data and services, which is what allows a single XAML tree to drive both Windows and Android and keeps the test suite free of UI plumbing.

# 🛠️ Build from Source

```powershell
git clone https://github.com/Iksutuy/ImgHub.git
cd ImgHub

# Tests: 449 cases, all offline — no network, no API cost
dotnet test tests\ImgHub.Core.Tests           # 236
dotnet test tests\ImgHub.Integration.Tests    # 213

# Run the desktop app
dotnet run --project src\ImgHub.Desktop

# Package a release (AOT desktop zip + APK + SHA256SUMS.txt)
powershell -File tools\make-release.ps1
```

Building the Android head additionally requires `dotnet workload install android` plus an Android SDK and a JDK. [docs/DELIVERY.md](docs/DELIVERY.md) has the full packaging guide.

# 📁 Project Structure

```
ImgHub/
├── src/
│   ├── ImgHub.Core/                platform-agnostic business layer
│   ├── ImgHub.App/                 shared UI (XAML + MVVM)
│   ├── ImgHub.Desktop/             Windows / Linux / macOS head
│   └── ImgHub.Android/             Android head
├── tests/
│   ├── ImgHub.Core.Tests/          236 unit tests
│   └── ImgHub.Integration.Tests/   213 end-to-end tests
├── legacy/                         previous Python + curses implementation
├── docs/                           documentation
├── tools/                          release packaging, icon and font generation
├── build.ps1                       one-shot build (desktop + APK)
└── ImgHub.slnx                     solution file
```

`legacy/` is the earlier terminal client written in Python. It is kept as a reference implementation and behaviour contract for the C# rewrite — 959 Python tests still pin the expected API payloads, cost arithmetic and error handling.

# 📖 Documentation

| Document | Contents |
|---|---|
| [docs/HANDOVER.md](docs/HANDOVER.md) | Start here — how to run it, where to change what, how to debug |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Layer structure, data flow, provider differences, extension points |
| [docs/CONSTRAINTS.md](docs/CONSTRAINTS.md) | Hard constraints and the traps behind them |
| [docs/DELIVERY.md](docs/DELIVERY.md) | Packaging and release |
| [docs/FEATURES.md](docs/FEATURES.md) | Full feature inventory and known gaps |
| [docs/i18n.md](docs/i18n.md) | How localisation is implemented |
| [docs/port-status.md](docs/port-status.md) | Migration progress and per-round fix records |
| [CHANGELOG.md](CHANGELOG.md) | Version history |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Contribution guide |
| [SECURITY.md](SECURITY.md) | Security model and private reporting |
| [NOTICE.md](NOTICE.md) | Third-party licences, including embedded fonts |

# 📝 Roadmap

Known gaps, roughly in priority order:

- **Android polish** — the APK builds and runs, but Chinese text rendering has not been verified on a real device, and "save to gallery" goes through the system file picker rather than writing to MediaStore.
- **Platform coverage** — only Windows is verified. Linux and macOS should work, since Avalonia is cross-platform, but they have not been tested.
- **Environment diagnostics** — the `doctor.py` panel from the Python version has not been ported.
- **Keyboard shortcuts** — the shortcut hints in the message panel are text only, with no key bindings behind them.
- **File size** — `ImageApi.cs` and `MainViewModel.cs` have grown large; splitting them is planned as a separate refactor.

# 🤝 Contributing

Issues and pull requests are welcome. [CONTRIBUTING.md](CONTRIBUTING.md) covers the three constraints that matter most: the layering rule, where to edit UI given the two layouts, and compiled bindings.

Two things worth knowing up front:

- Tests run entirely offline against deterministic placeholder images, so running them never costs money.
- XAML changes are not covered by tests. If you touch a view, run the desktop app and click through it.

# 📜 License

GPL-3.0 — see [LICENSE](LICENSE).

Bundled third-party components and embedded fonts are listed in [NOTICE.md](NOTICE.md). No API credentials ship with the project; every key is supplied by the user and stored locally.
