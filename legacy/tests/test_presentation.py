#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""表现层测试：配色、分隔线、终端预览。

用法：python3 tests/test_presentation.py
"""
from __future__ import annotations

import os
import re
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(ROOT))

from impydroid import console, termimg, ui          # noqa: E402

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(errors="replace")
    except Exception:                                # noqa: BLE001
        pass

RESULTS: list[tuple[str, bool, str]] = []
ANSI = re.compile(r"\x1b\[[0-9;]*m")


def check(name: str, cond, extra: str = "") -> None:
    RESULTS.append((name, bool(cond), extra))


def strip(s: str) -> str:
    return ANSI.sub("", s)


# ============================================================ 配色
def test_color() -> None:
    # auto：非终端时自动关闭（否则日志里全是转义码）
    os.environ["IMGAGENT_COLOR"] = "never"
    console.set_color("never")
    check("配色: never 时不打色", console.paint("x", "menu") == "x")
    check("配色: never 时 color_enabled 为假", console.color_enabled() is False)

    console.set_color("always")
    check("配色: always 时启用", console.color_enabled() is True)
    painted = console.paint("x", "menu")
    check("配色: always 时带转义码", painted.startswith("\x1b[") and painted.endswith("\x1b[0m"),
          repr(painted))

    # 四类内容必须是**不同的颜色**
    styles = {
        "menu": console.menu("m"),
        "info": console.info("i"),
        "input": console.prompt("p"),
        "warn": console.warn("w"),
        "err": console.err("e"),
        "dim": console.dim("d"),
    }
    codes = {k: v[:v.index("m") + 1] for k, v in styles.items()}
    check("配色: 菜单/提示/输入 三者代码互不相同",
          len({codes["menu"], codes["info"], codes["input"]}) == 3, str(codes))
    check("配色: 六个语义色互不相同", len(set(codes.values())) == 6, str(codes))
    for k, v in styles.items():
        check(f"配色: {k} 用后即复位", v.endswith("\x1b[0m"), repr(v[-6:]))

    # 正文（不套颜色）应保持原样
    check("配色: 正文不打色", console.paint("plain") == "plain")

    console.set_color("auto")


# ============================================================ 分隔线
def test_rule() -> None:
    console.set_color("never")
    r = console.rule()
    check("分隔线: 无标签时是满宽横线",
          len(r) > 10 and set(r) <= set("=-\u2500"), repr(r[:20]))
    r2 = console.rule("标题")
    check("分隔线: 带标签时包含标签", "标题" in r2, r2[:30])
    # 显示宽度应正好等于终端宽度（中文占 2 列，所以不能用 len()）
    w = console.display_width(strip(r2))
    check("分隔线: 显示宽度=终端宽度（中文算 2 列）",
          w == console.term_width(), f"{w} vs {console.term_width()}")
    # 不同长度的标签，显示宽度必须完全一致（否则右边参差不齐）
    a = console.display_width(strip(console.rule("生成新图")))
    b = console.display_width(strip(console.rule("设置")))
    c = console.display_width(strip(console.rule("a")))
    check("分隔线: 不同标签显示宽度完全对齐",
          a == b == c, f"{a} / {b} / {c}")
    console.set_color("auto")


# ============================================================ 终端预览
def test_termimg() -> None:
    from impydroid.placeholder import placeholder_png
    d = Path(tempfile.mkdtemp(prefix="termimg-"))
    png = d / "t.png"
    png.write_bytes(placeholder_png("termimg test", step=1, size=256))
    txt = d / "t.txt"
    txt.write_text("not an image")

    for mode in ("truecolor", "256", "ascii"):
        lines = termimg.render(png, cols=40, mode=mode)
        ok = lines is not None and len(lines) > 0
        check(f"终端预览: {mode} 能渲染", ok)
        if ok:
            plain = strip(lines[0])
            check(f"终端预览: {mode} 宽度=40 列", len(plain) == 40, str(len(plain)))
            if mode != "ascii":
                check(f"终端预览: {mode} 用半块字符",
                      "\u2580" in plain or "#" in plain)
                check(f"终端预览: {mode} 每行以复位收尾",
                      lines[0].endswith("\x1b[0m"))
    # 竖横比：40 列宽的方图应约 20 行（半块字符一格两像素）
    lines = termimg.render(png, cols=40, mode="256")
    check("终端预览: 行数≈列数/2（保持比例）",
          18 <= len(lines) <= 22, str(len(lines)))
    # 非 PNG 必须优雅失败
    check("终端预览: 非 PNG 返回 None", termimg.render(txt) is None)
    # 缓存：第二次应立即命中且结果一致
    a = termimg.render(png, cols=30, mode="ascii")
    b = termimg.render(png, cols=30, mode="ascii")
    check("终端预览: 重复渲染结果一致（缓存）", a == b)
    # off 模式
    check("终端预览: off 模式不渲染", termimg.render(png, mode="off") is None)
    check("终端预览: describe 返回字符串", isinstance(termimg.describe(), str))


# ============================================================ 预览降级链
def test_preview_chain() -> None:
    from impydroid import android, preview
    from impydroid.placeholder import placeholder_png
    d = Path(tempfile.mkdtemp(prefix="pvchain-"))
    png = d / "p.png"
    png.write_bytes(placeholder_png("chain", 0, 64))

    # 无 pyjnius；Tkinter 在容器里开不了窗口 -> 应该落到 term。
    # 测试环境里强制禁用 tkinter 窗口（会真弹窗卡住 CI），才能稳定测降级链。
    android.BRIDGE.ok = False
    android.BRIDGE.reason = "selftest: no pyjnius"
    console.set_color("never")
    os.environ["IMGAGENT_TERM_IMG"] = "ascii"
    _saved_tk0 = preview._tk_show
    preview._tk_show = lambda p, prompt="": False
    try:
        used = preview.show(png, "probe")
    finally:
        preview._tk_show = _saved_tk0
    check("预览链: 落到终端画（无需插件）", used in ("term", "tk", "viewer"), used)
    # 关掉终端画 -> 必须还能给出路径（不能抛异常）。
    # Windows 上 os.startfile / tkinter 可能可用，会返回 viewer/tk ——
    # 这里强制禁用这两级，才测得到真正"兜底给路径"。
    _saved_open = android.windows_open
    _saved_tk = preview._tk_show
    if os.name == "nt":
        android.windows_open = lambda p: False
    preview._tk_show = lambda p, prompt="": False
    try:
        os.environ["IMGAGENT_TERM_IMG"] = "off"
        used2 = preview.show(png, "probe")
        check("预览链: 全不可用时给路径不抛异常", used2 == "path", used2)
    finally:
        android.windows_open = _saved_open
        preview._tk_show = _saved_tk
    os.environ.pop("IMGAGENT_TERM_IMG", None)
    console.set_color("auto")


# ============================================================ 分隔线接进动作
def test_actions_have_sections() -> None:
    import inspect
    for fn in (ui.do_generate, ui.do_edit, ui.do_upload, ui.do_history,
               ui.do_import_from_home, ui.do_settings, ui.do_data_dir,
               ui.do_android_status):
        src = inspect.getsource(fn)
        check(f"动作: {fn.__name__} 有分隔线", "section(" in src)



# ============================================================ 输入提示必须上色
def test_prompt_is_colored() -> None:
    """ask() 和几处直接 input() 的提示都必须是亮黄 —— 这类遗漏很难肉眼发现。"""
    import inspect
    from impydroid import photos, preview, ui

    # ask() 内部必须用 cprompt()
    src = inspect.getsource(ui.ask)
    check("ask(): 输入提示用 cprompt", "cprompt(" in src, src[:120])
    check("ask(): 不再裸用 f\"{prompt}\"", 'input(f"{prompt}' not in src)

    # 三个"取消语义"的 input 也要上色
    for mod, name in ((ui, "do_history"), (ui, "do_import_from_home"),
                      (photos, "pick"), (preview, "show_menu")):
        fn = getattr(mod, name, None)
        if fn is None:
            continue
        code = inspect.getsource(fn)
        if "input(" in code:
            check(f"{mod.__name__}.{name}: input 也上了色",
                  "cprompt(" in code or "dim(" in code, code[-200:])

    # paint 幂等：已含转义码就不再叠加
    from impydroid.console import paint, set_color
    set_color("always")
    try:
        once = paint("x", "input")
        twice = paint(once, "input")
        check("paint(): 幂等（不产生嵌套转义）", once == twice, repr(twice))
        check("paint(): 空串不加码", paint("", "input") == "")
        # 四种语义色确实不同
        from impydroid.console import err, info, menu, prompt
        vals = {menu("t"), info("t"), prompt("t"), err("t")}
        check("四种语义色互不相同", len(vals) == 4, str(len(vals)))
    finally:
        set_color("auto")

def main() -> int:
    print("presentation test suite")
    print("-" * 62)
    for fn in (test_color, test_rule, test_termimg, test_preview_chain,
               test_actions_have_sections, test_prompt_is_colored):
        try:
            fn()
        except Exception as e:                       # noqa: BLE001
            import traceback
            check(f"{fn.__name__} 崩溃", False,
                  f"{type(e).__name__}: {e}\n{traceback.format_exc()[-300:]}")
    passed = sum(1 for _, ok, _ in RESULTS if ok)
    for name, ok, extra in RESULTS:
        line = f"  {'OK  ' if ok else 'FAIL'} {name}"
        if extra and not ok:
            line += f"\n         {extra}"
        print(line)
    print("-" * 62)
    print(f"{passed}/{len(RESULTS)} passed")
    return 0 if passed == len(RESULTS) else 1


if __name__ == "__main__":
    sys.exit(main())
