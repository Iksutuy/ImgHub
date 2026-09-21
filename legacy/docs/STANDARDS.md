# imgagent / impydroid — 技术标准

> 本项目沉淀出的**可复用工程标准**（不限于本项目）。
> 每条都标注了实证依据 —— 不是理论，是踩坑换来的。

---

## 一、代码组织标准

### STD-01 单文件不超过 ~3600 行，超过就拆

**本项目的临界点**：`curses_ui.py` 3543 行（项目最大）。

**拆分判据**（按变化频率）：
- 状态管理 vs 渲染 vs 动作分发 → 三者变化频率不同，可拆
- 但**不要为拆而拆**：过度拆分会让"改一个功能要跳 5 个文件"

**本项目的取舍**：TUI 虽大但**保持单文件**，因为
- 状态字典 `st` 被所有部分共享
- 拆开后循环引用风险高
- 用**清晰的分节注释**替代（`# ===== 面板渲染 =====`）

### STD-02 模块 docstring 必须声明"为什么"

```python
"""纯标准库的 PNG 读写（不依赖 Pillow）。

为什么要自己写：Pydroid 里 Pillow 不是标配，装它有失败风险；而"把参考图缩小"
能显著省钱（图片输入按 token 计费）。这里实现 5 种反滤波 + RGB/RGBA/灰度/调色板
解码 + 最近邻缩放 + 编码。
"""
```

**标准**：docstring 第一句说"是什么"，第二段说"**为什么这样**"。
只有"是什么"的 docstring 对维护者价值有限。

### STD-03 关键决策必须留"考古注释"

```python
# ⚠️ 旧版按画幅"智能"提到 2k —— 那是个坏设计：
# 16:9 会悄悄变成 2k，**成本翻倍而用户完全不知情**（1k low $0.0048 vs 2k low $0.0119）。
# 现在固定用 1k，让花费可预期。
```

**用途**：防止后人"优化"回坏设计。格式：`⚠️ 旧版如何 → 为什么错 → 现在如何`。

### STD-04 配置访问统一走属性，不 `from X import Y`

```python
from . import settings      # ✅
settings.HOME

from .settings import HOME  # ❌ 值被绑定在导入时，运行期改变不传播
```

---

## 二、错误处理标准

### STD-05 错误信息必须包含"下一步做什么"

```python
# ❌ 差
"HTTP Error 403: Forbidden"

# ✅ 好
"账户余额不足（当前 $0.02，需要 $0.05）—— 去 APIMart 充值；
 编辑比文生图贵（要传参考图）"
```

**标准**：错误信息 = **发生了什么** + **为什么** + **怎么办**。

### STD-06 状态码不能作为唯一判据，必须读 body

**实证**：APIMart 对"余额不足"用 **402 和 403 两种码**。

**标准**：
- 读 body 的 `error.message` 和 `error.type`
- 用**语义关键词**（`insufficient` / `quota` / `balance`）而非只看状态码
- 状态码只作为辅助

### STD-07 判断顺序：业务语义优先于 HTTP 语义

```python
# ✅ 余额（业务）先于权限（HTTP 语义）
if 余额关键词: → 余额不足
elif status in (401, 403): → 权限
```

**理由**：同一个 HTTP 码可能对应多种业务含义。

### STD-08 异常包装必须保留原始上下文

```python
def _http_error_to_api(e: HTTPError) -> ApiError:
    raw = getattr(e, "_cached_body", None)   # ← 缓存，因为流只能读一次
    if raw is None:
        raw = e.read()
        e._cached_body = raw
    msg, code = err_from_body(raw)
    return ApiError(f"{msg} (HTTP {e.code}/{code})", e.code)
```

**标准**：包装后的异常必须能追溯原始错误码 + 原始消息。

### STD-09 静默降级是危险的，除非有明确理由

```python
# ❌ 危险：上传失败静默降级 → 任务变成"纯文生图"
#    用户以为在改自己的图，其实生成了一张全新的图
try:
    url = upload_image(...)
except Exception:
    pass

# ✅ 正确：明确报错
url = upload_image(...)   # 失败抛 ApiError
```

**判据**：降级后**用户能否察觉**？不能察觉的降级 = 数据正确性 bug。

**例外**：外观类降级（真彩色→256色→ASCII）是安全的——用户看得见。

---

## 三、TUI / curses 开发标准

### STD-10 三种行号语义必须分清

| 函数 | 行号含义 |
|---|---|
| `_box_line(win, y, x, w, row, ...)` | **框内相对行号**（内部会 +1 跳过上边框） |
| `_safe_addstr(win, y_abs, x, ...)` | **绝对屏幕行号** |
| `_draw_*(win, y, x, h, w, ...)` | 面板**左上角绝对坐标** |

**混用 = 文字互相覆盖**（v5.13.0 真实 bug）。

