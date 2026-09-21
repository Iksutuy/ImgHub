# imgagent / impydroid — 交接手册

> 给接手开发者：**15 分钟上手，1 小时能改功能。**
> 配套文档：`docs/ARCHITECTURE.md`（框架）、`docs/CONSTRAINTS.md`（铁律）、`docs/STANDARDS.md`（标准）

---

## 0. 五分钟了解这是什么

一个**在 Android 手机上跑**的 AI 生图客户端（TUI 界面，纯标准库）。

- **能做什么**：文生图、多参考图编辑、批量生成、提示词润色、成本追踪
- **跑在哪**：Pydroid 3 / Termux
- **代码量**：13,912 行（含测试 4,076 行）
- **测试**：956 项，全离线，`bash tools/verify_all.sh` 一条命令跑完
- **当前版本**：5.17.0

---

## 1. 十分钟跑起来

```bash
cd <项目目录>

# ① 环境自检（**出问题第一个跑这个**）
python3 main.py --doctor

# ② 离线跑（不联网不花钱，能看界面）
IMGAGENT_FORCE_OFFLINE=1 python3 main.py

# ③ 跑全部校验（改完代码必跑）
bash tools/verify_all.sh
```

**没有网络/没有 key 也能跑**——离线模式用占位图。

### 想接真实 API

```bash
# APIMart（推荐，异步、支持多图编辑）
export IMGAGENT_API_PROVIDER=apimart
export IMGAGENT_APIMART_API_KEY=sk-xxx

# 或 OpenRouter（同步）
export IMGAGENT_API_KEY=sk-or-xxx

python3 main.py
```

---

## 2. 代码地图：改哪里找什么

### 2.1 按"我想做什么"索引

| 我想… | 去哪个文件 | 找什么 |
|---|---|---|
| 加一个快捷键 | `curses_ui.py` | `_CTRL_ACTIONS`（注册）+ 动作表 + `_KEYMAP`（底栏显示） |
| 改界面布局 | `curses_ui.py` | `_draw_all()`（宽屏/窄屏分支） |
| 加一个面板 | `curses_ui.py` | 仿 `_draw_refs` / `_draw_info` |
| 加一个浮窗 | `curses_ui.py` | 仿 `_pick_pages`（最完整） |
| 改生成流程 | `curses_ui.py` | `_act_generate` |
| 改编辑流程 | `curses_ui.py` | `_act_edit` + `load_edit_images` |
| 改 API 请求 | `api.py` | `_build_payload`（按 provider 分叉） |
| 加 API 参数 | `api.py` | `generate()` 签名 + `_build_payload` + `_generate_apimart` |
| 改错误提示 | `api.py` | `error_hint`（TUI 单行）/ `explain_error`（CLI 多行） |
| 加设置项 | `store.py` | `Session.__init__` + `_sanitize` + `_load_files`（**三处都要加**） |
| 改常量/路径 | `settings.py` | 集中在这里 |
| 改提示词润色 | `polish.py` | `picks()` |
| 改 PNG 处理 | `pngcodec.py` | `shrink_for_reference()` |
| 改终端渲染 | `termimg.py` | 三种模式 + LRU 缓存 |

### 2.2 关键函数速查

```python
# 生成/编辑（api.py）
generate(prompt, *, api_key, model, quality, aspect, n=1, refs=None,
         offline=False, step=0, resolution="1k", output_format="png") -> GenResult

# 组装编辑图片（curses_ui.py）★ 顺序有语义
load_edit_images(sess, st) -> ([(bytes, media), ...], desc)

# 错误提示（api.py）
error_hint(e) -> str          # TUI 单行
explain_error(e) -> None      # CLI 多行（print）
_http_error_to_api(e) -> ApiError   # HTTPError → 可读 ApiError
```

### 2.3 TUI 状态字典（`st`）

```python
_new_state() = {
    "mode": "gen",        # gen | edit
    "hist": 0,            # 历史索引（★ 同时决定「待修改图」）
    "scroll": 0,
    "busy": "",           # 非空 = 后台任务中
    "spin": 0, "t0": 0,
    "refs": [],           # 参考图文件名列表
    "ref_sel": 0,         # 参考图选中项
    "inp_hist_idx": -1,   # 提示词历史游标
    "inp_draft": "",      # 翻历史前的草稿
    "last_submit": "",    # 失败时用于恢复提示词
}
```

