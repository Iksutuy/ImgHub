#!/usr/bin/env python3
"""检查所有模块的运行时 import 是否真的能解析。

为什么需要这个：pyflakes 只看单文件语法和"名字有没有被定义"，
**不检查** `from .mod import Name` 里的 Name 是否真的存在于 mod。
所以一个错误的导入（比如把 httpclient 的 ApiError 写成从 settings 导入）
能顺利通过 pyflakes + 全部测试，直到运行时才炸。

这个脚本对每个模块里**所有**（含函数体内的）相对导入做真实的 import +
getattr 验证，能在提交前抓住这类 bug。

用法：
    python3 tools/check_imports.py          # 检查全部模块
    python3 tools/check_imports.py -v       # 打印每个通过的导入
"""
from __future__ import annotations

import ast
import importlib
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PKG = "impydroid"


def _collect_imports(path: Path) -> list[tuple[int, str, str]]:
    """收集文件里所有相对导入：(行号, 模块名, 导入的名字)。"""
    try:
        tree = ast.parse(path.read_text(encoding="utf-8"))
    except SyntaxError:
        return []
    out: list[tuple[int, str, str]] = []
    for node in ast.walk(tree):
        if isinstance(node, ast.ImportFrom) and node.level == 1 and node.module:
            for a in node.names:
                out.append((node.lineno, node.module, a.name))
    return out


def main(argv: list[str]) -> int:
    verbose = "-v" in argv
    sys.path.insert(0, str(ROOT))

    files = sorted((ROOT / PKG).glob("*.py"))
    checked = 0
    problems: list[str] = []
    seen: set[tuple[str, str, str]] = set()

    for f in files:
        for lineno, mod, name in _collect_imports(f):
            if name == "*":
                continue
            key = (f.name, mod, name)
            if key in seen:
                continue
            seen.add(key)
            checked += 1
            try:
                m = importlib.import_module(f"{PKG}.{mod}")
            except Exception as e:                       # noqa: BLE001
                problems.append(
                    f"{f.name}:{lineno}  from .{mod} import {name}"
                    f"  → 模块本身导入失败：{type(e).__name__}: {e}")
                continue
            if not hasattr(m, name):
                problems.append(
                    f"{f.name}:{lineno}  from .{mod} import {name}"
                    f"  → {mod}.py 里没有这个符号")
                continue
            if verbose:
                print(f"  OK  {f.name}:{lineno}  .{mod}.{name}")

    print(f"\n检查了 {checked} 条相对导入（{len(files)} 个模块）")
    if problems:
        print(f"\n发现 {len(problems)} 个问题：")
        for p in problems:
            print(f"  [X] {p}")
        return 1
    print("全部可解析 ✓")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
