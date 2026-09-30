# =====================================================================
#  make-release.ps1 - package the assets for a GitHub Release
#
#  Usage (run from the repo root):
#      powershell -ExecutionPolicy Bypass -File tools\make-release.ps1
#      powershell -ExecutionPolicy Bypass -File tools\make-release.ps1 -SkipBuild
#
#  Produces, under <repo>\dist\ :
#      imghub-<version>-win-x64.zip     Native AOT desktop, WHOLE folder
#      imghub-<version>-android.apk
#      SHA256SUMS.txt
#
#  Why the desktop artifact is a ZIP and not a bare .exe: the AOT binary
#  needs libSkiaSharp.dll / libHarfBuzzSharp.dll / av_libglesv2.dll next to
#  it, otherwise it dies with 0xC0000409 on startup (CONSTRAINTS E1).
#
#  NOTE: ASCII-only on purpose (AGENTS.md 3.6). Windows PowerShell 5.1 reads
#        UTF-8-no-BOM as ANSI, so CJK comments would break parsing.
# =====================================================================
param(
    [string]$Configuration = "Release",
    # Skip rebuilding; package whatever is already in <repo>\release.
    # Useful when you just ran the AOT publish by hand.
    [switch]$SkipBuild,
    # Relative to the REPO ROOT. Empty = <repo>\dist.
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot | Split-Path -Parent

function Fail([string]$Message) {
    Write-Host "FAIL: $Message" -ForegroundColor Red
    exit 1
}

function Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# --------------------------------------------------------------- version
# Single source of truth = <Version> in Directory.Build.props.
Step "Reading version from Directory.Build.props"
$props = Join-Path $root "Directory.Build.props"
if (-not (Test-Path $props)) { Fail "Directory.Build.props not found at $props" }

$ver = ""
foreach ($line in (Get-Content $props)) {
    $t = $line.Trim()
    if ($t.StartsWith("<Version>") -and $t.EndsWith("</Version>")) {
        $ver = $t.Substring(9, $t.Length - 19)
        break
    }
}
if ([string]::IsNullOrWhiteSpace($ver)) { Fail "<Version> not found in Directory.Build.props" }
Write-Host "version = $ver"

$androidProject = Join-Path $root "src\ImgHub.Android\ImgHub.Android.csproj"
$androidVer = ""
foreach ($line in (Get-Content $androidProject)) {
    $t = $line.Trim()
    if ($t.StartsWith("<ApplicationDisplayVersion>")) {
        $androidVer = $t -replace '^<ApplicationDisplayVersion>', '' -replace '</ApplicationDisplayVersion>$', ''
        break
    }
}
if ($androidVer -ne $ver) {
    Write-Host "WARNING: ApplicationDisplayVersion ($androidVer) != <Version> ($ver)." -ForegroundColor Yellow
    Write-Host "         They must be kept in sync (docs/DELIVERY.md section 5)." -ForegroundColor Yellow
}

$out = if ([string]::IsNullOrWhiteSpace($OutDir)) {
    Join-Path $root "dist"
} elseif ([System.IO.Path]::IsPathRooted($OutDir)) {
    # Allow an absolute path (useful for smoke-testing outside the repo).
    $OutDir
} else {
    # Relative paths resolve against the REPO ROOT, not the current directory.
    Join-Path $root $OutDir
}
$release = Join-Path $root "release"

# --------------------------------------------------------------- build
if (-not $SkipBuild) {
    Step "Stopping running ImgHub processes (they would lock the output files)"
    Get-Process -Name "ImgHub*" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500

    Step "Publishing desktop Native AOT"
    dotnet publish (Join-Path $root "src\ImgHub.Desktop") -c $Configuration -r win-x64 `
        -p:PublishAot=true -p:DebugType=none -p:DebugSymbols=false `
        -o (Join-Path $release "desktop-aot")
    if ($LASTEXITCODE -ne 0) { Fail "dotnet publish (AOT) failed" }

    Step "Building Android APK"
    & (Join-Path $root "build.ps1") -Target android -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { Fail "build.ps1 -Target android failed" }
} else {
    Step "Skipping build (-SkipBuild); packaging existing artifacts"
}

# --------------------------------------------------------------- verify
# Do NOT skip this: a ZIP missing one native DLL looks fine until the user
# double-clicks it and gets 0xC0000409.
Step "Verifying AOT output"
$aotDir = Join-Path $release "desktop-aot"
$aotExe = Join-Path $aotDir "ImgHub.Desktop.exe"
if (-not (Test-Path $aotExe)) { Fail "missing $aotExe -- run without -SkipBuild" }

$required = @("libSkiaSharp.dll", "libHarfBuzzSharp.dll", "av_libglesv2.dll")
foreach ($dll in $required) {
    $p = Join-Path $aotDir $dll
    if (-not (Test-Path $p)) { Fail "missing native dependency: $dll (AOT exe would crash with 0xC0000409)" }
    Write-Host ("  OK  {0,-24} {1,8:N2} MB" -f $dll, ((Get-Item $p).Length / 1MB))
}
Write-Host ("  OK  {0,-24} {1,8:N2} MB" -f "ImgHub.Desktop.exe", ((Get-Item $aotExe).Length / 1MB))

Step "Locating Android APK"
$apk = Get-ChildItem (Join-Path $release "android") -Filter "imghub-*-android.apk" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $apk) { Fail "no imghub-*-android.apk under release\android" }
Write-Host ("  OK  {0} ({1:N2} MB)" -f $apk.Name, ($apk.Length / 1MB))
if ($apk.Name -notlike "*$ver*") {
    Write-Host "  NOTE: APK is named '$($apk.Name)' but <Version> is '$ver'." -ForegroundColor Yellow
    Write-Host "        It will be repackaged under the current version name." -ForegroundColor Yellow
}

# --------------------------------------------------------------- package
Step "Packaging into $out"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null

$zip = Join-Path $out "imghub-$ver-win-x64.zip"
Compress-Archive -Path (Join-Path $aotDir "*") -DestinationPath $zip -CompressionLevel Optimal
if ($LASTEXITCODE -ne 0 -and -not (Test-Path $zip)) { Fail "Compress-Archive failed" }

$apkDest = Join-Path $out "imghub-$ver-android.apk"
Copy-Item $apk.FullName $apkDest -Force

# --------------------------------------------------------------- checksums
Step "Writing SHA256SUMS.txt"
$lines = @()
foreach ($f in @($zip, $apkDest)) {
    $hash = (Get-FileHash $f -Algorithm SHA256).Hash.ToLower()
    $lines += "$hash  $(Split-Path $f -Leaf)"
    Write-Host "  $hash  $(Split-Path $f -Leaf)"
}
$lines | Set-Content (Join-Path $out "SHA256SUMS.txt") -Encoding ASCII

# --------------------------------------------------------------- summary
Step "Done"
Get-ChildItem $out | ForEach-Object { Write-Host ("  {0,-34} {1,8:N2} MB" -f $_.Name, ($_.Length / 1MB)) }
Write-Host ""
Write-Host "Next: create a tag and a GitHub Release, then attach everything in $out"
Write-Host "  git tag v$ver"
Write-Host "  git push origin v$ver"
Write-Host "Then paste .github\RELEASE_TEMPLATE.md into the release body."
