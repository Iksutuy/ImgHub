# 安全策略

## 报告安全问题

**请不要开公开 issue。**

请用 GitHub 的私密渠道：
**[Security Advisories → Report a vulnerability](https://github.com/Iksutuy/ImgHub/security/advisories/new)**

请附上：影响版本、复现步骤、实际影响，以及（若有）PoC。

本项目的维护者会先确认收到，再评估修复与公开时间线。

---

## 本项目的安全模型（有助于你判断什么算漏洞）

### 凭据存在哪、怎么保护

| 项 | 说明 |
|---|---|
| API key 存储位置 | **用户本地数据目录**（默认 `%LOCALAPPDATA%\imghub`），**不在仓库里** |
| 文件形式 | `.imghub_*_key`（兼容读旧的 `.imgagent_*_key`） |
| 日志 | `AppLog` 对凭据**脱敏**（形如 `sk-or-v1-abc***`），**key 绝不落盘** |
| 代码库 | **不含任何 API 凭据** —— 所有 key 由用户自行配置 |
| ⚠️ 已知边界 | **Windows 上 key 文件未做主动权限加固**，依赖 `%LOCALAPPDATA%` 的默认 ACL（当前用户 + Administrators）。要加固需引入 `System.Security.AccessControl`，属独立决策。见 [`docs/code-review-v0.5.37.md`](docs/code-review-v0.5.37.md) |

若你发现**凭据被写进日志、被提交进仓库、或随构建产物分发出去**，那是漏洞，请报告。

### 外部输入的处理

用户可控输入包括：提示词、参考图/蒙版文件、provider 返回的 JSON、
以及用户自己填的**自定义端点 URL**（千问业务空间域名、OpenAI 企业网关）。

已做的防护：

- 文件名经 `Session.SafeFilename` 的 `Unsafe` 正则过滤后再用于路径拼接
  （`explorer.exe /select,"{path}"` 这类调用因此不可能被注入）
- JSON 侧只用 source generation（`AppJsonContext`）与 `JsonObject` DOM，不反射反序列化
- 网络错误按 9 类分类后给出具体提示，**不把原始响应体直接展示给用户**

若你发现**路径穿越、命令注入、或 provider 响应导致的任意代码执行/信息泄露**，请报告。

---

## 不在范围内

| 项 | 原因 |
|---|---|
| 需要用户机器已被攻陷才能利用的问题 | 超出本项目威胁模型 |
| 上游 provider 自身的服务端漏洞 | 请直接报给对应厂商 |
| 用户自行把 API key 提交进自己的 fork | 本项目无强制手段，建议用环境变量 |
| 「第三方库有已知 CVE」而未给出可利用路径 | 请附上实际影响分析；依赖会随 SDK 升级 |

---

## 支持范围

本项目为**单人维护的开源项目**，只对**最新发布版本**提供安全修复。
安全修复会随下一个 release 发布，并在 [`CHANGELOG.md`](CHANGELOG.md) 中说明。
