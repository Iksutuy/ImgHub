#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# =====================================================================
#  ★★★  入口文件 —— Pydroid 3 点运行 / Termux 里 python main.py  ★★★
#  支持：Pydroid 3（Android）、Termux（Android）、桌面 Linux
#
#  用法：
#      python3 main.py                正常启动
#      python3 main.py --offline      强制离线（占位图，不联网不花钱）
#      python3 main.py --home DIR     指定数据目录
#      python3 main.py --doctor       环境诊断（出问题时先跑这个）
#
#  ⚠️ impydroid/ 里的 .py 都是模块，别单独运行（会提示你正确的入口）。
#
#  为什么这个文件写得这么谨慎：Pydroid 3 不是 `python main.py`，而是
#      exec(open(file).read(), __main__.__dict__)
#  这带来三个坑，这里逐个堵掉：
#    ① 脚本目录**不会**自动进 sys.path  -> 手动加
#    ② `__file__` 可能没被设进 __main__ -> 多路探测自己的位置
#    ③ 相对导入会失败（没有包上下文）    -> 先补 sys.path，再用绝对导入
# =====================================================================
from __future__ import annotations

import os
import sys


def _locate() -> str:
    """找到 main.py 所在的目录（也就是 impydroid/ 的上一层）。"""
    # ① 常规：被解释器执行时 __file__ 存在
    try:
        return os.path.dirname(os.path.abspath(__file__))
    except NameError:
        pass
    # ② Pydroid/exec：从 __main__.__file__ 找
    main_mod = sys.modules.get("__main__")
    cand = getattr(main_mod, "__file__", None) if main_mod is not None else None
    if cand:
        return os.path.dirname(os.path.abspath(cand))
    # ③ 从 sys.argv[0] 找
    if sys.argv and sys.argv[0]:
        cand = os.path.abspath(sys.argv[0])
        if os.path.isfile(cand):
            return os.path.dirname(cand)
    # ④ 从 cwd 逐层向上找，看哪一层有 impydroid/
    d = os.getcwd()
    for _ in range(6):
        if os.path.isdir(os.path.join(d, "impydroid")):
            return d
        parent = os.path.dirname(d)
        if parent == d:
            break
        d = parent
    return os.getcwd()


HERE = _locate()
if HERE not in sys.path:
    sys.path.insert(0, HERE)

# ---------- 自检：给人话，而不是 ImportError 堆栈 ----------
if not os.path.isdir(os.path.join(HERE, "impydroid")):
    sys.stderr.write(
        "\n找不到 impydroid 包目录。\n"
        f"  main.py 所在目录 : {HERE}\n"
        f"  期望存在        : {os.path.join(HERE, 'impydroid', '__init__.py')}\n\n"
        "请确认 main.py 和 impydroid/ 文件夹在**同一个目录**下\n"
        "（在 Pydroid 里要导入整个文件夹，不是只导入单个 .py）。\n\n")
    raise SystemExit(2)

try:
    from impydroid.app import cli
except ImportError as e:
    sys.stderr.write(
        f"\n无法导入 impydroid 包：{type(e).__name__}: {e}\n\n"
        f"  sys.path[0] = {sys.path[0] if sys.path else '(空)'}\n"
        f"  包目录      = {os.path.join(HERE, 'impydroid')}\n\n"
        "若刚才是首次运行，请确认包里所有 .py 都拷全了。\n\n")
    raise SystemExit(2)

if __name__ == "__main__":
    sys.exit(cli())
