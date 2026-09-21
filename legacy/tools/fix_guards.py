#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""给包内**所有**模块补齐自举保护，并把位置修正到 docstring/__future__ 之后。

为什么需要工具：每次新增一个 .py 就容易漏掉 guard，而漏掉后
"在 Pydroid 里直接打开它"就会甩 ImportError。人工记不住，交给脚本。

用法：
    python3 tools/fix_guards.py            # 补齐/修正
    python3 tools/fix_guards.py --check    # 只检查，不修改（CI 用）
"""
from __future__ import annotations

import argparse
import ast
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
PKG = ROOT / "impydroid"

START = "# ---------------------------------------------------------------------------\n# 自举保护"
END = "# ---------------------------------------------------------------------------\n"

GUARD = '''# ---------------------------------------------------------------------------
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
            "\\n[!] %s 是包的内部模块，不是启动入口。\\n"
            "    正确入口是同一目录下的 main.py：\\n\\n"
            "        %s\\n\\n"
            "    正在替你改用正确入口启动...\\n\\n"
            % (_os.path.basename(__file__), _os.path.join(_PARENT, "main.py")))
        try:
            from impydroid.app import cli as _cli
            _sys.exit(_cli())
        except SystemExit:
            raise
        except Exception as _e:
            _sys.stderr.write("启动失败：%s: %s\\n" % (type(_e).__name__, _e))
            _sys.exit(2)
# ---------------------------------------------------------------------------
'''


def strip_guard(src: str) -> str:
    """删掉已有的 guard（可能有多份，反复删）。"""
    for _ in range(5):
        i = src.find(START)
        if i < 0:
            break
        j = src.find(END, i + len(START) + 10)
        if j < 0:
            break
        src = src[:i] + src[j + len(END):]
    return src


def insert_pos(src: str) -> int:
    """guard 应插在 docstring 与 __future__ 之后（前面不能再有语句）。"""
    tree = ast.parse(src)
    lines = src.splitlines(keepends=True)
    pos = 0
    for node in tree.body:
        if isinstance(node, ast.Expr) and isinstance(node.value, ast.Constant) \
                and isinstance(node.value.value, str):
            pos = node.end_lineno
            continue
        if isinstance(node, ast.ImportFrom) and node.module == "__future__":
            pos = node.end_lineno
        break
    del lines
    return pos


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args()

    problems: list[str] = []
    fixed: list[str] = []

    for f in sorted(PKG.glob("*.py")):
        src = f.read_text(encoding="utf-8")
        n = src.count("自举保护")
        if f.name == "__init__.py":
            if n != 1:
                problems.append(f"{f.name}: {n} 份 guard")
            continue
        if n != 1:
            problems.append(f"{f.name}: {n} 份 guard（应为 1）")
            if not args.check:
                body = strip_guard(src)
                pos = insert_pos(body)
                lines = body.splitlines(keepends=True)
                out = "".join(lines[:pos]) + GUARD + "".join(lines[pos:])
                ast.parse(out)                     # 语法自检
                f.write_text(out, encoding="utf-8")
                fixed.append(f.name)
            continue
        # 已有 1 份：检查位置是否正确（guard 必须在 docstring/__future__ 之后）
        if n == 1:
            body = strip_guard(src)
            pos = insert_pos(body)
            lines = body.splitlines(keepends=True)
            out = "".join(lines[:pos]) + GUARD + "".join(lines[pos:])
            if out != src:
                problems.append(f"{f.name}: guard 位置不对")
                if not args.check:
                    ast.parse(out)
                    f.write_text(out, encoding="utf-8")
                    fixed.append(f.name + "（位置修正）")

    if fixed:
        print(f"已修复 {len(fixed)} 个：")
        for x in fixed:
            print("  ", x)
    if problems and args.check:
        print(f"发现 {len(problems)} 个问题：")
        for x in problems:
            print("  ", x)
        return 1
    if not problems:
        print(f"全部 {len(list(PKG.glob('*.py')))} 个模块的 guard 都正确。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
