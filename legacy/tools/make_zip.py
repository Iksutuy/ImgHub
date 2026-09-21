#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把项目打包成 zip（只依赖标准库，不需要 zip 命令）。

    python3 tools/make_zip.py                      # 同时打 Pydroid + Termux 两个包
    python3 tools/make_zip.py --target termux      # 只打 Termux 包
    python3 tools/make_zip.py --target pydroid     # 只打 Pydroid 包
    python3 tools/make_zip.py --check              # 只看会打包哪些文件

产物命名（带版本号）：
    imgagent-<版本>-pydroid3.zip
    imgagent-<版本>-termux.zip

两个包的**代码完全相同**（同一份源码三平台自适应），区别只在：
  * zip 内附带的说明文件不同（INSTALL.txt）
  * 顶层目录名不同（解压后不会互相覆盖）

特性：
  * 所有条目以 `<顶层目录>/` 开头 —— 解压后是一个干净文件夹，不会散落一地
  * 自动排除缓存、运行时产物（key / 设置 / 历史 / 图片）、编辑器目录
  * 打包后自动校验：解包到临时目录、跑入口自检
"""
from __future__ import annotations

import argparse
import hashlib
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent

# 不打包的目录。分两类：
#   ① 运行时垃圾：__pycache__ / .git / 用户数据目录
#   ② 开发者才需要的：tests（测试）/ tools（验证脚本）
#      Kivy GUI 实验，依赖 kivy + libmtdev + X server，Android 上跑不起来）
# 这样最终包只含"用户运行所需"的内容，体积小、目录干净。
EXCLUDE_DIRS = {"__pycache__", ".git", ".idea", ".vscode", "imgagent_data",
                ".pytest_cache", ".mypy_cache", "dist", "build",
                "tests", "tools"}
EXCLUDE_FILES = {".imgagent_key", ".imgagent_apimart_key", "state.json",
                 "config.json", "history.jsonl",
                 # Android/Kivy 打包专用（普通用户不需要）
                 "buildozer.spec", "build_android.sh",
                 "icon.png", "splash.png", "requirements.txt",
                 ".gitignore"}
EXCLUDE_EXT = {".pyc", ".pyo", ".zip", ".tmp", ".log"}

# 每个目标平台的元信息
TARGETS = {
    "pydroid3": {
        "top": "imgagent-pydroid3",
        "title": "imgagent — Pydroid 3 版",
        "install": """\
【安装步骤】Pydroid 3
------------------------------------------------------------------------
1. 把本 zip 解压到任意目录（推荐 /storage/emulated/0/Download/imgagent）
   · 用手机自带「文件管理」解压即可，不需要电脑。

2. 打开 Pydroid 3 → 左上角菜单 → Open → 选中解压出来的 main.py

3. 点右下角 ▶ 运行。首次会看到 IMGAGENT-CANARY 一行字，说明启动成功。

4.（可选但推荐）让它能读手机相册：
   Pydroid 3 菜单 → Pip → 搜索 pyjnius → Install
   装不上就先装 "Pydroid repository plugin"（同 Pip 菜单里搜）

【存图位置】
  默认存在脚本同目录的 imgagent_data/。
  如果 /sdcard 有写权限，图片同时会存一份到：
      /sdcard/Pictures/imgagent/
  这样手机相册和文件管理器都能看到。

【常见问题】
  · 一片黑屏没输出 → 菜单里选 0 退出，然后跑 --doctor 看诊断
      在 main.py 里把最后的 cli() 改成 cli() 前加参数，或直接用
      python3 -m impydroid --doctor
  · 提示 ImportError → 确认 main.py 和 impydroid/ 文件夹在同一目录

【配置 API Key】
  首次运行会问你要 key（APIMart 或 OpenRouter 都支持）。
  key 会存到数据目录，下次自动读取。
""",
    },
    "termux": {
        "top": "imgagent-termux",
        "title": "imgagent — Termux 版",
        "install": """\
【安装步骤】Termux
------------------------------------------------------------------------
1. 先装 Python（Termux 自带的可能是精简版）：
       pkg update && pkg install python

2.（重要）授权访问手机存储 —— 只需跑一次，会弹权限框：
       termux-setup-storage
   跑完确认这个目录存在：
       ls ~/storage/shared/DCIM

3.（可选，但强烈推荐）装预览工具，效果远好于自带字符画：
       pkg install chafa
       pkg install termux-api      # 提供 termux-open（调系统看图器）

4. 解压并进入目录：
       cd ~
       unzip /path/to/imgagent-*-termux.zip
       cd imgagent-termux

5. 运行：
       python3 main.py