---

## 3. 常见任务操作手册

### 任务 A：加一个"循环切换"的选项（如新参数）

**参考 `_act_cycle_resolution` 的完整实现**（约 25 行）：

```python
# ① settings.py 加常量
MY_OPTIONS = ["a", "b", "c"]

# ② store.py Session 加字段 + _sanitize 校验
self.my_option = "a"
# _sanitize 里：
if getattr(self, "my_option", "a") not in settings.MY_OPTIONS:
    self.my_option = "a"

# ③ curses_ui.py 注册键位
_CTRL_ACTIONS[0x05] = "my_action"       # ← 先查有没有冲突！

# ④ 动作表
"my_action": (lambda: _act_cycle_my(sess, st), False),

# ⑤ 实现函数
def _act_cycle_my(sess, st):
    from .settings import MY_OPTIONS
    choices = list(MY_OPTIONS)
    cur = getattr(sess, "my_option", "a")
    idx = choices.index(cur) if cur in choices else 0
    sess.my_option = choices[(idx + 1) % len(choices)]
    try: sess.save_config()
    except Exception: pass
    log(f"选项 → {sess.my_option}（Ctrl+X 继续）", "ok")
    _flash(f"选项 → {sess.my_option}")

# ⑥ _KEYMAP 加条目（底栏显示）
("^X", "选项", "选项", 0),    # priority 0=生图亮 / 1=程序暗

# ⑦ 传给 API（如果要影响请求）
api.generate(..., my_option=getattr(sess, "my_option", "a"))
# 并在 api.py 的 generate 签名 + _build_payload/_generate_apimart 里透传

# ⑧ 加测试（见下）
```

**检查清单**：☐ 键位无冲突 ☐ `_sanitize` 有校验 ☐ `_KEYMAP` 有条目 ☐ 测试

### 任务 B：加一个新浮窗

**参考 `_pick_pages`**（最完整的浮窗，含折行/翻页/滚动）。骨架：

```python
def _my_overlay(stdscr, sess, title, ...) -> X | None:
    while True:
        h, w = stdscr.getmaxyx()
        stdscr.erase()
        _draw_topbar(...)
        _box(stdscr, box_y, box_x, box_h, box_w, title, ...)
        # ... 渲染内容（用 _box_line，注意 row 是相对行号）
        # ... 渲染底栏提示
        stdscr.refresh()
        _dump_debug(stdscr, {...}, suffix=".my")   # ← 便于自动化测试
        ch = _read_key(stdscr)
        if ch is None: continue
        code = _key_code(ch)
        if code == 27: return None        # Esc 统一关闭
        # ... 处理其它键
```

**必须**：`Esc` 关闭 / 不 `print` / 有 `_dump_debug` 导出（测试要用）

### 任务 C：加一个新 provider

**改三处，必须一致**：

```python
# ① settings.py：端点、模型清单、质量档、默认模型
API_PROVIDER 分支
_XXX_BASE
MODEL_CHOICES_XXX
QUALITIES_XXX

# ② api.py::_build_payload：字段名映射（最容易错）
if settings.API_PROVIDER == "xxx":
    payload = {"model":..., "prompt":..., "YOUR_FIELD": aspect, ...}
    return "/your/endpoint", payload

# ③ api.py：同步 or 异步流程
#    同步 → 仿 _generate_openrouter
#    异步 → 仿 _generate_apimart（上传/提交/轮询/下载）
```

**测试**：`test_provider_config` / `test_apimart_payload` 是模板。

### 任务 D：改错误提示

**改 `api.py` 的两个函数**（保持逻辑一致）：

```python
def error_hint(e) -> str:      # TUI 单行
def explain_error(e) -> None:  # CLI 多行（print + 建议）
```

**⚠️ 铁律**：**余额判断必须先于权限判断**（`CONSTRAINTS.md: E3`）。

### 任务 E：发布新版本

