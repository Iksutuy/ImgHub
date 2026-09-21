# imgagent / impydroid — 约束文档

> 每一条都是**踩过的坑**换来的。违反它们 = 复现已修过的 bug。
> 格式：编号 / 约束 / 来源（真实故障） / 后果

---

## A. 平台约束（Android 环境决定）

### A1. 只用标准库，禁止新增**强制**第三方依赖

- **来源**：Pydroid 里 `pip install` 常失败（编译依赖/网络/没装 repository plugin）
- **后果**：用户装不上 → 整个程序跑不起来
- **做法**：需要图片处理就自己写（`pngcodec.py` 是纯标准库 PNG 编解码）
- **实测**：全部 import 里，**只有 3 个非标准库**，且全在 `try/except` 内可选：

| 模块 | 位置 | 缺失时的行为 |
|---|---|---|
| `certifi` | `httpclient.py` | 退回系统 CA / 不校验 |
| `jnius` | `android.py` | 降级为"扫描目录 + 输路径" |
| `PIL` | `preview.py` | 降级到终端字符画 |
| `tkinter` | `preview.py` | 降级到下一级预览 |
| `android`* | `android.py:131` | p4a 风格回调注册，失败即跳过 |

\* `android` 不是 pip 包，是 **Pydroid/p4a 注入的模块**，同样在 `try/except` 内。

**新增可选依赖的判据**：必须有 `try/except` + 明确的降级路径，
且降级后功能仍可用（只是体验差些）。

### A2. `console.py` 必须最先导入

- **来源**：输出里用了 `═ ─ ✓ ✗ ⚠` 等字符。某些 ROM/编码下第一次 `print` 就抛
  `UnicodeEncodeError`，而异常信息本身也打不出来 → **黑屏零输出**
- **做法**：
  1. `stdout/stderr` 的 `errors` 改成 `"replace"`
  2. 提供本模块的 `print`，永不抛异常 + 强制 flush
- **后果**：顺序错了 = 用户什么都看不到

### A3. `os.link` 在 Pydroid 里不存在

- **来源**：保存图片用了硬链接
- **做法**：`store` 里有 `_ensure_in_home()`，硬链接失败要**回退到复制**
- **后果**：保存失败还会写坏记录

### A4. Termux 与 Pydroid 的相册路径**顺序不同**

- **来源**：路径顺序错了会导致图存到"用户看不见的地方"
- **做法**：`settings` 里有平台分支，顺序不能随便调

### A5. `doctor.py` 必须只用 ASCII

- **来源**：这个模块的存在就是为查出"终端能不能正常打印"
- **后果**：它自己用了装饰字符就会死于同一个问题，什么都查不出来

### A6. curses 的输入必须用 `get_wch` 而不是 `getch`

- **来源**：`getch` 会把中文按字节拆开
- **后果**：中文输入乱码

### A7. 绝不能用 `print` 在 curses 运行中输出

- **来源**：`print` 直接打到终端，把界面冲成乱码（真机截图里的花屏）
- **做法**：
  - 后台任务期间 `stdout` 必须被重定向（`test_tui_bg_redirects_stdout`）
  - **所有** `_act_*` 动作函数内不得有 `print`（`test_tui_no_print_in_any_action`）
  - 只有"设置 API key"允许挂起 curses，其它都必须留在 TUI 内

---

## B. 架构约束

### B1. `settings.py` 是**唯一真源**，必须用属性访问

```python
# ✅ 正确
from . import settings
settings.HOME

# ❌ 禁止
from .settings import HOME
```

- **来源**：`from X import Y` 会把值绑定在导入时，运行期改（测试、`--home`）不传播
- **后果**：`--home` 后写到错误目录

### B2. 依赖方向严格单向（表现层 → 编排层 → 能力层 → 基础层）

- 下层绝不 import 上层
- **检查**：`tools/check_imports.py`

### B3. `pyflakes` 查不出跨模块的错误导入

- **来源**：`from .settings import ApiError`（实际在 `httpclient` 里）—— 通过了 pyflakes
  + 全部测试，直到用户点"生成"才炸
- **做法**：`tools/check_imports.py` 对**所有**（含函数体内的）相对导入做真实
  `import + getattr` 验证
- **后果**：这类 bug 只在运行时暴露

### B4. TUI 不应有模块级可变全局状态

- **来源**：跨线程共享是隐患
- **做法**：全部状态放 `st: dict`（`_new_state()`）
- **检查**：`test_tui_no_module_globals`

### B5. 每个模块恰好一份"自举保护"guard