### STD-11 不写屏幕右下角

curses 写到最后一行最后一列会触发自动滚动 → **整个界面上跳**。

```python
def _safe_addstr(win, y, x, text, attr=0):
    h, w = win.getmaxyx()
    if y >= h: return
    limit = (w - x - 1) if y == h - 1 else (w - x)   # ← 最后一行少写 1 列
    ...
```

### STD-12 curses 运行中禁止 `print`

**做法**：
1. 所有 `_act_*` 动作函数内不得有 `print`（用静态检查）
2. 后台任务期间重定向 `stdout`：
```python
with contextlib.redirect_stdout(io.StringIO()):
    result = api.generate(...)
```
3. 需要多行输出时用 TUI 内浮窗（`_output_overlay`）

### STD-13 浮窗的关闭键要统一

**本项目标准**：所有浮窗 `Esc` 关闭。不要一个用 `q`、一个用 `Esc`、一个用任意键。

### STD-14 长文本用"分页"而非"截断"

```python
# ❌ 截断：40 列屏幕上 70 字的中文候选只剩前 20 字
_pick_list(...)   # 内部用 _trunc 压成一行

# ✅ 分页：每项一整页 + 按显示宽度折行 + ←→ 翻页
_pick_pages(...)
```

**标准**：文本长度不可控时，**永远不要截断**——用分页/折行/滚动。

### STD-15 宽度计算必须用显示宽度（CJK 算 2 列）

```python
def _disp_w(s: str) -> int:   # 不能用 len(s)
    ...
```

### STD-16 渲染要缓存（尤其解码/缩放）

**实证**：预览每帧全量解码 PNG → 生成图片后界面卡死。
**做法**：LRU 缓存（不能"满了就全清"）。

### STD-17 快捷键只占 Ctrl 组合，把普通字符留给用户

手机上要打中文，占用普通字母会打断输入。

**加键位前必查冲突**：
```python
busy = set(_CTRL_ACTIONS) | {k for k in _INPUT_KEYS if isinstance(k, int) and k <= 31}
```

### STD-18 功能分组用视觉区分，而非只靠文字

**本项目的做法**：底栏按键分档着色
- 生图类（`priority=0`）→ 亮色
- 程序类（`priority=1`）→ 暗色
- 退出（`priority=-1`）→ 红色

**标准**：分组信息要**一眼看出**，不能只靠阅读标签。

---

## 四、并发标准

### STD-19 只在 IO 阻塞处并发，CPU 密集处不要

**实测数据（本机 Python 3.12 / aarch64）**：

| 场景 | 1 线程 | 2 线程 | 4 线程 |
|---|---|---|---|
| HTTP 请求 ×3 | 17.2s | — | **3.2s**（5.3x） |
| PNG 缩放 2048 | 0.20s | 0.99s | 0.87s（**更慢**） |

**结论**：GIL 下纯 Python 循环无法并行，CPU 密集并发只会更慢。

### STD-20 并发结果的顺序要可预期

`gather()` 保持**输入顺序**返回（不是完成顺序）。
错误不中断整体——单个任务失败只影响它自己（结果里带异常对象）。

```python
results = gather(fn, items)   # [结果 or 异常, ...] 顺序与 items 一致
for r in results:
    if isinstance(r, BaseException):
        handle_error(r)
    else:
        use(r)
```

### STD-21 线程数默认 4

手机 CPU 核心有限（通常 4–8），过多线程反而拖慢。

---

## 五、测试标准

### STD-22 测试默认离线，真实调用必须合并

**用户的明确要求**："测试时尽量合并测试为我省点钱"

**做法**：
- 错误场景用 mock 构造（`io.BytesIO` 造假 body）→ **$0**
- 真实调用只在最终验证做 1 次，用最低成本参数
- 实测：mock 22 项 = $0；真实 1 次 = $0.0052

### STD-23 测试名 + docstring 要写清"当初怎么坏的"

```python
def test_tui_no_column_conflicts() -> None:
    """回归：任何面板/浮窗的写入都不能在同一行发生列冲突。"""
```

**价值**：后人看到测试失败时，立刻知道违反了什么约束。

### STD-24 数量断言不能硬编码

```bash
# ❌ 加用例就误报
if [ "$out" = "54/54 passed" ]; then

# ✅ 分子=分母即通过
passed_all() { ... a=$(cut -d/ -f1); b=$(cut -d/ -f2 | cut -d' ' -f1); [ "$a" = "$b" ]; }
```

### STD-25 测试要覆盖"源码层"（静态断言）

有些 bug 无法通过行为测试发现（比如"某函数里不该有 print"）。

```python
import ast
tree = ast.parse(Path(cui.__file__).read_text())
for node in ast.walk(tree):
    if isinstance(node, ast.FunctionDef) and node.name.startswith("_act_"):
        # 检查函数体内有无 print 调用
```

