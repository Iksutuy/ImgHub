"""impydroid Curses TUI —— 分区边框界面（Termux 手机竖屏优先）。

布局（宽屏 ≥76 列）：
    ┌─ imgagent v5.1.0 ─ Termux ─ ⠋ 就绪 ─ 2026-09-18 14:36:27 ─┐  顶栏（动画+时间）
    ├─ 当前 ────────────────┬─ 历史 / 累计 ─────────────────────┤
    │ 文件 ...              │  1) ...001        $0.0048         │  左上=当前信息
    │ 尺寸 1024x1024        │ ▶2) ...002        $0.0114         │  右上=历史+累计
    │ 模型 gpt-image-2.5    │  ─────────────────────────        │
    │ 质量 low  画幅 1:1    │  累计 $0.0194   3 张              │
    ├─ 预览 ────────────────┼─ 消息 ───────────────────────────┤
    │                       │  14:36:27 生成完成                │  左=预览
    │   （ASCII 图）         │  14:36:10 轮询中…                │  右中=程序消息
    │                       │  14:35:58 参考图已上传            │
    ├───────────────────────┴──────────────────────────────────┤
    │ > 输入提示词，Enter 生成  e 切编辑  Esc 清空                │  输入框（独立）
    └──────────────────────────────────────────────────────────┘
     [g]生成 [e]编辑 [u]撤回 [空格]预览 [r]刷新 [m]菜单 [q]退出

窄屏（<76 列）：纵向堆叠（当前 / 预览 / 历史 / 消息），空间不足时按优先级丢弃面板。

要点：
  · 长任务（生成/编辑）跑在后台线程，主循环继续刷新动画与时钟
  · 需要 input() 的操作（上传/历史/设置）临时挂起 curses，跑完自动恢复
  · 所有面板都有边框；输入框常驻底部
"""
from __future__ import annotations
# ---------------------------------------------------------------------------
# 自举保护：本文件是包的内部模块。若在 Pydroid 里直接打开它点运行，
# 相对导入一定会失败（attempted relative import with no known parent package）。
# 这段代码把包目录补进 sys.path，然后**转交给真正的入口 main.py**。
# 正常被 import 时它什么都不做（零开销）。
if __name__ == "__main__" or __package__ in (None, ""):
    import os as _os
    import sys as _sys

    _HERE = _os.path.dirname(_os.path.abspath(__file__))
    _PARENT = _os.path.dirname(_HERE)
    if _PARENT not in _sys.path:
        _sys.path.insert(0, _PARENT)
    if __name__ == "__main__":
        _sys.stderr.write(
            "\n[!] %s 是包的内部模块，不是启动入口。\n"
            "    正确入口是同一目录下的 main.py：\n\n"
            "        %s\n\n"
            "    正在替你改用正确入口启动...\n\n"
            % (_os.path.basename(__file__), _os.path.join(_PARENT, "main.py")))
        try:
            from impydroid.app import cli as _cli
            _sys.exit(_cli())
        except SystemExit:
            raise
        except Exception as _e:
            _sys.stderr.write("启动失败：%s: %s\n" % (type(_e).__name__, _e))
            _sys.exit(2)
# ---------------------------------------------------------------------------

from . import settings
from .console import SYMBOLS_OK

import collections
import io
from collections import OrderedDict
import os
import sys
import threading
import time as _time

try:
    import curses as _c
    _HAS_CURSES = True
except ImportError:
    _HAS_CURSES = False
    # 无 curses 的平台（Windows 未装 windows-curses 时）。
    # 提供键码常量命名空间：让 _CSI_KEYS/_SS3_KEYS/_INPUT_KEYS 和输入逻辑
    # 仍能正常构建/测试（值采用 ncurses/PDCurses 的通用键码）；
    # 真正进 TUI 的 run()/_main_loop 等有 _HAS_CURSES guard，不会误调 curses。
    # 这是"没 curses 也能 import + 退 CLI"的基础 —— 也是本项目在无 curses
    # 平台曾 import 即崩（NameError: _c）的真实 bug 修法。
    import types as _types
    _c = _types.SimpleNamespace(
        error=OSError,                 # 无 curses 时永不真实抛（有 run guard）
        KEY_UP=259, KEY_DOWN=258, KEY_RIGHT=261, KEY_LEFT=260,
        KEY_HOME=262, KEY_END=360, KEY_PPAGE=339, KEY_NPAGE=338,
        KEY_DC=330, KEY_IC=331, KEY_ENTER=343, KEY_BACKSPACE=263,
        KEY_F1=265, KEY_F2=266, KEY_F3=267, KEY_F4=268,
        A_BOLD=0x200000,
        COLOR_BLACK=0, COLOR_RED=1, COLOR_GREEN=2, COLOR_YELLOW=3,
        COLOR_BLUE=4, COLOR_CYAN=5, COLOR_WHITE=7,
        # 绘制相关：无 curses 时不可用，这些仅防误引用崩溃
        has_colors=lambda: False, start_color=lambda: None,
        use_default_colors=lambda: None, init_pair=lambda *a: None,
        curs_set=lambda n: None, keypad=lambda w, b: None, napms=lambda ms: None,
        color_pair=lambda n: 0,
    )

# ⚠️ 关键：curses 的 get_wch() 依赖 locale 才能把多字节序列解码成 Unicode。
# 不设 locale 时（默认 "C"），中文会被当成孤立字节 → 输入乱码。
# 必须在 initscr() **之前**调用。setlocale 失败不致命（退回 ASCII）。
_LOCALE_OK = False
try:
    import locale as _locale
    for _loc in ("", "C.UTF-8", "en_US.UTF-8", "zh_CN.UTF-8"):
        try:
            _locale.setlocale(_locale.LC_ALL, _loc)
            _LOCALE_OK = True
            break
        except Exception:
            continue
except Exception:
    pass

# ======================================================================
#  PDCurses（windows-curses）兼容层
# ======================================================================
# Windows 上用 windows-curses（PDCurses 封装），它的键盘 API 语义与 ncurses
# **不同**，实测（真控制台 CREATE_NEW_CONSOLE）：
#
#    输入           getch()        get_wch()
#    ------------   ------------   ------------------
#    'a'            97 ✓           'a' ✓
#    KEY_UP(259)    259 ✓          'ă'  ✗ ← 返回 chr(259)！
#    KEY_F1(265)    265 ✓          'ĉ'  ✗
#    Ctrl+G         7 ✓            '\x07' ✓
#    中文 '中'      20013 ✓        '中' ✓
#    超时           -1 ✓           抛 error
#
# 后果：若照搬 ncurses 的 get_wch()，Windows 上按方向键会插入 'ă' 之类乱码，
# 且 KEY_* 永不命中（`_CTRL_ACTIONS`/导航全废）。修法：PDCurses 下改用
# getch()，再把 >255 的返回值（Unicode 码点或 KEY_* 键码）正确分类。
#
# 判据：PDCurses 没有 `curses.ncurses_version` 属性。
_IS_PDCURSES = _HAS_CURSES and not hasattr(_c, "ncurses_version")
_PD_KEY_CODES: frozenset = frozenset()
if _IS_PDCURSES:
    _PD_KEY_CODES = frozenset(
        getattr(_c, n) for n in dir(_c)
        if n.startswith("KEY_") and isinstance(getattr(_c, n), int))

# ======================================================================
#  常量
# ======================================================================
_REFRESH_MS = 140          # 主循环节拍（毫秒）—— 决定动画流畅度
_MIN_COLS = 30
_MIN_LINES = 14
_WIDE_COLS = 76            # 达到此宽度用双栏布局

_SPINNER_BRAILLE = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏"
_SPINNER_ASCII = "|/-\\"
_SPINNER_DOTS = "⠁⠂⠄⡀⢀⠠⠐⠈"          # 旋转点
_SPINNER_BOUNCE = "▁▂▃▄▅▆▇█▇▆▅▄▃▂"     # 弹跳条
_SPINNER_CLOCK = "🕐🕑🕒🕓🕔🕕🕖🕗🕘🕙🕚🕛"
# 三种动画轮换（每 3 秒换一种，避免长时间看同一个看腻）
_SPINNER_SETS = (_SPINNER_BRAILLE, _SPINNER_BOUNCE, _SPINNER_DOTS)