Pydroid 用 `exec(open(file).read(), __main__.__dict__)` 跑脚本，
**脚本目录不进 `sys.path`**，包内文件含相对导入就报
`attempted relative import with no known parent package`。

- **做法**：每个包内模块加 guard（正常 import 时零开销；被当脚本跑时补 `sys.path` 并转交 `main.py`）
- **位置**：必须插在 docstring 和 `from __future__` **之后**
- **禁止**：`raise SystemExit(2)`（会阻止启动）
- **检查**：`tools/fix_guards.py` 自动补齐 + `verify_all.sh` 第 4 步

### B6. 顶层入口只能有一个

`main.py` / `run.py` / `app.py` **只应存在一个**（`verify_all.sh` 会检查）。
历史上曾同时存在 `main.py` 和 `run.py`，用户不知道该跑哪个。

---

## C. UI 约束

### C1. 功能键必须是 Ctrl 组合，普通字母/数字必须能正常输入

- **来源**：手机要打中文，占用普通字母会打断输入
- **检查**：`test_tui_ctrl_keys_not_stealing_chars`

### C2. 功能键不能与输入框键位冲突

输入框已占用：`^H`退格 `^J`/`^M`提交 `^A`行首 `^E`行尾 `^U`清空行 `^W`删词

**加新功能键前必查**：
```python
conflict = set(_CTRL_ACTIONS) & {k for k in _INPUT_KEYS if isinstance(k, int) and k <= 31}
```

- **来源**：曾用 `0x0d`（Ctrl+M）注册功能键，但那是输入框的 Enter
- **后果**：回车变成切换模型

### C3. 绝不能写屏幕右下角

- **来源**：会触发 curses 自动滚动，**整个界面上跳一行**
- **做法**：`_safe_addstr` 里对最后一行做 `w-1` 截断
- **检查**：`test_tui_no_bottom_right_write`

### C4. 同一行不能有列冲突

- **来源**：两个面板/浮窗的写入范围重叠 → 文字互相覆盖
- **检查**：`test_tui_no_column_conflicts`

### C5. `_box_line` 的 row 是**框内相对行号**，`_safe_addstr` 要**绝对行号**

```python
_box_line(stdscr, y, x, w, 0, ...)      # row=0 → 内部算出 y+1（跳过上边框）
_safe_addstr(stdscr, y + h - 2, ...)    # 绝对行号
```

- **来源**：v5.13.0 开发中混用，导致「待修改图」那行被「参考图 N/4」覆盖
- **检查**：`test_tui_no_column_conflicts`

### C6. 预览渲染必须缓存

- **来源**：每帧全量解码 → 生成图片后界面卡死
- **做法**：`termimg.py` 用 LRU 缓存（不能"满了就全清"）

### C7. 浮窗统一用 `Esc` 关闭

v5.13.0 起不再用 `q`（用户要求）。`Ctrl+C` 保留为强制退出。

### C8. 文字不能截断到看不全

- **来源**：真机反馈"润色界面每一行显示不完全"
- **做法**：长文本用 `_pick_pages`（每项一整页、按显示宽度折行），
  而不是 `_pick_list`（`_trunc` 压成一行）
- **检查**：`test_polish_pages_display`

### C9. `_trunc` 按**显示宽度**算（中文算 2 列）

不能用 `len()`。

---

## D. 业务逻辑约束

### D1. 待修改图**永远**在 `image_urls` 第 1 位

- **来源**：用户反馈"好像只是高清化了参考图，没改我的原图"
- **依据**：实测证明 `image_urls` 顺序有语义（第 1 张=主体）
- **检查**：`test_refs_two_concepts_separated`

### D2. 「待修改图」和「参考图」必须彻底分离

- 待修改图 = `PgUp/PgDn` 光标决定
- 参考图 = **只能**通过 `Ctrl+Y` 弹窗增删，与光标**无关**

**为什么用弹窗**：早期尝试"Ctrl+Y 加光标处那张"，导致浏览历史时会顺带改掉
待修改图 → 概念混淆。

### D3. 提示词在任务失败时要放回输入框

- **来源**：用户明确说过"丢了很生气"
- **检查**：`test_prompt_kept_on_failure`

### D4. 质量档位不能给出会 400 的选项

| provider | 支持档位 |
|---|---|
| OpenRouter | auto/low/medium/high |
| APIMart + `gpt-image-2.5*` | + xhigh/max |
| APIMart + `gpt-image-2` | **不能**给 xhigh/max（会 400，不自动降级） |