```bash
# ① 三处同步版本号
#    impydroid/__init__.py 的 __version__
#    README.md 的版本历史
# ② 跑全量校验
bash tools/verify_all.sh
# ③ 打包（自动校验解包+启动）
python3 tools/make_zip.py --outdir /workspace/imgagent
# ④ 产出
#    imgagent-<版本>-pydroid3.zip
#    imgagent-<版本>-termux.zip
```

---

## 4. 测试体系

### 4.1 五个测试文件

| 文件 | 项数 | 内容 |
|---|---|---|
| `test_impydroid.py` | 805 | 主测试（核心逻辑 + TUI + API + 存储） |
| `test_presentation.py` | 51 | 配色 / 分隔线 / 终端渲染 |
| `test_polish.py` | 31 | 润色流程（离线） |
| `test_concurrency.py` | 27 | 并发与性能 |
| `test_entrypoints.py` | 42 | 入口鲁棒性（模拟 Pydroid 的 exec 模型） |

**总计 956 项。**

### 4.2 一条命令跑完全部

```bash
bash tools/verify_all.sh      # 10 步，全绿才可交付
```

步骤：guard 自动补齐 → 跨模块 import → 语法+pyflakes → 入口唯一性 →
guard 一致性 → 入口鲁棒性 → 功能测试 → ASCII 终端 → 表现层 → 润色 → 并发

### 4.3 写测试的模板

```python
def test_my_feature() -> None:
    """回归：<当初怎么坏的>。"""
    import curses
    from impydroid import curses_ui as cui

    # mock 掉 curses 的颜色（否则报 "must call initscr() first"）
    cui._c.color_pair = lambda n: n
    cui._c.start_color = cui._c.use_default_colors = lambda: None
    cui._c.init_pair = lambda *a: None

    class MW:                      # mock window
        def __init__(self): self.calls = []
        def getmaxyx(self): return 40, 100
        def erase(self): pass
        def refresh(self): pass
        def addnstr(self, y, x, t, n, attr=0):
            self.calls.append((y, x, t[:n]))

    # ... 断言
    check("描述", 条件, "实际值")
```

**要点**：
- 每个测试文件末尾 `print(f"{ok}/{total} passed")`（`verify_all.sh` 靠这个判断）
- 用 `check(name, cond, extra)` 而非裸 `assert`（能看到失败详情）
- **不要花真实 API 的钱**——用 mock

### 4.4 测试 mock 模式（省钱关键）

```python
# 构造假的 HTTPError（0 成本）
import io, json, urllib.error
def mk(code, body):
    return urllib.error.HTTPError("url", code, "msg", None,
                                  io.BytesIO(json.dumps(body).encode()))
```

### 4.5 真机等价验证（pty）

```python
import os, pty, time, fcntl, termios, struct
env = dict(os.environ)
env.update(COLUMNS="100", LINES="44", TERM="xterm-256color",
           IMGAGENT_HOME="/tmp/x", IMGAGENT_FORCE_OFFLINE="1",
           IMGAGENT_TUI_DEBUG="/tmp/screen.txt",      # ← 屏幕导出文件
           IMGAGENT_TUI_DEBUG_FRAMES="99999")
pid, fd = pty.fork()
if pid == 0:
    os.execvpe(sys.executable, [sys.executable, "-u", "main.py"], env)
fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", 44, 100, 0, 0))
os.set_blocking(fd, False)        # ★ 必须！否则 write 挂住
time.sleep(2.5)                   # TUI 启动要时间
os.write(fd, b"\x07")             # Ctrl+G = 生成
time.sleep(1.5)
print(open("/tmp/screen.txt", encoding="utf-8").read())   # 读屏幕
```

**调试环境变量**：
| 变量 | 作用 |
|---|---|
| `IMGAGENT_TUI_DEBUG` | 导出屏幕到文件（每帧） |
| `IMGAGENT_TUI_DEBUG_FRAMES` | 导出多少帧 |
| `IMGAGENT_TUI_KEYLOG` | 记录按键 |
| `IMGAGENT_FORCE_OFFLINE` | 强制离线 |
| `IMGAGENT_HOME` | 数据目录 |

---

