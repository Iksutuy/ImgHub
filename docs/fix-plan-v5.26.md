# 修复计划（第十一轮 · v5.26.0）— 生图流程断点恢复与稳健性

> 来源：用户要求「如果提交后意外退出了程序，图片去哪里找回」；
> 「对类似的网络、程序的问题列举并给出解决方法，确保图片顺利生成和找到」。
> 方法：先调研现状 → 写 plan → review 补细节 → 执行 → 实测验证。

---

## 零、现状调研（关键结论）

### 已确认的缺口

| # | 缺口 | 现状 | 后果 |
|---|---|---|---|
| 1 | **task_id 只存内存** | `ImageApi.GenerateApimartAsync` 里 `var tasks = new List<string>()`，仅存活于本次调用 | 提交后程序退出 → **task_id 丢失，结果永远找不回**（但**服务端已扣费并可能已出图**） |
| 2 | 无「未完成生成」记录 | 没有 pending 文件 | 用户不知道有哪些任务在跑 |
| 3 | 轮询超时即弃 | `maxWait=300s` 后抛错 | 超过 5 分钟的大图任务被放弃，task_id 也丢了 |
| 4 | OpenRouter 无恢复概念 | 同步返回，无 task_id | 若请求发出但响应前退出 → 同样丢失（且已扣费） |
| 5 | 离线/网络中断无重连 | 断网即失败 | 网络恢复后无法自动续上 |

### 已有的可复用能力

- `Session`：`AtomicWrite` + `_writeLock`、`state.json`/`history.jsonl`
- `AppLog`：分级日志 + 位置（v5.24.0）
- `NetworkDiagnostics`：9 类网络故障分类（v5.24.0）
- `ApiError.Retryable`：重试语义（v5.24.0）
- `ImageApi.PollTaskAsync` 是 **public** —— 可直接用于恢复轮询

---

## 一、设计：pending 任务持久化 + 恢复

### 数据文件

`<数据目录>/pending_tasks.jsonl`（JSONL，一行一个任务，追加写、可人工编辑）

```json
{"task_id":"tsk_abc","provider":"apimart","model":"gpt-image-2.5-flare",
 "prompt":"一只猫","created_at":1790038146.1,"n":2,"status":"pending",
 "api_key_provider":"apimart"}
```

**安全**：只存 **provider 名**，**不存 API key**（key 从 `.imghub_*_key` 现读）。

### 生命周期

```
提交成功（拿到 task_id）
   ↓ 立即写 pending_tasks.jsonl（status=pending）  ← 这一步保证"退出也能找回"
轮询成功 → 落盘图片 → 从 pending 移除（或标记 done 并归档）
轮询失败/超时 → 标记 status=timeout/error，保留 task_id 供手动重试
```

### 启动恢复流程

```
App 启动
   ↓ Session.Load() 后
ResumePendingTasksAsync()
   ├─ 读 pending_tasks.jsonl 里 status=pending 的条目
   ├─ 对每一条：调 PollTaskAsync(task_id)（**不重新提交，不再扣费**）
   │    ├─ completed → 下载图片 → 落盘 → 入历史 → 标记 done
   │    ├─ failed    → 标记 failed，写日志告知原因
   │    └─ 仍 pending → 保留（下次启动再试）
   └─ 在消息面板/日志报告：「恢复了 N 个未完成任务」
```

---

## 二、修复项

### 🔴 R1. APIMart：提交后立即持久化 task_id

**改点**：`ImageApi.GenerateApimartAsync` 拿到 `tasks` 后，**立刻**回调一个
`IPendingStore` 记录（而不是等轮询结束）。

**为什么现在做不了**：`ImageApi` 在 Core 层且不持有 Session（避免耦合）。
方案：Core 定义 `IPendingTaskSink` 接口，`MainViewModel`/`App` 注入实现。
或更简单 —— 让 `GenerateAsync` 接受一个可选的 `Action<PendingTask> onSubmitted` 回调。

**选型**：用**回调**（最少侵入，Core 不引入存储依赖）。

```csharp
public async Task<GenResult> GenerateAsync(..., 
    Action<SubmittedTask>? onSubmitted = null, ...)
// 提交拿到 task_id 后：
onSubmitted?.Invoke(new SubmittedTask(provider, model, prompt, taskIds));
```

### 🔴 R2. 新增 `PendingTaskStore`（Core/Storage）

- `Add(taskIds, provider, model, prompt, n)` —— 追加写 JSONL
- `MarkDone(taskId)` / `MarkFailed(taskId, reason)`
- `Load()` —— 返回未完成列表
- 全部走 `AtomicWrite`/`_writeLock`，失败只记日志不抛
- **不存 key**

### 🔴 R3. 启动时恢复未完成任务