def _spinner_frame(idx: int) -> str:
    """按帧号取动画字符（自动在几套动画间轮换）。"""
    sets = _spinner_sets()
    period = 60                      # 每 60 帧（约 8.4s）换一套
    which = (idx // period) % len(sets)
    frames = sets[which]
    return frames[idx % len(frames)]


def _spinner_sets() -> tuple:
    want = os.environ.get("IMGAGENT_TUI_SPINNER", "").strip().lower()
    if want == "ascii":
        return (_SPINNER_ASCII, _SPINNER_ASCII, _SPINNER_ASCII)
    if want == "dots":
        return (_SPINNER_DOTS,)
    if want == "bounce":
        return (_SPINNER_BOUNCE,)
    if want == "braille":
        return (_SPINNER_BRAILLE,)
    return _SPINNER_SETS


def _progress_bar(done: int, total: int, width: int,
                  style: str = "block") -> str:
    """画进度条。style: block / line / dots。"""
    width = max(4, width)
    filled = int(round(width * max(0, min(1.0, done / max(1, total)))))
    if style == "line":
        return "━" * filled + "─" * (width - filled)
    if style == "dots":
        return "●" * filled + "○" * (width - filled)
    return "█" * filled + "░" * (width - filled)

# 颜色对
_C_TITLE, _C_FRAME, _C_LABEL = 1, 2, 3
_C_VALUE, _C_HL, _C_OK, _C_ERR, _C_DIM = 4, 5, 6, 7, 8

# 消息级别 → 颜色
_LEVEL_COLOR = {"info": _C_DIM, "ok": _C_OK, "err": _C_ERR,
                "warn": _C_LABEL, "out": _C_VALUE}


# ======================================================================
#  文本工具
# ======================================================================
def _disp_w(text: str) -> int:
    """终端显示宽度（CJK 记 2 列，制表符记 1）。"""
    import unicodedata
    total = 0
    for ch in text:
        if ch == "\t":
            total += 1
        elif unicodedata.east_asian_width(ch) in ("W", "F"):
            total += 2
        elif unicodedata.combining(ch):
            pass
        else:
            total += 1
    return total


def _trunc(text: str, limit: int) -> str:
    """按显示宽度截断，超出部分用 … 收尾。"""
    if _disp_w(text) <= limit:
        return text
    out, used = [], 0
    for ch in text:
        w = _disp_w(ch)
        if used + w > limit - 1:
            break
        out.append(ch)
        used += w
    return "".join(out) + "…"


def _pad(text: str, width: int) -> str:
    """右侧补空格到指定显示宽度。"""
    return text + " " * max(0, width - _disp_w(text))


def _wide_count(text: str) -> int:
    """text 里宽字符（CJK/全角）的个数。

    PDCurses 下用来把「视觉列」换算成「cell 坐标」：PDCurses 的 addstr 用
    字符索引定位（宽字符只占 1 cell），而 conhost 渲染宽字符占 2 列，
    两者相差的正是宽字符个数。
    """
    import unicodedata
    return sum(1 for ch in text
               if unicodedata.east_asian_width(ch) in ("W", "F"))


def _vis_to_cell(win, y: int, vis_x: int, *, from_cell: int = 0) -> int:
    """把「视觉列 vis_x」换算成 PDCurses 的「cell 坐标」。

    ⚠️ 真控制台实测的确凿结论：`渲染列 = cell 索引 + 该 cell 前的宽字符数`。
    所以要让某内容渲染在视觉列 v，必须写到 cell `v - (从起点到该处的宽字符数)`。
    （ncurses 下坐标本身就是视觉列，原样返回。）

    本函数用**目标行当前内容**推算：`instr` 读回从 `from_cell` 起的一段，
    数其中有几个宽字符，`cell = vis_x - 宽字符数`。

    `from_cell`：只看从该 cell 起的内容。同一行上有多个面板时（左右分栏），
    右面板必须传自己的起始 cell，否则会把左面板的中文也算进来 → 少算 →
    右面板整体左移/溢出。

    ⚠️ `instr` 的 n 在 PDCurses 是**字节数**（UTF-8 一个汉字/边框符 = 3 字节），
    要按 `(字符数 * 4 + 8)` 传才不会读不满。
    """
    if not _IS_PDCURSES:
        return vis_x
    span = max(0, vis_x - from_cell)
    try:
        raw = win.instr(y, from_cell, span * 4 + 8)
    except Exception:                                    # noqa: BLE001
        return vis_x
    if isinstance(raw, bytes):
        raw = raw.decode("utf-8", "replace")
    return max(from_cell, vis_x - _wide_count(raw[:span]))


def _safe_addstr(win, y: int, x: int, text: str, attr: int = 0,
                 *, base_cell: int = 0) -> None:
    """安全写串（越界/编码问题静默跳过）。

    `x` 是**视觉列**（与 ncurses 语义一致）。PDCurses 下换算成 cell ——
    这是修 Windows「文字重叠 / 框线歪斜」的关键。

    `base_cell`：同一行有多个面板时，右面板要传自己的起始 cell，
    换算时才不会把左面板的宽字符算进来。

    ⚠️ 关键：**绝不写屏幕右下角那一格**（y=h-1, x=w-1）。
    curses 在写右下角时会自动滚动一整屏，导致整个界面往上跳一行。
    所以最后一行的可用宽度要减 1。
    """
    try:
        h, w = win.getmaxyx()
        if not text or y < 0 or y >= h or x < 0 or x >= w:
            return
        cx = _vis_to_cell(win, y, x, from_cell=base_cell)
        if cx >= w:
            return
        limit = w - cx
        if y == h - 1:
            limit -= 1              # 保护右下角，避免滚动
        if limit <= 0:
            return
        win.addnstr(y, cx, text, limit, attr)
    except Exception:
        pass


# 边框风格（环境变量 IMGAGENT_TUI_BORDER 可切：round / square / double / ascii）
_BORDER_STYLES = {
    "round":  ("╭", "─", "╮", "│", "╰", "╯"),
    "square": ("┌", "─", "┐", "│", "└", "┘"),
    "double": ("╔", "═", "╗", "║", "╚", "╝"),
    "heavy":  ("┏", "━", "┓", "┃", "┗", "┛"),
    "ascii":  ("+", "-", "+", "|", "+", "+"),
}


def _border_style() -> tuple[str, ...]:
    want = (os.environ.get("IMGAGENT_TUI_BORDER") or "round").strip().lower()
    style = _BORDER_STYLES.get(want, _BORDER_STYLES["round"])
    if not SYMBOLS_OK and style is not _BORDER_STYLES["ascii"]:
        return _BORDER_STYLES["ascii"]      # 终端不支持 Unicode 时降级
    return style


def _box(stdscr, y: int, x: int, h: int, w: int, title: str = "",
         attr: int = 0, title_attr: int = 0, focused: bool = False) -> None:
    """画带标题的边框盒（h、w 含边框）。

    美化点：
      · 圆角（╭╮╰╯）比直角柔和，手机上看更舒服
      · 标题两侧留白 + 独立颜色，形成"嵌在边框里"的观感
      · focused 时用亮色 + 加粗，区分当前操作的面板
    可用 IMGAGENT_TUI_BORDER 切换风格（round/square/double/heavy/ascii）。
    """
    if h < 2 or w < 4:
        return
    if focused:
        attr |= _c.A_BOLD
    tl, hz, tr, vt, bl, br = _border_style()

    # 顶边：标题嵌进边框，左右用"接线符"分隔，视觉上像"标签贴在框上"。
    #
    # 布局（总宽必须正好 = w）：
    #   ╭─┤ 标题 ├────────────────╮
    #   └1┘└2┘└tw ┘└──── rest ────┘
    #   1(左角) + 1(横) + 2(接线) + tw(标题) + 2(接线) + rest(横) + 1(右角) = w
    if title:
        # ╭ ─ ┤ ␣标题␣ ├ ──rest── ╮
        #  1  1  1    tw    1   rest   1   =>  rest = w - 5 - tw
        t = _trunc(title, max(1, w - 10))
        label = f" {t} "
        tw = _disp_w(label)
        rest = max(0, w - 5 - tw)
        _safe_addstr(stdscr, y, x, tl + hz + "┤", attr)
        _safe_addstr(stdscr, y, x + 3, label, title_attr or attr)
        _safe_addstr(stdscr, y, x + 3 + tw, "├" + hz * rest + tr, attr)
    else:
        _safe_addstr(stdscr, y, x, tl + hz * (w - 2) + tr, attr)

    for i in range(1, h - 1):
        _safe_addstr(stdscr, y + i, x, vt, attr)
        _safe_addstr(stdscr, y + i, x + w - 1, vt, attr)
    _safe_addstr(stdscr, y + h - 1, x, bl + hz * (w - 2) + br, attr)


def _box_line(stdscr, y: int, x: int, w: int, row: int, text: str,
              attr: int = 0) -> None:
    """在盒内第 row 行写内容（左内边距 1，自动裁剪）。

    ⚠️ PDCurses 坐标语义（真控制台实测）：`渲染列 = cell 索引 + 该 cell 前的
    宽字符数`；`addstr(y, x)` 的 x 是 **cell 索引**，而代码布局用视觉列。

    正确做法：写 `│ + 内容(按**视觉宽** pad 到 w-2) + │` 一行**一次**写入，
    交给 `_safe_addstr` 把视觉列 x 换算成 cell。

    为什么按视觉宽（`_pad`）而不是按 cell 数：本行渲染宽必须正好 = w，
    才不会溢出到相邻面板；含 k 个宽字符时字符数 = w-k，其右竖线落在
    cell `x+w-1-k`，渲染列正好 `x+w-1` —— 与 `_box`（同样经换算）画的
    右竖线**落在同一 cell**，不会错位、不会残留穿透。
    """
    vt = _border_style()[3]
    if _IS_PDCURSES:
        # PDCurses：整行重写 `│内容│`，右竖线才能落在正确的 cell（渲染列
        # 正好 x+w-1）。先清空整个 cell 区间，抹掉 _box 预画的右竖线残留。
        line = vt + _pad(_trunc(text, max(1, w - 3)), max(0, w - 2)) + vt
        _safe_addstr(stdscr, y + 1 + row, x, " " * max(0, w), attr)
        _safe_addstr(stdscr, y + 1 + row, x, line, attr)
    else:
        # ncurses：坐标本来就是视觉列，保持原写法（内容写在左内边距处）
        _safe_addstr(stdscr, y + 1 + row, x + 1,
                     _pad(_trunc(text, w - 3), w - 2), attr)


# ======================================================================
#  消息缓冲
# ======================================================================
_LOG: "collections.deque[tuple[str, str, str]]" = collections.deque(maxlen=400)


def log(text: str, level: str = "info") -> None:
    """记一条程序消息（会显示在「消息」面板）。"""
    ts = _time.strftime("%H:%M:%S")
    for line in str(text).splitlines():
        line = line.rstrip()
        if line:
            _LOG.append((ts, level, line))


class _StdoutToLog(io.TextIOBase):
    """把 print() 输出重定向进消息面板（用于复用 ui.* 里已有的打印）。"""

    def write(self, s: str) -> int:
        for line in s.splitlines():
            line = line.strip()
            if line:
                log(line, "out")
        return len(s)

    def flush(self) -> None:
        pass


# ======================================================================
#  按键读取（UTF-8 安全）
# ======================================================================
_KEYLOG: list[str] = []


# 方向键等转义序列的尾字符 → curses 键码
# 用于 timeout（非阻塞）模式下**手工组装**被拆散的转义序列
#
# ⚠️ 顶层构建：Windows 无内置 curses（可选 windows-curses）时 _c 是上面的
# SimpleNamespace 占位（含这些键码的真实值），所以这里总能安全构建 ——
# 无 curses 平台 import 即崩（NameError: _c）是历史上真实 bug，已修。
_CSI_KEYS = {
    "A": _c.KEY_UP, "B": _c.KEY_DOWN, "C": _c.KEY_RIGHT, "D": _c.KEY_LEFT,
    "H": _c.KEY_HOME, "F": _c.KEY_END,
    "5~": _c.KEY_PPAGE, "6~": _c.KEY_NPAGE,
    "1~": _c.KEY_HOME, "4~": _c.KEY_END, "3~": _c.KEY_DC,
    "2~": _c.KEY_IC,
}
# SS3（ESC O x）形式的键（某些终端/模式用这个）
_SS3_KEYS = {
    "A": _c.KEY_UP, "B": _c.KEY_DOWN, "C": _c.KEY_RIGHT, "D": _c.KEY_LEFT,
    "H": _c.KEY_HOME, "F": _c.KEY_END,
    "P": _c.KEY_F1, "Q": _c.KEY_F2, "R": _c.KEY_F3, "S": _c.KEY_F4,
}


def _read_burst(win) -> str:
    """读一小段后续按键（用于拼装转义序列）。

    短暂切到非阻塞（0 超时）尽可能多读几个字符，读完立刻恢复。
    这样既不会为普通按键引入延滞，又能把被 timeout 拆散的
    CSI 序列（ESC [ A）拼回完整形态。
    """
    got = ""
    for _ in range(6):                       # 最多 6 个尾字符
        try:
            win.timeout(15)                  # 15ms：够本机序列到达，人不敏感
            ch = win.get_wch()
        except _c.error:
            break                            # 超时 = 序列结束
        except Exception:
            break
        if isinstance(ch, str):
            got += ch
        else:
            break
        # 已经是完整序列（以 ~ 或字母结尾）就停
        if got and (got.endswith("~") or got[-1].isalpha()):
            break
    try:
        win.timeout(_REFRESH_MS)             # 恢复主循环节拍
    except Exception:
        pass
    return got


def _read_key(win):
    """读一个按键（UTF-8 安全 + 转义序列组装 + PDCurses 兼容）。

    三个必须处理的坑：

    1. **ncurses：必须用 get_wch() 而不是 getch()**。
       getch() 返回单个字节（0-255），中文 UTF-8 是 3 字节会被拆散
       → 输入框里出现乱码。get_wch() 返回完整 Unicode 字符。

    2. **timeout() 模式下 curses 不会组装转义序列**。
       实测：设了 stdscr.timeout(140) 后，按 ↑ 会依次收到
       `'\x1b'`、`'['`、`'A'` 三个**独立**字符，而不是 int KEY_UP。
       后果：方向键被当成字符插进输入框（表现为乱码）。
       修法：收到 ESC 时主动再读几个字符，命中 CSI/SS3 表就还原成键码。

    3. **PDCurses（windows-curses）：反而必须用 getch()**。
       实测 get_wch() 对 KEY_UP 返回 `chr(259)`（即 'ă'）而不是 int 259，
       会把方向键变成乱码字符；而 getch() 正确返回 259。
       中文则用 getch() 得到码点（如 20013）再转 str。详见 _IS_PDCURSES 注释。

    返回：
      - str：普通字符（可能是中文）
      - int：特殊键（方向键、功能键等）
      - None：无输入（超时）
    """
    if _IS_PDCURSES:
        return _read_key_pdcurses(win)

    try:
        got = win.get_wch()
    except _c.error:
        return None                          # 超时
    except KeyboardInterrupt:
        return 3
    except Exception:
        return None

    # ---- 转义序列组装 ----
    if got == "\x1b" or got == 27:
        tail = _read_burst(win)
        if not tail:
            _KEYLOG.append("'\\x1b'")
            return "\x1b"                    # 单独的 Esc 键
        if tail[0] == "[":
            code = _CSI_KEYS.get(tail[1:])
            if code is not None:
                _KEYLOG.append(f"CSI {tail!r} → {code}")
                return code
            _KEYLOG.append(f"CSI? {tail!r}")
            return None                      # 不认识的 CSI，吞掉
        if tail[0] == "O":
            code = _SS3_KEYS.get(tail[1:2])
            if code is not None:
                _KEYLOG.append(f"SS3 {tail!r} → {code}")
                return code
            _KEYLOG.append(f"SS3? {tail!r}")
            return None
        # 不是 CSI/SS3：把 ESC 当独立按键，其余字符"还"回输入
        # （不做 pushback，直接返回 ESC —— 极少见，够用）
        _KEYLOG.append(f"ESC+{tail!r}")
        return "\x1b"

    _KEYLOG.append(repr(got))
    return got


def _read_key_pdcurses(win):
    """PDCurses（windows-curses）下的按键读取。

    为什么单独一条路：PDCurses 的 get_wch() 把 KEY_UP(259) 变成 chr(259)='ă'，
    只有 getch() 能拿到正确的键码。这里用 getch() + 分类：

      getch() 返回        含义                     → 归一成
      ---------------     ---------------------    ----------------
      -1                  超时 / 无输入            → None
      0..31, 127          控制字符 / DEL           → str（Ctrl 组合键）
      32..255             ASCII 可打印             → str
      KEY_* 码（257..）  方向键/功能键（PDCurses  → int（特殊键）
                         的键码范围，实测 257-548）
      >255 且非 KEY_*     Unicode 码点（中文/emoji）→ str（chr(code)）

    Escape/Ctrl+C 等由 getch 直接给控制码，无需转义序列组装
    （PDCurses 自己会把方向键组合成 KEY_*，不会拆成 ESC [ A）。
    """
    try:
        code = win.getch()
    except _c.error:
        return None                          # 超时
    except KeyboardInterrupt:
        return 3
    except Exception:
        return None
    if code == -1 or code == _c.ERR:
        return None                          # 超时
    if code in _PD_KEY_CODES:
        _KEYLOG.append(f"PD KEY {code}")
        return code                          # 特殊键：保持 int
    # 控制字符 / DEL → str（与 ncurses 路径一致，_key_code 再归一）
    if 0 <= code < 0x20 or code == 0x7f:
        _KEYLOG.append(f"PD ctrl {code!r}")
        return chr(code)
    # 其它 >255：Unicode 码点（中文/emoji 等）
    try:
        ch = chr(code)
    except (ValueError, OverflowError):
        return None
    _KEYLOG.append(f"PD ch {ch!r}")
    return ch


def _key_is(got, *names: str) -> bool:
    """判断按键是否等于某个命名键（字母/数字键）。"""
    if isinstance(got, str):
        return got in names
    return False


def _key_code(got):
    """把按键归一成 int 便于比较。

    三种输入形态（实测 get_wch 的行为）：
      · **int**  —— curses 特殊键（KEY_LEFT=260、KEY_ENTER=343…）
      · **str 单控制字符** —— Ctrl 组合键（实测返回 '\x07' 而非 7！）
      · **str 普通字符** —— 字母/数字/中文（Ctrl 键要能识别，普通字符返回 None）

    所以：控制字符（0x00-0x1f）和 DEL(0x7f) 都要转成 int，
    否则 _CTRL_ACTIONS 表永远命不中（这是实现 Ctrl 键功能的关键）。
    """
    if isinstance(got, int):
        return got
    if isinstance(got, str) and len(got) == 1:
        o = ord(got)
        if o < 0x20 or o == 0x7f:
            return o                          # Ctrl 组合 / 退格
    return None


# ======================================================================
#  输入行编辑器
# ======================================================================
class _InputLine:
    """常驻底部的单行输入框。支持 ← → 光标、退格、Esc 清空、Enter 提交。"""

    def __init__(self) -> None:
        self.buf: list[str] = []
        self.cur = 0

    @property
    def text(self) -> str:
        return "".join(self.buf)

    def clear(self) -> None:
        self.buf.clear()
        self.cur = 0

    def set_text(self, text: str) -> None:
        """整体替换内容，光标放到末尾（翻历史/恢复草稿用）。"""
        self.buf = list(text or "")
        self.cur = len(self.buf)

    # ---- 各按键动作（返回 'submit'/'cancel'/None，或 None 表示已处理）----
    def _do_clear(self) -> str:
        self.clear()
        return "cancel"

    def _do_clear_silent(self) -> None:
        self.clear()

    def _do_backspace(self) -> None:
        if self.cur > 0:
            self.buf.pop(self.cur - 1)
            self.cur -= 1

    def _do_delete(self) -> None:
        if self.cur < len(self.buf):
            self.buf.pop(self.cur)

    def _move(self, delta: int) -> None:
        self.cur = max(0, min(len(self.buf), self.cur + delta))

    def _to_start(self) -> None:
        self.cur = 0

    def _to_end(self) -> None:
        self.cur = len(self.buf)

    def _delete_word(self) -> None:
        """Ctrl+W：先删尾部空格，再删到上一个空格。"""
        while self.cur > 0 and self.buf[self.cur - 1] == " ":
            self.buf.pop(self.cur - 1)
            self.cur -= 1
        while self.cur > 0 and self.buf[self.cur - 1] != " ":
            self.buf.pop(self.cur - 1)
            self.cur -= 1

    def _insert(self, text: str) -> None:
        """插入文本（多字符逐字插入，支持中文与粘贴）。"""
        for c in text:
            self.buf.insert(self.cur, c)
            self.cur += 1

    def handle(self, ch) -> str | None:
        """处理按键（ch 可能是 str 字符或 int 键码）。

        返回 'submit' / 'cancel' / None。
        用**表驱动**而不是长 if-elif 链：加新快捷键只需往 _KEYS 里加一行，
        `handle` 本身保持简单（之前是 14 个分支、圈复杂度 28）。

        注意 ch 有两种形态（这正是当初踩过的坑）：
          · str —— get_wch() 对普通字符返回完整 Unicode（中文也是一个 str）
          · int —— 特殊键（KEY_LEFT 等）或旧终端上的单字节
        所以同一个动作通常要注册两个 key（"" 和 27 这种）。
        """
        action = _INPUT_KEYS.get(ch)
        if action is not None:
            return action(self)
        # 普通字符：str（get_wch 路径）
        if isinstance(ch, str):
            if ch.isprintable():
                self._insert(ch)
            return None
        # 兜底：int 可打印 —— 某些终端 / locale 下 get_wch 会退化成 int。
        #
        # ⚠️ 上限必须是 127（ASCII 可打印）而不是 0x110000：
        #    curses 的所有特殊键码都在 256 以上（KEY_F1=265, KEY_DC=330,
        #    KEY_ENTER=343 …）。用 0x110000 当上限会把 KEY_F1 当成
        #    chr(265) 插进输入框（旧代码的真实 bug，已修）。
        #    非 ASCII 字符走上面的 str 分支（get_wch 正常时不会退化到 int）。
        if isinstance(ch, int) and 32 <= ch < 127:
            self._insert(chr(ch))
            return None
        return None


# 输入框按键表：key → (输入行实例) → 'submit' / 'cancel' / None
# 同一个动作往往要注册 str 和 int 两种形态（见 handle 的 docstring）。
# 无 curses 时 _c 是占位命名空间（含真实键码），所以这里总能安全构建。
_INPUT_KEYS: dict = {
    # Esc / Ctrl+[ → 清空并取消
    "\x1b": _InputLine._do_clear, 27: _InputLine._do_clear,
        # Enter（不同终端可能给 \n、\r、10、13 或 KEY_ENTER）
        "\n": lambda self: "submit", "\r": lambda self: "submit",
        10: lambda self: "submit", 13: lambda self: "submit",
        _c.KEY_ENTER: lambda self: "submit",
        # Backspace（DEL / BS / KEY_BACKSPACE 三种形态）
        "\x7f": _InputLine._do_backspace, "\x08": _InputLine._do_backspace,
        127: _InputLine._do_backspace, 8: _InputLine._do_backspace,
        _c.KEY_BACKSPACE: _InputLine._do_backspace,
        # 光标移动
        _c.KEY_LEFT: lambda self: self._move(-1),
        _c.KEY_RIGHT: lambda self: self._move(+1),
        _c.KEY_HOME: _InputLine._to_start, "\x01": _InputLine._to_start,
        _c.KEY_END: _InputLine._to_end, "\x05": _InputLine._to_end,
        # 删除
        _c.KEY_DC: _InputLine._do_delete,
        # 行编辑快捷键（readline 习惯）
        "\x15": _InputLine._do_clear_silent,     # Ctrl+U 清空整行
        "\x17": _InputLine._delete_word,         # Ctrl+W 删一个词
    }


# ======================================================================
#  主入口
# ======================================================================
def _is_interactive() -> bool:
    return sys.stdin.isatty() and sys.stdout.isatty()


def has_support() -> bool:
    """当前环境能否跑 curses TUI。"""
    if not _HAS_CURSES:
        return False
    return _is_interactive()


def run(sess, *, key=None, offline=False) -> int:
    """启动 TUI。返回 0。不可用时退回命令行菜单（返回 0，由调用方决定）。"""
    if not _HAS_CURSES:
        print("  · curses 模块不可用，退回命令行菜单")
        return 0
    if not _is_interactive():
        print("  · 非交互式终端，退回命令行菜单")
        return 0
    try:
        _c.wrapper(_main_loop, sess, key, offline)
    except _c.error as e:
        print(f"  · curses 终端配置失败（{e}），退回命令行菜单")
    except Exception as e:
        print(f"  [X] TUI 失败：{e}，退回命令行菜单")
        import traceback
        traceback.print_exc(file=sys.stderr)
    return 0


# ======================================================================
#  主循环
# ======================================================================
def _main_loop(stdscr, sess, key, offline):
    """TUI 主循环：刷新 → 读键 → 分发。

    按键分发拆成三个职责清晰的子函数（原来全挤在这一个 173 行的函数里）：
      _handle_nav_key    方向/翻页/Home/End（历史列表导航）
      _handle_action_key 功能键（生成/编辑/撤回/设置/退出…）
      _handle_input_key  输入框按键
    """
    _init_colors()
    _c.curs_set(0)
    stdscr.keypad(True)
    stdscr.timeout(_REFRESH_MS)

    st = _new_state()
    inp = _InputLine()

    log("TUI 已启动", "ok")
    log(f"平台 {_plat()} · 模型 {sess.model} · 质量 {sess.quality}", "info")
    if sess.current:
        log(f"当前图 {sess.current.file}", "info")

    while True:
        h, w = stdscr.getmaxyx()
        if h < _MIN_LINES or w < _MIN_COLS:
            stdscr.erase()
            _safe_addstr(stdscr, 0, 0, f"终端太小 {w}x{h}，请放大")
            stdscr.refresh()
            _c.napms(400)
            continue

        st["spin"] = (st["spin"] + 1) % 60

        # 失败恢复：把上次提交的提示词放回输入框（只做一次）
        pending = st.pop("_restore_text", "")
        if pending:
            inp.set_text(pending)
            st["inp_hist_idx"] = -1        # 游标归位

        try:
            _draw_all(stdscr, sess, inp, st, offline)
        except Exception as e:                               # noqa: BLE001
            log(f"渲染异常：{type(e).__name__} {e}", "err")
        stdscr.refresh()

        ch = _read_key(stdscr)
        if ch is None:
            continue

        # ---- 三级分发：返回 True 表示"这一帧处理完了" ----
        if _handle_action_key(stdscr, sess, key, offline, st, inp, ch):
            if st.get("_quit"):
                break
            continue
        if _handle_prompt_history(inp, st, ch):
            continue
        if _handle_nav_key(sess, st, ch):
            continue
        if _handle_input_key(stdscr, sess, key, offline, st, inp, ch):
            continue

    # 调试：导出按键序列（IMGAGENT_TUI_KEYLOG=/path）
    _kp = os.environ.get("IMGAGENT_TUI_KEYLOG", "").strip()
    if _kp:
        try:
            with open(_kp, "w", encoding="utf-8") as f:
                f.write("\n".join(_KEYLOG))
        except OSError:
            pass
    return 0


def _new_state() -> dict:
    """TUI 会话状态（替代散落的局部变量）。"""
    return {
        "mode": "gen",          # gen | edit —— 输入框当前语义
        "hist": 0,              # 历史选中索引
        "scroll": 0,
        "busy": "",             # 非空 = 后台任务进行中（驱动动画）
        "spin": 0,
        "t0": _time.time(),
        "refs": [],              # 参考图文件名列表（Ctrl+Y 增删，最多 REF_MAX）
        "ref_sel": 0,            # 参考图列表里的选中项（Home/End 移动，Ctrl+K 删）
        "inp_hist_idx": -1,     # 提示词历史游标（-1 = 当前输入）
        "inp_draft": "",        # 翻历史前暂存的草稿
        "last_submit": "",      # 最近一次提交的内容（失败时用于恢复）
    }


def _init_colors() -> None:
    """初始化配色（失败时退回单色，不影响功能）。

    ⚠️ PDCurses（windows-curses）不支持 `-1`（终端默认色）作 init_pair 背景：
    `use_default_colors()` 会"成功"返回，但随后的 `init_pair(fg, -1)` 抛 error。
    所以 PDCurses 下直接用 COLOR_BLACK，避免每个颜色对都先失败一次。
    """
    if _c.has_colors():
        _c.start_color()
        if _IS_PDCURSES:
            bg = _c.COLOR_BLACK
        else:
            try:
                _c.use_default_colors()
                bg = -1
            except Exception:                                # noqa: BLE001
                bg = _c.COLOR_BLACK
        for idx, fg in ((_C_TITLE, _c.COLOR_CYAN), (_C_FRAME, _c.COLOR_BLUE),
                        (_C_LABEL, _c.COLOR_YELLOW), (_C_VALUE, _c.COLOR_WHITE),
                        (_C_HL, _c.COLOR_BLACK), (_C_OK, _c.COLOR_GREEN),
                        (_C_ERR, _c.COLOR_RED), (_C_DIM, _c.COLOR_CYAN)):
            try:
                _c.init_pair(idx, fg, bg)
            except Exception:                                # noqa: BLE001
                _c.init_pair(idx, fg, _c.COLOR_BLACK)
        try:
            _c.init_pair(_C_HL, _c.COLOR_BLACK, _c.COLOR_CYAN)
        except Exception:                                    # noqa: BLE001
            pass
    else:
        for i in range(1, 9):
            try:
                _c.init_pair(i, _c.COLOR_WHITE, -1)
            except Exception:                                # noqa: BLE001
                try:
                    _c.init_pair(i, _c.COLOR_WHITE, _c.COLOR_BLACK)
                except Exception:                            # noqa: BLE001
                    pass


# Ctrl 组合键 → 动作名。
# 控制字符编码：Ctrl+A=0x01 … Ctrl+Z=0x1a。
# ⚠️ 刻意避开的（输入框已经占用）：
#   0x01 Ctrl+A = Home      0x05 Ctrl+E = End
#   0x08 Ctrl+H = 退格      0x0a Ctrl+J / 0x0d Ctrl+M = 回车
#   0x15 Ctrl+U = 清空行    0x17 Ctrl+W = 删词
#   0x1b Ctrl+[ = Esc
# 所以功能键选了下面这些"空闲"的组合。
_CTRL_ACTIONS: dict[int, str] = {
    0x07: "gen",        # Ctrl+G  生成
    0x04: "edit",       # Ctrl+D  编辑（D=改动）
    0x12: "undo",       # Ctrl+R  撤回（R=rewind）
    0x10: "quality",    # Ctrl+P  质量
    0x13: "settings",   # Ctrl+S  设置
    0x09: "data_dir",   # Ctrl+I  目录
    0x0e: "env",        # Ctrl+N  环境
    0x18: "script_imgs",  # Ctrl+X  脚本目录图
    0x02: "history",    # Ctrl+B  历史（B=back）
    0x06: "upload",     # Ctrl+F  上传（F=file）
    0x14: "help",       # Ctrl+T  帮助（T=Tips）
    0x0f: "refresh",    # Ctrl+O  刷新（O=refresh）
    0x19: "toggle_ref",  # Ctrl+Y  把选中的历史图加入/移出参考图（Y=加入）
    0x0b: "del_ref",     # Ctrl+K  删除选中的参考图（K=kill）
    0x17: "aspect",      # Ctrl+W  循环切换画幅（W=width）
    0x1a: "model",       # Ctrl+Z  循环切换模型（flare ↔ sunburst）
    0x05: "resolution",  # Ctrl+E  循环切换分辨率（1k/2k/4k）
    0x15: "fmt",         # Ctrl+U  循环切换输出格式（png/jpeg/webp）
    0x01: "batch_n",     # Ctrl+A  循环切换批量张数（1-4，A=Amount）
    0x0c: "preview",    # Ctrl+L  预览
    0x11: "quit",       # Ctrl+Q  退出
}


def _ctrl_action(code: int) -> str | None:
    """控制字符码 → 动作名。"""
    return _CTRL_ACTIONS.get(code)


def _handle_action_key(stdscr, sess, key, offline, st, inp, ch) -> bool:
    """功能键分发（**全部 Ctrl 组合**，普通字母/数字让给输入框）。

    为什么改成 Ctrl：早期用裸字母（g/e/u/r/s/q/3…）当功能键，
    导致写提示词时**打不出这些字符** —— 按 q 就退出、按 3 就上传。

    实现：`_CTRL_ACTIONS`（控制字符码 → 动作名）查表 + 下面的处理器分支。
    少数动作（quit/gen/edit/refresh）需要特殊前置判断，单独处理；
    其余都是"直接调用某个 _act_* 函数"，语义一目了然。
    """
    code = _key_code(ch)

    # Ctrl+C：无条件退出（不给确认，符合终端习惯）
    if code == 3:
        st["_quit"] = True
        return True

    if code is None:
        return False
    action = _ctrl_action(code)
    if action is None:
        return False

    # ---- 需要前置判断的动作 ----
    if action == "quit":
        if inp.text:
            log("输入框非空，先按 Esc 清空再退出", "warn")
        elif _confirm_quit(stdscr, sess):
            st["_quit"] = True
        return True
    if action == "gen":
        st["mode"] = "gen"
        log("输入模式 → 生成（Ctrl+G）", "info")
        return True
    if action == "edit":
        if not sess.current:
            log("还没有可编辑的图，请先生成或上传", "warn")
            return True
        st["mode"] = "edit"
        log("输入模式 → 编辑（Ctrl+D）", "info")
        return True
    if action == "refresh":
        sess.load()
        st["hist"] = min(st["hist"], max(0, len(sess.items) - 1))
        clear_preview_cache()                # 让预览重算
        log("已刷新历史（Ctrl+O）", "ok")
        _flash("已刷新")
        return True

    # ---- 其余动作：统一的"调用表格" ----
    # 元组含义：(无参调用, 需要 stdscr)
    table = {
        "undo":        (lambda: _act_undo(sess, st), False),
        "preview":     (lambda: _act_preview(stdscr, sess, st), True),
        "quality":     (lambda: _act_cycle_quality(sess), False),
        "history":     (lambda: _act_history(stdscr, sess), True),
        "upload":      (lambda: _act_upload(stdscr, sess, False), True),
        "script_imgs": (lambda: _act_upload(stdscr, sess, True), True),
        "settings":    (lambda: _act_settings(stdscr, sess, st), True),
        "data_dir":    (lambda: _act_data_dir(stdscr, sess), True),
        "env":         (lambda: _act_env(stdscr, sess), True),
        "help":        (lambda: _help_overlay(stdscr, sess), True),
        "toggle_ref":  (lambda: _ref_add_or_remove(stdscr, sess, st), True),
        "del_ref":     (lambda: _ref_delete_selected(st), False),
        "aspect":      (lambda: _act_cycle_aspect(sess, st), False),
        "model":       (lambda: _act_cycle_model(sess, st), False),
        "resolution":  (lambda: _act_cycle_resolution(sess, st), False),
        "fmt":         (lambda: _act_cycle_fmt(sess, st), False),
        "batch_n":     (lambda: _act_cycle_batch_n(sess, st), False),
    }
    entry = table.get(action)
    if entry is None:
        return False
    fn, _ = entry
    fn()
    return True


# 提示词历史按键：**↑↓ 翻历史**（像终端翻命令历史，最符合直觉）。
#
# 键位历史：
#   v5.10.0 用 F3/F4 —— 但真机反馈"软键盘根本按不出 F 键"，
#   而 ↑↓ 是最自然的"翻历史"手势。所以改成 ↑↓。
#   代价：原来 ↑↓ 用于"选择历史图片"，改为 PgUp/PgDn。
#
# F3/F4 保留为别名（外接键盘用户可能习惯），PgUp/PgDn 不再是历史键。
_HIST_KEYS = None        # 延迟初始化（依赖 _c）


def _hist_keys() -> dict:
    global _HIST_KEYS
    if _HIST_KEYS is None:
        _HIST_KEYS = {}
        for attr, tag in (("KEY_UP", "prev"), ("KEY_DOWN", "next"),
                          # 兼容别名（外接键盘）
                          ("KEY_F3", "prev"), ("KEY_F4", "next"),
                          ("KEY_F5", "prev"), ("KEY_F6", "next")):
            code = getattr(_c, attr, None)
            if code is not None:
                # KEY_UP/KEY_DOWN 优先级最高（后写的会覆盖前面的）
                _HIST_KEYS[code] = tag
    return _HIST_KEYS


def _handle_prompt_history(inp: _InputLine, st: dict, ch) -> bool:
    """F3/F4（F5/F6 也行）翻提示词历史。返回 True = 已处理。

    行为（模仿 shell）：
      · F3 → 上一条（更早的）；到顶了就停住
      · F4 → 下一条（更新的）；越过最新一条则回到"当前输入"
      · 翻开历史后**保留光标在末尾**，回车即用
    """
    code = _key_code(ch)
    if code is None:
        return False
    tag = _hist_keys().get(code)
    if tag is None:
        return False

    from .store import load_prompt_history
    hist = load_prompt_history(kind=st.get("mode") or "gen")
    if not hist:
        _flash("还没有历史提示词（提交过就会有）")
        return True

    idx = st.get("inp_hist_idx", -1)
    # 第一次翻历史时，把当前输入框的内容存成草稿 ——
    # 这样 F4 越过最新一条时能回到"你原本正在打的字"，而不是回到空框。
    if idx < 0 and tag == "prev":
        st["inp_draft"] = inp.text
    if tag == "prev":
        idx = min(len(hist) - 1, idx + 1)
    else:
        idx -= 1
        if idx < -1:
            idx = -1

    st["inp_hist_idx"] = idx
    if idx < 0:
        # 回到"本次会话刚开始输入的内容"（如果没存过就清空）
        inp.set_text(st.get("inp_draft", ""))
    else:
        inp.set_text(hist[idx])
    _flash(f"提示词历史 {idx + 1}/{len(hist)}（↓ 可回到草稿）"
               if idx >= 0 else "回到当前草稿")
    return True


def _handle_nav_key(sess, st, ch) -> bool:
    """导航键分工（v5.13.0 起）：

      · PgUp / PgDn  —— 选**待修改图**（上一张 / 下一张历史图）
      · Home / End   —— 在**参考图列表**里跳到第一张 / 最后一张
                         （配合 Ctrl+K 删除）
      · ↑ / ↓        —— 翻提示词历史（由 _handle_prompt_history 处理）

    为什么 Home/End 让给参考图列表：
      删除参考图需要"选中哪一张"的概念，而 ↑↓ 已归提示词历史。
      Home/End 原来"跳历史首尾"用 PgUp/PgDn 连按也能做到，代价更小。
    """
    code = _key_code(ch)
    if code is None:
        return False
    items = sess.items
    n = len(items)

    if code == _c.KEY_PPAGE:
        st["hist"] = max(0, st["hist"] - 1)
    elif code == _c.KEY_NPAGE:
        st["hist"] = min(max(0, n - 1), st["hist"] + 1)
    elif code == _c.KEY_HOME:
        _ref_select_first_last("first", st)
        return True
    elif code == _c.KEY_END:
        _ref_select_first_last("last", st)
        return True
    else:
        return False
    _ensure_visible(st, n)
    return True


def _handle_input_key(stdscr, sess, key, offline, st, inp, ch) -> bool:
    """输入框按键。返回 True = 已处理。submit 时触发生成/编辑。"""
    action = inp.handle(ch)
    if action == "cancel":
        log("已清空输入", "info")
        return True
    if action == "submit":
        text = inp.text.strip()
        if not text:
            inp.clear()
            return True
        if st["busy"]:
            log("正在忙，请稍候…", "warn")
            return True

        # 记录到提示词历史（**提交时**就记，不管后面成不成功）——
        # 这样"打了一大段结果 401 失败"也不会丢，用户能按 F4 找回来。
        try:
            from .store import push_prompt_history, trim_prompt_history
            push_prompt_history(text, kind=st["mode"])
            trim_prompt_history()
        except Exception:                            # noqa: BLE001
            pass

        # 保留一份"上次提交的内容"：如果这次失败/取消，用来恢复输入框
        st["last_submit"] = text
        inp.clear()
        # 记下提交时刻的输入框历史游标，方便继续往上翻
        st["inp_hist_idx"] = -1

        if st["mode"] == "edit" and sess.current:
            _act_edit(stdscr, sess, key, offline, st, text, {})
        else:
            _act_generate(stdscr, sess, key, offline, st, text, {})
        return True
    # 到这里说明输入框没返回 submit/cancel：
    #   · 光标移动 / 退格 / Ctrl+U / Ctrl+W 等 → 已被 _InputLine 处理
    #   · 普通字符（字母/数字/中文）           → 已被 _InputLine 插入
    # 两种情况都算"这一帧处理完了"，返回 True。
    # 注意：Ctrl **功能键**在主循环的 _handle_action_key 阶段就拦截了，
    # 走不到这里 —— 所以不会出现"按 Ctrl+S 却往输入框插了 "。
    return True


# ======================================================================
#  调试支持
# ======================================================================
def _dump_debug(stdscr, info: dict, suffix: str = "") -> None:
    """把当前屏幕内容 + 布局信息写到 IMGAGENT_TUI_DEBUG 指定的文件。

    用途：自动化测试排版（比解析 ANSI 转义序列可靠得多）。
    格式：第一行是 JSON 布局信息，后面是屏幕每行文本。
    """
    path = os.environ.get("IMGAGENT_TUI_DEBUG", "").strip()
    if not path:
        return
    path = path + suffix
    if not suffix:
        limit = int(os.environ.get("IMGAGENT_TUI_DEBUG_FRAMES", "3"))
        if _dump_debug.counter >= limit:
            return
        _dump_debug.counter += 1
    try:
        import json
        h, w = stdscr.getmaxyx()
        lines = []
        for y in range(h):
            # ⚠️ PDCurses 的 instr(y, x, n) 的 n 是**字节数**（不是字符数），
            # 而 UTF-8 里 `─`/`│`/中文都是 3 字节 → 传 w 会读不满整行。
            # 传 w*4 保证覆盖整行，再按 cell 截取。
            try:
                raw = stdscr.instr(y, 0, max(w * 4, w + 8))
            except Exception:
                raw = b""
            if isinstance(raw, bytes):
                raw = raw.decode("utf-8", "replace")
            lines.append(raw[:w].rstrip())
        with open(path, "w", encoding="utf-8") as f:
            f.write(json.dumps(info, ensure_ascii=False) + "\n")
            f.write("\n".join(lines))
    except Exception:
        pass


_dump_debug.counter = 0


# ======================================================================
#  浮层：退出确认 / 帮助
# ======================================================================
def _modal(stdscr, title: str, lines: list[str], footer: str,
           accent: int = _C_TITLE) -> None:
    """在屏幕中央画一个模态浮层。"""
    h, w = stdscr.getmaxyx()
    cw = min(w - 4, max(34, max((_disp_w(x) for x in lines + [title, footer]),
                                default=20) + 6))
    ch_ = min(h - 2, len(lines) + 5)
    y = max(0, (h - ch_) // 2)
    x = max(0, (w - cw) // 2)
    # 遮罩（用空格填充底色，视觉上"压暗"）
    for i in range(ch_):
        _safe_addstr(stdscr, y + i, x,
                     _pad(" " * cw, cw), _c.color_pair(_C_HL))
    _box(stdscr, y, x, ch_, cw, title,
         attr=_c.color_pair(_C_HL) | _c.A_BOLD,
         title_attr=_c.color_pair(_C_HL) | _c.A_BOLD, focused=True)
    for i, ln in enumerate(lines):
        if i >= ch_ - 3:
            break
        _box_line(stdscr, y, x, cw, i, ln, _c.color_pair(_C_HL) | _c.A_BOLD)
    _safe_addstr(stdscr, y + ch_ - 2, x + 2,
                 _pad(_trunc(footer, cw - 4), cw - 3),
                 _c.color_pair(accent) | _c.A_BOLD)
    stdscr.refresh()
    _dump_debug(stdscr, {"modal": title, "size": (h, w)}, suffix=".modal")


def _confirm_quit(stdscr, sess) -> bool:
    """退出确认框。返回 True 表示真的退出。"""
    n = len(sess.items)
    lines = [
        f"历史 {n} 张，累计 ${sess.total_cost:.4f}",
        "",
        "图片文件会保留在磁盘（DCIM/Camera/imgagent）",
        "历史记录与设置也会保留，下次启动自动恢复。",
    ]
    _modal(stdscr, "确认退出", lines, "  [y] 退出    [n]/[Esc] 取消  ")
    while True:
        got = _read_key(stdscr)
        if got is None:
            continue
        if _key_is(got, "y", "Y"):
            return True
        if _key_is(got, "n", "N") or got in ("\x1b", 27) or got == _c.KEY_ENTER \
                or got in ("\n", "\r"):
            return False
        if _key_code(got) == 3:
            return True


def _help_overlay(stdscr, sess) -> None:
    """按键帮助浮层（按任意键关闭）。

    功能键全部是 **Ctrl 组合** —— 普通字母和数字一律进输入框，
    所以写提示词时不会再有"打不出 q / 3"的问题。
    """
    lines = [
        "功能键（全部 Ctrl 组合，普通字母/数字可正常输入）",
        "",
        "  ^G   生成        ^D   编辑（改图）   ^L   看原图",
        "  ^R   撤回        ^O   刷新          ^P   换质量档",
        "  ^W   换画幅      ^R   换分辨率     ^F   上传图片",
        "  ^M   换模型      ^V   换格式        ^Y   加入/移出参考图",
        "  ^K   删参考图                            ",
        "  ^S   设置        ^I   数据目录      ^N   环境状态",
        "  ^T   本帮助      ^Q   退出（会确认）",
        "  ^Y   加入/移出参考图（当前选中的图）",
        "  ^K   删除选中的参考图",
        "  ^A   批量张数（1-4，影响每次生成数量）",
        "",
        "所有浮窗都用 Esc 关闭/返回",
        "",
        "输入框（光标处按 Enter 提交）",
        "  ↑ / ↓   翻提示词历史（像终端翻命令，↑ 更早 / ↓ 更新）",
        "  Enter   提交（生成 / 编辑）",
        "  Esc     清空输入框",
        "  ^U      清空整行        ^W   删一个词",
        "  ^A/^E   行首 / 行尾",
        "  ← →     移动光标        Backspace / Delete 删字符",
        "",
        "选「待修改图」（要被改的那张）",
        "  PgUp/PgDn   上一张 / 下一张历史图",
        "",
        "选「参考图」（提供人物设定 / 场景 / 风格）",
        "  Ctrl+Y      把当前选中的历史图加入 / 移出",
        "  Home/End    在参考图列表里选第一张 / 最后一张",
        "  Ctrl+K      删除选中的参考图",
        "  最多 4 张",
        "",
        "改图：Ctrl+D",
        "  → 输出 = 用参考图的设定去改「待修改图」",
        "  → 待修改图永远是第 1 张，参考图跟在后面",
        "",
        "翻提示词历史（输入框）",
        "  ↑ / ↓       翻之前打过的提示词；↓ 一直按会回到当前草稿",
        "",
        "  Ctrl+C  强制退出（不确认）",
    ]
    _modal(stdscr, "按键说明", lines, "  按任意键关闭  ",
           accent=_C_OK)
    while True:
        got = _read_key(stdscr)
        if got is not None:
            return


# ======================================================================
#  绘制
# ======================================================================
def _plat() -> str:
    try:
        from .android import platform_name
        return platform_name().title()
    except Exception:
        return "Desktop"


def _ensure_visible(st: dict, total: int) -> None:
    """让选中项保持在可见范围内（列表高度由绘制时更新到 st['list_h']）。"""
    lh = max(1, st.get("list_h", 6))
    if st["hist"] < st["scroll"]:
        st["scroll"] = st["hist"]
    elif st["hist"] >= st["scroll"] + lh:
        st["scroll"] = st["hist"] - lh + 1


def _draw_all(stdscr, sess, inp: _InputLine, st: dict, offline: bool) -> None:
    h, w = stdscr.getmaxyx()
    stdscr.erase()
    wide = w >= _WIDE_COLS

    top_h = 3
    hint_h = _hint_height(w)          # 提示行可能不止一行（多列排布）
    # 输入框高度随内容增长（长提示词需要多行显示）——最多占屏幕 1/3
    input_h = input_height(inp.text, w, max_h=max(3, min(8, h // 3)))
    # 内容区至少留 6 行，否则压缩输入框
    while input_h > 3 and h - top_h - input_h - hint_h < 6:
        input_h -= 1
    body_h = max(4, h - top_h - input_h - hint_h)

    _draw_topbar(stdscr, 0, 0, w, top_h, st, offline)

    if wide:
        left_w = max(34, int((w - 1) * 0.58))
        right_x = left_w + 1
        right_w = w - right_x
        info_h = min(9, max(6, body_h // 3))
        hist_h = min(10, max(5, (body_h - info_h) // 2))
        prev_h = body_h - info_h
        msg_h = body_h - hist_h

        _draw_info(stdscr, top_h, 0, info_h, left_w, sess, st, offline)
        # 左下：参考图列表（取代原字符画预览 —— 真机反馈"预览没有意义"）。
        # 要看图请按 Ctrl+L（系统看图器 / 全屏浮层）。
        _draw_refs(stdscr, top_h + info_h, 0, prev_h, left_w, sess, st)
        _draw_history(stdscr, top_h, right_x, hist_h, right_w, sess, st)
        _draw_messages(stdscr, top_h + hist_h, right_x, msg_h, right_w)
    else:
        # 纵向堆叠：当前 → 预览 → 历史 → 消息
        # 空间不够时按优先级丢弃（消息最后丢）；有富余时全部给消息面板
        need = {"info": 6, "preview": 8, "history": 6, "msg": 4}
        min_h = {"info": 4, "preview": 4, "history": 4, "msg": 3}
        order = ["info", "preview", "history", "msg"]
        heights: dict[str, int] = {}
        avail = body_h
        for i, name in enumerate(order):
            floor_rest = sum(min_h[n] for n in order[i + 1:])
            give = min(need[name], max(min_h[name], avail - floor_rest))
            give = max(0, give)
            heights[name] = give
            avail -= give
        # 富余空间全给消息面板（它最需要行数）
        if avail > 0 and heights.get("msg", 0) > 0:
            heights["msg"] += avail
            avail = 0
        y = top_h
        if heights["info"] >= 4:
            _draw_info(stdscr, y, 0, heights["info"], w, sess, st, offline)
            y += heights["info"]
        if heights["preview"] >= 3:
            _draw_refs(stdscr, y, 0, heights["preview"], w, sess, st)
            y += heights["preview"]
        if heights["history"] >= 4:
            _draw_history(stdscr, y, 0, heights["history"], w, sess, st)
            y += heights["history"]
        if heights["msg"] >= 4 and y < top_h + body_h:
            _draw_messages(stdscr, y, 0, min(heights["msg"], top_h + body_h - y), w)

    _draw_input(stdscr, h - input_h - hint_h, 0, input_h, w, inp, st)
    _draw_hint(stdscr, h - hint_h, w, hint_h)

    # 瞬时提示：覆盖在顶栏右半边（不占额外行，不影响布局）
    if _time.time() < _FLASH["until"] and _FLASH["text"]:
        _draw_flash(stdscr, 1, max(0, w // 2), w // 2 - 2)

    # 调试：导出实际布局（用于自动化测试排版）
    _dump_debug(stdscr, {
        "size": (h, w), "top_h": top_h, "input_h": input_h,
        "hint_h": hint_h, "body_h": body_h, "hint_at": h - hint_h,
        "input_at": h - input_h - hint_h, "wide": wide,
    })


def _draw_topbar(stdscr, y, x, w, h, st, offline) -> None:
    """顶栏：版本 · 平台 · 动画状态 · 时钟 · 计时。

    注意：这里**不需要 sess** —— 顶栏只显示版本/平台/忙碌状态/时钟，
    不依赖会话数据。早先版本多传了 sess 参数（未使用），已移除。
    

    动画：忙碌时字符逐帧变化 + 状态文字颜色脉冲（亮暗交替）。
    """
    try:
        from . import __version__ as _ver
    except Exception:                                    # noqa: BLE001
        _ver = "?"
    busy = bool(st["busy"])
    frame = st["spin"]
    spin = _spinner_frame(frame) if busy else "●"
    state = st["busy"] or ("离线" if offline else "就绪")
    clock = _time.strftime("%Y-%m-%d %H:%M:%S")
    elapsed = ""
    if busy:
        secs = int(_time.time() - st["t0"])
        elapsed = f" {secs}s"

    _box(stdscr, y, x, h, w, attr=_c.color_pair(_C_FRAME))
    inner = w - 4

    left_s = f" imgagent v{_ver}  {_plat()} "
    right_s = f" {clock}{elapsed} "
    # 忙碌时：状态文字带"脉冲"（属性交替）；空闲：静态
    pulse = busy and (frame % 2 == 0)
    spin_s = f" {spin} {state} "
    if busy:
        # 加一个会走的进度点（不定长任务，用滚动条表示"在进行中"）
        bar_w = 10
        pos = frame % (bar_w * 2)
        if pos >= bar_w:
            pos = bar_w * 2 - pos - 1
        bar = "─" * pos + "◆" + "─" * max(0, bar_w - pos - 1)
        spin_s = f" {spin} {state}{elapsed} {bar} "
        right_s = f" {clock} "

    mid_len = max(0, inner - _disp_w(left_s) - _disp_w(spin_s) - _disp_w(right_s))
    line = left_s + " " * mid_len + spin_s
    line = _pad(line, inner - _disp_w(right_s)) + right_s

    base = _c.color_pair(_C_HL) if busy else _c.color_pair(_C_TITLE)
    attr = base | _c.A_BOLD
    if busy and pulse:
        attr = base | _c.A_BOLD | _c.A_REVERSE
    # 整行写入（含左右竖线）：`_box` 预画的右竖线落在 cell x+w-1，若本行含
    # 中文会把它的渲染列推到 x+w-1+k（超出边框）。整行重写后右竖线落在
    # cell x+w-1-k，渲染列正好 x+w-1，与顶/底边对齐。
    vt2 = _border_style()[3]
    if _IS_PDCURSES:
        # PDCurses：整行重写 `│ 内容 │`，覆盖 _box 预画的右竖线（否则其渲染
        # 列会被本行的中文推到边框外）。先清空整个 cell 区间再写。
        row = vt2 + " " + _pad(_trunc(line, inner), inner) + " " + vt2
        _safe_addstr(stdscr, y + 1, x, " " * w, attr)
        _safe_addstr(stdscr, y + 1, x, row, attr)
    else:
        _safe_addstr(stdscr, y + 1, x + 2, _trunc(line, inner), attr)


def _draw_info(stdscr, y, x, h, w, sess, st, offline) -> None:
    """左上：当前图与配置。

    ⚠️ 显示的是**历史列表里选中的那一张**（st["hist"]），不是固定的"最新图"。
    否则用 ↑↓ 浏览历史时，这里始终显示同一张的信息，用户不知道翻到哪了。
    标题随之变化：选中最新图显示「当前」，否则显示「历史 3/7」。
    """
    items = sess.items
    idx = max(0, min(st.get("hist", 0), len(items) - 1)) if items else 0
    is_current = (idx == 0)
    if items:
        title = "当前" if is_current else f"历史 {idx + 1}/{len(items)}"
    else:
        title = "当前"
    _box(stdscr, y, x, h, w, title, attr=_c.color_pair(_C_FRAME),
         title_attr=_c.color_pair(_C_TITLE) | _c.A_BOLD, focused=is_current)

    cur = items[idx] if items else None
    rows: list[tuple[str, str]] = []
    if cur:
        rows.append(("文件", cur.file))
        rows.append(("尺寸", _img_size(cur.path)))
        rows.append(("类型", f"{cur.kind}  ${cur.cost:.4f}  {cur.tokens} tok"))
        if cur.prompt:
            rows.append(("提示词", cur.prompt))
        if not is_current:
            rows.append(("", "（浏览中 · Ctrl+B 可回到这张）"))
    else:
        rows.append(("文件", "（无）  Ctrl+G 生成 / Ctrl+F 上传"))
    rows.append(("模型", sess.model))
    rows.append(("质量", f"{sess.quality}   画幅 {sess.aspect}"))
    rows.append(("分辨率", getattr(sess, "resolution", "1k")))
    rows.append(("格式", getattr(sess, "output_format", "png").upper()))
    rows.append(("批量", f"{getattr(sess, 'batch_n', 1)} 张"))
    rows.append(("模式", ("离线（占位图）" if offline else "在线")
                 + f"   {sess.total_cost and f'累计 ${sess.total_cost:.4f}' or ''}"))

    for i, (k, v) in enumerate(rows):
        if i >= h - 2:
            break
        lab_w = 7
        line = f"{_pad(k, lab_w)} {v}"
        _box_line(stdscr, y, x, w, i, line,
                  _c.color_pair(_C_LABEL) if i % 2 == 0
                  else _c.color_pair(_C_VALUE))


def _img_size(path) -> str:
    try:
        from .pngcodec import png_size
        with open(path, "rb") as f:
            head = f.read(32)
        wh = png_size(head)
        if wh:
            kb = os.path.getsize(path) / 1024
            return f"{wh[0]}x{wh[1]}  {kb:.0f}KB"
    except Exception:
        pass
    return "—"


def _draw_history(stdscr, y, x, h, w, sess, st) -> None:
    """右上：历史任务 + 累计。"""
    items = sess.items
    title = f"历史 / 累计 ({len(items)})"
    _box(stdscr, y, x, h, w, title, attr=_c.color_pair(_C_FRAME),
         title_attr=_c.color_pair(_C_TITLE) | _c.A_BOLD)
    list_h = max(1, h - 3)          # 留一行给"累计"
    st["list_h"] = list_h
    total = len(items)
    start = max(0, min(st["scroll"], max(0, total - list_h)))
    end = min(start + list_h, total)
    for i in range(start, end):
        it = items[i]
        row = i - start
        mark = "▶" if i == st["hist"] else " "
        name = _trunc(it.file, max(6, w - 20))
        cost = f"${it.cost:.4f}" if it.cost else "  —   "
        line = f"{mark}{i + 1:2d}) {_pad(name, max(6, w - 20))} {cost}"
        attr = (_c.color_pair(_C_HL) | _c.A_BOLD) if i == st["hist"] \
            else _c.color_pair(_C_VALUE)
        _box_line(stdscr, y, x, w, row, line, attr)
    # 底部：累计（走 _box_line，保证与边框/右竖线对齐，不会被中文撑宽）
    sum_line = f"累计 ${sess.total_cost:.4f}   {total} 张"
    if h >= 3:
        _box_line(stdscr, y, x, w, h - 3, sum_line,
                  _c.color_pair(_C_OK) | _c.A_BOLD)


def _draw_messages(stdscr, y, x, h, w) -> None:
    """右中：程序消息（时间 + 文本）。"""
    _box(stdscr, y, x, h, w, "消息", attr=_c.color_pair(_C_FRAME),
         title_attr=_c.color_pair(_C_TITLE) | _c.A_BOLD)
    rows = max(0, h - 2)
    if rows <= 0:
        return
    recent = list(_LOG)[-rows:]
    for i, (ts, level, text) in enumerate(recent):
        line = f"{ts} {text}"
        _box_line(stdscr, y, x, w, i, line,
                  _c.color_pair(_LEVEL_COLOR.get(level, _C_VALUE)))


def _render_ascii(path, cols: int, rows: int) -> list[str] | None:
    """渲染成**纯文本** ASCII 行（不含 ANSI 转义码）。

    为什么不用 termimg.render 的默认模式：它可能返回带 ANSI 颜色码的字符串，
    而 curses 的 addstr **不会解释**这些码 —— 会原样打印成 `^[[38;5;59m`。
    所以这里强制 mode="ascii"，颜色由 curses 自己管。

    ⚠️ 真机问题（用户反馈"预览毫无作用"）：
    旧实现为了"严格保持宽高比"，在面板**又矮又宽**时（手机竖屏很常见，
    预览面板约 50 列 × 6 行）会把图主动压到 12 列宽 → 76% 宽度是空白、
    图小到看不清，看起来"毫无作用"。

    现在两种策略（IMGAGENT_TUI_FIT 环境变量）：
      fill  —— **默认**。优先填满面板宽度，比例可能被拉伸，但看得清。
      ratio —— 严格保持宽高比（旧行为），面板形状不利时图会很小。
    """
    cached = _preview_get(path, cols, rows, "ascii")
    if cached is not None:
        return cached
    from . import termimg

    avail_w = max(8, cols - 2)
    avail_w -= avail_w % 2
    # rows <= 0 有特殊含义：**不要按高度压缩**，返回完整行数。
    # 预览浮层用它来做滚动（压缩过就没有可滚的内容了）。
    avail_h = int(rows) if rows and rows > 0 else 0

    fit = (os.environ.get("IMGAGENT_TUI_FIT") or "fill").strip().lower()
    pic_cols = avail_w
    if fit == "ratio":
        ratio = _image_aspect(path)              # ratio = h / w
        if ratio and int(pic_cols * ratio / 2.0) > avail_h:
            # ASCII 行数 ≈ 列数 × ratio / 2（字符格高约为宽的 2 倍）
            pic_cols = max(8, int(avail_h * 2.0 / ratio))
            pic_cols -= pic_cols % 2

    try:
        lines = termimg.render(path, cols=pic_cols, mode="ascii")
    except Exception:                            # noqa: BLE001
        return None
    if not lines:
        return None

    # 行数超出面板高度时**均匀抽样**而不是简单截断 ——
    # 截断会丢掉图片下半部分（真机表现为"只看到上半截"）。
    #
    # 但**预览浮层**需要完整行数来做滚动，所以 rows <= 0 表示"不要压缩"。
    if avail_h > 0 and len(lines) > avail_h:
        step = len(lines) / avail_h
        lines = [lines[min(len(lines) - 1, int(i * step))]
                 for i in range(avail_h)]

    out = [_trunc(ln.replace("\x1b", ""), avail_w) for ln in lines]
    _preview_put(path, cols, rows, "ascii", out)
    return out


def _image_aspect(path) -> float | None:
    """读图片的 高/宽 比（走已缓存的解码）。失败返回 None。"""
    try:
        from .termimg import decode_cached
        got = decode_cached(path)
        if got:
            w, h, _ = got
            if w:
                return h / w
    except Exception:                            # noqa: BLE001
        pass
    return None


# 缓存定义（变量 + 说明）
_PREVIEW_CACHE: "OrderedDict[tuple, object]" = OrderedDict()
_PREVIEW_CACHE_MAX = 8


def _preview_key(path, cols: int, rows: int, mode: str):
    try:
        st = path.stat()
        return (str(path), st.st_mtime_ns, cols, rows, mode)
    except OSError:
        return None


def _preview_get(path, cols: int, rows: int, mode: str):
    """取缓存的渲染结果（命中则标记为最近使用）。"""
    key = _preview_key(path, cols, rows, mode)
    if key is None:
        return None
    hit = _PREVIEW_CACHE.get(key)
    if hit is not None:
        _PREVIEW_CACHE.move_to_end(key)
    return hit


def _preview_put(path, cols: int, rows: int, mode: str, value) -> None:
    key = _preview_key(path, cols, rows, mode)
    if key is None:
        return
    _PREVIEW_CACHE[key] = value
    _PREVIEW_CACHE.move_to_end(key)
    while len(_PREVIEW_CACHE) > _PREVIEW_CACHE_MAX:
        _PREVIEW_CACHE.popitem(last=False)


def clear_preview_cache() -> None:
    """清空预览缓存（测试用；窗口尺寸剧变时也可调用）。"""
    _PREVIEW_CACHE.clear()


# ======================================================================
#  真彩色预览（curses 内）
# ======================================================================
# 原理：curses 只认"颜色对"（pair），不认 ANSI 转义码。但实测：
#   · can_change_color() = True
#   · init_color(idx, r, g, b) 可重定义 0..255 号颜色（RGB 各 0-1000）
#   · COLOR_PAIRS = 65536，可注册大量颜色对
# 所以：把图片降采样成 N 个代表色，逐一 init_color 注册到 16..255 号，
# 再用 init_pair 建对，用半块字符 ▀（上像素=前景，下像素=背景）渲染。
# 这样能显示**接近真彩**的彩色字符画（受限于 256 色，会做颜色量化）。
_COLOR_CACHE: dict[int, int] = {}       # rgb565 -> color_index
_NEXT_COLOR = [16]                       # 下一个可用的颜色号（16 起，避开基础色）
_PAIRS_READY = False


def _rgb_to_565(r: int, g: int, b: int) -> int:
    return ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3)


def _setup_color_pairs() -> bool:
    """初始化颜色对使用（必须在 initscr 之后调用一次）。"""
    global _PAIRS_READY
    if _PAIRS_READY:
        return True
    try:
        _c.start_color()
        try:
            _c.use_default_colors()
        except Exception:                                # noqa: BLE001
            pass
        _PAIRS_READY = True
        return True
    except Exception:                                    # noqa: BLE001
        return False


def _alloc_color(r: int, g: int, b: int) -> int | None:
    """把 RGB 注册成一个 curses 颜色号（带缓存）。返回颜色号，失败返回 None。"""
    key = _rgb_to_565(r, g, b)
    hit = _COLOR_CACHE.get(key)
    if hit is not None:
        return hit
    if not _c.can_change_color() or _NEXT_COLOR[0] > 255:
        return None
    idx = _NEXT_COLOR[0]
    try:
        # curses 的 RGB 范围是 0-1000
        _c.init_color(idx, r * 1000 // 255, g * 1000 // 255, b * 1000 // 255)
    except Exception:                                    # noqa: BLE001
        return None
    _COLOR_CACHE[key] = idx
    _NEXT_COLOR[0] += 1
    return idx


def _alloc_pair(fg: int, bg: int) -> int | None:
    """注册一个 (fg, bg) 颜色对，返回 pair 号。"""
    key = fg * 1000 + bg
    hit = _PAIR_CACHE.get(key)
    if hit is not None:
        return hit
    if _NEXT_PAIR[0] > 32000:
        return None
    num = _NEXT_PAIR[0]
    try:
        _c.init_pair(num, fg, bg)
    except Exception:                                    # noqa: BLE001
        return None
    _PAIR_CACHE[key] = num
    _NEXT_PAIR[0] += 1
    return num


_PAIR_CACHE: dict[int, int] = {}
_NEXT_PAIR = [20]                        # 从 20 起（前 20 留给 UI 静态配色）


def _render_color(path, cols: int, rows: int) -> list[list[tuple[str, int]]] | None:
    """渲染成"带颜色属性"的字符矩阵（结果带缓存）。

    返回 [[(字符, pair序号), ...], ...]；不支持时返回 None。
    每个字符格用半块 ▀：上半是前景色、下半是背景色 —— 一个格子两个像素。

    ⚠️ 性能：这个函数**每帧都会被调用**（预览面板重绘）。不缓存的话，
    每帧都要全量解码 PNG + 重新分配颜色对 —— 手机上就是"生成一张图后
    整个界面卡成幻灯片"的根因（实测 1024² 图桌面 50ms，手机 1s+）。
    """
    if not _setup_color_pairs():
        return None
    if not _c.can_change_color():
        return None
    cached = _preview_get(path, cols, rows, "color")
    if cached is not None:
        return cached
    try:
        from .termimg import decode_cached       # ← 用**已缓存的**解码结果
        got = decode_cached(path)
        if got is None:
            return None
        w, h, pix = got
    except Exception:                                    # noqa: BLE001
        return None
    if not w or not h:
        return None

    out_rows = max(1, min(rows, cols * h // (2 * w)))
    px_h = max(1, out_rows * 2)
    matrix: list[list[tuple[str, int]]] = []

    def sample(sx: int, sy: int) -> tuple[int, int, int]:
        x = max(0, min(w - 1, sx))
        y = max(0, min(h - 1, sy))
        i = x * 3
        if i + 2 >= len(pix[y]):
            return (0, 0, 0)
        return (pix[y][i], pix[y][i + 1], pix[y][i + 2])

    for ry in range(out_rows):
        row_cells: list[tuple[str, int]] = []
        for cx in range(cols):
            sx = int((cx + 0.5) * w / cols)
            ty = int((ry * 2 + 0.5) * h / px_h)
            by = int((ry * 2 + 1.5) * h / px_h)
            tr, tg, tb = sample(sx, ty)
            br, bg, bb = sample(sx, by)
            fi = _alloc_color(tr, tg, tb)
            bi = _alloc_color(br, bg, bb)
            if fi is None or bi is None:
                return None
            pr = _alloc_pair(fi, bi)
            if pr is None:
                return None
            row_cells.append(("▀", pr))
        matrix.append(row_cells)
    _preview_put(path, cols, rows, "color", matrix)
    return matrix


def _draw_color_preview(stdscr, y0: int, x0: int, path, cols: int, rows: int) -> bool:
    """把彩色预览画到窗口。成功返回 True。"""
    matrix = _render_color(path, cols, rows)
    if not matrix:
        return False
    for i, row in enumerate(matrix):
        yy = y0 + i
        for j, (ch, pair) in enumerate(row):
            try:
                stdscr.addstr(yy, x0 + j, ch, _c.color_pair(pair))
            except Exception:                            # noqa: BLE001
                pass
    return True


def _wrap_by_width(text: str, width: int) -> list[str]:
    """按显示宽度折行（CJK 算 2 列）。空串返回 [""]。"""
    width = max(1, width)
    if not text:
        return [""]
    out, cur, used = [], "", 0
    for ch in text:
        wd = _disp_w(ch)
        if used + wd > width and cur:
            out.append(cur)
            cur, used = ch, wd
        else:
            cur += ch
            used += wd
    out.append(cur)
    return out


def _draw_input(stdscr, y, x, h, w, inp: _InputLine, st: dict) -> None:
    """底部输入框：多行折行 + 不遮挡文字的光标。

    修掉两个真机 bug：

    ① **不换行**：旧实现只画一行，超宽直接 _trunc 成 "…" ——
       写长提示词时只能看到前几十个字。
       现在按显示宽度折成多行；内容超出可见高度时**内部滚动**跟随光标。

    ② **光标遮挡**：旧实现往文本流里**插入** "▏" 当光标，
       该位置的字符被挤走（真机截图里「实践」的「实」被挡）。
       现在用 curses 的**颜色属性**（反白）标记光标所在字符，
       不插入任何字符、不改变布局。
    """
    mode = st["mode"]
    tag = "生成" if mode == "gen" else "编辑"
    title = f"输入 · {tag}   ↑↓ 翻历史 · Enter 提交 · Esc 清空"
    _box(stdscr, y, x, h, w, title, attr=_c.color_pair(_C_FRAME),
         title_attr=_c.color_pair(_C_TITLE) | _c.A_BOLD, focused=True)

    text = inp.text
    avail_w = w - 4                 # 盒内可用宽度
    avail_h = h - 2                 # 盒内可用行数

    if not text:
        ph = ("写提示词，Enter 开始生成" if mode == "gen"
              else "写编辑要求，如「把背景换成雪原，主体不变」")
        _safe_addstr(stdscr, y + 1, x + 2,
                     _pad(_trunc("> " + ph, w - 4), w - 4),
                     _c.color_pair(_C_DIM))
        return

    # ---- 构造"带提示符"的完整显示文本，再统一折行 ----
    # 这样光标行列可以从同一份折行结果里算出来，不用手工处理前缀偏移。
    PREFIX = "> "
    display = PREFIX + text
    # 光标在 display 里的字符下标（前缀长度 + 光标前字符数）
    caret_idx = len(PREFIX) + inp.cur

    lines = _wrap_by_width(display, avail_w)

    # ---- 定位光标行列（按折行结果累计）----
    caret_row, caret_col = 0, 0
    acc = 0
    for i, line in enumerate(lines):
        n = len(line)
        if acc + n >= caret_idx:
            caret_row = i
            caret_col = caret_idx - acc
            break
        acc += n
    else:
        caret_row = len(lines) - 1
        caret_col = len(lines[-1])

    # ---- 内部滚动：让光标行可见 ----
    top = 0
    if len(lines) > avail_h:
        if caret_row >= avail_h:
            top = caret_row - avail_h + 1
        top = min(top, len(lines) - avail_h)
    visible = lines[top:top + avail_h]

    # ---- 逐行画（PDCurses 下整行写入含左右竖线；光标处再叠加反白）----
    # 为什么 PDCurses 要整行写（含竖线）：`_box` 预画的右竖线在含中文的行
    # 会被"推到"面板外（渲染列 = cell + 宽字符数）。整行重写 `│内容│`
    # 后右竖线落在正确的 cell，渲染列正好 x+w-1，与边框对齐。
    vt3 = _border_style()[3]
    for i, line in enumerate(visible):
        abs_row = top + i
        row_y = y + 1 + i
        if _IS_PDCURSES:
            body = _pad(_trunc(line, avail_w), avail_w)
            # 先清空整个 cell 区间（含 _box 预画的右竖线那格）
            _safe_addstr(stdscr, row_y, x, " " * w, _c.color_pair(_C_VALUE))
            _safe_addstr(stdscr, row_y, x, vt3 + " " + body + " " + vt3,
                         _c.color_pair(_C_VALUE))
        elif abs_row != caret_row:
            _safe_addstr(stdscr, row_y, x + 1,
                         _pad(_trunc(line, avail_w), avail_w),
                         _c.color_pair(_C_VALUE))
            continue
        if abs_row != caret_row:
            continue
        # 光标行：拆成 [前] [光标字符] 三段（PDCurses 下在整行之上叠加反白）
        before = line[:caret_col]
        cur_ch = line[caret_col:caret_col + 1]
        after = line[caret_col + 1:]
        bx = x + 1 + _disp_w(before)
        if not _IS_PDCURSES:
            _safe_addstr(stdscr, row_y, x + 1, before, _c.color_pair(_C_VALUE))
        if cur_ch:
            # 用反白属性标记光标字符（不占额外格）
            _safe_addstr(stdscr, row_y, bx, cur_ch,
                         _c.color_pair(_C_HL) | _c.A_BOLD | _c.A_REVERSE)
        elif bx < x + w - 1:
            # 光标在行尾：显示一个反白空格表示插入点
            _safe_addstr(stdscr, row_y, bx, " ",
                         _c.color_pair(_C_HL) | _c.A_REVERSE)
        if after and not _IS_PDCURSES:
            _safe_addstr(stdscr, row_y, bx + max(1, _disp_w(cur_ch)),
                         _trunc(after, max(0, w - 3 - _disp_w(before))),
                         _c.color_pair(_C_VALUE))


def input_height(text: str, width: int, max_h: int = 8) -> int:
    """给定文本和终端宽度，输入框应该多高（含边框）。

    布局代码用它在输入变长时动态加高输入区。
    """
    avail_w = max(4, width - 4)
    lines = _wrap_by_width("> " + text, avail_w) if text else [""]
    return max(3, min(max_h, len(lines) + 2))


# 快捷键表：(显示键, 完整说明, 紧凑说明, 优先级)
# 功能键全部是 Ctrl 组合 —— 普通字母/数字让给输入框。
_KEYMAP = [
    # ── 生图类（亮色 priority=0）：日常最常用，一眼看到 ─────────────
    ("^G", "生成", "生成", 0),       # 主操作
    ("^A", "批量", "批量", 0),       # 批量张数
    ("^D", "编辑", "编辑", 0),       # 改当前图
    ("^Z", "模型", "模型", 0),       # flare/sunburst
    ("^E", "分辨率", "分辨率", 0),   # 1k/2k/4k
    ("^U", "格式", "格式", 0),       # png/jpeg/webp
    ("^W", "画幅", "画幅", 0),       # 比例
    ("^P", "质量", "质量", 0),       # low/med/high
    ("^R", "撤回", "撤回", 0),       # 回上一张
    ("^L", "预览", "预览", 0),       # 全屏看原图
    # ── 程序类（暗色 priority=1）：设置/浏览/管理 ───────────────────
    ("^F", "上传", "上传", 1),       # 从相册选图
    ("^B", "历史", "历史", 1),       # 浏览历史记录
    ("^Y", "参考图", "参考图", 1),   # 加入/移出参考图
    ("^K", "删参考", "删参考", 1),   # 删除选中的参考图
    ("^O", "刷新", "刷新", 1),       # 重新加载历史
    ("^X", "脚本图", "脚本图", 1),   # 从脚本目录导入
    ("^I", "目录", "目录", 1),       # 查看数据目录
    ("^N", "环境", "环境", 1),       # 环境状态
    ("^S", "设置", "设置", 1),       # 配置面板
    ("^T", "帮助", "帮助", 1),       # 本帮助浮层
    # ── 退出（永远保留，放最后）────────────────────────────────────
    ("^Q", "退出", "退出", -1),
]


def _hint_rows(width: int, compact: bool = False) -> list[list[tuple[str, int]]]:
    """把快捷键表排成多列对齐的行，**每条带颜色属性**。

    返回：list[list[(文本, attr)]]，外层是行，内层是该行的字符+颜色对。
    priority=0（生图类）→ _C_VALUE（亮），priority=1（程序类）→ _C_DIM（暗），
    priority=-1（退出）→ _C_ERR（红色警示）。
    """
    entries = [(f"[{k}]{short if compact else full}", pr)
               for k, full, short, pr in _KEYMAP]
    if not entries:
        return []
    cell = max(_disp_w(e) for e, _ in entries) + 2
    usable = max(10, width - 1)
    ncol = max(1, usable // cell)
    max_rows = _hint_max_rows(width)
    capacity = ncol * max_rows               # 最多能显示多少个
    if capacity < len(entries):
        # **保持原顺序**，按优先级从低到高裁掉末尾的（-1 = 永不裁）
        order = sorted(range(len(entries)),
                       key=lambda i: (-entries[i][1], -i))  # 低优先级、靠后的先裁
        drop = set(order[:len(entries) - capacity])
        entries = [e for i, e in enumerate(entries) if i not in drop]

    def _color_for(pr: int) -> int:
        if pr == -1:
            return _c.color_pair(_C_ERR)            # 退出 → 红色
        return _c.color_pair(_C_VALUE) if pr == 0             else _c.color_pair(_C_DIM)              # 生图类亮 / 程序类暗

    rows: list[list[tuple[str, int]]] = []
    for i in range(0, len(entries), ncol):
        chunk = entries[i:i + ncol]
        row: list[tuple[str, int]] = []
        for text, pr in chunk:
            attr = _color_for(pr)
            row.append((_pad(text, cell), attr))
        rows.append(row)
    return rows


def _hint_compact(width: int) -> bool:
    """窄屏用紧凑标签（≤72 列）。"""
    return width <= 72


def _hint_max_rows(width: int) -> int:
    """提示占几行：根据实际条目数和宽度计算，确保全部显示。

    原来固定 2/3 行会导致宽屏时底部键位被裁剪（用户反馈"缺失"）。
    现在动态计算：先算出每个 cell 占的列数，再算需要多少行能放下全部条目。
    下限 1 行，上限 3 行（再多的话内容区会被压缩太狠）。
    """
    # 快速估算：用最宽的条目长度作为 cell 宽度
    entries = [(f"[{k}]{short}", pr) for k, full, short, pr in _KEYMAP]
    if not entries:
        return 1
    cell_w = max(_disp_w(e) for e, _ in entries) + 2
    ncol = max(1, (width - 1) // cell_w)
    needed = (len(entries) + ncol - 1) // ncol   # 向上取整
    return min(4, max(1, needed))   # 最多 4 行：窄屏也能放下全部 21 个键


def _hint_height(width: int, max_rows: int | None = None) -> int:
    """提示行需要的行数。"""
    cap = max_rows or _hint_max_rows(width)
    return max(1, min(cap, len(_hint_rows(width, compact=_hint_compact(width)))))


def _draw_hint(stdscr, y, w, rows: int = 1) -> None:
    """底部快捷键提示（多列对齐，正好占 rows 行，按 priority 着色）。"""
    grid = _hint_rows(w, compact=_hint_compact(w))[:max(1, rows)]
    for i, row in enumerate(grid):
        x = 0
        for text, attr in row:
            t = _trunc(text, w - x)
            if not t:
                break
            # 必须走 _safe_addstr：PDCurses 下它会把宽字符展开，
            # 使 x（视觉列）与写入的 cell 坐标一致（否则每遇中文偏 1 列）
            _safe_addstr(stdscr, y + i, x, t, attr)
            x += _disp_w(t)
            if x >= w - 1:
                break


# ======================================================================
#  动作：TUI 原生（跑在后台线程，主循环继续动画）
# ======================================================================
def _start_job(st: dict, label: str) -> None:
    st["busy"] = label
    st["t0"] = _time.time()


def _end_job(st: dict) -> None:
    st["busy"] = ""


def _run_bg(stdscr, st: dict, label: str, fn, offline: bool = False) -> bool:
    """在后台线程跑 fn，期间保持动画刷新。

    两个关键点：

    ① **stdout/stderr 必须重定向**。后台线程里的 print（比如 api.py 轮询
       时每 10 秒的进度提示）会直接写到终端，把 curses 界面冲乱 ——
       真机现象是底部按键提示区出现「还在生成中（已等 10s…）」。
       这里换成"把行转成 log() 消息"的 sink，输出会进「消息」面板。

    ② offline 从调用方传入（早先用模块级全局 _LAST_SESS/_LAST_OFFLINE，
       那是隐式的跨线程共享状态）。
    """
    result: dict = {"err": None}

    def worker() -> None:
        try:
            fn()
        except BaseException as e:               # noqa: BLE001
            result["err"] = e

    class _ToLog(io.TextIOBase):
        """把 print 的每行转成 log() 消息（不写终端）。"""
        def write(self, text):
            for ln in text.splitlines():
                if ln.strip():
                    log(ln.strip(), "info")
            return len(text)
        def flush(self):
            pass

    _start_job(st, label)
    saved_out, saved_err = sys.stdout, sys.stderr
    sys.stdout = sys.stderr = _ToLog()           # type: ignore[assignment]
    th = threading.Thread(target=worker, daemon=True)
    th.start()
    try:
        while th.is_alive():
            _bg_paint(stdscr, st, offline)
            st["spin"] = (st["spin"] + 1) % 60
            _time.sleep(_REFRESH_MS / 1000.0)
    finally:
        sys.stdout, sys.stderr = saved_out, saved_err
    _end_job(st)
    if result["err"] is not None:
        log(f"任务失败：{result['err']}", "err")
        return False
    return True


def _bg_paint(stdscr, st: dict, offline: bool) -> None:
    """后台任务期间的最小重绘（顶栏 + 消息 + 底栏提示）。"""
    try:
        h, w = stdscr.getmaxyx()
        stdscr.erase()
        _draw_topbar(stdscr, 0, 0, w, 3, st, offline)
        if w >= _WIDE_COLS:
            right_x = max(34, int((w - 1) * 0.58)) + 1
            _draw_messages(stdscr, 4, right_x, h - 9, w - right_x)
        else:
            _draw_messages(stdscr, max(4, h - 9), 0, 6, w)
        hh = _hint_height(w)
        _draw_hint(stdscr, h - hh, w, hh)
        stdscr.refresh()
    except Exception:                            # noqa: BLE001
        pass
    try:
        _read_key(stdscr)                        # 排空输入缓冲
    except Exception:                            # noqa: BLE001
        pass


# ======================================================================
#  瞬时提示（flash）
# ======================================================================
_FLASH: dict = {"text": "", "until": 0.0}


def _flash(text: str, seconds: float = 1.6) -> None:
    """设置一条会短暂显示的提示（画在顶栏下方）。

    为什么需要：有些操作（换质量档、刷新等）反馈只在「消息」面板里，
    而消息面板在屏幕底部，用户按完快捷键后视线在上方 → 观感是"没反应"。
    """
    _FLASH["text"] = text
    _FLASH["until"] = _time.time() + seconds


def _draw_flash(stdscr, y: int, x: int, w: int) -> int:
    """把瞬时提示**覆盖**画在指定位置（不占额外行，避免布局跳动）。

    真机 bug 修复：这个函数曾经定义了但**从来没被调用** —— 所以
    _flash() 设置了状态也白搭，Ctrl+P 的提示条根本不出现。
    现在由 _draw_all 调用，覆盖在顶栏右半边。
    """
    if _time.time() >= _FLASH["until"] or not _FLASH["text"]:
        return 0
    text = " " + _FLASH["text"] + " "
    _safe_addstr(stdscr, y, x, _trunc(text, w),
                 _c.color_pair(_C_HL) | _c.A_BOLD | _c.A_REVERSE)
    return 1


# ======================================================================
#  提示词润色（TUI 版）
# ======================================================================
def _polish_flow_tui(stdscr, sess, text: str, *, kind: str = "gen") -> str:
    """TUI 里的润色流程。返回最终要用的提示词。

    ⚠️ 这是补上的功能：重写 TUI 时**漏掉了润色这一步** ——
    命令行版 ui.polish_flow() 会在生成/编辑前问要不要 AI 润色，
    但 curses 版直接拿用户输入去生成，导致"润色好像没了"。

    流程（与命令行版对齐）：
      ① 开关关闭 → 直接用原文
      ② 问"要不要润色" → 不要就用原文
      ③ 调 LLM 生成 4 条候选（后台线程跑，期间保持动画）
      ④ 浮窗列出候选 → 选一条 / n 用原文 / r 重新生成
    """
    from . import polish
    if not polish.enabled():
        return text

    # ② 先问要不要润色
    if not _ask_yes_no(stdscr, sess, "提示词润色",
                       [f"原文：{_trunc(text, 60)}", "",
                        "要用 AI 润色/扩写这条提示词吗？",
                        "（一次生成 4 个候选让你挑，通常 3~25 秒）"]):
        return text

    while True:
        # ③ 后台调 LLM（保持动画，stdout 已被 _run_bg 重定向）
        box: dict = {"options": None, "err": None}

        def job() -> None:
            try:
                box["options"] = polish.picks(text, kind=kind,
                                              aspect=sess.aspect)
            except Exception as e:                       # noqa: BLE001
                box["err"] = e

        _run_bg(stdscr, {"busy": "", "spin": 0, "t0": _time.time()},
                "润色中", job, sess.offline)

        if box["err"] is not None:
            log(f"润色失败：{box['err']}（用原提示词）", "warn")
            return text
        options = box["options"] or []
        if not options:
            log("润色没返回内容（用原提示词）", "warn")
            return text

        # ④ 逐条翻页展示候选（←→ 切换）
        #    为什么不用 _pick_list：它会用 _trunc 把候选压成一行，
        #    而润色候选通常 40-70 字，手机上只能看到前一半（真机反馈过）。
        #    这里每条候选占一整页，自动折行，完整可读。
        SKIP = "── 不做润色，用原提示词 ──"
        REDO = "── 重新生成几条 ──"
        got = _pick_pages(
            stdscr, sess, f"润色候选（{len(options)} 条）",
            options, extra=[(SKIP, ""), (REDO, "")])
        if got is None:
            log("已取消润色，使用原提示词", "info")
            return text
        if got == SKIP:
            log("已跳过润色，使用原提示词", "info")
            return text
        if got == REDO:
            continue                                     # 再来一轮
        # 找到是第几条（按内容匹配，避免下标歧义）
        try:
            idx = options.index(got)
        except ValueError:
            return got
        log(f"已选用第 {idx + 1} 条润色结果（{len(got)} 字）", "ok")
        return got


def _ask_yes_no(stdscr, sess, title: str, lines: list[str],
                default: bool = False) -> bool:
    """TUI 内的 Y/N 询问浮窗。返回 True/False。

    default 只在用户按 Esc 时生效（模拟"回车取默认值"的语义）。
    """
    hint = ("  [Y] 是    [N] 否  （默认：%s）  "
            % ("是" if default else "否"))
    while True:
        h, w = stdscr.getmaxyx()
        stdscr.erase()
        _draw_topbar(stdscr, 0, 0, w, 3, {"busy": "", "spin": 0, "t0": 0},
                     sess.offline if sess else False)
        box_h = min(h - 5, max(7, len(lines) + 4))
        box_y = max(3, (h - box_h) // 2)
        box_w = min(w - 2, max(44, min(76, w - 4)))
        box_x = max(0, (w - box_w) // 2)
        _box(stdscr, box_y, box_x, box_h, box_w, _trunc(title, box_w - 8),
             attr=_c.color_pair(_C_FRAME),
             title_attr=_c.color_pair(_C_TITLE) | _c.A_BOLD, focused=True)
        for i, ln in enumerate(lines[:box_h - 3]):
            _box_line(stdscr, box_y, box_x, box_w, i,
                      _trunc(ln, box_w - 4), _c.color_pair(_C_VALUE))
        _safe_addstr(stdscr, box_y + box_h - 2, box_x + 2,
                     _pad(_trunc(hint, box_w - 4), box_w - 5),
                     _c.color_pair(_C_OK) | _c.A_BOLD)
        stdscr.refresh()

        ch = _read_key(stdscr)
        if ch is None:
            continue
        code = _key_code(ch)
        if _key_is(ch, "y", "Y"):
            return True
        if _key_is(ch, "n", "N"):
            return False
        if code == 27:
            return default
        if code in (_c.KEY_ENTER,):
            return default
        if _key_is(ch, "\n", "\r"):
            return default


def _act_generate(stdscr, sess, key, offline, st, text, err_holder) -> None:
    from . import api
    from .httpclient import ApiError

    # 润色（可选，失败不阻断）
    text = _polish_flow_tui(stdscr, sess, text, kind="gen")
    log(f"开始生成：{_trunc(text, 40)}", "info")

    def job() -> None:
        try:
            res = api.generate(text, api_key=key or "", model=sess.model,
                               quality=sess.quality, aspect=sess.aspect,
                               n=getattr(sess, "batch_n", 1),
                               offline=offline, step=sess.counter,
                               resolution=getattr(sess, "resolution", "1k"),
                               output_format=getattr(sess, "output_format", "png"))
        except ApiError as e:
            log(f"生成失败：{e}", "err")

            from .api import error_hint as _eh

            _h = _eh(e)

            if _h:

                log(f"  → {_h}", "warn")
            return
        except Exception as e:                   # noqa: BLE001
            log(f"生成异常：{type(e).__name__} {e}", "err")
            return
        n_ok = _land_all(sess, res, text, kind="offline" if offline else "gen")
        if n_ok:
            log(f"生成完成 · {n_ok} 张 · ${res.cost:.4f} · {res.tokens} tok", "ok")
        else:
            log("生成成功但落盘失败", "err")

    ok = _run_bg(stdscr, st, "生成中", job, offline)
    _keep_prompt_on_failure(st, ok)


def _keep_prompt_on_failure(st: dict, ok: bool) -> None:
    """任务失败时，把上次提交的提示词**放回输入框**。

    为什么需要：提交时输入框立刻被清空，若失败（401 / 网络错 / 用户取消），
    辛苦打的提示词就没了 —— 用户明确反馈"很生气"。
    现在失败会自动恢复；F3 还能翻更早的。
    """
    if ok:
        st["last_submit"] = ""
        return
    text = st.get("last_submit") or ""
    if not text:
        return
    st["_restore_text"] = text          # 主循环下一帧放回输入框
    log("生成未成功，提示词已放回输入框（F3 可翻更早的）", "warn")


def _act_edit(stdscr, sess, key, offline, st, text, err_holder) -> None:
    """改图。支持**多张参考图**（用 Ctrl+Y 从历史里挑，最多 REF_MAX 张）。

    参考图来源优先级：
      ① st["refs"] 里有图 → 全部用上（多图编辑）
      ② 没有参考图     → 回退到"用当前选中的历史图"（原来的单图行为）
    """
    from . import api
    from .httpclient import ApiError

    # 组装图片：**待修改图永远在第 1 位**，参考图跟后面（顺序有语义）
    refs, src_desc = load_edit_images(sess, st)
    if not refs:
        log("没有可用的图 —— 先用 PgUp/PgDn 选一张作为待修改图", "warn")
        return

    # 润色（edit 模式，提示词会按"修改要求"的方向改写）
    text = _polish_flow_tui(stdscr, sess, text, kind="edit")
    log(f"开始编辑（基于 {src_desc}）：{_trunc(text, 40)}", "info")

    def job() -> None:
        try:
            res = api.generate(text, api_key=key or "", model=sess.model,
                               quality=sess.quality, aspect=sess.aspect,
                               refs=refs, offline=offline,
                               step=sess.counter,
                               resolution=getattr(sess, "resolution", "1k"),
                               output_format=getattr(sess, "output_format", "png"))
        except ApiError as e:
            log(f"编辑失败：{e}", "err")

            from .api import error_hint as _eh2

            _h2 = _eh2(e)

            if _h2:

                log(f"  → {_h2}", "warn")
            return
        except Exception as e:                   # noqa: BLE001
            log(f"编辑异常：{type(e).__name__} {e}", "err")
            return
        n_ok = _land_all(sess, res, text, kind="edit", note=f"基于 {src_desc}")
        if n_ok:
            log(f"编辑完成 · ${res.cost:.4f} · {res.tokens} tok", "ok")
        else:
            log("编辑成功但落盘失败", "err")

    ok = _run_bg(stdscr, st, "编辑中", job, offline)
    _keep_prompt_on_failure(st, ok)


def _land_all(sess, res, prompt: str, kind: str, note: str = "") -> int:
    """把生成结果落盘并入库。返回成功张数。"""
    from .store import Item
    from .ui import _save, _ensure_in_home
    ok = 0
    per = res.cost / max(1, len(res.images))
    for i, (data, media) in enumerate(res.images):
        try:
            path = _save(data, media, prompt, sess.counter + 1 + i)
            home = _ensure_in_home(path, data)
            if home is None:
                log(f"第 {i+1} 张未能放进工作目录", "err")
                continue
            it = Item(file=home.name, prompt=prompt, kind=kind,
                      model=sess.model, quality=sess.quality,
                      cost=per, tokens=res.tokens, note=note)
            sess.push(it)
            sess.log(it)
            ok += 1
            log(f"已保存 {home.name}", "ok")
        except Exception as e:                   # noqa: BLE001
            log(f"落盘失败：{e}", "err")
    sess.total_cost += res.cost
    sess.save_state()
    return ok


# ======================================================================
#  动作：需交互的走"挂起 curses → 跑 → 恢复"
# ======================================================================
def _suspend_and_run(stdscr, fn, pause_hint: str = "按 Enter 返回 TUI") -> None:
    """临时退出 curses 跑一个用 input() 的函数，再恢复。"""
    try:
        _c.def_prog_mode()
        _c.endwin()
    except Exception:
        pass
    try:
        with _redirect_stdout():
            fn()
    except (EOFError, KeyboardInterrupt):
        pass
    except Exception as e:                       # noqa: BLE001
        print(f"\n  [X] 出错：{type(e).__name__}: {e}")
    try:
        print(f"\n  {pause_hint}…")
        input()
    except (EOFError, KeyboardInterrupt):
        pass
    try:
        _c.reset_prog_mode()
        stdscr.clear()
        stdscr.refresh()
    except Exception:
        pass


class _redirect_stdout:
    """把 print 同时写到真实终端和消息面板。"""

    def __enter__(self):
        self._old = sys.stdout
        outer = self._old

        class _Tee(io.TextIOBase):
            def write(self, s):
                for line in s.splitlines():
                    line = line.strip()
                    if line:
                        log(line, "out")
                try:
                    outer.write(s)
                except Exception:
                    pass
                return len(s)

            def flush(self):
                try:
                    outer.flush()
                except Exception:
                    pass

        sys.stdout = _Tee()
        return self

    def __exit__(self, *a):
        sys.stdout = self._old
        return False


def _act_undo(sess, st) -> None:
    if not sess.items:
        log("没有可撤回的图", "warn")
        return
    top = sess.items[0]
    if top.kind in ("upload", "import", "offline"):
        log(f"{top.kind} 的记录不能撤回", "warn")
        return
    sess.items.pop(0)
    sess.save_state()
    st["hist"] = min(st["hist"], max(0, len(sess.items) - 1))
    log(f"已撤回 {top.file}（文件仍在磁盘）", "ok")


def _act_preview(stdscr, sess, st) -> None:
    """预览当前图。

    真机 bug：旧实现调 preview.show()，它会 print() —— 在 curses 运行中
    这些输出直接打到终端，把界面冲成乱码（真机截图里的花屏）。
    现在分两级：
      ① 系统看图器（termux-open / Android bridge）—— 最清晰，且不干扰 TUI
      ② 失败就在 TUI 内弹一个大 ASCII 预览浮层（不 print）
    """
    items = sess.items
    if not items:
        log("还没有图片可预览", "warn")
        return
    idx = max(0, min(st["hist"], len(items) - 1))
    cur = items[idx]

    # ① 系统看图器
    try:
        from . import android
        if android.ready():
            mime = "image/png"
            if android.BRIDGE.view_image(cur.path, mime):
                log(f"已用系统看图器打开 {cur.file}", "ok")
                return
        from .android import termux_open
        if termux_open(cur.path):
            log(f"已用 termux-open 打开 {cur.file}", "ok")
            return
    except Exception as e:                               # noqa: BLE001
        log(f"调系统看图器失败：{type(e).__name__}", "warn")

    # ② TUI 内大图预览浮层
    _preview_overlay(stdscr, sess, cur)


def _preview_overlay(stdscr, sess, item) -> None:
    """TUI 内的全屏预览浮层（不 print，不破坏界面）。

    支持 ↑↓ / PgUp/PgDn / Home/End **滚动** —— 按"填满宽度"策略渲染时，
    竖图可能比屏幕高，不滚动就只能看到上半截。
    另外 `f` 键可现场切换缩放策略（fill ↔ ratio），方便对比观感。
    """
    top = 0
    while True:
        h, w = stdscr.getmaxyx()
        stdscr.erase()
        box_y, box_x = 1, 1
        box_h, box_w = h - 2, w - 2
        _box(stdscr, box_y, box_x, box_h, box_w,
             _trunc(f"预览 · {item.file}", box_w - 10),
             attr=_c.color_pair(_C_FRAME),
             title_attr=_c.color_pair(_C_TITLE) | _c.A_BOLD, focused=True)

        view_h = max(1, box_h - 3)          # 留出边框 + 底部提示行
        view_w = max(8, box_w - 2)
        total_rows = 0

        try:
            if not item.path.exists():
                _box_line(stdscr, box_y, box_x, box_w, 0, "文件不存在",
                          _c.color_pair(_C_ERR))
            else:
                painted = False
                if (os.environ.get("IMGAGENT_TUI_COLOR", "auto").lower()
                        not in ("off", "none", "0", "disable")):
                    painted = _draw_color_preview(stdscr, box_y + 1, box_x + 1,
                                                  item.path, view_w, view_h)
                if not painted:
                    # ASCII 模式：传 rows=0 拿**完整行数**，由这里做滚动
                    lines = _render_ascii(item.path, view_w, 0)
                    if lines:
                        total_rows = len(lines)
                        visible = lines[top:top + view_h]
                        for i, ln in enumerate(visible):
                            _safe_addstr(stdscr, box_y + 1 + i, box_x + 1,
                                         ln, _c.color_pair(_C_VALUE))
                    else:
                        _box_line(stdscr, box_y, box_x, box_w, 0,
                                  "（这个文件解不开——可能是 WebP/HEIC）",
                                  _c.color_pair(_C_DIM))
        except Exception as e:                           # noqa: BLE001
            _box_line(stdscr, box_y, box_x, box_w, 0,
                      f"预览失败 {type(e).__name__}", _c.color_pair(_C_ERR))

        pos = ""
        if total_rows > view_h:
            pos = f"  {top + 1}-{min(total_rows, top + view_h)}/{total_rows}"
        hint = f"  ↑↓ 滚动  PgUp/PgDn 翻页  f 切换缩放  Esc 返回{pos}"
        _safe_addstr(stdscr, box_y + box_h - 2, box_x + 2,
                     _pad(_trunc(hint, box_w - 4), box_w - 5),
                     _c.color_pair(_C_DIM))
        stdscr.refresh()

        ch = _read_key(stdscr)
        if ch is None:
            continue
        code = _key_code(ch)
        if code == 27 or code == 3:
            return
        if _key_is(ch, "f"):
            # 现场切换缩放策略（方便用户对比 fill / ratio 哪个观感更好）
            cur = (os.environ.get("IMGAGENT_TUI_FIT") or "fill").lower()
            nxt = "ratio" if cur == "fill" else "fill"
            os.environ["IMGAGENT_TUI_FIT"] = nxt
            clear_preview_cache()
            log(f"预览缩放 → {nxt}", "info")
        elif code == _c.KEY_UP:
            top = max(0, top - 1)
        elif code == _c.KEY_DOWN:
            top = min(max(0, total_rows - view_h), top + 1)
        elif code == _c.KEY_PPAGE:
            top = max(0, top - view_h)
        elif code == _c.KEY_NPAGE:
            top = min(max(0, total_rows - view_h), top + view_h)
        elif code == _c.KEY_HOME:
            top = 0
        elif code == _c.KEY_END:
            top = max(0, total_rows - view_h)


def _act_cycle_quality(sess) -> None:
    """循环切换质量档。

    改进：除消息面板外，再弹一个**短暂的顶部提示**（用户按完立刻能看到），
    否则反馈只在最下面的「消息」里，看起来像"没反应"。
    """
    from .settings import quality_choices
    choices = quality_choices(sess.model)
    cur = sess.quality
    idx = choices.index(cur) if cur in choices else 0
    sess.quality = choices[(idx + 1) % len(choices)]
    sess.save_config()
    n = len(choices)
    log(f"质量 → {sess.quality}（第 {idx + 2}/{n} 档，Ctrl+P 继续切换）", "ok")
    _flash(f"质量 → {sess.quality}")


def _act_upload(stdscr, sess, from_home: bool = False) -> None:
    """上传/导入图片（TUI 原生：浮窗选文件 → 落盘 → 入库）。

    旧实现调 ui.do_upload()（print + input），必须挂起 curses。
    现在把"选哪张"做成 TUI 列表浮窗，落盘逻辑直接调 store 的公共函数。
    """
    from .store import Item, safe_filename, ext_for
    from .pngcodec import sniff_media_type, png_size

    # ---- 1) 选文件 ----
    if from_home:
        paths = _list_home_images()          # 脚本目录里的图
        title = "选脚本目录里的图"
    else:
        paths = _scan_photo_paths()          # 相册/共享目录里的图
        title = "选相册里的图"
    if not paths:
        log("没找到可选的图片", "warn")
        if not from_home:
            log("提示：相册读不到时，可在 Termux 里跑 termux-setup-storage", "info")
        return

    labels = [f"{p.name}   ({_fmt_size(p)})" for p in paths]
    got = _pick_list(stdscr, sess, title, labels)
    if got is None:
        return
    src = paths[labels.index(got)]

    # ---- 2) 读取 + 校验 ----
    try:
        data = src.read_bytes()
    except OSError as e:
        log(f"读不到文件：{e}", "err")
        return
    from .settings import MAX_UPLOAD
    if len(data) > MAX_UPLOAD:
        log(f"超过 {MAX_UPLOAD // 1048576}MB 上限", "err")
        return
    media = sniff_media_type(data)
    if media is None:
        log("不是 PNG/JPEG/WebP/GIF/BMP（文件头不匹配）", "err")
        return

    # ---- 3) 落盘 ----
    import time as _t
    from .settings import HOME
    dest_name = safe_filename(_t.strftime("%m%d_%H%M%S_import"), src.stem,
                              "." + ext_for(media), limit=28)
    try:
        (HOME / dest_name).write_bytes(data)
    except OSError as e:
        log(f"无法写入 {HOME}：{e}", "err")
        return

    it = Item(file=dest_name, prompt=f"（导入 {src.name}）", kind="import",
              note=str(src))
    sess.push(it)
    sess.log(it)
    wh = png_size(data) if media == "image/png" else None
    log(f"已导入 {dest_name}"
        f"{f'  {wh[0]}x{wh[1]}' if wh else ''}  {len(data) / 1024:.0f}KB", "ok")
    log("现在按 e 可基于这张图修改", "info")


def _fmt_size(p) -> str:
    try:
        n = p.stat().st_size
    except OSError:
        return "?"
    if n < 1024:
        return f"{n}B"
    if n < 1048576:
        return f"{n // 1024}KB"
    return f"{n / 1048576:.1f}MB"


def _scan_photo_paths() -> list:
    """扫描相册/共享目录里的图片（不 print）。"""
    try:
        from . import photos
        found = photos.scan(budget=6.0)
        if found:
            return list(found)
    except Exception as e:                               # noqa: BLE001
        log(f"扫描相册失败：{type(e).__name__}", "warn")
    return []


def _list_home_images() -> list:
    """列出 settings.HOME 里的图片。"""
    from .settings import HOME, IMG_EXT
    out = []
    try:
        for f in sorted(HOME.iterdir()):
            if f.suffix.lower() in IMG_EXT and f.is_file():
                out.append(f)
    except OSError:
        pass
    return out


def _act_history(stdscr, sess) -> None:
    """历史 / 回退（TUI 原生列表浮窗）。"""
    if not sess.items:
        log("历史为空", "warn")
        return
    labels = []
    for i, it in enumerate(sess.items):
        mark = "← 当前" if i == 0 else ""
        preview = (it.prompt or "").replace("\n", " ")[:28]
        labels.append(f"{i})  ${it.cost:.4f}  {it.file[:34]}  {preview} {mark}")
    got = _pick_list(stdscr, sess, f"历史 {len(sess.items)} 张（选一条回到它）",
                     labels)
    if got is None:
        return
    idx = labels.index(got)
    if idx == 0:
        log("这已经是当前图了", "info")
        return
    item = sess.items.pop(idx)
    sess.items.insert(0, item)
    sess.save_state()
    log(f"已回到 {item.file}", "ok")


def _act_settings(stdscr, sess, st) -> None:
    """TUI 原生设置面板（不挂起 curses）。

    为什么重写：旧实现调用命令行版的 ui.do_settings()，必须先
    endwin() 挂起 curses —— 用户看来就是"按设置跳出了 GUI"，
    而且返回后整个布局会闪一下。改成 curses 内的模态列表。
    """
    from .settings import provider_label
    items: list[tuple[str, str]] = []          # (标签, 当前值)

    def rebuild() -> None:
        items.clear()
        items.extend([
            ("模型", sess.model),
            ("质量", sess.quality),
            ("画幅", sess.aspect),
            ("离线模式", "是" if sess.offline else "否"),
            ("生成后自动问预览", "是" if sess.preview else "否"),
            ("provider", provider_label()),
            ("API key", "(已设置)" if _has_key() else "(未设置)"),
            ("润色", "开" if _polish_on() else "关"),
            ("润色详情", "→"),
            ("配置润色", "→"),
            ("查看数据目录", "→"),
            ("环境状态", "→"),
            ("密钥诊断", "→"),
        ])
    rebuild()

    sel = 0
    while True:
        h, w = stdscr.getmaxyx()
        stdscr.erase()
        _draw_topbar(stdscr, 0, 0, w, 3, st, sess.offline)

        box_h = min(h - 5, max(8, len(items) + 4))
        box_y = max(3, (h - box_h) // 2)
        box_w = min(w - 2, max(40, min(70, w - 4)))
        box_x = max(0, (w - box_w) // 2)
        _box(stdscr, box_y, box_x, box_h, box_w, "设置",
             attr=_c.color_pair(_C_FRAME),
             title_attr=_c.color_pair(_C_TITLE) | _c.A_BOLD, focused=True)

        for i, (label, value) in enumerate(items):
            if i >= box_h - 3:
                break
            mark = "▶ " if i == sel else "  "
            # 用 _pad（按**视觉宽**补齐）而不是 f"{label:<18}"（按字符数）：
            # 中文标签视觉宽是字符数的 2 倍，用字符数补齐会导致值起始列不齐。
            line = f"{mark}{_pad(label, 18)} {value}"
            attr = (_c.color_pair(_C_HL) | _c.A_BOLD) if i == sel \
                else _c.color_pair(_C_VALUE)
            _box_line(stdscr, box_y, box_x, box_w, i + 1, line, attr)

        hint = "  ↑↓ 选择   Enter 修改   Esc 关闭  "
        _safe_addstr(stdscr, box_y + box_h - 2, box_x + 2,
                     _pad(_trunc(hint, box_w - 4), box_w - 5),
                     _c.color_pair(_C_DIM))
        stdscr.refresh()
        _dump_debug(stdscr, {"panel": "settings", "size": (h, w)}, suffix=".set")

        ch = _read_key(stdscr)
        if ch is None:
            continue
        code = _key_code(ch)
        if code == 27:
            break
        if code == _c.KEY_UP:
            sel = max(0, sel - 1)
            continue
        if code == _c.KEY_DOWN:
            sel = min(len(items) - 1, sel + 1)
            continue
        if code in (_c.KEY_ENTER,) or _key_is(ch, "\n", " "):
            label = items[sel][0]
            _settings_edit(stdscr, sess, st, label)
            sess.save_config()
            rebuild()

    stdscr.clear()


def _has_key() -> bool:
    try:
        from .store import load_key
        return bool(load_key(silent=True))
    except Exception:                                    # noqa: BLE001
        return False


def _polish_on() -> bool:
    try:
        from . import polish
        return bool(polish.enabled())
    except Exception:                                    # noqa: BLE001
        return False


def _settings_edit(stdscr, sess, st, label: str) -> None:
    """修改一个设置项（在 TUI 内选择，不挂起 curses）。"""
    from .settings import (model_choices, quality_choices, ASPECTS,
                           provider_label)

    if label == "模型":
        choices = model_choices()
        got = _pick_list(stdscr, sess, f"选择模型（{provider_label()}）", choices)
        if got:
            sess.model = got
            # 换模型后质量档可能失效（xhigh/max 只有 2.5 系支持）
            from .settings import quality_supported
            if not quality_supported(sess.quality, sess.model):
                old = sess.quality
                sess.quality = "low"
                log(f"模型不支持 {old}，质量已回退到 low", "warn")
            log(f"模型 → {sess.model}", "ok")
        return

    if label == "质量":
        choices = quality_choices(sess.model)
        got = _pick_list(stdscr, sess, "选择质量档", choices)
        if got:
            sess.quality = got
            log(f"质量 → {sess.quality}", "ok")
        return

    if label == "画幅":
        got = _pick_list(stdscr, sess, "选择画幅", list(ASPECTS))
        if got:
            sess.aspect = got
            log(f"画幅 → {sess.aspect}", "ok")
        return

    if label == "离线模式":
        sess.offline = not sess.offline
        log(f"离线模式 → {'开' if sess.offline else '关'}", "ok")
        return

    if label == "生成后自动问预览":
        sess.preview = not sess.preview
        log(f"自动预览 → {'开' if sess.preview else '关'}", "ok")
        return

    if label == "provider":
        from .settings import (set_provider, provider_choices,
                               model_matches_provider, default_model,
                               quality_supported)
        pairs = provider_choices()
        labels = [f"{name}{'  ← 当前' if k == settings.API_PROVIDER else ''}"
                  for k, name in pairs]
        got = _pick_list(stdscr, sess, "选择 provider", labels)
        if got is None:
            return
        idx = labels.index(got)
        key, name = pairs[idx]
        set_provider(provider=key)
        log(f"provider → {name}", "ok")
        # 模型名跟 provider 走：旧名字在新 provider 下一定无效
        if not model_matches_provider(sess.model):
            sess.model = default_model()
            log(f"模型不匹配，已改为 {sess.model}", "warn")
        # 质量档也要纠正（xhigh/max 在 OpenRouter 上会 400）
        if not quality_supported(sess.quality, sess.model):
            old_q = sess.quality
            sess.quality = "low"
            log(f"质量 {old_q} 不受支持，已回退 low", "warn")
        return

    if label == "润色":
        from . import polish
        settings.set_polish(enabled=not polish.enabled())
        log(f"润色 → {'开' if polish.enabled() else '关'}", "ok")
        return

    if label == "润色详情":
        from . import polish
        _k = polish._key()
        _message_box(stdscr, sess, "润色配置", [
            f"状态   {'开' if polish.enabled() else '关'}",
            f"端点   {polish._endpoint()}",
            f"模型   {polish._model()}",
            f"key    {(_k[:6] + '...' + _k[-4:]) if _k and len(_k) > 10 else (_k or '（未配置）')}",
            "在「配置润色」里填写端点/模型/key",
            "（也可用环境变量 IMGAGENT_POLISH_*）",
        ])
        return

    if label == "配置润色":
        _act_polish_config(stdscr, sess)
        return

    if label == "查看数据目录":
        _act_data_dir(stdscr, sess)
        return
    if label == "环境状态":
        _act_env(stdscr, sess)
        return
    if label == "密钥诊断":
        _act_key_doctor(stdscr, sess)
        return

    if label == "API key":
        # key 必须用 input() 输入（curses 里做掩码输入不划算），
        # 这是**唯一**会短暂挂起 curses 的设置项 —— 且是用户主动点进来的，
        # 不会造成"按设置键就跳出 GUI"的意外观感。
        def fn() -> None:
            from .ui import _input_key_for_current_provider
            _input_key_for_current_provider(sess)
        _suspend_and_run(stdscr, fn)
        sess.save_config()
        return


def _act_polish_config(stdscr, sess) -> None:
    """配置润色（端点/模型/key），在 TUI 内输入并持久化。

    仿 key 输入的做法：短暂挂起 curses 用 input() 收文本（curses 内做掩码/
    行编辑输入不划算），但这是用户主动点进来的设置项，不会造成"按设置键就
    跳出界面"的意外观感。
    """
    from . import polish
    cur_base = polish._base_url()
    cur_model = polish._model()

    def fn() -> None:
        from .console import info, ok, print as cprint, dim
        cprint(f"\n  {info('配置提示词润色')}")
        cprint(dim("   （留空 = 不改动该项；端点/模型存 config.json，"
                   "key 存独立文件）"))
        cprint(f"   当前端点：{cur_base}")
        cprint(f"   当前模型：{cur_model}")
        _k = polish._key()
        cprint(f"   当前 key ：{(_k[:6] + '...' + _k[-4:]) if _k and len(_k) > 10 else (_k or '（未配置）')}")
        b = input("  端点 base_url（留空跳过）> ").strip()
        m = input("  模型名（留空跳过）> ").strip()
        k = input("  key（留空跳过）> ").strip()
        if b:
            settings.set_polish(base_url=b, persist=True)
            cprint(f"  {ok('[OK]')} 端点 -> {b}")
        if m:
            settings.set_polish(model=m, persist=True)
            cprint(f"  {ok('[OK]')} 模型 -> {m}")
        if k:
            settings.set_polish(api_key=k, persist=True)
            cprint(f"  {ok('[OK]')} key 已保存")
        if not (b or m or k):
            cprint(dim("  · 未做任何改动"))

    _suspend_and_run(stdscr, fn)
    log(f"润色配置：{'可用' if polish.enabled() else '仍不可用'}", "info")
    return


def _message_box(stdscr, sess, title: str, lines: list[str]) -> None:
    """信息浮层（按任意键关闭）。用于展示只读信息，不挂起 curses。"""
    while True:
        h, w = stdscr.getmaxyx()
        stdscr.erase()
        _draw_topbar(stdscr, 0, 0, w, 3, {"busy": "", "spin": 0, "t0": 0},
                     sess.offline)
        box_h = min(h - 5, max(6, len(lines) + 4))
        box_y = max(3, (h - box_h) // 2)
        box_w = min(w - 2, max(40, min(72, w - 4)))
        box_x = max(0, (w - box_w) // 2)
        _box(stdscr, box_y, box_x, box_h, box_w, _trunc(title, box_w - 6),
             attr=_c.color_pair(_C_FRAME),
             title_attr=_c.color_pair(_C_TITLE) | _c.A_BOLD, focused=True)
        for i, ln in enumerate(lines[:box_h - 3]):
            _box_line(stdscr, box_y, box_x, box_w, i + 1,
                      _trunc(ln, box_w - 4), _c.color_pair(_C_VALUE))
        _safe_addstr(stdscr, box_y + box_h - 2, box_x + 2,
                     _pad(_trunc("  按任意键关闭  ", box_w - 4),
                          box_w - 5),
                     _c.color_pair(_C_DIM))
        stdscr.refresh()
        if _read_key(stdscr) is not None:
            return


def _pick_pages(stdscr, sess, title: str, choices: list[str],
                extra: list[tuple[str, str]] | None = None) -> str | None:
    """**逐条翻页**的选择浮窗（←→ 切换，长文本完整折行显示）。

    为什么需要它（真机反馈"润色界面每一行显示不完全"）：
      _pick_list 把每个候选用 _trunc 压成一行，40-50 字的中文候选在
      手机竖屏（约 40-50 列 ≈ 20-25 个汉字）只能看到前一半。
      而润色候选天生就长（模型爱写细节），截断等于看不到重点。

    现在每条候选占**一整页**，按显示宽度自动折行，完整可读。
    ←→ 翻页，Enter 选用，Esc 取消。

    extra: 附加的可选项（如"不做润色"/"重新生成"），
           它们显示在候选**之后**，同样可以用 ←→ 翻到。
    """
    pages: list[tuple[str, str]] = [(f"候选 {i + 1}", c)
                                    for i, c in enumerate(choices)]
    for label, _ in (extra or []):
        pages.append((label, ""))        # 动作项，没有正文
    if not pages:
        return None

    idx = 0
    scroll = 0                        # 超长候选的正文滚动位置
    while True:
        h, w = stdscr.getmaxyx()
        stdscr.erase()
        _draw_topbar(stdscr, 0, 0, w, 3, {"busy": "", "spin": 0, "t0": 0},
                     sess.offline)

        box_h = max(8, min(h - 4, 20))
        box_y = max(3, (h - box_h) // 2)
        box_w = min(w - 2, max(36, min(84, w - 4)))
        box_x = max(0, (w - box_w) // 2)

        page_label, body = pages[idx]
        head = f"{title} · {page_label}（{idx + 1}/{len(pages)}）"
        _box(stdscr, box_y, box_x, box_h, box_w, _trunc(head, box_w - 6),
             attr=_c.color_pair(_C_FRAME),
             title_attr=_c.color_pair(_C_TITLE) | _c.A_BOLD, focused=True)

        inner_w = max(8, box_w - 6)
        rows = max(1, box_h - 4)

        wrapped_overflow = False
        if body:
            # 正文按显示宽度折行（中文算 2 列），完整可读。
            # ⚠️ 折行后可能超出可见行数（超长候选），所以支持 ↑↓ 滚动。
            lines = _wrap_by_width(body, inner_w)
            too_long = len(lines) > rows
            wrapped_overflow = too_long
            top2 = max(0, min(scroll, max(0, len(lines) - rows)))
            show = lines[top2:top2 + rows]
            for i, ln in enumerate(show):
                _box_line(stdscr, box_y, box_x, box_w, i + 1, "  " + ln,
                          _c.color_pair(_C_VALUE))
            if too_long:
                pos = f"{top2 + 1}-{min(len(lines), top2 + rows)}/{len(lines)}"
                _safe_addstr(stdscr, box_y + 1, box_x + box_w - len(pos) - 4,
                             f"[{pos}]", _c.color_pair(_C_DIM))
        else:
            _box_line(stdscr, box_y, box_x, box_w, 1, "  （确认执行）",
                      _c.color_pair(_C_DIM))

        nav = "  ←→ 翻页" + ("  ↑↓ 滚动" if wrapped_overflow else "") \
            + "   Enter 选用   Esc 取消"
        _safe_addstr(stdscr, box_y + box_h - 2, box_x + 2,
                     _pad(_trunc(nav, box_w - 4), box_w - 5),
                     _c.color_pair(_C_DIM))
        stdscr.refresh()
        _dump_debug(stdscr, {"panel": "pick_pages", "title": title,
                             "page": idx + 1, "total": len(pages)},
                    suffix=".pick")

        ch = _read_key(stdscr)
        if ch is None:
            continue
        code = _key_code(ch)
        if code == 27:
            return None
        if code in (_c.KEY_LEFT,):
            idx = max(0, idx - 1)
            scroll = 0                # 翻页时重置滚动
            continue
        if code in (_c.KEY_RIGHT,):
            idx = min(len(pages) - 1, idx + 1)
            scroll = 0
            continue
        if code == _c.KEY_HOME:
            idx = 0
            scroll = 0
            continue
        if code == _c.KEY_END:
            idx = len(pages) - 1
            scroll = 0
            continue
        # ↑↓ 滚动正文（超长候选时用）——翻页请用 ←→
        if code == _c.KEY_UP:
            scroll = max(0, scroll - 1)
            continue
        if code == _c.KEY_DOWN:
            scroll += 1
            continue
        if code in (_c.KEY_PPAGE,):
            scroll = max(0, scroll - rows)
            continue
        if code in (_c.KEY_NPAGE,):
            scroll += rows
            continue
        if code in (_c.KEY_ENTER,) or _key_is(ch, "\n", "\r", " "):
            if body:
                return choices[idx]
            # 动作项：返回它的标签（调用方按标签处理）
            return extra[idx - len(choices)][0]
    return None


def _pick_list(stdscr, sess, title: str, choices: list[str]) -> str | None:
    """curses 内的单选浮层。返回选中项，Esc 返回 None。

    与 _suspend_and_run 的区别：**完全不离开 curses**，所以不会
    出现"跳出 GUI"的观感。
    """
    if not choices:
        return None
    sel = 0
    top = 0
    while True:
        h, w = stdscr.getmaxyx()
        stdscr.erase()
        _draw_topbar(stdscr, 0, 0, w, 3, {"busy": "", "spin": 0, "t0": 0},
                     sess.offline)
        box_h = min(h - 5, max(6, min(len(choices) + 4, 18)))
        box_y = max(3, (h - box_h) // 2)
        box_w = min(w - 2, max(40, min(78, w - 4)))
        box_x = max(0, (w - box_w) // 2)
        _box(stdscr, box_y, box_x, box_h, box_w, _trunc(title, box_w - 6),
             attr=_c.color_pair(_C_FRAME),
             title_attr=_c.color_pair(_C_TITLE) | _c.A_BOLD, focused=True)

        visible = box_h - 3
        if sel < top:
            top = sel
        elif sel >= top + visible:
            top = sel - visible + 1
        for i in range(top, min(len(choices), top + visible)):
            mark = "▶ " if i == sel else "  "
            attr = (_c.color_pair(_C_HL) | _c.A_BOLD) if i == sel \
                else _c.color_pair(_C_VALUE)
            _box_line(stdscr, box_y, box_x, box_w, i - top + 1,
                      mark + _trunc(choices[i], box_w - 6), attr)

        _safe_addstr(stdscr, box_y + box_h - 2, box_x + 2,
                     _pad(_trunc("  ↑↓ 选择   Enter 确认   Esc 取消",
                                 box_w - 4), box_w - 5),
                     _c.color_pair(_C_DIM))
        stdscr.refresh()
        _dump_debug(stdscr, {"panel": "pick", "title": title},
                    suffix=".pick")

        ch = _read_key(stdscr)
        if ch is None:
            continue
        code = _key_code(ch)
        if code == 27:
            return None
        if code == _c.KEY_UP:
            sel = max(0, sel - 1)
            continue
        if code == _c.KEY_DOWN:
            sel = min(len(choices) - 1, sel + 1)
            continue
        if code == _c.KEY_PPAGE:
            sel = max(0, sel - 10)
            continue
        if code == _c.KEY_NPAGE:
            sel = min(len(choices) - 1, sel + 10)
            continue
        if code == _c.KEY_ENTER or _key_is(ch, "\n", " "):
            return choices[sel]


# ======================================================================
#  通用浮窗：让命令行版 ui.* 函数在 curses 内运行
# ======================================================================
class _CapturePrint:
    """临时把 print 的输出收进列表（不写到终端）。

    为什么需要：ui.py 里的函数（do_data_dir / do_android_status / …）
    全是 print 驱动。在 curses 运行时它们会把界面冲成花屏。
    用它把输出**收集起来**，再由 _output_overlay() 画成浮窗。
    """

    def __init__(self) -> None:
        self.lines: list[str] = []
        self._old = None

    def __enter__(self):
        self._old = sys.stdout

        class _Sink(io.TextIOBase):
            def __init__(self, bucket):
                self.bucket = bucket
            def write(self, s):
                for ln in s.splitlines():
                    self.bucket.append(ln)
                return len(s)
            def flush(self):
                pass

        sys.stdout = _Sink(self.lines)
        return self

    def __exit__(self, *a):
        if self._old is not None:
            sys.stdout = self._old
        return False


def _output_overlay(stdscr, sess, title: str, lines: list[str],
                    max_lines: int = 0) -> None:
    """把一组文本行显示成可滚动的浮窗（按 Esc 关闭，↑↓ 滚动）。"""
    if not lines:
        lines = ["(没有输出)"]
    top = 0
    while True:
        h, w = stdscr.getmaxyx()
        stdscr.erase()
        _draw_topbar(stdscr, 0, 0, w, 3, {"busy": "", "spin": 0, "t0": 0},
                     sess.offline if sess else False)
        # 顶栏占 0..2 行；浮窗从第 3 行开始，避免和顶栏底边重叠
        box_y, box_x = 3, 1
        box_h = h - box_y
        box_w = w - 2
        _box(stdscr, box_y, box_x, box_h, box_w, _trunc(title, box_w - 8),
             attr=_c.color_pair(_C_FRAME),
             title_attr=_c.color_pair(_C_TITLE) | _c.A_BOLD, focused=True)
        view_h = box_h - 3
        # 自动滚到底（首次显示时最有用：用户想看最新的）
        if max_lines and len(lines) > view_h and top == 0:
            top = max(0, len(lines) - view_h)
        top = max(0, min(top, max(0, len(lines) - view_h)))
        for i in range(view_h):
            k = top + i
            if k >= len(lines):
                break
            _box_line(stdscr, box_y, box_x, box_w, i,
                      _trunc(lines[k], box_w - 4), _c.color_pair(_C_VALUE))
        pos = f"{top + 1}-{min(len(lines), top + view_h)}/{len(lines)}"
        _safe_addstr(stdscr, box_y + box_h - 2, box_x + 2,
                     _pad(_trunc(f"  ↑↓ 滚动  PgUp/PgDn 翻页  Esc 关闭   {pos}",
                                 box_w - 4), box_w - 5),
                     _c.color_pair(_C_DIM))
        stdscr.refresh()
        _dump_debug(stdscr, {"overlay": title, "lines": len(lines)},
                    suffix=".ov")

        ch = _read_key(stdscr)
        if ch is None:
            continue
        code = _key_code(ch)
        if code == 27 or code == 3:
            return
        if code == _c.KEY_UP:
            top = max(0, top - 1)
        elif code == _c.KEY_DOWN:
            top = min(max(0, len(lines) - view_h), top + 1)
        elif code in (_c.KEY_PPAGE,):
            top = max(0, top - view_h)
        elif code in (_c.KEY_NPAGE,):
            top = min(max(0, len(lines) - view_h), top + view_h)
        elif code == _c.KEY_HOME:
            top = 0
        elif code == _c.KEY_END:
            top = max(0, len(lines) - view_h)


def _run_captured(stdscr, sess, title: str, fn, *,
                  scroll_to_end: bool = False) -> list[str]:
    """在 curses 内执行 fn（print 被捕获），然后弹浮窗展示输出。

    返回捕获到的行（调用方可以据此判断结果）。
    """
    lines: list[str] = []
    err: str = ""
    try:
        with _CapturePrint() as cap:
            fn()
        lines = [ln.rstrip() for ln in cap.lines]
    except (EOFError, KeyboardInterrupt):
        lines.append("(已中断)")
    except Exception as e:                               # noqa: BLE001
        err = f"{type(e).__name__}: {e}"
        lines.append(f"[X] 出错：{err}")
    lines = [ln for ln in lines if ln.strip()] or ["(没有输出)"]
    if err:
        log(f"{title} 出错：{err}", "err")
    _output_overlay(stdscr, sess, title, lines,
                    max_lines=1 if scroll_to_end else 0)
    return lines


def _ui_call(name: str, *args):
    """调用 ui.py 里的一个函数（延迟导入，避免循环依赖）。"""
    from . import ui as _ui
    return getattr(_ui, name)(*args)


def _act_data_dir(stdscr, sess) -> None:
    """数据目录信息（curses 浮窗，不挂起）。"""
    _run_captured(stdscr, sess, "数据目录",
                  lambda: _ui_call("do_data_dir"))


# ======================================================================
#  两张图的概念（务必分清）
# ======================================================================
#  **待修改图**（base）—— 要被改的那张。你选中的历史图。
#      · 只能有 1 张
#      · 它是"主体/底图"，输出会保留它的构图与主体
#      · 快捷键：PgUp/PgDn 选图即改它；它总是 image_urls 的**第 1 张**
#
#  **参考图**（refs）—— 提供"人物设定 / 场景 / 风格"的素材。
#      · 最多 REF_MAX 张
#      · 输出**不是**参考图本身，而是"用参考图的设定去改待修改图"
#      · 快捷键：Ctrl+Y 加/删（加的是当前选中的历史图）
#
#  实测依据（APIMart /images/generations）：
#    POST {"image_urls": [A, B], "prompt": "A 是主体，把背景换成 B 的样式"}
#    → 输出 = 主体来自 A、背景来自 B 的**新图**
#    （用纯红图 + 蓝白图验证：输出上蓝/下白/中部红蓝混合）
#  所以 image_urls 的**顺序有语义**：第 1 张 = 主体（待修改图），
#  后面的 = 参考。这正是"以参考图设定修改原图"的实现方式。
#
#  ⚠️ 旧版的错误：把参考图直接当成 image_urls 传上去，
#     结果模型把它们当成"要融合的素材"，输出更接近参考图本身，
#     用户反馈"好像只是高清化了参考图，没有修改我的原图"。
#     现在明确：待修改图**永远排第一**，参考图跟在后面。
# ======================================================================

REF_MAX = 8          # 参考图上限（API 支持 16 张）（防止上传慢、费用高）
_REF_SEL = None      # 参考图列表里的"选中项"（用于删除），全局避免污染 st


def _ref_sel(st: dict) -> int:
    """参考图列表里当前选中的下标（Home/End/Ctrl+K 用）。"""
    return max(0, min(int(st.get("ref_sel", 0)), max(0, len(_ref_files(st)) - 1)))


def _ref_files(st: dict) -> list[str]:
    return st.setdefault("refs", [])


def _act_cycle_aspect(sess, st) -> None:
    """Ctrl+W：循环切换画幅（和 Ctrl+P 换质量档对称）。"""
    from .settings import ASPECTS
    choices = list(ASPECTS)
    cur = getattr(sess, "aspect", "") or "1:1"
    idx = choices.index(cur) if cur in choices else 0
    sess.aspect = choices[(idx + 1) % len(choices)]
    try:
        sess.save_config()
    except Exception:                            # noqa: BLE001
        pass
    n = len(choices)
    log(f"画幅 → {sess.aspect}（第 {idx + 2}/{n} 档，Ctrl+W 继续）", "ok")
    _flash(f"画幅 → {sess.aspect}")


def _act_cycle_model(sess, st) -> None:
    """Ctrl+M：循环切换模型（flare ↔ sunburst）。

    ⚠️ sunburst 不支持 xhigh/max 质量档。切过去时若当前是 xhigh/max，
    自动降级到 high，并记一条 warn 提示。
    """
    from .settings import MODEL_CHOICES_APIMART
    models = [m for m in MODEL_CHOICES_APIMART if "gpt-image-2.5" in m]
    cur = sess.model
    idx = models.index(cur) if cur in models else 0
    nxt = models[(idx + 1) % len(models)]
    # 检查质量档合法性：sunburst 只有 auto/low/medium/high
    import importlib
    import impydroid.settings as _st
    importlib.reload(_st)
    if not _st.quality_supported(sess.quality, nxt):
        old_q = sess.quality
        sess.quality = "low"
        log(f"模型切到 {nxt}（{old_q} 不支持，已降级为 low）", "warn")
    else:
        log(f"模型 → {nxt}", "ok")
    sess.model = nxt
    try:
        sess.save_config()
    except Exception:                            # noqa: BLE001
        pass
    _flash(f"模型 → {nxt}")


def _act_cycle_resolution(sess, st) -> None:
    """Ctrl+R：循环切换分辨率（1k → 2k → 4k → 1k）。"""
    from .settings import RESOLUTIONS
    choices = list(RESOLUTIONS)
    cur = getattr(sess, "resolution", "1k") or "1k"
    idx = choices.index(cur) if cur in choices else 0
    sess.resolution = choices[(idx + 1) % len(choices)]
    try:
        sess.save_config()
    except Exception:                            # noqa: BLE001
        pass
    n = len(choices)
    log(f"分辨率 → {sess.resolution}（第 {idx + 2}/{n} 档，Ctrl+R 继续）", "ok")
    _flash(f"分辨率 → {sess.resolution}")


def _act_cycle_fmt(sess, st) -> None:
    """Ctrl+J：循环切换输出格式（png → jpeg → webp → png）。"""
    from .settings import OUTPUT_FORMATS
    choices = list(OUTPUT_FORMATS)
    cur = getattr(sess, "output_format", "png") or "png"
    idx = choices.index(cur) if cur in choices else 0
    sess.output_format = choices[(idx + 1) % len(choices)]
    try:
        sess.save_config()
    except Exception:                            # noqa: BLE001
        pass
    n = len(choices)
    log(f"格式 → {sess.output_format}（第 {idx + 2}/{n} 档，Ctrl+J 继续）", "ok")
    _flash(f"格式 → {sess.output_format}")


def _act_cycle_batch_n(sess, st) -> None:
    """Ctrl+A：循环切换批量张数（1→2→3→4→1）。"""
    cur = getattr(sess, "batch_n", 1)
    nxt = (cur % 4) + 1
    sess.batch_n = nxt
    try:
        sess.save_config()
    except Exception:                            # noqa: BLE001
        pass
    log(f"批量张数 → {nxt}（Ctrl+A 继续）", "ok")
    _flash(f"批量 {nxt} 张")







def _ref_add_or_remove(stdscr, sess, st) -> None:
    """Ctrl+Y：弹出列表，选一张图加入参考图（已在列表里则移出）。

    ⚠️ 为什么用弹窗而不是"加光标处那张"：
    光标已经用来表示「待修改图」了。如果 Ctrl+Y 也用光标，
    那"浏览历史挑参考图"就会顺带改掉待修改图 —— 概念必然混淆。
    弹窗让两个操作彻底独立：浏览只改待修改图，Ctrl+Y 只改参考图。
    """
    items = sess.items
    if not items:
        log("还没有图片", "warn")
        return
    refs = _ref_files(st)
    base = _base_file(sess, st)

    # 候选 = 全部历史图（排除待修改图，避免"自己参考自己"）
    cand = [it for it in items if it.file != base]
    if not cand:
        log("没有别的图可以当参考图（先 Ctrl+G 生成或 Ctrl+F 上传）", "warn")
        return

    choices = []
    for it in cand:
        if it.file in refs:
            choices.append(f"✓ {it.file}   （已在参考图里 · 选它=移出）")
        else:
            choices.append(f"  {it.file}")
    got = _pick_list(stdscr, sess, f"参考图 {len(refs)}/{REF_MAX} · 选一张加入/移出",
                     choices)
    if got is None:
        return
    name = cand[choices.index(got)].file

    if name in refs:
        refs.remove(name)
        st["ref_sel"] = min(st.get("ref_sel", 0), max(0, len(refs) - 1))
        log(f"已移出参考图：{name}（剩 {len(refs)} 张）", "info")
        return
    if len(refs) >= REF_MAX:
        log(f"参考图最多 {REF_MAX} 张，先按 Ctrl+K 或在这里选 ✓ 移出", "warn")
        return
    refs.append(name)
    st["ref_sel"] = len(refs) - 1
    log(f"已加入参考图：{name}（{len(refs)}/{REF_MAX}）"
        + ("  Ctrl+D 开始改图" if refs else ""), "ok")


def _clear_refs(sess, st) -> None:
    """清空参考图列表（Ctrl+K 连按到底后就没了；这个给"一键清空"用）。"""
    n = len(_ref_files(st))
    _ref_files(st).clear()
    st["ref_sel"] = 0
    log(f"已清空参考图（{n} 张）" if n else "参考图列表本来就是空的", "info")


def _ref_delete_selected(st: dict) -> None:
    """Ctrl+K：删掉参考图列表里当前选中的那张。"""
    refs = _ref_files(st)
    if not refs:
        log("参考图列表是空的", "warn")
        return
    i = _ref_sel(st)
    name = refs.pop(i)
    st["ref_sel"] = min(i, max(0, len(refs) - 1))
    log(f"已删除参考图：{name}（剩 {len(refs)} 张）", "info")


def _ref_move(step: int, st: dict) -> None:
    """在参考图列表里上下移动**选中项**（Home/End 用）。

    为什么要单独的选中项：删除（Ctrl+K）需要知道"删哪一张"。
    用 ↑↓ 会与"翻提示词历史"冲突，所以用 Home/End —— 它们原本是
    "历史上第一张/最后一张图"，但那个功能用 PgUp/PgDn 多次按也能达到，
    而"选择参考图里的一张"没有别的键可用。
    """
    n = len(_ref_files(st))
    if n == 0:
        log("参考图列表是空的", "warn")
        return
    cur = _ref_sel(st)
    new_i = max(0, min(n - 1, cur + step))
    if new_i == cur:
        return                       # 到底了，不重复提示
    st["ref_sel"] = new_i
    _flash(f"参考图 {new_i + 1}/{n}")


def _ref_select_first_last(which: str, st: dict) -> None:
    """Home/End：跳到参考图列表的第一张 / 最后一张。"""
    n = len(_ref_files(st))
    if n == 0:
        log("参考图列表是空的", "warn")
        return
    st["ref_sel"] = 0 if which == "first" else n - 1
    _flash(f"参考图 {st['ref_sel'] + 1}/{n}")


def _base_file(sess, st) -> str:
    """「待修改图」= PgUp/PgDn 当前选中的那张。

    概念分工（这是"不混淆"的关键）：
      · **待修改图** —— 光标决定（PgUp/PgDn）。它是要被改的主体。
      · **参考图**   —— 只能通过 Ctrl+Y 弹列表增删，**与光标无关**。
    所以"浏览历史"只会改变前者，"增删参考图"只影响后者，互不干扰。
    """
    items = sess.items
    if not items:
        return ""
    idx = max(0, min(st.get("hist", 0), len(items) - 1))
    return items[idx].file


def load_edit_images(sess, st) -> tuple[list[tuple[bytes, str]], str]:
    """组装编辑用的图片列表：**待修改图在第 1 位，参考图跟后面**。

    返回 (images, 描述文本)。images 为空表示没有可用的图。
    """
    from .pngcodec import shrink_for_reference

    out: list[tuple[bytes, str]] = []
    base = _base_file(sess, st)
    if base:
        it = next((x for x in sess.items if x.file == base), None)
        if it is not None:
            try:
                out.append(shrink_for_reference(it.path.read_bytes(), 1024))
            except Exception as e:                   # noqa: BLE001
                log(f"待修改图读不到：{type(e).__name__}", "warn")

    n_ref = 0
    for name in _ref_files(st):
        if len(out) >= settings_max_refs():
            break
        it = next((x for x in sess.items if x.file == name), None)
        if it is None:
            log(f"参考图已不在历史里，跳过：{name}", "warn")
            continue
        try:
            out.append(shrink_for_reference(it.path.read_bytes(), 1024))
            n_ref += 1
        except Exception as e:                       # noqa: BLE001
            log(f"参考图解码失败 {name}：{type(e).__name__}", "warn")

    if not out:
        return [], ""
    if n_ref:
        desc = f"待修改 {base or '?'} + {n_ref} 张参考图"
    else:
        desc = base or "?"
    return out, desc


def settings_max_refs() -> int:
    """总图片数上限（待修改图 1 张 + 参考图 REF_MAX 张）。"""
    from .settings import MAX_REFS
    return min(MAX_REFS, 1 + REF_MAX)


def _draw_refs(stdscr, y, x, h, w, sess, st) -> None:
    """左下：**待修改图 + 参考图**列表（取代原字符画预览）。

    版面（框内从上到下）：
      第 1 行  ▶ 待修改  [#n] 文件名        ← PgUp/PgDn 选
      第 2 行    参考图 N/4（Ctrl+Y 加/删）  ← 分组标题
      第 3+ 行   参考图条目（▶ 标出选中项）  ← Home/End 选、Ctrl+K 删
      最后一行   操作提示

    ⚠️ 注意 _box_line 的 row 是**框内相对行号**（内部会 +1 跳过上边框），
    而 _safe_addstr 要**绝对行号**。混用会导致两段文字画在同一行互相覆盖
    （v5.13.0 开发中踩过：待修改图那行被"参考图 N/4"覆盖掉）。
    """
    refs = _ref_files(st)
    base = _base_file(sess, st)
    title = f"待修改图 + 参考图 {len(refs)}/{REF_MAX}"
    _box(stdscr, y, x, h, w, title, attr=_c.color_pair(_C_FRAME),
         title_attr=_c.color_pair(_C_TITLE) | _c.A_BOLD,
         focused=bool(base or refs))

    rows = h - 2                      # 框内可用行数
    if rows <= 0:
        return
    sel = _ref_sel(st)

    # ── 第 1 行：待修改图 ──
    if not base:
        _box_line(stdscr, y, x, w, 0, "  （无图）Ctrl+G 生成 / Ctrl+F 上传",
                  _c.color_pair(_C_DIM))
        return
    pos = next((j + 1 for j, it in enumerate(sess.items) if it.file == base), None)
    _box_line(stdscr, y, x, w, 0, f" ▶ 待修改 [#{pos}] {base}",
              _c.color_pair(_C_HL) | _c.A_BOLD)

    if rows < 3:
        return
    # ── 第 2 行：参考图分组标题 ──
    _box_line(stdscr, y, x, w, 1,
              f"   参考图 {len(refs)}/{REF_MAX}（Ctrl+Y 加/删）",
              _c.color_pair(_C_DIM))

    # ── 第 3 行起：参考图条目 ──
    body = rows - 3                   # 给底部提示留 1 行
    if not refs:
        if body > 0:
            _box_line(stdscr, y, x, w, 2, "   （暂无）", _c.color_pair(_C_DIM))
    else:
        for i, name in enumerate(refs[:max(0, body)]):
            p2 = next((j + 1 for j, it in enumerate(sess.items)
                       if it.file == name), None)
            mark = "▶" if i == sel else " "
            _box_line(stdscr, y, x, w, 2 + i, f" {mark} {i + 1}) [#{p2}] {name}",
                      (_c.color_pair(_C_HL) | _c.A_BOLD) if i == sel
                      else _c.color_pair(_C_VALUE))

    # ── 最后一行：操作提示（用绝对行号，因为要贴底）──
    hint = "  Home/End 选参考图 · Ctrl+K 删 · Ctrl+D 改图"
    _safe_addstr(stdscr, y + h - 2, x + 1,
                 _pad(_trunc(hint, w - 2), w - 2),
                 _c.color_pair(_C_OK) | _c.A_BOLD)


def _act_key_doctor(stdscr, sess) -> None:
    """密钥诊断：显示当前 provider、key 来源、实际读到的值。

    为什么需要：用户遇到 401 时，最常见的根因是"provider 没切、key 存在
    另一个文件里"，从界面上完全看不出来。这个浮窗直接摊开来看。
    """
    from .settings import (provider_label, provider_base,
                           API_PROVIDER, key_file_for)
    from .store import load_key
    import os as _os

    lines: list[str] = []
    lines.append(f"当前 provider   {API_PROVIDER}（{provider_label()}）")
    lines.append(f"端点 base       {provider_base()}")
    lines.append("")
    lines.append("── key 读取顺序 ──")
    if API_PROVIDER == "apimart":
        env_name = "IMGAGENT_APIMART_API_KEY"
    else:
        env_name = "OPENROUTER_API_KEY"
    env_val = _os.environ.get(env_name, "")
    lines.append(f"① 环境变量 {env_name}")
    lines.append(f"   {'已设置：' + env_val[:8] + '…' if env_val else '（未设置）'}")
    kf = key_file_for()
    lines.append(f"② key 文件 {kf}")
    try:
        exists = kf.exists()
        size = kf.stat().st_size if exists else 0
    except OSError:
        exists, size = False, 0
    lines.append(f"   存在={exists}  大小={size}B")
    lines.append("")
    lines.append("── 实际读到的 key ──")
    got = ""
    try:
        got = load_key(silent=True) or ""
    except Exception as e:                           # noqa: BLE001
        lines.append(f"  读取失败：{type(e).__name__}: {e}")
    if got:
        lines.append(f"  {got[:8]}…{got[-4:]}  （长度 {len(got)}）")
        lines.append("  ✓ 已读到 key")
    else:
        lines.append("  ✗ **没有读到 key** —— 401 通常就是这个原因")
        lines.append("")
        lines.append("── 怎么办 ──")
        lines.append("在上面的设置列表里选「API key」输入并保存。")
        lines.append("注意：key 是**按 provider 分开存**的。")
        lines.append(f"      当前要存到 → {kf.name}")

    _output_overlay(stdscr, sess, "密钥诊断", lines)


def _act_env(stdscr, sess) -> None:
    """环境状态（curses 浮窗，不挂起）。"""
    _run_captured(stdscr, sess, "环境状态",
                  lambda: _ui_call("do_android_status"))
