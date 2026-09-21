"""预览 —— 四级降级，任何一级失败都自动往下走。

    ① 系统看图器（ACTION_VIEW）—— 零依赖、体验最好（需 pyjnius）
    ② Tkinter 窗口 —— **不需要 repository plugin**（Pydroid 自带 tkinter）
    ③ 终端字符画 —— **零依赖**！用自带 PNG 解码器直接在终端里画出来
    ④ 打印文件路径 —— 保底，至少知道去哪看

第 ③ 级是为"没装 Pydroid repository plugin / 没装 pyjnius"准备的：
本包已经自带纯 Python 的 PNG 解码器，所以不装任何东西也能在终端里看到图。
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

from pathlib import Path

from . import android, termimg
from .console import dim, err, info, print, prompt as cprompt, rule, warn

_MIME_BY_EXT = {"png": "image/png", "jpg": "image/jpeg", "jpeg": "image/jpeg",
                "webp": "image/webp", "gif": "image/gif", "bmp": "image/bmp"}


def tk_available() -> bool:
    """tkinter 能不能导入（不真开窗口）。"""
    try:
        import importlib
        importlib.import_module("tkinter")
        return True
    except Exception:                                     # noqa: BLE001
        return False


def show(path: Path, prompt: str = "", *, allow_term: bool = True) -> str:
    """预览一张图。返回实际用了哪一级：viewer / win / tk / term / path。"""
    # ① 系统看图器（Android）
    if android.ready():
        mime = _MIME_BY_EXT.get(path.suffix.lower().lstrip("."), "image/*")
        if android.BRIDGE.view_image(path, mime):
            print(f"  {info('已用系统看图器打开')}")
            return "viewer"

    # ①' Windows 系统看图器（os.startfile，零依赖）
    if android.is_windows() and android.windows_open(path):
        print(f"  {info('已用系统看图器打开')}")
        return "viewer"

    # ② Tkinter
    if _tk_show(path, prompt):
        return "tk"

    # ③ 终端字符画（零依赖）
    if allow_term and termimg.supported():
        if termimg.show(path, f"终端预览 · {path.name}"):
            return "term"
        # 走到这说明图不是 PNG（我们解不开），给出可操作的解释
        print(dim("  · 终端预览只支持 PNG（这个文件解不开）"))

    # ④ 保底
    print(f"  {dim('预览不可用，图片在：')}{path}")
    if not android.ready():
        print(dim("        （想更好看：Pydroid 里装 pyjnius -> 菜单 9 看状态）"))
    return "path"


def _tk_show(path: Path, prompt: str = "") -> bool:
    try:
        import tkinter as tk
    except Exception:                                     # noqa: BLE001
        return False
    try:
        root = tk.Tk()
    except Exception:                                     # noqa: BLE001
        return False                                      # 无显示环境
    try:
        root.title("imgagent preview")
        root.configure(bg="#101315")
        photo = None
        try:
            from PIL import Image, ImageTk                 # type: ignore
            im = Image.open(path)
            max_side = 900
            if max(im.size) > max_side:
                r = max_side / float(max(im.size))
                im = im.resize((int(im.width * r), int(im.height * r)))
            photo = ImageTk.PhotoImage(im)
        except Exception:                                 # noqa: BLE001
            try:
                photo = tk.PhotoImage(file=str(path))     # 原生只认 GIF/PNG/PPM
            except Exception:                             # noqa: BLE001
                root.destroy()
                return False
        lbl = tk.Label(root, image=photo, bg="#101315")
        lbl.image = photo          # 必须留引用，否则被 GC 掉显示空白
        lbl.pack(padx=10, pady=10)
        if prompt:
            tk.Label(root, text=prompt[:80], fg="#9fb3c8", bg="#101315",
                     wraplength=860, justify="left").pack(padx=10, pady=(0, 6))
        tk.Button(root, text="close", command=root.destroy,
                  bg="#1e242b", fg="#e6edf3", relief="flat").pack(pady=(0, 10))
        root.attributes("-topmost", True)
        root.after(200, lambda: root.attributes("-topmost", False))
        root.update_idletasks()
        w, h = root.winfo_width(), root.winfo_height()
        sw, sh = root.winfo_screenwidth(), root.winfo_screenheight()
        root.geometry(f"+{max(0, (sw - w) // 2)}+{max(0, (sh - h) // 3)}")
        root.mainloop()
        return True
    except Exception:                                     # noqa: BLE001
        try:
            root.destroy()
        except Exception:                                 # noqa: BLE001
            pass
        return False


def show_menu(path: Path, prompt: str = "") -> None:
    """预览方式选择菜单（菜单 6 用）。让用户自己挑，因为各人环境不同。

    三种环境的能力完全不同：
      · Termux ：没有 JVM（pyjnius 无效），但有 termux-open / chafa / timg / viu
      · Pydroid：pyjnius 可用时能调系统看图器；tkinter 看安装情况
      · 桌面   ：通常有 xdg-open
    """
    is_termux = android.is_termux()
    has_viewer = android.ready()
    has_tk = tk_available()
    has_term = termimg.supported()
    has_chafa = bool(android._which("chafa") or android._which("timg")
                     or android._which("viu")) if is_termux else False
    has_topen = bool(android._which("termux-open")) if is_termux else False

    print()
    print(rule("选择预览方式", "menu"))
    opts = []
    n = 0

    def _next() -> str:
        nonlocal n
        n += 1
        return str(n)

    if is_termux:
        if has_topen:
            opts.append((_next(), "系统看图器（termux-open）",
                         lambda: android.termux_open(path)))
        if has_chafa:
            opts.append((_next(), "终端高清图（chafa/timg/viu）",
                         lambda: android.termux_terminal_viewer(
                             path)))
    elif has_viewer:
        opts.append((_next(), "系统看图器", lambda: android.BRIDGE.view_image(
            path, _MIME_BY_EXT.get(path.suffix.lower().lstrip("."), "image/*"))))

    opts.append((_next(), "终端字符画" if has_term else "终端字符画（不可用）",
                 lambda: termimg.show(path, f"终端预览 · {path.name}")))
    opts.append((_next(), f"内置窗口（Tkinter{'可用' if has_tk else '不可用'}）",
                 lambda: _tk_show(path, prompt)))
    for k, name, _ in opts:
        print(f"  {k}) {name}")
    path_key = _next()
    print(f"  {path_key}) 只看文件路径")
    print("  0) 返回")
    if is_termux and not has_topen and not has_chafa:
        print(dim("  提示：Termux 里装这两个能让预览好很多："))
        print(dim("        pkg install chafa        # 终端高清图"))
        print(dim("        pkg install termux-api   # 配合 termux-open 调系统看图器"))

    try:
        raw = input(f"{cprompt('选')}: ").strip()
    except (EOFError, KeyboardInterrupt):
        print()
        return
    if raw == path_key:
        print(f"  {dim('图片在：')}{path}")
        try:
            print(f"  {dim('大小：')}{path.stat().st_size / 1024:.0f}KB")
        except OSError:
            pass
        return
    for k, name, fn in opts:
        if raw == k:
            try:
                done = fn()
            except Exception as e:                        # noqa: BLE001
                print(f"  {err('失败')}：{type(e).__name__}: {e}")
                return
            if done is False:
                print(f"  {warn('这个方式不可用')}，试试别的（或先跑菜单 9 看状态）")
            return
    if raw not in ("0", ""):
        print(dim("  · 没这个选项"))