## 5. 环境变量总表

### 5.1 API / provider

| 变量 | 默认 | 说明 |
|---|---|---|
| `IMGAGENT_API_PROVIDER` | `openrouter` | `openrouter` \| `apimart` |
| `IMGAGENT_API_KEY` | — | OpenRouter key |
| `IMGAGENT_APIMART_API_KEY` | — | APIMart key |
| `IMGAGENT_APIMART_BASE_URL` | 官方地址 | 可换代理 |
| `IMGAGENT_MODEL` | — | 覆盖默认模型 |
| `OPENROUTER_BASE_URL` | `https://openrouter.ai/api/v1` | OpenRouter 端点 |
| `IMGAGENT_APIMART_RESOLUTION` | `1k` | 分辨率（UI 里也能改） |
| `IMGAGENT_APIMART_TIMEOUT` | `300` | 轮询超时秒数 |

### 5.2 润色（独立于生图的 LLM 端点）

| 变量 | 说明 |
|---|---|
| `IMGAGENT_POLISH` | 关掉润色（如 `0`） |
| `IMGAGENT_POLISH_API_KEY` | 润色用的 key（可与生图不同） |
| `IMGAGENT_POLISH_BASE_URL` | 润色端点（OpenAI 兼容 chat/completions） |
| `IMGAGENT_POLISH_MODEL` | 润色模型 |
| `IMGAGENT_POLISH_TIMEOUT` | 润色超时 |
| `IMGAGENT_POLISH_MAX_TOKENS` | 润色最大 token |

### 5.3 运行环境

| 变量 | 说明 |
|---|---|
| `IMGAGENT_HOME` | 数据目录（默认脚本同目录的 `imgagent_data/`） |
| `IMGAGENT_FORCE_OFFLINE` | 强制离线（占位图，不花钱） |
| `IMGAGENT_HISTORY_MAX` | 历史条数上限 |
| `PREFIX` | Termux 的 `/data/data/com.termux/files/usr` |
| `IMGAGENT_COLOR` / `IMGAGENT_TUI_COLOR` | 配色开关 |
| `IMGAGENT_TUI` | TUI 开关 |
| `IMGAGENT_TERM_IMG` | 终端字符画开关 |

### 5.4 TUI 外观 / 调试

| 变量 | 默认 | 说明 |
|---|---|---|
| `IMGAGENT_TUI_BORDER` | `round` | 边框风格（round/square/double/heavy/ascii） |
| `IMGAGENT_TUI_SPINNER` | — | 顶栏动画开关 |
| `IMGAGENT_TUI_FIT` | — | 自适应开关 |
| `IMGAGENT_TUI_DEBUG` | — | **屏幕导出路径**（自动化测试用） |
| `IMGAGENT_TUI_DEBUG_FRAMES` | — | 导出帧数 |
| `IMGAGENT_TUI_KEYLOG` | — | **按键日志路径** |

---

## 6. 已知陷阱（新接手必读）

| # | 陷阱 | 后果 | 查 |
|---|---|---|---|
| 1 | `_box_line` 的 row 是**相对**行号，`_safe_addstr` 是**绝对** | 文字互相覆盖 | CONSTRAINTS C5 |
| 2 | 写屏幕右下角 | 界面整体上跳 | C3 |
| 3 | curses 里 `print` | 花屏 | A7 |
| 4 | 键位与输入框冲突 | 回车变切模型 | C2 |
| 5 | 裸 `urlopen` | 错误信息全丢 | E1 |
| 6 | 403 当权限问题 | 误诊（实际是余额） | E2/E3 |
| 7 | `HTTPError.read()` 读两次 | 第二次空 | E4 |
| 8 | `from settings import X` | `--home` 不生效 | B1 |
| 9 | 模块级可变全局 | 跨线程隐患 | B4 |
| 10 | guard 位置错（在 `__future__` 前） | pyflakes 报错 | B5 |
| 11 | 两个入口文件 | 用户不知道跑哪个 | B6 |
| 12 | CPU 密集用并发 | 更慢 | STD-19 |
| 13 | 测试数量硬编码 | 加用例误报 | STD-24 |
| 14 | 参考图**顺序**搞错 | 输出的是参考图而非改后的原图 | D1/D2 |
| 15 | 静默降级上传失败 | 用户以为在改图，其实生成了新图 | D6 |
| 16 | 长文本 `_trunc` 截断 | 用户看不见内容 | C8 |

