# tests — 测试工程

> **全部离线跑，不花钱**（`Offline=true` + 确定性占位图）。

## 两个工程

| 工程 | 项数 | 覆盖 |
|---|---|---|
| `Imgagent.Core.Tests` | **55** | 常量目录、成本估算、错误翻译、文件名安全、图像编解码、存储、润色清洗 |
| `Imgagent.Integration.Tests` | **37** | 端到端：生成闭环、编辑参考图顺序、撤回、持久化、润色浮窗、区域编辑、多图 |

## 跑测试

```powershell
dotnet test tests\Imgagent.Core.Tests
dotnet test tests\Imgagent.Integration.Tests

# 只跑某项
dotnet test tests\Imgagent.Integration.Tests --filter "FullyQualifiedName~RegionEdit"
```

## 测试哲学

1. **离线优先**：不用真实 API，避免花钱。用 `Placeholder.Png`（相同 prompt+step 恒同图）
2. **契约驱动**：测试是"活文档" —— 每个公开属性/命令/行为都被断言锁死
   （实战：App 层被误删后，**凭测试契约完整重建**）
3. **铁律必有测试**：见 `docs/CONSTRAINTS.md` F3 的对照表

## 关键测试文件

| 文件 | 说明 |
|---|---|
| `Core.Tests/CatalogTests.cs` | 质量档过滤、成本估算无 10 倍偏差、模型命名匹配 |
| `Core.Tests/ErrorHintsTests.cs` | **余额先于权限**（403 是余额不是权限 —— 最贵的一条） |
| `Core.Tests/SafeFilenameTests.cs` | 文件名三道防线（防路径穿越） |
| `Core.Tests/ImagingTests.cs` | 占位图确定性、CRC32 与 Python 版一致、嗅探、缩小 |
| `Core.Tests/SessionAndPolishTests.cs` | 配置 snake_case 读写、净化、提示词历史、润色清洗 |
| `Integration.Tests/WorkbenchFlowTests.cs` | 端到端 37 项（含 FakeStorage/FakePlatform） |

## 加测试从哪下手

- 改 Core 逻辑 → 加进 `Core.Tests`
- 改 UI 行为 → 加进 `WorkbenchFlowTests`（用 `MainViewModel` 直接驱动，无需真实窗口）
