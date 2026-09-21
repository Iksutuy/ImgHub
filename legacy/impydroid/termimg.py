"""在**终端里**预览图片 —— 零依赖，不需要 pyjnius，也不需要 repository plugin。

原理：本包已经自带纯 Python 的 PNG 解码器（pngcodec），拿到像素后就能直接
用字符把它"画"出来。三种画法，按终端能力自动选：

    truecolor  用半块字符 ▀ 的**前景色+背景色**一次表示上下两个像素，
               一个字符格 = 2 个像素 -> 竖向分辨率翻倍，效果最好
    256        同上，但颜色量化到 xterm-256 调色板（兼容性更广）
    ascii      灰度字符画（" .:-=+*#%@"），任何终端都能显示

不依赖 Pillow、不依赖 PIL.ImageTk —— 因为图是我们自己解码的。
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

import os
import threading
from collections import OrderedDict
from pathlib import Path

from .console import SYMBOLS_OK, color_enabled, print, rule
from .pngcodec import load_png_rgb

# ---------------------------------------------------------------- 能力探测
# 解码缓存（按 (路径, mtime_ns) 键）。三点设计考量：
#   1. **加锁**：当前只在 curses 主线程用，但 UI 里已有后台线程（_run_bg），
#      将来若在后台预渲染就会并发访问 —— 加锁成本极低，提前防住。
#   2. **LRU 淘汰**：早先是"满了(4 条)就 clear()"，这在交替预览 5 张图时
#      会持续全清、命中率归零。改成 OrderedDict LRU 只淘汰最旧的一条。
#   3. **容量 4**：手机上解码后的 RGB 行数据很占内存（1024² ≈ 3MB），
#      留 4 张够来回切换用，再多就浪费。
_DECODED_CACHE: "OrderedDict[tuple[str, int], tuple[int, int, list[bytes]]]" = \
    OrderedDict()
_DECODED_CACHE_LOCK = threading.Lock()
_DECODED_CACHE_MAX = 4
_ASCII_RAMP = " .:-=+*#%@"


def term_color_mode() -> str:
    """终端支持哪种画法：truecolor / 256 / ascii。

    COLORTERM 里出现 truecolor/24bit 才算真彩色；否则退到 256；
    完全不给颜色（或在管道里）就用 ASCII。
    """
    want = (os.environ.get("IMGAGENT_TERM_IMG") or "auto").strip().lower()
    if want in ("off", "none", "disable"):
        return "off"
    if want in ("ascii", "256", "truecolor"):
        return want
    if not color_enabled():
        return "ascii"
    ct = (os.environ.get("COLORTERM") or "").lower()
    if "truecolor" in ct or "24bit" in ct:
        return "truecolor"
    try:
        cols = int(os.environ.get("TERM_COLORS") or 0)
    except ValueError:
        cols = 0
    term = (os.environ.get("TERM") or "").lower()
    if cols >= (1 << 24) or "direct" in term:
        return "truecolor"
    if "256color" in term or cols >= 256:
        return "256"
    return "256"          # Pydroid 的终端一般是 256 色起步，先按 256 试


# ---------------------------------------------------------------- 解码（带缓存）
def decode_cached(path: Path) -> tuple[int, int, list[bytes]] | None:
    """解码 PNG；按 (路径, mtime) 做 LRU 缓存，反复预览不重复解码。

    线程安全（内部加锁）。解码**在锁外**做 —— 否则多线程预览不同图时
    会被串行化，白白浪费并发。
    """
    try:
        st = path.stat()
        key = (str(path), st.st_mtime_ns)
    except OSError:
        return None

    with _DECODED_CACHE_LOCK:
        hit = _DECODED_CACHE.get(key)
        if hit is not None:
            _DECODED_CACHE.move_to_end(key)      # 标记为"最近用过"
            return hit

    # 锁外解码（可能耗几十毫秒）
    try:
        data = path.read_bytes()
        w, h, rows = load_png_rgb(data)
    except Exception:                     # noqa: BLE001 - 非 PNG / 坏文件 / 不支持的类型
        return None

    with _DECODED_CACHE_LOCK:
        _DECODED_CACHE[key] = (w, h, rows)
        _DECODED_CACHE.move_to_end(key)
        while len(_DECODED_CACHE) > _DECODED_CACHE_MAX:
            _DECODED_CACHE.popitem(last=False)   # 淘汰最旧的一条
    return w, h, rows


def clear_decode_cache() -> None:
    """清空解码缓存（测试用；也可在内存紧张时调用）。"""
    with _DECODED_CACHE_LOCK:
        _DECODED_CACHE.clear()


def _px(rows: list[bytes], w: int, h: int, x: int, y: int) -> tuple[int, int, int]:
    x = 0 if x < 0 else (w - 1 if x >= w else x)
    y = 0 if y < 0 else (h - 1 if y >= h else y)
    i = x * 3
    r = rows[y]
    return r[i], r[i + 1], r[i + 2]


def _xterm256(r: int, g: int, b: int) -> int:
    """把 RGB 映射到 xterm-256 调色板（含 24 级灰阶）。"""
    if abs(r - g) < 12 and abs(g - b) < 12:            # 近似灰 -> 用灰阶段
        v = (r + g + b) // 3
        return 232 + min(23, max(0, (v - 4) * 24 // 248))
    ri = (r * 5 + 127) // 255
    gi = (g * 5 + 127) // 255
    bi = (b * 5 + 127) // 255
    return 16 + 36 * ri + 6 * gi + bi


def _lum(r: int, g: int, b: int) -> float:
    return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0


# ---------------------------------------------------------------- 渲染
def render(path: Path, cols: int | None = None, mode: str | None = None) -> list[str] | None:
    """渲染成若干行字符串。失败（非 PNG／解不开）返回 None。"""
    got = decode_cached(path)
    if got is None:
        return None
    w, h, rows = got
    mode = mode or term_color_mode()
    if mode == "off":
        return None
    cols = cols or max(24, min(64, _fit_cols()))
    # 终端字符格约 1:2（宽:高）。truecolor/256 用半块字符 -> 1 格 = 2 像素，
    # 所以行数 = cols * h / (2 * w) 才能保持比例；ascii 一格一像素 -> h/(2w) 同样系数。
    out_rows = max(1, int(round(cols * h / (2.0 * w))))
    lines: list[str] = []

    if mode == "ascii":
        for j in range(out_rows):
            y = int((j + 0.5) * h / out_rows)
            buf = []
            for i in range(cols):
                x = int((i + 0.5) * w / cols)
                r, g, b = _px(rows, w, h, x, y)
                buf.append(_ASCII_RAMP[min(9, int(_lum(r, g, b) * 10))])
            lines.append("".join(buf))
        return lines

    # truecolor / 256：一个字符格表示上下两像素
    half = "\u2580" if SYMBOLS_OK else "#"
    for j in range(out_rows):
        y0 = int((j * 2 + 0.5) * h / (out_rows * 2))
        y1 = int(((j * 2 + 1) + 0.5) * h / (out_rows * 2))
        reset = "\x1b[0m"
        buf = []
        for i in range(cols):
            x = int((i + 0.5) * w / cols)
            tr, tg, tb = _px(rows, w, h, x, y0)
            br, bg, bb = _px(rows, w, h, x, y1)
            if mode == "truecolor":
                buf.append(f"\x1b[38;2;{tr};{tg};{tb}m\x1b[48;2;{br};{bg};{bb}m{half}")
            else:
                buf.append(f"\x1b[38;5;{_xterm256(tr, tg, tb)}m"
                           f"\x1b[48;5;{_xterm256(br, bg, bb)}m{half}")
        lines.append("".join(buf) + reset)
    return lines


def _fit_cols() -> int:
    try:
        import shutil
        return max(20, shutil.get_terminal_size().columns - 4)
    except Exception:                     # noqa: BLE001
        return 48


def show(path: Path, label: str = "", cols: int | None = None,
         mode: str | None = None) -> bool:
    """在终端里画出来。成功返回 True。"""
    lines = render(path, cols=cols, mode=mode)
    if not lines:
        return False
    used = mode or term_color_mode()
    if label:
        print(rule(label, "menu"))
    for ln in lines:
        print(ln)
    print(rule(f"终端预览 · {used} · {len(lines)} 行", "dim"))
    return True


def supported() -> bool:
    """当前终端能不能画（能不能解出 PNG）。"""
    return term_color_mode() != "off"


def describe() -> str:
    mode = term_color_mode()
    if mode == "off":
        return "关闭"
    tip = {"truecolor": "真彩色", "256": "256 色", "ascii": "字符画"}.get(mode, mode)
    return f"{tip}（{mode}）"