---

## 7. 快速排查

| 症状 | 先查 |
|---|---|
| 界面花屏/上跳 | 有没有 `print`？有没有写右下角？ |
| 中文输不进 | 用了 `getch` 还是 `get_wch`？ |
| 黑屏无输出 | `console.py` 最先导入了吗？`--doctor` |
| 报 403/402 | **余额**（不是权限） |
| 报 "attempted relative import" | guard 在不在？位置对不对？ |
| 报 "must call initscr() first" | 测试里没 mock `color_pair` |
| 测试挂死（不报错） | stub 的接口名与实际不符 |
| 生成按钮点了没反应 | `_act_generate` 里的 import 可解析吗？`check_imports.py` |
| 改的功能没生效 | `_sanitize` 里被重置了？缓存没更新？ |
| 图存错地方 | `settings.set_home()` 漏改了某个路径？ |
| 预览卡死 | 渲染缓存没生效？ |

---

## 8. 待办 / 已知未完成

### 8.1 明确未做（有意）

| 项 | 原因 |
|---|---|
| 透明背景（`background`） | 用户不是电商场景 |
| 精确像素尺寸（`size="1600x1200"`） | 比例名够用，实现复杂 |
| 流式部分图 | API 不支持 |

### 8.2 潜在改进点

| 项 | 说明 | 难度 |
|---|---|---|
| 余额预检 | APIMart 无公开余额端点；可用 `/api/pricing` 做成本预估 | 中 |
| 编辑失败自动降级到文生图 | 余额不足时减少挫败 | 低 |
| 参考图从相册直接加 | 现在只能从历史选 | 低 |
| `Ctrl+U` 语义拆分 | 现在主界面=切格式、输入框=清空行（隐式） | 低 |
| 错误信息"可复制" | curses 里选中麻烦 | 中 |
| 测试超时保护 | 挂死时自动失败（`signal.alarm`） | 低 |

### 8.3 诚实边界（未验证的部分）

| 项 | 状态 |
|---|---|
| 人物设定迁移效果 | **未验证**（需要真人像实验）——"用参考图改脸部/服装"的效果未知 |
| 参考图权重 | API 无显式权重字段，只靠顺序。**未做定量实验** |
| 预扣额度机制 | 只有文档依据（"按最高档 max 预留"），未实测具体扣减 |
| `38650/42383` 单位 | 是 credits 不是 USD，**换算率未知**（提示里按 USD 显示是错的） |
| pyjnius 的 Pydroid Activity 类名 | 5 个候选里**猜的**（`android.py` 按顺序试） |
| 相册扫描目录 | **猜的**（`photos.py`） |
| HEIC 支持 | 不支持 |

---

## 9. 交接检查清单

接手后请依次确认：

- [ ] `python3 main.py --doctor` 全绿
- [ ] `IMGAGENT_FORCE_OFFLINE=1 python3 main.py` 能进 TUI
- [ ] `bash tools/verify_all.sh` 10 步全绿（POSIX） / `powershell -File tools\verify_windows.ps1`（Windows）
- [ ] 读过 `CONSTRAINTS.md` 的 16 条陷阱
- [ ] 知道"改完必跑 verify_all.sh / verify_windows.ps1"
- [ ] 知道"测试默认离线，真实调用要合并"
- [ ] 知道 403 ≠ 权限
- [ ] 知道待修改图必须排第 1 位
- [ ] 能说出 `_box_line` 和 `_safe_addstr` 的行号差异
- [ ] 知道 `settings` 要用属性访问

---

## 9.5 Windows 适配记录（v5.18.0）

**目标**：Windows 桌面也能跑，TUI 优先（装 windows-curses 后）、无 curses 自动降级 CLI。

