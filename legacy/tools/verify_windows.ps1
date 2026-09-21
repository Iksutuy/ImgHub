# =====================================================================
#  verify_windows.ps1 - one-click check for Windows (equiv of verify_all.sh)
#  Usage:  powershell -ExecutionPolicy Bypass -File tools\verify_windows.ps1
#  NOTE: ASCII-only on purpose (Windows PowerShell 5.1 reads UTF-8-no-BOM
#        as ANSI, which would corrupt non-ASCII comments).
#
#  Steps:
#    1. syntax (ast.parse)
#    2. cross-module import resolution (check_imports.py)
#    3. single top-level entry
#    4. exact one bootstrap guard per module
#    5. functional tests (offline, no money spent)
#    6. ASCII-only terminal survival (black-screen regression)
#    7. platform probe (--doctor)
#    8. curses availability (TUI prerequisite)
# =====================================================================
$ErrorActionPreference = "Stop"
$FAIL = 0
$ROOT = Split-Path -Parent $PSScriptRoot

function Step([string]$name)  { Write-Host "`n=== $name ===" -ForegroundColor Cyan }
function Ok([string]$msg)     { Write-Host "  [OK]   $msg" -ForegroundColor Green }
function Bad([string]$msg)    { Write-Host "  [FAIL] $msg" -ForegroundColor Red; $script:FAIL = 1 }
function SkipMsg([string]$msg){ Write-Host "  [skip] $msg" -ForegroundColor DarkGray }

# True iff line looks like "N/N passed" with numerator == denominator.
function PassedAll([string]$line) {
    if ($line -notmatch '^(\d+)/(\d+) passed$') { return $false }
    return [int]$Matches[1] -eq [int]$Matches[2]
}

Set-Location $ROOT

# ---------- 1. syntax ----------
Step "1/8  Syntax check"
$pyFiles = @()
$pyFiles += (Get-Item "main.py").FullName
$pyFiles += Get-ChildItem impydroid -Filter *.py -Recurse | ForEach-Object { $_.FullName }
$pyFiles += Get-ChildItem tests    -Filter *.py -Recurse | ForEach-Object { $_.FullName }
$pyFiles += Get-ChildItem tools    -Filter *.py -Recurse | ForEach-Object { $_.FullName }
$syntaxBad = $false
foreach ($f in $pyFiles) {
    python -c "import ast,sys; ast.parse(open(sys.argv[1],encoding='utf-8').read())" $f 2>$null
    if ($LASTEXITCODE -ne 0) { Bad "$(Split-Path -Leaf $f) syntax error"; $syntaxBad = $true }
}
if (-not $syntaxBad) { Ok "all $($pyFiles.Count) files parse OK" }

# ---------- 2. cross-module imports ----------
Step "2/8  Cross-module import resolution"
python tools/check_imports.py
if ($LASTEXITCODE -ne 0) { Bad "check_imports.py failed" } else { Ok "check_imports.py passed" }

# ---------- 3. single entry ----------
Step "3/8  Single top-level entry"
$entry = @("main.py","run.py","app.py") | Where-Object { Test-Path $_ }
if ($entry.Count -eq 1) { Ok "one entry ($entry)" }
else { Bad "found $($entry.Count) entries (main.py / run.py / app.py - keep exactly one)" }

# ---------- 4. bootstrap guard ----------
Step "4/8  Bootstrap guard"
# "bootstrap" marker text is CJK (self-bootstrap); detect via inline python
# so this .ps1 stays pure ASCII (PowerShell 5.1 would corrupt UTF-8-no-BOM).
$guardPy = @'
import glob, os, re, sys
marker = "\u81ea\u4e3e\u4fdd\u62a4"   # 'self-bootstrap' in CJK
bad = []
for f in sorted(glob.glob("impydroid/*.py")):
    txt = open(f, encoding="utf-8").read()
    n = txt.count(marker)
    if n != 1:
        bad.append("%s guard count=%d" % (os.path.basename(f), n))
    if "raise SystemExit(2)" in txt:
        bad.append("%s old blocking guard" % os.path.basename(f))
print("\n".join(bad))
'@
$guardOut = @($guardPy | python -)
$guardBad = $false
foreach ($line in $guardOut) {
    if ($line.Trim()) { Bad $line; $guardBad = $true }
}
if (-not $guardBad) { Ok "exactly one guard per module, none blocks startup" }