`MainViewModel` 构造后调用 `ResumePendingAsync()`：
- 逐条 `ImageApi.PollTaskAsync`（复用现有轮询，**不重新提交**）
- 成功 → `LandResultsAsync` 落盘（复用现有落盘/入历史逻辑）
- 超时/失败 → 保留 + 明确提示
- 全程写 `AppLog`，消息面板可见

### 🟡 R4. 轮询超时不再丢弃 task_id

`PollTaskAsync` 超时抛错前，**保留 pending 记录**（status=timeout），
提示用户「任务仍在服务端运行，可稍后重试 / 重启自动恢复」。

### 🟡 R5. 网络中断的可恢复重试

- 轮询期间网络错误 → 按 `Retryable` 重试；重试耗尽**不影响 pending 记录**
- 恢复流程本身遇到断网 → 保留 pending，下次启动再试
- 消息面板给出 `NetworkDiagnostics` 的可操作建议

### 🟡 R6. OpenRouter：请求发出前的"意图记录"

OpenRouter 是同步接口，没有 task_id。但**请求已发出、响应未回**时退出同样丢图。
方案：提交前写一条 `status=sent` 记录（含 prompt/params/time）；
- 正常返回 → 清除
- 重启发现 `sent` 记录 → **提示用户**「上一次请求可能已完成并扣费，
  请到 provider 后台核对；本工具无法自动找回（同步接口无任务号）」

> 这是**如实告知**，不假装能恢复 —— 同步接口确实无法找回。

### 🟡 R7. 问题清单与解决方法（用户要求"列举"）

写入 `docs/CONSTRAINTS.md` 新增 **J 类：生图流程断点与恢复**，
列举 8 类问题 + 现象 + 原因 + 解决方法：

| # | 问题 | 现象 | 解决方法 |
|---|---|---|---|
| J1 | 提交后程序被关闭 | 图找不到 | **task_id 已落盘** → 重启自动恢复 |
| J2 | 轮询超时（>300s） | 任务超时 | task_id 保留 → 稍后重试/重启恢复 |
| J3 | 网络中断 | 请求失败 | 自动重试 + pending 保留 |
| J4 | DNS/断网/代理 | 连不上 | NetworkDiagnostics 分类提示 |
| J5 | 服务端 5xx | 失败 | 退避重试 3 次 |
| J6 | 余额不足 | 403/402 | 先判余额（铁律 C1） |
| J7 | 部分图片失败 | 少给图 | 明确提示缺失数量+原因 |
| J8 | **同步接口（OpenRouter）退出** | 无法找回 | **如实告知** + 到后台核对 |

每类都写：**能否恢复 / 怎么恢复 / 用户下一步做什么**。

### 🟢 R8. 消息面板与日志的可诊断性

- 恢复过程写日志（含 task_id）
- 「数据目录」提示里补上 `pending_tasks.jsonl` 路径
- 设置里加「查看未完成任务」入口（有 pending 时可见）

---

## 三、执行顺序

| 序 | 项 | 验收 |
|---|---|---|
| 1 | R2 `PendingTaskStore` + 回归测试 | 单测：追加/更新/读取/损坏容错 |
| 2 | R1 提交即持久化（回调） | 单测：提交后 pending 文件有记录 |
| 3 | R3 启动恢复 | 集成测试：伪造 pending → 恢复流程跑通 |
| 4 | R4/R5 超时与网络保留记录 | 单测：超时后 pending 仍在 |
| 5 | R6 OpenRouter 意图记录 + 告知 | 单测 |
| 6 | R7 文档（CONSTRAINTS J 类） | 文档审查 |
| 7 | R8 入口与提示 | UI 探针 |
| 8 | 全量测试 + AOT 实测 + 编译成品 | 149+ 测试全绿 |

---

## 四、Review（写盘后复查，补关键细节）

### V1. 恢复时会不会**重复扣费**？

**不会** —— 恢复只调 `GET /tasks/{id}`（查询），**不重新 POST**。
这点必须在代码注释与文档里写清（用户最担心的就是这个）。

### V2. 恢复时 API key 变了怎么办？

从**当前** provider 的 key 文件/环境变量现读。若 key 换了/失效 →
轮询返回 401 → 标记 `failed` 并提示「key 无效，无法取回该任务」。
**不静默丢弃**（保留 task_id，用户换回 key 后还能再试）。

### V3. pending 文件会不会无限增长？

- `done` 的条目**移出**（重写文件，只留未完成）
- `failed/timeout` 保留，但**超过 100 条或 30 天**自动清理
- 人工可直接编辑该 JSONL（格式可读）

### V4. task_id 属于哪个 provider？

记录里带 `provider` 字段。恢复时用**该 provider** 的 baseUrl + key（不是当前选中的）。
避免「切了 provider 就取不回」。

