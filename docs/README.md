# imgagent（Avalonia 版）— 文档索引

> 跨平台图形化工作台：**Windows 桌面** + **Android**，一套 UI 两端复用。
> 技术栈：Avalonia 12.1.2 / .NET 10 / C#
> 上一代：`mobile/`（Python + curses TUI，作为参考实现与行为契约来源）

---

## 文档分工（先读哪份）

| 文档 | 回答什么问题 | 适合 |
|---|---|---|
| **[HANDOVER.md](HANDOVER.md)** | 怎么跑起来 / 改功能去哪 / 出问题查什么 | 新接手的人（**先读这份**） |
| **[ARCHITECTURE.md](ARCHITECTURE.md)** | 系统怎么搭的 / 数据怎么流 / 双 provider 差异 | 要改架构的人 |
| **[CONSTRAINTS.md](CONSTRAINTS.md)** | **哪些事绝对不能做** / 为什么 | 写代码前必读 |
| **[DELIVERY.md](DELIVERY.md)** | 怎么打包 / 产物在哪 / 各平台注意事项 | 要发布的人 |
| **[port-status.md](port-status.md)** | 迁移进度 / 历次修复记录 / 已知限制 | 查历史与现状 |

**建议顺序**：HANDOVER → ARCHITECTURE → CONSTRAINTS → DELIVERY

---

## 一句话架构

```
Imgagent.Core    平台无关业务层（无 UI 依赖，可单测）
    ↑
Imgagent.App     共享 UI（XAML + MVVM，两端复用）
    ↑
Imgagent.Desktop / Imgagent.Android    各平台 head（只做平台适配）
```

**关键原则**：`Core` **绝不引用 Avalonia/Android** —— 这是架构红线。
（实战验证：App 层曾被误删，因 Core 无损，仅凭测试契约就完整重建。）

---

## 快速入口

```powershell
cd avalonia

# 跑测试（92 项：Core 55 + 集成 37）
dotnet test tests\Imgagent.Core.Tests
dotnet test tests\Imgagent.Integration.Tests

# 跑桌面版
dotnet run --project src\Imgagent.Desktop

# 打包（桌面 Native AOT）
powershell -File build.ps1 -Target desktop-aot

# 打包（Android APK）
powershell -File build.ps1 -Target android
```

---

## 目录速览

| 路径 | 说明 |
|---|---|
| `src/Imgagent.Core/` | 业务逻辑（API/存储/图像/润色），**平台无关** |
| `src/Imgagent.App/` | XAML 视图 + ViewModel + 控件 |
| `src/Imgagent.Desktop/` | Windows/Linux/macOS head + 图标 + 清单 |
| `src/Imgagent.Android/` | Android head + 图标资源 + 清单 |
| `tests/` | Core 单测 + 端到端集成测试（离线跑，不花钱） |
| `docs/` | 本目录的文档 |
| `build.ps1` | 一键构建脚本 |
| `make-icon.ps1` | 由 PNG 生成多尺寸 ICO |

每个目录都有自己的 `README.md` 说明职责 —— 见下节「目录级说明」。

---

## 目录级说明文档

| 目录 | 说明文件 |
|---|---|
| `src/Imgagent.Core/` | [README.md](../src/Imgagent.Core/README.md) |
| `src/Imgagent.App/` | [README.md](../src/Imgagent.App/README.md) |
| `src/Imgagent.Desktop/` | [README.md](../src/Imgagent.Desktop/README.md) |
| `src/Imgagent.Android/` | [README.md](../src/Imgagent.Android/README.md) |
| `tests/` | [README.md](../tests/README.md) |
