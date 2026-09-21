#!/usr/bin/env bash
# 一条命令跑完全部校验。改完代码后跑它，任何一步失败都会非零退出。
#
#   bash tools/verify_all.sh
#
# 覆盖：
#   1. 语法 + pyflakes
#   2. 入口唯一性（不能同时存在两个入口文件）
#   3. 每个模块恰好一份自举保护，且不阻止启动
#   4. 入口鲁棒性测试（模拟 Pydroid 的 exec 模型，34 项）
#   5. 功能测试（离线，54 项）
#   6. ASCII-only 终端存活（黑屏回归）
set -uo pipefail
cd "$(dirname "$0")/.."
FAIL=0
step() { printf '\n=== %s ===\n' "$1"; }
ok()   { printf '  [OK]   %s\n' "$1"; }
bad()  { printf '  [FAIL] %s\n' "$1"; FAIL=1; }
skip() { printf '  [skip] %s\n' "$1"; }

# 判断一行 "N/N passed" 是否全过（不硬编码数量，加用例不会误报）
passed_all() {
  printf '%s' "$1" | grep -qE '^[0-9]+/[0-9]+ passed$' || return 1
  local a b
  a=$(printf '%s' "$1" | cut -d/ -f1)
  b=$(printf '%s' "$1" | cut -d/ -f2 | cut -d' ' -f1)
  [ "$a" = "$b" ]
}

# ---------- 1. 语法 + pyflakes ----------
step "0/10 自举保护（自动补齐）"
python3 tools/fix_guards.py || true

step "1/10 运行时 import 解析（跨模块 from X import Y）"
python3 tools/check_imports.py || FAIL=1

step "2/10 syntax + pyflakes"
# 用 nullglob 风格：不存在的 glob 直接跳过，避免误报
shopt -s nullglob
FILES=(main.py impydroid/*.py tests/*.py tools/*.py)
shopt -u nullglob
[ ${#FILES[@]} -gt 0 ] || { bad "找不到任何 .py 文件"; }
for f in "${FILES[@]}"; do
  if python3 -c "import ast,sys;ast.parse(open(sys.argv[1],encoding='utf-8').read())" "$f" 2>/dev/null; then
    :
  else
    bad "$f 语法错误"
  fi
done
[ "$FAIL" -eq 0 ] && ok "全部文件语法 OK"
PYFILES=(main.py impydroid/*.py tests/*.py)
shopt -s nullglob
PYFILES+=(tools/*.py)
shopt -u nullglob
if python3 -m pyflakes "${PYFILES[@]}" 2>/tmp/_pf.txt; then
  ok "pyflakes 0 告警"
else
  if python3 -c "import pyflakes" 2>/dev/null; then
    bad "pyflakes 有告警"; cat /tmp/_pf.txt
  else
    skip "pyflakes 未安装"
  fi
fi

# ---------- 2. 入口唯一性 ----------
step "3/10  入口唯一性"
entry_count=0
for cand in main.py run.py app.py; do
  [ -f "$cand" ] && entry_count=$((entry_count + 1))
done
if [ "$entry_count" -eq 1 ]; then
  ok "只有一个顶层入口($(ls main.py run.py app.py 2>/dev/null | head -1))"
else
  bad "顶层入口有 $entry_count 个（main.py / run.py / app.py 只应存在一个）"
fi

# ---------- 3. 自举保护一致性 ----------
step "4/10  自举保护"
guard_bad=0
for f in impydroid/*.py; do
  n=$(grep -c "自举保护" "$f" 2>/dev/null | head -1 || true)
  n=${n:-0}
  if [ "$n" -ne 1 ]; then
    bad "$(basename "$f") 有 $n 份 guard（应为 1）"
    guard_bad=1
  fi
  # 旧版 guard 会 raise SystemExit(2) 阻止启动 —— 必须没有
  if grep -q 'raise SystemExit(2)' "$f"; then
    bad "$(basename "$f") 含会阻止启动的旧版 guard"
    guard_bad=1
  fi
done
[ "$guard_bad" -eq 0 ] && ok "每个模块恰好 1 份 guard，且都不会阻止启动"

# ---------- 4. 入口鲁棒性 ----------
step "5/10  入口鲁棒性（模拟 Pydroid 的 exec 模型）"
out=$(python3 tests/test_entrypoints.py 2>&1 | tail -1)
case "$out" in
  *) if passed_all "$out"; then ok "$out"; else bad "$out"; fi ;;
esac

# ---------- 5. 功能测试 ----------
step "6/10  功能测试（离线）"
out=$(python3 tests/test_impydroid.py 2>&1 | tail -1)
case "$out" in
  *) if passed_all "$out"; then ok "$out"; else bad "$out"; fi ;;
esac

# ---------- 6. ASCII-only 终端 ----------
step "7/10  ASCII-only 终端存活（黑屏回归）"
python3 - <<'PY'
import subprocess, sys, textwrap
inner = (
    "import sys, io\n"
    "class AsciiOnly(io.TextIOBase):\n"
    "    encoding = 'ascii'\n"
    "    def __init__(self, s): self._s = s\n"
    "    def write(self, t):\n"
    "        t.encode('ascii'); self._s.write(t); return len(t)\n"
    "    def flush(self): self._s.flush()\n"
    "    def isatty(self): return False\n"
    "    def reconfigure(self, **kw): raise AttributeError('no')\n"
    "real = sys.stdout\n"
    "sys.stdout = AsciiOnly(real)\n"
    "import impydroid\n"
    "from impydroid import console, ui\n"
    "console.print(ui.BANNER)\n"
    "console.print('danger: \\u2550 \\u2713 \\u4e2d\\u6587')\n"
    "real.write('ASCII-OK\\n'); real.flush()\n"
)
r = subprocess.run([sys.executable, "-c", inner], capture_output=True, text=True, timeout=120)
ok = r.returncode == 0 and "ASCII-OK" in (r.stdout or "")
print("  ASCII-only 终端 :", "OK" if ok else "FAIL")
if not ok:
    print("  stderr:", (r.stderr or "")[-300:])
sys.exit(0 if ok else 1)
PY
[ $? -eq 0 ] && ok "ASCII-only 存活" || bad "ASCII-only 失败"

# ---------- 7. 表现层（配色 / 分隔线 / 终端预览） ----------
step "8/10  表现层（配色 / 分隔线 / 终端预览）"
out=$(python3 tests/test_presentation.py 2>&1 | tail -1)
if passed_all "$out"; then ok "$out"; else bad "$out"; fi

# ---------- 8. 提示词润色 ----------
step "9/10  提示词润色（离线，不打真实端点）"
out=$(python3 tests/test_polish.py 2>&1 | tail -1)
if passed_all "$out"; then ok "$out"; else bad "$out"; fi

# ---------- 9. 并发与性能 ----------
step "10/10  并发与性能（离线）"
out=$(python3 tests/test_concurrency.py 2>&1 | tail -1)
if passed_all "$out"; then ok "$out"; else bad "$out"; fi

# ---------- 汇总 ----------
printf '\n'
if [ "$FAIL" -eq 0 ]; then
  echo "全部通过。可以交付。"
else
  echo "有失败项，先修再交付。"
fi
exit "$FAIL"