| 改动文件 | 内容 |
|---|---|
| `curses_ui.py` | ① 顶层 `_CSI_KEYS/_SS3_KEYS/_INPUT_KEYS` 不再无条件引用 `_c`——无 curses 时用 SimpleNamespace 占位（含真实键码），模块可正常 import，输入逻辑可测可用；② 真正进 TUI 的函数仍由 `_HAS_CURSES` 挡住 |
| `console.py` | `harden_stdio`：Windows 上优先 `reconfigure(encoding="utf-8")`，根治 GBK 乱码；失败仍按 `errors=replace` 兜底（配合 `probe_console` 自动降级 ASCII，不会黑屏） |
| `settings.py` | `pick_home` 拆出 `_candidate_homes`：Windows 候选 = 脚本同目录 → `%LOCALAPPDATA%\imgagent` → 用户目录 → 临时目录；不再白试 `/sdcard` 路径 |
| `store.py` | `album_dirs`：Windows 直接返回桌面目录（`我的图片` / `下载`），不再试 Android 相册路径 |
| `android.py` | 新增 `is_windows()` / `windows_open()`（`os.startfile` 系统看图器）；`platform_name()` 返回 `"windows"` |
| `preview.py` | `show()`：Windows 优先 `os.startfile` 预览，然后 Tkinter → 终端字符画 → 保底路径 |
| `doctor.py` | [7] 平台提示走 Windows 分支；[9] 复用 `_candidate_homes` |
| `make_zip.py` | 新增 `--target windows`：打包 + `启动.bat`（chcp 65001）+ `requirements-windows.txt` |
| `verify_windows.ps1` | PowerShell 版一键校验（纯 ASCII，兼容 Windows PowerShell 5.1） |

**Windows 特有行为 / 差异**：

1. `os.chmod(path, 0o600)` 是 no-op（NTFS 无 POSIX 权限位）——测试里已按 `os.name=="nt"` 跳过权限断言。
2. `os.link` 在 NTFS 同卷可用、跨卷抛 OSError——已有复制兜底，无需改。
3. **TUI 需要 `windows-curses`**（可选依赖，try/except 内）：装了用 TUI，没装自动 CLI 菜单。
4. 中文输出依赖终端 UTF-8：Windows Terminal / PowerShell 7 / `chcp 65001` 正常；老 cmd 可能乱码（但不会黑屏）。
5. 平台路径测试（`test_platform_paths`）在 Windows 上跳过——该契约只对 Android/Termux 有意义。

**已知 Windows 边界**：

- `--doctor` 全部 ASCII 输出（与 Android 一致，`doctor.py` 遵循 A5 约束）。
- 系统相册选择器（ACTION_OPEN_DOCUMENT）是 Android 专用；Windows 上用「上传」菜单 5 从目录导入。

### 9.6 v5.18.1 修复（Windows 实测反馈）

| 症状 | 根因 | 修法 |
|---|---|---|
| 界面全是 `←[90m` 乱码 | 传统 conhost 默认不解析 ANSI；`color_enabled()` 只看 `isatty()` 就上色 | `console._enable_windows_vt()`：用 ctypes 开 `ENABLE_VIRTUAL_TERMINAL_PROCESSING`；开不了就关闭颜色（`color_enabled()` 返回 False），宁可不彩色也不出乱码 |
| 残留"安卓原生能力 / 装 pyjnius"提示 | `app.py` 非 Termux 分支无条件打印安卓状态 | 平台能力探测改为按 `platform_name()` 分派（termux / pydroid / windows / desktop 各一套提示） |
| 润色端点/模型/key 被硬编码 | `polish.py` 内置私有端点 `api.prc.dpdns.org` + 真实 key | 移除 `_DEFAULT_KEY`；默认端点/模型降级为**建议值**；key 必须用户自配；`enabled()` 未配 key 时 False；`_call()` 未配 key 直接抛错不发请求 |

**润色配置持久化（新增）**：

| 项 | 存放位置 |
|---|---|
| `polish_base_url` / `polish_model` | `config.json`（`settings.set_polish(..., persist=True)` 写入） |
| polish key | 独立文件 `.imgagent_polish_key`（`save_polish_key` / `load_polish_key`） |

