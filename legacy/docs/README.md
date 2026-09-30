# 交接文档包 — 索引

> **imgagent / impydroid v5.17.0**
> 面向接手开发者。按顺序读，约 1.5 小时能完全上手。

---

## 四份文档的分工

| 文档 | 读它来回答 | 篇幅 |
|---|---|---|
| **[HANDOVER.md](HANDOVER.md)** | 「我怎么跑起来 / 改某功能要去哪 / 遇到问题查什么」 | 471 行 |
| **[ARCHITECTURE.md](ARCHITECTURE.md)** | 「整个系统是怎么搭的 / 数据怎么流 / 两个 provider 差在哪」 | 466 行 |
| **[CONSTRAINTS.md](CONSTRAINTS.md)** | 「哪些事绝对不能做 / 为什么」 | 323 行 |
| **[STANDARDS.md](STANDARDS.md)** | 「这个项目沉淀出的通用工程标准」 | 427 行 |

**建议阅读顺序**：HANDOVER（跑起来）→ ARCHITECTURE（看懂）→ CONSTRAINTS（别踩坑）→ STANDARDS（进阶）

---

## 快速入口

```bash
# 环境自检（出问题第一个跑）
python3 main.py --doctor

# 离线跑（不花钱看界面）
IMGAGENT_FORCE_OFFLINE=1 python3 main.py

# 全部校验（改完代码必跑）
bash tools/verify_all.sh
```

---

## 项目速览

| 项 | 值 |
|---|---|
| 版本 | 5.18.3（见 `impydroid/__init__.py` 的 `__version__`） |
| 代码量 | 约 14,700 行（`impydroid/` + `tests/` + `tools/` + `main.py`） |
| 测试 | **959 项**，全离线 |
| 模块 | 20 个（`impydroid/` 19 + `main.py`） |
| 依赖 | **纯标准库** + 5 个可选（全有降级） |
| 平台 | Pydroid 3 / Termux（Android），亦可跑 Windows |
| 最大文件 | `curses_ui.py` 3,805 行 |

> ⬆️ 上表的行数/版本随迭代变化；**测试项数按实际脚本输出为准**
> （`test_impydroid.py` 801 + `test_entrypoints.py` 42 + `test_polish.py` 38
> + `test_presentation.py` 51 + `test_concurrency.py` 27 = 959）。

---

## 十条最关键的规则（先记这些）

1. **改完必须跑** `bash tools/verify_all.sh`（10 步全绿才可交付）
2. **测试默认离线**，真实 API 调用要合并（用户明确要求省钱）
3. **403 ≠ 权限**——APIMart 余额不足用 402/403 两种码，必须读 body
4. **待修改图永远在 `image_urls` 第 1 位**（顺序有语义）
5. **所有 `urlopen` 必须包 `try/except HTTPError`**，否则错误信息全丢
6. **`_box_line` 的 row 是框内相对行号**，`_safe_addstr` 要绝对行号（混用会覆盖文字）
7. **curses 运行中禁止 `print`**（会花屏）
8. **功能键只能是 Ctrl 组合**，普通字母留给中文输入
9. **`settings` 用属性访问**（`from . import settings`），不要 `from .settings import X`
10. **每个 bug 都留一条回归测试 + 一句"当初怎么坏的"注释**

---

## 文档中的诚实边界

交接文档里标注了**未验证/不确定**的部分，接手时不要当成事实：

| 项 | 状态 |
|---|---|
| 人物设定迁移效果 | 未验证（需真人像实验） |
| 参考图权重控制 | API 无此字段，只靠顺序，未做定量实验 |
| 预扣额度机制 | 只有文档依据（"按最高档 max 预留"），未实测 |
| `38650/42383` 单位 | credits 不是 USD，**换算率未知** |
| pyjnius 的 Activity 类名 | 5 个候选里**猜的**（`android.py` 按顺序试） |
| 相册扫描目录 | **猜的**（`photos.py`） |

---

## 文档质量保证

四份文档里的**技术声明都已用代码核实**：

| 核实项 | 结果 |
|---|---|
| 提到的 23 个函数/方法 | ✅ 全部真实存在 |
| 提到的 23 个文件 | ✅ 全部存在（`run.py` 是"不该存在"的反例） |
| 提到的 29 个环境变量 | ✅ 全部在代码里 |
| 提到的 24 个快捷键 | ✅ 全部已注册或为输入框键 |
| 测试项数（956） | ✅ 实测一致 |
| "纯标准库" | ✅ AST 扫描确认（只有 5 个可选依赖，全在 try/except） |
| 无裸 urlopen | ✅ AST 静态校验 |
| 无 print 在 `_act_*` | ✅ AST 静态校验 |
| 余额判断前置于权限 | ✅ 源码位置校验 |
| 待修改图排第 1 位 | ✅ 源码位置校验 |

---

## 相关文件

| 路径 | 说明 |
|---|---|
| `../README.md` | 用户手册（面向使用者） |
| `../tools/verify_all.sh` | 全量校验脚本（打包时被排除，仅源码仓库有） |
| `../tests/` | 测试（956 项，打包时被排除，仅源码仓库有） |
| 发布产物 | 见项目根目录的 `imgagent-<版本>-<平台>.zip` |