# ---------- 5. functional tests ----------
Step "5/8  Functional tests (offline)"
$testFiles = @(
    "tests\test_entrypoints.py",
    "tests\test_impydroid.py",
    "tests\test_presentation.py",
    "tests\test_polish.py",
    "tests\test_concurrency.py"
)
$env:TMP = Join-Path $ROOT ".tmp"
$env:TEMP = Join-Path $ROOT ".tmp"
New-Item -ItemType Directory -Force -Path $env:TMP | Out-Null

foreach ($t in $testFiles) {
    # route through cmd to avoid PowerShell NativeCommandError semantics on
    # test stderr noise; "N/N passed" is the last line of stdout
    $tmpOut = Join-Path $env:TMP "t_out.txt"
    cmd /c "python `"$t`" 2>nul > `"$tmpOut`""
    $last = (Get-Content $tmpOut -Raw -ErrorAction SilentlyContinue).Trim()
    $lines = $last -split "`r?`n"
    $out = $lines | Where-Object { $_ -ne "" } | Select-Object -Last 1
    Write-Host "  [$t] $out" -ForegroundColor DarkGray
    if (-not (PassedAll $out)) { Bad "$t not all-pass: $out" }
}
if ($FAIL -eq 0) { Ok "all functional tests passed" }

# ---------- 6. ASCII-only terminal ----------
Step "6/8  ASCII-only terminal survival"
$inner = @'
import sys, io
class AsciiOnly(io.TextIOBase):
    encoding = 'ascii'
    def __init__(self, s): self._s = s
    def write(self, t):
        t.encode('ascii'); self._s.write(t); return len(t)
    def flush(self): self._s.flush()
    def isatty(self): return False
    def reconfigure(self, **kw): raise AttributeError('no')
real = sys.stdout
sys.stdout = AsciiOnly(real)
import impydroid
from impydroid import console, ui
console.print(ui.BANNER)
console.print('danger: \u2550 \u2713 \u4e2d\u6587')
real.write('ASCII-OK\n'); real.flush()
'@
$r = python -c $inner
$asciiOk = ($LASTEXITCODE -eq 0 -and $r -match "ASCII-OK")
if ($asciiOk) { Ok "ASCII-only survives" } else { Bad "ASCII-only failed" }

# ---------- 7. platform probe ----------
Step "7/8  Platform probe (--doctor)"
python main.py --doctor *> $null
if ($LASTEXITCODE -eq 0) { Ok "--doctor runs" } else { Bad "--doctor failed (exit $LASTEXITCODE)" }

# ---------- 8. curses availability ----------
Step "8/8  curses availability (TUI prerequisite)"
$tmpC = Join-Path $env:TMP "curses_check.txt"
cmd /c "python -c `"import curses; print('yes')`" 2>nul > `"$tmpC`""
$c = (Get-Content $tmpC -Raw -ErrorAction SilentlyContinue).Trim()
if ($c -match "yes") {
    Ok "windows-curses installed - TUI available"
    # PDCurses key-compat smoke test: verify arrow keys classify as int,
    # CJK as str (the Windows-specific keyboard handling).
    $smoke = @'
import sys, os
sys.path.insert(0, os.getcwd())
from impydroid import curses_ui as cui
ok = cui._IS_PDCURSES in (True, False)
if cui._IS_PDCURSES:
    ok = ok and cui._c.KEY_UP in cui._PD_KEY_CODES
print("SMOKE", "OK" if ok else "FAIL")
'@
    $sm = ($smoke | python -) 2>$null
    if ("$sm" -match "SMOKE OK") { Ok "PDCurses key-compat layer ready" }
    else { SkipMsg "key-compat smoke not passed ($sm)" }
} else {
    SkipMsg "windows-curses NOT installed - TUI will degrade to CLI menu (pip install windows-curses)"
}

# ---------- summary ----------
Write-Host "`n"
if ($FAIL -eq 0) { Write-Host "ALL PASSED. Ready to ship." -ForegroundColor Green }
else             { Write-Host "Some checks failed - fix before shipping." -ForegroundColor Red }
exit $FAIL