- 启动时 `app.main()` 调 `settings.load_polish_config()` + `load_polish_key()` 自动恢复（环境变量优先级最高）。
- 配置入口：CLI 设置 `12) 配置润色`（`ui._configure_polish`）；TUI 设置面板 `配置润色`（`curses_ui._act_polish_config`）。
- `settings.set_home()` 必须同步 `_POLISH_KEY_FILE`（漏了会 `--home` 后写错地方，同 A4/E 类陷阱）。

### 9.7 v5.18.2 修复（装上 windows-curses 后实测 TUI）

装上 `windows-curses`（PDCurses 封装）后用**真控制台**（`CREATE_NEW_CONSOLE`）实测，
发现 PDCurses 的键盘 API 语义与 ncurses **完全不同**：

| 输入 | `getch()` | `get_wch()`（ncurses 用法） |
|---|---|---|
| ASCII `'a'` | 97 ✓ | `'a'` ✓ |
| `KEY_UP`(259) | **259 ✓** | **`'ă'` = chr(259) ✗** |
| `KEY_F1`(265) | 265 ✓ | `'ĉ'` ✗ |
| Ctrl+G(7) | 7 ✓ | `'\x07'` ✓ |
| 中文 `'中'` | 20013 ✓ | `'中'` ✓ |
| 超时 | -1 ✓ | 抛 `error` |

**后果**：照搬 ncurses 的 `get_wch()`，Windows 上方向键会插入 `ă` 之类乱码，
`KEY_*` 永不命中（导航/`_CTRL_ACTIONS` 全废）。而项目 A6 约束偏偏要求用
`get_wch`（那是为 ncurses 的中文输入而定的）。

**修法**（`curses_ui.py`）：

- 新增 `_IS_PDCURSES = _HAS_CURSES and not hasattr(_c, "ncurses_version")`（PDCurses 无此属性）。
- `_read_key` 在 PDCurses 下改走 `_read_key_pdcurses()`：用 `getch()`，按
  `-1`→None / `KEY_*`→int / `<0x20 或 0x7f`→str / `>255 非 KEY_*`→`chr(code)` 分类。
- `_PD_KEY_CODES`：PDCurses 的 KEY_* 码集合（实测范围 257–548）。
- `_init_colors`：PDCurses 不支持 `init_pair(fg, -1)`（`use_default_colors()` 假成功、
  随后 init_pair 抛 error）→ PDCurses 直接用 `COLOR_BLACK` 作背景。

**实测结果**（`CREATE_NEW_CONSOLE` 真控制台，120×30）：`_read_key` 分类
方向键=int(259/258/260/339)、中文=str、Ctrl+G=str、Esc=str 全部正确；
`_draw_all` 渲染出完整多面板界面；`_main_loop` 持续运行，消息面板确认
Ctrl+D / PgUp / PgDn 等按键被正确识别分发。

> ⚠️ 已知微小边界：PDCurses 里 `chr(0x0103)='ă'`、`chr(0x0104)='Ą'` 等少量
> 拉丁扩展字符的码点与 KEY_* 键码重叠，输入这些字符会被当成方向键
> （PDCurses 固有限制，中文/常用字符不受影响）。

**测试跨平台化**（原先只考虑 POSIX，Windows 上误报）：