**切换模型时必须检查当前质量是否合法，不合法就降级。**

- **检查**：`test_quality_tiers`、`test_session_sanitize`

### D5. `config.json` 里的非法组合要在启动时纠正

`Session._sanitize()` 检查：`quality_supported` / `model_matches_provider` /
`resolution` / `output_format` / `batch_n`。

- **来源**：上次在 APIMart 选了 xhigh，但 config 里 provider 是 openrouter
  → 启动后质量仍是 xhigh，一生成就 400

### D6. 参考图上传失败**不能静默降级**

- **来源**：旧版把所有异常 `pass` 掉 → 上传 400 失败 → `image_urls` 为空
  → 任务变成"纯文生图" → **用户以为在改自己的图，其实生成了一张全新的图**
- **这是个静默的数据正确性 bug**

### D7. 索引计算不能用 `int(s) - 1` 后不校验

- **来源**：负数索引会静默绕回（`int("-1") - 1 = -2` → 选到错误项）
- **检查**：`test_pick_index`

### D8. 成本估算不能有 10 倍偏差

- **来源**：`2k` 会重复乘倍数
- **检查**：`test_cost_estimate`

---

## E. 网络与错误处理约束

### E1. 所有 `urlopen` 必须包 `try/except HTTPError`

- **来源**：提交任务的 `urlopen` 没包 → 裸异常冒泡到 UI →
  用户只看到 `HTTPError HTTP Error 403: Forbidden`，body 信息全丢
- **检查**：AST 静态校验（`test_http_error_translation` 第 ⑦ 项）

### E2. 状态码会骗人，必须读 body

APIMart 余额不足用**两种**状态码：

| 码 | body |
|---|---|
| 402 | `insufficient balance (current: ..., required: ...)` |
| **403** | `insufficient balance: insufficient quota: ...`（type=`quota_not_enough`） |

**403 ≠ 权限问题。**

### E3. 错误判断顺序：**余额必须先于权限**

```python
# ✅ 正确
if status == 402 or "insufficient" in low or "quota" in low:  → 余额不足
elif status in (401, 403):                                    → key 无效

# ❌ 错误（403 被误诊）
elif status in (401, 403) or "invalid_api_key" in low:  → "key 无效"
```

### E4. `HTTPError` 的两个陷阱

```python
str(HTTPError)   → "HTTP Error 403: Forbidden"   # body 读不到
e.status         → AttributeError                 # 只有 .code
```

```python
e.read()  # 第一次：有内容
e.read()  # 第二次：空！流只能读一次
```

**做法**：`_http_error_to_api()` 显式读 body，**缓存到 `e._cached_body`**。

### E5. 网络类错误自动重试，4xx 直接抛

重试条件：`429` 或 `500 <= code < 600`。退避：`min(8.0, 1.5**attempt)` + 抖动。

---

## F. 测试约束（用户明确要求省钱）

### F1. 测试默认离线，不花一分钱

`offline=True` → `placeholder_png`。全部 805 项测试基于此。

### F2. 真实 API 调用必须合并

- **来源**：用户明确要求"测试时尽量合并测试为我省点钱"
- **做法**：错误场景用 mock 构造 `HTTPError`（`io.BytesIO` 造假 body），
  真实调用只在**最终验证**时做 1 次，且用最低成本参数
  （flare + low + 1k + n=1 + 小图）
- **实测**：mock 22 项错误场景 = $0；真实 1 次 = $0.0052

### F3. 测试数量不能硬编码进脚本

- **来源**：`verify_all.sh` 曾硬编码 `"54/54 passed"`，加用例就误报
- **做法**：`passed_all()` 按"N/N 格式且分子=分母"判断

### F4. 每个修复都要有回归测试锁死

测试名带 `回归：` 前缀，docstring 说明**当初怎么坏的**。
当前共 **956** 项测试（805 + 51 + 31 + 27 + 42）。

---

## G. 交付约束

### G1. 改完必须跑 `bash tools/verify_all.sh`（10 步全绿才可交付）

### G2. 打包后自动校验：解包 → 跑入口自检

### G3. 版本号三处同步

`impydroid/__init__.py` 的 `__version__` + `README.md` 版本历史 + zip 文件名

### G4. 敏感信息不进包

`EXCLUDE_FILES` 含 `.imgagent_key` / `.imgagent_apimart_key` / 运行时数据。

### G5. API key 泄漏过就要提醒 rotate

历史上 key 出现在日志/测试脚本里——**必须提醒用户去后台换 key**。