**本项目的源码层检查**：
- 无裸 `urlopen`（必须有 try/except）
- 无 `print` 在 `_act_*` 里
- 无模块级可变全局
- 浮窗不再用 `q` 关闭
- 每个模块恰好 1 份 guard

### STD-26 测试必须能测"交互流程"（不只是纯函数）

**本项目的方式**：`stub` 底层 IO 函数 + 真实跑上层逻辑

```python
# stub 掉按键读取，喂一串键给浮窗
def drive(keys, choices):
    it = iter(keys)
    cui._read_key = lambda win: next(it, "\x1b")
    return cui._pick_pages(MW(), FS(), "t", choices)
```

**价值**：能测出"翻页越界"、"Esc 返回 None"这类交互契约。

### STD-27 用 pty 做真机等价验证

```python
pid, fd = pty.fork()
if pid == 0:
    os.execvpe(sys.executable, [...], env)   # 指定 COLUMNS/LINES/TERM
fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", 44, 100, 0, 0))
os.set_blocking(fd, False)     # ← 关键，否则 write 会挂住
```

**必须做的事**：
- 设 `os.set_blocking(fd, False)`
- 给足等待时间（TUI 启动约 2.5s）
- 用**导出文件**读屏幕（不是抓 stdout，curses 输出难解析）

---

## 六、交付标准

### STD-28 一条命令跑完全部校验

```bash
bash tools/verify_all.sh
```

**必须覆盖**：语法 → lint → 跨模块导入 → 入口唯一性 → guard 一致性 →
入口鲁棒性 → 功能测试 → 极端终端 → 各子系统。

### STD-29 打包后要自动校验（解包 + 启动）

```python
# make_zip.py 打完包后
# 1. 解包到临时目录
# 2. 跑入口自检（--doctor 或等价）
# 3. 失败则报错，不产出坏包
```

### STD-30 版本号三处同步

`__init__.py` 的 `__version__` + `README.md` 版本历史 + zip 文件名。

---

## 七、诊断标准

### STD-31 错误诊断的四步法

**实证于 403 问题**：

1. **先复现**（不要凭猜测）
   本项目：用参数组合扫描，意外复现了 403
2. **读 body**（状态码会骗人）
   发现 `403` 的 body 是 `quota_not_enough`，不是权限
3. **查代码有没有解析 body**
   发现裸 `HTTPError` 冒泡，body 全丢
4. **对比同类代码路径**
   `upload_image` 有 `try/except`、提交任务没有 → **差异即 bug 线索**

### STD-32 "挂死"比"失败"更难诊断，要优先区分

**实证**：测试超时 600s（挂死）而不是报错。

**挂死的典型原因**：stub 的接口与实际调用不符
```python
# 测试 stub 了旧接口
cui._pick_list = lambda ...: "x"
# 但实际代码已改用新接口 → 真实函数跑起来 while True 等键输入 → 挂死
got = _pick_pages(...)
```

**做法**：先跑单个测试确认是"失败"还是"挂死"，再定位。

### STD-33 中断任务的恢复顺序

**实证于"润色分页任务中断"**：

1. 看文件 mtime（找出中断时正在改哪个文件）
2. 找备份/临时文件残留（`*.bak` / `*.orig` / `*.rej`）
3. `grep` 新函数是否存在 + 是否已被调用
4. 跑测试看是"失败"还是"挂死"

**本次第 4 步立刻定位到根因**（stub 用了旧接口名）。

---

## 八、成本控制标准（面向付费 API 的项目）

### STD-34 默认参数要用最省的档位

`resolution="1k"`、`quality="low"`、`n=1`。

**理由**：`2k` ≈ `1k` 的 2 倍 token，`4k` ≈ 4 倍。

### STD-35 参考图必须先缩放再上传

```python
shrink_for_reference(raw, 1024)   # 缩到 1024px
```

**理由**：图片输入按 token 计费（参考图 1024×1024 ≈ 2048 image tokens）。

### STD-36 成本必须在界面上可见

每次生成显示 `$0.0048`，累计显示 `累计 $0.0427`。

### STD-37 提防"预扣按最高档"

**实证**：APIMart 文档说"提交时会按当前尺寸的**最高档 max** 预留额度"。
所以用户选 `low` 也要有 `$0.05` 以上余额才能发起编辑。

**含义**：**余额检查要比实际成本更宽松**。

### STD-38 离线模式是必备的省钱/调试通道

`offline=True` → 占位图，不联网不花钱。用途：
1. 跑通流程再决定花钱
2. 网络故障时的保底
3. **全部测试的基础**

### STD-39 多步修改要显式构造上下文

API 是**无状态**的——不记得你上一张画了什么。
所以"基于上一张修改"必须把图**显式**传回去（`image_urls` / `input_references`）。