| 位置 | 问题 | 修法 |
|---|---|---|
| `test_entrypoints.py` | `subprocess.run(text=True)` 在 Windows 按 GBK 解码，而子进程输出是 UTF-8 → `UnicodeDecodeError` → out 空 → 误报 | 加 `encoding="utf-8", errors="replace"` |
| `test_impydroid.py` LRU 用例 | `k[0].split("/")[-1]` 在 Windows 取不到文件名（分隔符是 `\`） | 改 `os.path.basename` |
| `test_impydroid.py` / `test_presentation.py` preview 用例 | Windows 上 `os.startfile`/Tkinter 可用会返回 viewer/tk，且真弹窗会卡测试 | mock `_tk_show` + 平台分支断言 |
| `test_impydroid.py` store key 权限 | Windows chmod 是 no-op | 按 `os.name=="nt"` 跳过权限断言 |
| `test_impydroid.py` 平台路径用例 | 只对 Android/Termux 有意义 | Windows 上 return |

**结果**：装上 windows-curses 后五套测试 **959/959 全绿**（此前 900+ 因环境/平台误报）。

---

### 9.8 v5.18.3 修复（Windows TUI 排版错位 —— 真机反馈）

**症状**（用户截图）：面板内文字重叠、框线歪斜、部分内容溢出到相邻面板。

**根因**（真控制台 + Win32 `ReadConsoleOutputCharacterW` 像素级实测）：

> **PDCurses 的 `addstr(y, x)` 的 `x` 是「cell 索引」；而 conhost 渲染时
> `渲染列 = cell 索引 + 该 cell 前的宽字符数`（中文/全角占 2 列，但只占 1 cell）。**

代码布局一律用 `_disp_w`（**视觉列**：中文算 2）算坐标，于是：
- 盒子里含中文的内容行，`_box` 预先画在 cell `x+w-1` 的右竖线，渲染列会变成
  `x+w-1+k`（k=该行宽字符数）→ **溢出到相邻面板**（重叠）；
- 框顶 `├` 写在 `x+3+tw`（tw 用视觉宽）→ 渲染列右移 → **框顶错位**；
- 设置面板用 `f"{label:<18}"`（按字符数补齐）→ 中文标签列不齐。

**决定性实测数据**（新控制台里 `instr` 读回）：
```
写 '中文' 到 cell 0  → getyx.x = 2（PDCurses 认为占 2 cell）
但 conhost 渲染后右竖线视觉列 = 19（= cell 14 + 5 个宽字符）
```

**修法**（`curses_ui.py`）：

| 函数 | 改动 |
|---|---|
| `_vis_to_cell(win, y, vis_x)` | 新增。把「视觉列」换算成 PDCurses 的「cell」：`cell = vis_x - 该范围内的宽字符数`（`instr` 读回该行来数；注意 **PDCurses 的 `instr` 的 n 是字节数**，要 `×4+8` 才读得满）。ncurses 下原样返回 |
| `_wide_count(text)` | 新增。数宽字符个数 |
| `_safe_addstr(win, y, x, text, attr)` | `x` 仍是**视觉列**；PDCurses 下经 `_vis_to_cell` 换算成 cell 再写 |
| `_box_line` | PDCurses 下改为**整行重写** `│内容│`（内容按视觉宽 `_pad`）—— 整行一次写入，conhost 从左到右渲染，右竖线渲染列正好 `x+w-1`；且**必须先写 `w` 个空格清空整个 cell 区间**，抹掉 `_box` 预画的右竖线残留（否则残留穿透到面板外） |
| `_draw_topbar` / `_draw_input` | 同上：PDCurses 下整行重写（含左右竖线）+ 先清空 cell 区间 |
| `_draw_history` 累计行 | 从 `_safe_addstr(x+1, _pad(...))` 改为走 `_box_line`，避免中文撑宽 |
| `_dump_debug` | 读回时 `instr(n=w*4)`（n 是字节数），否则含中文/边框的行读不满 |

**验证方法**（可复现）：
1. `CREATE_NEW_CONSOLE` 起真控制台跑 `_draw_all`；
2. `IMGAGENT_TUI_DEBUG=...` 导出屏幕（已修 `instr` 字节数 bug）；
3. 按「视觉列 = cell + 宽字符数」重算每行竖线位置 —— 必须全部落在
   `[0, 68, 70, 119]`（120 列布局）；
4. 修复后**所有行严格对齐**（此前逐行漂移到 122/127）。

**测试影响**：`test_impydroid.py` 的 mock window 模拟的是 **ncurses 坐标语义**
（列冲突判定、`_pick_pages` 折行判定等）。这些用例里临时 `cui._IS_PDCURSES = False`
（用例结束恢复），避免 PDCurses 的"整行重写/清空"干扰布局判定。

**结果**：真控制台实测排版**完全对齐**；五套测试 **959/959 全绿**。

---

## 10. 一句话总结项目哲学

> **在手机上跑 = 处处受限。**
> 所以：只用标准库、离线能跑、成本可见、错误可读、
> 每个 bug 都留一条回归测试和一句"当初怎么坏的"注释。