【存图位置】
  · 工作目录（历史 / 设置 / key）：脚本同目录的 imgagent_data/
  · 手机可见的图片：~/storage/shared/Pictures/imgagent/
    这个路径 = 手机上的 /sdcard/Pictures/imgagent，
    相册和文件管理器都能看到。

【常见问题】
  · 读不到相册 → 忘了跑 termux-setup-storage（第 2 步）
  · 图片打不开 → 装 termux-api（提供 termux-open）
  · 预览是乱码 → 装 chafa
  · 想看环境状态 → 运行后在菜单里选 9

【配置 API Key】
  首次运行会问你要 key（APIMart 或 OpenRouter 都支持）。
  key 会存到数据目录，下次自动读取。

【不想每次输 key】
       export IMGAGENT_APIMART_API_KEY=sk-xxxx
  写进 ~/.bashrc 即可持久化。
""",
    },
    "windows": {
        "top": "imgagent-windows",
        "title": "imgagent — Windows 版",
        "install": """\
【安装步骤】Windows
------------------------------------------------------------------------
1. 装 Python 3.10+（建议 3.12/3.13）： https://www.python.org/downloads/
   安装时勾选 "Add Python to PATH"。

2.（推荐，TUI 必装）装 windows-curses 之后 TUI 全屏界面才可用；
   不装也能用命令行菜单（自动降级）：
       pip install windows-curses
   注：已针对 Windows 的 PDCurses 做键盘兼容 —— 方向键 / 中文 /
   Ctrl 快捷键在 TUI 下实测全部可用。

3. 解压本 zip，双击运行「启动.bat」或在命令行里：
       cd imgagent-windows
       python main.py

4. 首次会问你要 API key（APIMart 或 OpenRouter 都支持）。
   key 存到数据目录，下次自动读取；也可以设环境变量：
       set IMGAGENT_APIMART_API_KEY=sk-xxxx
   （PowerShell:  $env:IMGAGENT_APIMART_API_KEY="sk-xxxx"）

【存图位置】
  · 工作目录（历史/设置/key）：脚本同目录的 imgagent_data/
  · 生成的图会优先存到 我的图片\\imgagent\\（资源管理器可见）
  数据迁移：把 imgagent_data 整个拷走即可。

【终端推荐】
  · Windows Terminal（Win11 自带；PowerShell 7 也 OK）
  · 老 cmd 下中文/边框可能乱码 —— 先执行  chcp 65001

【常见问题】
  · 一片黑屏没输出 → 跑诊断： python main.py --doctor
  · TUI 打不开 → 装 windows-curses（第 2 步）
  · 想跑通流程不花钱 → python main.py --offline

【配置 API Key】
  首次运行会问你要 key（APIMart 或 OpenRouter 都支持）。
  key 会存到数据目录，下次自动读取。
