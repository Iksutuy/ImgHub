#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""入口鲁棒性测试 —— 模拟 Pydroid 3 的文件运行方式。

Pydroid 不是 `python file.py`，而是：
    exec(open(file).read(), __main__.__dict__)

这带来三个坑，本测试逐个覆盖：
  ① 脚本目录不进 sys.path
  ② __file__ 可能缺失
  ③ 直接打开包内部文件（相对导入必失败）

测试目标：**每一个 .py 文件**被这样执行时，都必须能自己活过来，
要么正常启动、要么给出人话提示并启动正确入口 —— 绝不能甩一个
ImportError 堆栈给用户。

用法：python3 tests/test_entrypoints.py
"""
from __future__ import annotations

import os
import subprocess
import sys
import textwrap
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PKG = ROOT / "impydroid"

# 模拟 Pydroid 的 exec 运行方式；stdin 直接给 "0\n"（菜单里选退出）
HARNESS = textwrap.dedent(r'''
    import os, sys, types, builtins

    target = sys.argv[1]
    # 把 stdin 换成"立刻退出"，免得交互卡住
    import io
    sys.stdin = io.StringIO("0\n")

    # ---- 复刻 Pydroid 的 iiec_run.py ----
    main = types.ModuleType("__main__")
    main.__dict__["__name__"] = "__main__"
    # 情况由环境变量决定：设了 NO_FILE 就模拟 __file__ 缺失
    if not os.environ.get("NO_FILE"):
        main.__dict__["__file__"] = os.path.abspath(target)
    # 关键：**不把脚本目录加进 sys.path**（这正是 Pydroid 的行为）
    sys.path[:] = [p for p in sys.path if os.path.abspath(p or ".") !=
                   os.path.dirname(os.path.abspath(target))]

    src = open(target, encoding="utf-8").read()
    try:
        exec(compile(src, target, "exec"), main.__dict__)
    except SystemExit:
        pass
    finally:
        sys.stdout.write("\n__HARNESS_DONE__\n")
        sys.stdout.flush()
''')


def run_one(path: Path, *, no_file: bool = False,
            cwd: Path | None = None) -> tuple[int, str, str]:
    env = {**os.environ, "IMGAGENT_FORCE_OFFLINE": "1"}
    if no_file:
        env["NO_FILE"] = "1"
    # encoding="utf-8"：子进程的 console.harden_stdio 会把 stdout/stderr 强制成
    # UTF-8；若这里用默认编码（Windows 上是 GBK）解码就会 UnicodeDecodeError，
    # 导致 out 为空、断言失败。显式指定 UTF-8 才与子进程输出一致。
    r = subprocess.run([sys.executable, "-c", HARNESS, str(path)],
                       capture_output=True, text=True,
                       encoding="utf-8", errors="replace", env=env,
                       cwd=str(cwd or ROOT), timeout=120)
    return r.returncode, r.stdout or "", r.stderr or ""


def main() -> int:
    results: list[tuple[str, bool, str]] = []

    def check(name: str, cond: bool, extra: str = "") -> None:
        results.append((name, cond, extra))

    # ---------- 1. 入口文件 run.py ----------
    rc, out, err = run_one(ROOT / "main.py")
    check("main.py 能启动", rc == 0 and "IMGAGENT-CANARY" in out,
          f"rc={rc} err={err[-300:]}")

    # ---------- 2. run.py 在 __file__ 缺失时也能启动 ----------
    rc, out, err = run_one(ROOT / "main.py", no_file=True)
    check("main.py 在 __file__ 缺失时能启动", rc == 0 and "IMGAGENT-CANARY" in out,
          f"rc={rc} err={err[-300:]}")

    # ---------- 3. python -m impydroid ----------
    env = {**os.environ, "IMGAGENT_FORCE_OFFLINE": "1"}
    r = subprocess.run([sys.executable, "-m", "impydroid"],
                       input="0\n", capture_output=True, text=True,
                       encoding="utf-8", errors="replace",
                       env=env, cwd=str(ROOT), timeout=120)
    check("python -m impydroid 能启动",
          r.returncode == 0 and "IMGAGENT-CANARY" in (r.stdout or ""),
          f"rc={r.returncode} err={(r.stderr or '')[-300:]}")

    # ---------- 4. 逐个打开包内部文件（最常见的误操作） ----------
    for f in sorted(PKG.glob("*.py")):
        rc, out, err = run_one(f)
        started = "IMGAGENT-CANARY" in out
        told = "不是启动入口" in err or "请改用入口文件运行" in err
        no_traceback = "ImportError" not in err and "Traceback" not in err
        check(f"直接运行 {f.name} 不会甩 ImportError",
              rc == 0 and no_traceback,
              f"rc={rc} started={started} told={told} err={err[-200:]}")
        if f.name != "__init__.py":
            check(f"  {f.name} 会自动转到正确入口", started or told,
                  f"started={started} told={told}")

    # ---------- 5. __init__.py 单独跑：应该给提示 + 启动 ----------
    rc, out, err = run_one(PKG / "__init__.py")
    check("__init__.py 直接运行有友好提示",
          ("不是启动入口" in err or "请改用入口文件运行" in err
           or "IMGAGENT-CANARY" in out),
          f"rc={rc} err={err[-200:]}")
    check("__init__.py 不甩 ImportError",
          "attempted relative import" not in err, err[-200:])

    # ---------- 汇总 ----------
    passed = sum(1 for _, ok, _ in results if ok)
    print("entrypoints test")
    print("-" * 62)
    for name, ok, extra in results:
        line = f"  {'OK  ' if ok else 'FAIL'} {name}"
        if extra and not ok:
            line += f"\n         {extra}"
        print(line)
    print("-" * 62)
    print(f"{passed}/{len(results)} passed")
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
