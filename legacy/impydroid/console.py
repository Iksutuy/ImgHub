"""控制台加固 + 配色 + 分隔线 —— **必须最先导入**。

两件事：

一、加固（防"黑屏无输出"）
    本程序输出里用了 `═ ─ ✓ ✗ ⚠ 👁` 等装饰字符。Pydroid 的终端在某些 ROM /
    编码设置下写不出它们，于是**第一次 print 就抛 UnicodeEncodeError**；
    而异常信息本身也打不出来 —— 用户看到的就是"黑屏、零输出"。
    两道防线：① 把 stdout/stderr 的 errors 改成 "replace"；
             ② 提供本模块的 `print`，永不抛异常 + 强制 flush。

二、配色与分隔线
    四类内容用四种颜色区分：
        menu()   菜单、分区标题        -> 亮青
        info()   系统提示、状态、成功  -> 亮绿
        prompt() 输入提示              -> 亮黄
        正文     普通输出              -> 终端默认色（不打色）
    外加 warn() 黄 / err() 红 / dim() 灰 作为补充。

    颜色只在**真的是终端**时才启用（管道/重定向时自动关闭，免得日志里全是转义码）。
    可用环境变量强制：IMGAGENT_COLOR=always / never / auto
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

import builtins
import os
import sys

_ORIG_PRINT = builtins.print

# ===========================================================================
#  一、加固
# ===========================================================================


def harden_stdio() -> dict:
    """尽量把标准流调成"永远不会因为编码而抛异常"的状态。返回诊断信息。

    Windows 特殊处理：默认代码页（GBK/cp936）会把中文/边框符打成乱码，
    所以 Windows 上**优先尝试强制 UTF-8**——现代终端（Windows Terminal、
    PowerShell 7、chcp 65001）都能正确显示；万一强制失败（老终端），
    仍按原编码 + errors=replace 兜底，且 probe_console() 会自动降级 ASCII，
    不会黑屏。
    """
    info: dict[str, str] = {}
    for name in ("stdout", "stderr"):
        stream = getattr(sys, name, None)
        if stream is None:
            info[name] = "None"
            continue
        enc = getattr(stream, "encoding", None)
        # ① Windows：先试强制 UTF-8（reconfigure 支持 encoding 参数）
        if os.name == "nt":
            try:
                stream.reconfigure(encoding="utf-8", errors="replace",
                                   line_buffering=True)  # type: ignore[union-attr]
                info[name] = f"{enc} -> utf-8 (reconfigured)"
                continue
            except Exception as _e:                      # noqa: BLE001
                info[name] = f"{enc} (utf-8 failed: {type(_e).__name__})"
        try:                                     # ② 常规：只改 errors（3.7+）
            stream.reconfigure(errors="replace", line_buffering=True)  # type: ignore[union-attr]
            info[name] = f"{enc} (reconfigured)"
            continue
        except Exception as e:                   # noqa: BLE001
            info[name] = f"{enc} (reconfigure failed: {type(e).__name__})"
        try:                                     # ③ 退路：自己包一层
            import io
            buf = getattr(stream, "buffer", None)
            if buf is not None:
                setattr(sys, name, io.TextIOWrapper(
                    buf, encoding="utf-8", errors="replace", line_buffering=True))
                info[name] += " -> wrapped"
        except Exception:                        # noqa: BLE001
            pass
    return info


def probe_console() -> tuple[bool, bool]:
    """判断终端能不能写符号/汉字。

    这里**不真写**，而是试着编码 —— 真写会污染输出。既然 errors 已经设成
    replace，真正的写入永远不会抛异常，所以"能不能编码"就是答案。
    """
    enc = getattr(sys.stdout, "encoding", None) or "ascii"
    symbols_ok = cjk_ok = True
    for ch, which in (("\u2550", "sym"), ("\u4e2d", "cjk")):
        try:
            ch.encode(enc)
        except (UnicodeEncodeError, LookupError):
            if which == "sym":
                symbols_ok = False
            else:
                cjk_ok = False
    return symbols_ok, cjk_ok


# 纯装饰性字符 → ASCII 替身（只在终端不支持时启用）
DECOR_MAP = {
    "\u2550": "=", "\u2500": "-", "\u2502": "|",
    "\u256d": "+", "\u256e": "+", "\u2570": "+", "\u256f": "+",
    "\u2713": "[OK]", "\u2717": "[X]", "\u26a0": "[!]",
    "\u2605": "*", "\u25b6": ">", "\U0001F441": "(eye)",
    "\u00b7": ".", "\u2026": "...", "\u2192": "->",
    "\u2588": "#", "\u2591": ".", "\u2592": ":", "\u2593": "*",
    "\u2580": "^", "\u2584": "_",
}

STDIO_INFO = harden_stdio()
SYMBOLS_OK, CJK_OK = probe_console()


def sanitize(text: str) -> str:
    """按终端能力把装饰字符换成 ASCII（ANSI 转义码是 ASCII，会原样保留）。"""
    if CJK_OK and SYMBOLS_OK:
        return text
    for k, v in DECOR_MAP.items():
        if k in text:
            text = text.replace(k, v)
    if not CJK_OK:
        text = text.encode("ascii", "replace").decode("ascii")
    return text


# ===========================================================================
#  二、配色
# ===========================================================================

_CODES = {
    "reset": "\x1b[0m",
    "menu": "\x1b[96m",        # 亮青 —— 菜单、分区标题
    "info": "\x1b[92m",        # 亮绿 —— 系统提示、状态、成功
    "input": "\x1b[93m",       # 亮黄 —— 输入提示
    "warn": "\x1b[33m",        # 黄   —— 警告
    "err": "\x1b[91m",         # 亮红 —— 错误
    "dim": "\x1b[90m",         # 灰   —— 次要信息、分隔线
    "title": "\x1b[1;97m",     # 白粗 —— 大标题
    "path": "\x1b[94m",        # 蓝   —— 文件路径
}

_COLOR_MODE: str | None = None       # 缓存的判定结果
_VT_OK: bool | None = None           # Windows 控制台能否解析 ANSI（缓存）


def _enable_windows_vt() -> bool:
    """Windows：给控制台开启 ANSI 转义序列处理。

    传统 conhost 默认**不解析** ANSI 转义码，会把 `\\x1b[90m` 原样显示成
    `←[90m`（用户看到的"乱码"）。Windows 10 1511+ 支持按需开启
    ENABLE_VIRTUAL_TERMINAL_PROCESSING。开启失败（老系统 / 非终端 /
    重定向到文件）返回 False，调用方据此关闭颜色，避免输出乱码。
    Windows Terminal / PowerShell 7 默认已开，这里是幂等操作。
    """
    global _VT_OK
    if _VT_OK is not None:
        return _VT_OK
    if os.name != "nt":
        _VT_OK = True
        return True
    try:
        import ctypes
        STD_OUTPUT_HANDLE = -11
        ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004
        kernel32 = ctypes.windll.kernel32                 # type: ignore[attr-defined]
        handle = kernel32.GetStdHandle(STD_OUTPUT_HANDLE)
        if handle in (0, -1, None):
            _VT_OK = False
            return False
        mode = ctypes.c_uint32()
        if not kernel32.GetConsoleMode(handle, ctypes.byref(mode)):
            _VT_OK = False                                # 不是控制台（管道/文件）
            return False
        if mode.value & ENABLE_VIRTUAL_TERMINAL_PROCESSING:
            _VT_OK = True                                 # 已开启
            return True
        ok = bool(kernel32.SetConsoleMode(
            handle, mode.value | ENABLE_VIRTUAL_TERMINAL_PROCESSING))
        _VT_OK = ok
        return ok
    except Exception:                                     # noqa: BLE001
        _VT_OK = False
        return False


def color_enabled() -> bool:
    """颜色是否启用。

    规则：环境变量 IMGAGENT_COLOR 优先（always / never / auto），
    auto 时要求 stdout 是终端，且 TERM 不是 dumb。
    这样重定向到文件时不会塞满转义码。

    ⚠️ Windows 额外一关：必须确认控制台能解析 ANSI（见 _enable_windows_vt），
    否则老 conhost 会把颜色码原样吐成 `←[90m` 乱码 —— 那种情况下宁可不上色。
    """
    global _COLOR_MODE
    if _COLOR_MODE is None:
        want = (os.environ.get("IMGAGENT_COLOR") or "auto").strip().lower()
        if want in ("always", "1", "yes", "on", "true"):
            # 用户强制开启：仍尝试打开 VT（幂等）；失败也尊重用户意愿
            _enable_windows_vt()
            _COLOR_MODE = "on"
        elif want in ("never", "0", "no", "off", "false"):
            _COLOR_MODE = "off"
        else:
            term = (os.environ.get("TERM") or "").lower()
            try:
                tty = bool(sys.stdout.isatty())
            except Exception:                    # noqa: BLE001
                tty = False
            if tty and term != "dumb":
                # Windows 老控制台：开不了 VT 就不上色（避免乱码）
                if os.name == "nt" and not _enable_windows_vt():
                    _COLOR_MODE = "off"
                else:
                    _COLOR_MODE = "on"
            else:
                _COLOR_MODE = "off"
    return _COLOR_MODE == "on"


def set_color(mode: str) -> None:
    """强制开关颜色（'always' / 'never' / 'auto'），便于运行时切换。"""
    global _COLOR_MODE
    _COLOR_MODE = None
    os.environ["IMGAGENT_COLOR"] = mode
    color_enabled()


def paint(text: str, *styles: str) -> str:
    """给文本上色。styles 里可以给多个（按顺序叠加）。颜色关闭时原样返回。

    幂等：如果文本里已经有 ANSI 转义码（说明上过色了），直接原样返回 ——
    否则重复上色会产出嵌套的转义码，既浪费字节，也可能让某些终端显示异常。
    """
    if not styles or not text or not color_enabled():
        return text
    if "\x1b[" in text:
        return text
    codes = "".join(_CODES.get(s, "") for s in styles)
    if not codes:
        return text
    return f"{codes}{text}{_CODES['reset']}"


# 语义化快捷函数（模块内其它地方就用这些，别再手写转义码）
def menu(text: str) -> str:
    return paint(text, "menu")


def info(text: str) -> str:
    return paint(text, "info")


def ok(text: str) -> str:
    return paint(text, "info")


def warn(text: str) -> str:
    return paint(text, "warn")


def err(text: str) -> str:
    return paint(text, "err")


def prompt(text: str) -> str:
    return paint(text, "input")


def title(text: str) -> str:
    return paint(text, "title")


def dim(text: str) -> str:
    return paint(text, "dim")


def path(text: str) -> str:
    return paint(text, "path")


# ===========================================================================
#  三、分隔线与排版
# ===========================================================================

def _dwidth(s: str) -> int:
    """字符串的**显示宽度**：CJK 与 emoji 占 2 列，组合符占 0 列。

    不这么做的话，中文标签会让分隔线左右不对称。
    """
    w = 0
    for ch in s:
        o = ord(ch)
        if 0x0300 <= o <= 0x036F or o == 0x200D:
            continue                                    # 组合符/零宽连接符
        if (0x1100 <= o <= 0x115F or 0x2E80 <= o <= 0xA4CF or 0xAC00 <= o <= 0xD7A3
                or 0xF900 <= o <= 0xFAFF or 0xFE30 <= o <= 0xFE6F
                or 0xFF00 <= o <= 0xFF60 or 0xFFE0 <= o <= 0xFFE6
                or 0x1F300 <= o <= 0x1FAFF or 0x1F900 <= o <= 0x1F9FF):
            w += 2
        else:
            w += 1
    return w


# 公开别名：外部（测试、其它模块）也要能算显示宽度
display_width = _dwidth


def term_width(default: int = 64, maximum: int = 72, minimum: int = 30) -> int:
    """分隔线宽度。

    刻意**不撑满终端**：手机竖屏终端很窄，撑满后一换行就断成两截，
    反而更乱。取一个舒适的固定区间，并且给手机留出边距。
    """
    try:
        import shutil
        cols = shutil.get_terminal_size().columns
    except Exception:                                   # noqa: BLE001
        return default
    if cols <= 0:
        return default
    # 两边各留 2 列余量，避免正好顶到边缘被折行
    return max(minimum, min(maximum, cols - 4))


def rule(label: str = "", style: str = "dim", width: int | None = None) -> str:
    """一条分隔线；带 label 时把标签居中。返回字符串（不打印）。"""
    ch = "\u2500" if SYMBOLS_OK else "-"
    w = width or term_width()
    if label:
        lab = f" {label} "
        rest = max(0, w - _dwidth(lab))
        left, right = rest // 2, rest - rest // 2
        return paint(ch * left + lab + ch * right, style)
    return paint(ch * w, style)


def section(label: str = "") -> None:
    """分区开始：空行 + 带标签的分隔线（菜单色）。"""
    print()
    print(rule(label, "menu"))


# ===========================================================================
#  四、安全 print
# ===========================================================================

def print(*args, **kwargs) -> None:       # noqa: A001 - 有意遮蔽内建
    """加固版 print：永不抛异常，永远 flush。"""
    kwargs.setdefault("flush", True)
    try:
        _ORIG_PRINT(*[sanitize(a) if isinstance(a, str) else a for a in args], **kwargs)
    except Exception:                     # noqa: BLE001
        try:
            _ORIG_PRINT(*[str(a).encode("ascii", "replace").decode("ascii")
                          for a in args], flush=True)
        except Exception:                 # noqa: BLE001
            pass                          # 输出失败也不能让程序死掉