""",
    },
}


# 必须随包分发的开发文档（打包时校验存在性，防止漏掉）
REQUIRED_DOCS = [
    "docs/README.md",
    "docs/HANDOVER.md",
    "docs/ARCHITECTURE.md",
    "docs/CONSTRAINTS.md",
    "docs/STANDARDS.md",
]


def check_docs() -> int:
    """校验 docs/ 是否齐全 —— 漏了文档的包对开发者没价值。"""
    missing = [d for d in REQUIRED_DOCS if not (ROOT / d).is_file()]
    if missing:
        print("  [X] 缺少开发文档：")
        for m in missing:
            print(f"      {m}")
        return 1
    total = sum((ROOT / d).stat().st_size for d in REQUIRED_DOCS)
    print(f"  [OK] 开发文档齐全（{len(REQUIRED_DOCS)} 份，{total // 1024}KB）")
    return 0


def collect() -> list[pathlib.Path]:
    def keep(p: pathlib.Path) -> bool:
        if p.name in EXCLUDE_FILES or p.suffix in EXCLUDE_EXT:
            return False
        return not any(part in EXCLUDE_DIRS for part in p.parts)

    return sorted(p for p in ROOT.rglob("*") if p.is_file() and keep(p))


def version() -> str:
    """从 impydroid/__init__.py 读版本号（单一真源，避免两处不一致）。"""
    import re
    txt = (ROOT / "impydroid" / "__init__.py").read_text(encoding="utf-8")
    m = re.search(r'__version__\s*=\s*"([^"]+)"', txt)
    return m.group(1) if m else "0.0.0"


def make(target: str, out: pathlib.Path) -> int:
    meta = TARGETS[target]
    top = meta["top"]
    files = collect()
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for p in files:
            z.write(p, arcname=str(pathlib.Path(top) / p.relative_to(ROOT)))
        # 每个平台的安装说明（不写进源码目录，避免污染）
        z.writestr(f"{top}/INSTALL.txt",
                   f"{meta['title']}\n"
                   f"版本 {version()}\n"
                   f"打包时间 {__import__('time').strftime('%Y-%m-%d %H:%M')}\n\n"
                   f"{meta['install']}")
        # Windows 额外：双击启动脚本 + 可选依赖清单
        if target == "windows":
            z.writestr(
                f"{top}/启动.bat",
                "@echo off\r\n"
                "chcp 65001 >nul\r\n"
                "cd /d %~dp0\r\n"
                "python main.py\r\n"
                "if errorlevel 1 pause\r\n")
            z.writestr(
                f"{top}/requirements-windows.txt",
                "# TUI 需要；不装也能用命令行菜单（自动降级）\r\n"
                "windows-curses>=2.3\r\n")
    size_kb = out.stat().st_size / 1024
    sha = hashlib.sha256(out.read_bytes()).hexdigest()
    print(f"  {out.name:<34} {len(files) + 1:3} 文件  {size_kb:6.1f} KB  "
          f"sha256={sha[:12]}…")
    return 0


def check(target: str) -> int:
    files = collect()
    print(f"[{target}] 会打包 {len(files)} 个文件（顶层目录 {TARGETS[target]['top']}/）：")
    for p in files:
        print(f"  {TARGETS[target]['top']}/{p.relative_to(ROOT)}")
    need = ["main.py", "impydroid/__init__.py", "impydroid/app.py",
            "impydroid/ui.py", "impydroid/android.py", "README.md"]
    miss = [n for n in need if not (ROOT / n).exists()]
    if miss:
        print(f"\n!! 缺少关键文件：{miss}")
        return 1
    print("\n关键文件都在。")
    return 0


def verify(zip_path: pathlib.Path, target: str) -> int:
    """解包到临时目录，跑一次入口自检。"""
    tmp = pathlib.Path(tempfile.mkdtemp(prefix="zipverify-"))
    try:
        zipfile.ZipFile(zip_path).extractall(tmp)
        pkg = tmp / TARGETS[target]["top"]
        if not (pkg / "main.py").is_file():
            print("  [FAIL] 解包后找不到 main.py")
            return 1
        if not (pkg / "INSTALL.txt").is_file():
            print("  [FAIL] 解包后找不到 INSTALL.txt")
            return 1
        env = {**os.environ,
               "IMGAGENT_HOME": str(tmp / "home"),
               "IMGAGENT_FORCE_OFFLINE": "1"}
        if target == "termux":
            env["PREFIX"] = "/data/data/com.termux/files/usr"   # 模拟 Termux
        r = subprocess.run([sys.executable, "main.py"],
                           input="0\n", capture_output=True, text=True,
                           cwd=str(pkg), env=env, timeout=120)
        ok = r.returncode == 0 and "IMGAGENT-CANARY" in (r.stdout or "")
        print(f"  {'[OK]  ' if ok else '[FAIL]'} {zip_path.name} 解包后能启动"
              + ("" if ok else f"  rc={r.returncode} err={(r.stderr or '')[-200:]}"))
        return 0 if ok else 1
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--target", choices=[*TARGETS, "all"], default="all",
                    help="打包哪个平台（默认两个都打）")
    ap.add_argument("--outdir", default=str(ROOT), help="输出目录")
    ap.add_argument("--check", action="store_true", help="只看会打包什么")
    ap.add_argument("--no-verify", action="store_true", help="跳过解包校验")
    args = ap.parse_args()

    targets = list(TARGETS) if args.target == "all" else [args.target]

    if args.check:
        rc = check_docs()
        for t in targets:
            rc |= check(t)
        return rc

    ver = version()
    outdir = pathlib.Path(args.outdir)
    outdir.mkdir(parents=True, exist_ok=True)

    print(f"打包 imgagent v{ver}")
    print(f"  源码目录 : {ROOT}")
    print(f"  输出目录 : {outdir}")
    print()
    if check_docs():
        print("\n文档不全，先补齐再打包。")
        return 1
    rc = 0
    made: list[tuple[pathlib.Path, str]] = []
    for t in targets:
        out = outdir / f"imgagent-{ver}-{t}.zip"
        rc |= make(t, out)
        made.append((out, t))

    if rc or args.no_verify:
        return rc

    print("\n解包校验：")
    for out, t in made:
        rc |= verify(out, t)
    if rc == 0:
        print(f"\n全部就绪。共 {len(made)} 个包，版本 v{ver}。")
    return rc


if __name__ == "__main__":
    sys.exit(main())
