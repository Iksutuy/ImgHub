# Imgagent.Core — 平台无关业务层

> **架构红线：本工程绝不引用 Avalonia / Android / 任何 UI 框架。**
> 只依赖 `SkiaSharp`（跨平台图像库，非 UI 框架）。

## 职责

所有"业务逻辑"都在这里，UI 只负责展示和转发。

## 文件说明

| 文件 | 职责 |
|---|---|
| `Catalog.cs` | **常量唯一真源**：模型清单、质量档、画幅、分辨率、成本估算表、限额 |
| `Models/Item.cs` | 一张图（生成/编辑/导入），字段对齐 `state.json` |
| `Models/GenResult.cs` | 生成结果：`Images: [(bytes, media)]` + Cost + Tokens |
| `Models/AppConfig.cs` | 配置（`[JsonPropertyName]` 映射 snake_case，与 Python 版互通） |
| `Models/ApiProvider.cs` | provider 枚举 + Key/Label/Parse |
| `Http/HttpJsonClient.cs` | HTTP 封装：JSON 请求 + 429/5xx 重试 + 可读错误 |
| `Http/ApiError.cs` | 带状态码的异常 + 错误体解析 |
| `Http/ErrorHints.cs` | **错误翻译**（余额先于权限 —— 铁律 C1） |
| `Services/ImageApi.cs` | **双 provider 生图**：OpenRouter（同步）/ APIMart（异步轮询） |
| `Services/PolishService.cs` | 提示词润色（4 候选批量模式 + 分隔符容错） |
| `Storage/Session.cs` | 配置/历史/提示词历史/key 存储（原子写 + 串行锁） |
| `Imaging/ImageCodec.cs` | 解码/嗅探 media_type/参考图缩小/格式转码 |
| `Imaging/Placeholder.cs` | 离线确定性占位图（CRC32 与 Python 版一致） |

## 为什么不放 UI 类型

缩略图（`Bitmap`）是 UI 关注点，放在 App 层的 `HistoryRow`/`PreviewThumb` 里。
**实战代价**：首版误把 `Thumb` 加到 `Item` 上 → 编译失败（Core 无 Avalonia 引用）。

## 关键不变量

- `Catalog` 是模型/成本/限额的**唯一真源**，别处不得硬编码
- `Provider` 切换必须同时纠正 `Model` 与 `Quality`（否则必 400）
- `Sanitize()` 在 `Load()` 必须跑（防 `null` 配置导致启动闪退）
- 写盘全部走 `_writeLock`（防 UI 线程与 Timer 并发写）

## 测试

`tests/Imgagent.Core.Tests`（55 项）覆盖：常量、成本估算、错误翻译、
文件名安全、图像编解码、存储与润色清洗。