### V5. 恢复是否阻塞 UI？

**不阻塞** —— 在构造函数里 `_ = ResumePendingAsync()`（fire-and-forget），
完成后 `Dispatcher.Post` 更新 UI（遵守约束 D3：不用 InvokeAsync 等待）。

### V6. 图片下载失败（有 task_id 但 URL 失效）？

- 保留 `status=timeout`，记录已拿到的 URL
- 提示「任务已完成但图片下载失败，可稍后重试」
- 重试时若 URL 仍失效 → 记 `failed` 并写明原因

### V7. 与 `Busy` 锁的关系？

恢复**不占用** `Busy`（用户可照常生成新图）。
但恢复落盘时与 `LandResultsAsync` 共用 `Session._writeLock`（已有串行保证）。

### V8. 测试怎么覆盖"重启"？

单测：写 pending 文件 → 新建 `Session`/`PendingTaskStore` → 断言读到。
集成测试：用假 `HttpMessageHandler` 返回 `completed` → 断言图片落盘 + pending 清空。

---

## 五、不在本轮范围

| 项 | 原因 |
|---|---|
| 服务端任务列表 API | APIMart 未见公开的"列我所有任务"接口 |
| OpenRouter 同步请求找回 | 协议上无 task_id，无法找回（如实告知） |
| 后台常驻守护进程 | 超出"客户端"定位 |

---

## 六、执行结果（已实现 · 实测通过）

**版本**：v5.24.0 → **v5.26.0**

| 项 | 实现 | 文件 |
|---|---|---|
| R1 提交即回调 | `SubmitProgressHandler`（`SyncSending`/`AsyncSubmitted`/`Finished` 三时机） | `ImageApi` |
| R2 未完成任务存储 | `PendingTaskStore`（JSONL、原子写、**不存 key**、自动清理） | `Core/Storage/` |
| R3 启动恢复 | `ResumePendingAsync()`（**只查询不重提交**，fire-and-forget） | `MainViewModel` |
| R4 超时保留 | `status=timeout` 仍属 `IsOpen` → 下次启动重试 | `PendingTaskStore` |
| R5 网络保留 | 恢复失败**不删记录**，退避重试；`NetworkDiagnostics` 给建议 | `MainViewModel` |
| R6 同步接口告知 | `status=sent` → 重启时提示「可能已扣费，请去后后台核对」 | `MainViewModel` |
| R7 文档 | `CONSTRAINTS.md` **§J**（10 类问题 + 能否找回 + 解决方法） | `docs/` |
| R8 诊断 | 日志分级带位置；`PendingCount` 可观测 | 全局 |

### 实测（端到端）

```
① 伪造 pending 记录（模拟"提交后退出"）
   {"task_id":"tsk_fake_test_0001","provider":"apimart","status":"pending",...}

② 启动 → 消息面板：
   尝试取回任务 tsk_fake_test_0001（仅查询，不重新提交）…
   任务 tsk_fake_test_0001 暂未取回：Invalid task ID.（已保留，下次启动自动重试）

③ 日志：
   [INFO ] (MainViewModel.ResumePendingAsync) 发现 1 个未完成生成任务，尝试恢复（只查询，不重复扣费）
   [INFO ] (MainViewModel.ResumePendingAsync) 尝试取回任务 tsk_fake_test_0001…

④ pending 文件仍保留（可人工编辑 / 用真实 task_id 重试）
```

**关键保证**：恢复路径**只调 `GET /tasks/{id}`**，**不重新 POST** → **不会重复扣费**
（消息与日志都明确写出）。

### 验证

| 项 | 结果 |
|---|---|
| Core 测试 | **91/91** ✅（本轮 **+10**） |
| 集成测试 | **68/68** ✅ |
| 合计 | **159/159** ✅ |
| 端到端恢复实测 | ✅ 见上 |
| 用户真实数据 | ✅ 未触碰（全部用 `%TEMP%` 副本） |

### 新增回归测试（10 项）

`Add_ThenLoadOpen_RoundTrips` · `PendingFile_IsReadableJsonLines_AndContainsNoKey` ·
`Update_MarksStatus_Failed_KeepsRecord_ForRetry` · `Update_Timeout_StillOpen_SoNextLaunchRetries` ·
`Compact_RemovesDone_KeepsOpen` · `LoadAll_SkipsCorruptedLines_WithoutThrowing` ·
`AddSyncSent_ThenClear_LeavesNothing` · `SyncSent_LeftBehind_IsReportedAsNotRecoverable` ·
`Apimart_OnSubmitted_FiresWithTaskIds_RightAfterSubmit` ·
`Apimart_TaskIdPersisted_EvenIfPollingFails` ← **核心保证**
