# =====================================================================
#  make-release.ps1 - package the assets for a GitHub Release
#
#  Usage (run from the repo root):
#      powershell -ExecutionPolicy Bypass -File tools\make-release.ps1
#      powershell -ExecutionPolicy Bypass -File tools\make-release.ps1 -SkipBuild
#      powershell -ExecutionPolicy Bypass -File tools\make-release.ps1 -SkipAndroid
#
#  Produces, under <repo>\dist\ :
#      imghub-<version>-win-x64.zip     Native AOT desktop, runnable files only
#      imghub-<version>-android.apk     (omitted with -SkipAndroid)
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
    # Ship the desktop build only: skip building and attaching the APK.
    # The Android head is not finished yet, so desktop-only releases are the
    # normal case for now.
    [switch]$SkipAndroid,
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

    if ($SkipAndroid) {
        Step "Skipping Android build (-SkipAndroid)"
    } else {
        Step "Building Android APK"
        & (Join-Path $root "build.ps1") -Target android -Configuration $Configuration
        if ($LASTEXITCODE -ne 0) { Fail "build.ps1 -Target android failed" }
    }
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

$apk = $null
$apkDest = $null
if ($SkipAndroid) {
    Step "Skipping Android APK (-SkipAndroid)"
} else {
    Step "Locating Android APK"
    $apk = Get-ChildItem (Join-Path $release "android") -Filter "imghub-*-android.apk" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $apk) { Fail "no imghub-*-android.apk under release\android" }
    Write-Host ("  OK  {0} ({1:N2} MB)" -f $apk.Name, ($apk.Length / 1MB))
    if ($apk.Name -notlike "*$ver*") {
        Write-Host "  NOTE: APK is named '$($apk.Name)' but <Version> is '$ver'." -ForegroundColor Yellow
        Write-Host "        It will be repackaged under the current version name." -ForegroundColor Yellow
    }
}

# --------------------------------------------------------------- package
Step "Packaging into $out"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null

# Package from an explicit ALLOW-LIST, not "everything in the folder".
# WHY: the AOT output directory collects things that must not ship --
#   * third-party *.pdb (SkiaSharp ~80 MB, HarfBuzz ~20 MB): the publish sets
#     DebugSymbols=false for ImgHub's own binary, but the native packages drop
#     their own symbols next to the DLLs. Zipping verbatim made a 52 MB
#     download where half was debug symbols.
#   * stray archives left in the folder by hand (a desktop-aot.rar appeared
#     this way and rode along in the first build of the package).
# An allow-list fails loudly on surprise content instead of silently shipping it.
$stage = Join-Path $env:TEMP ("imghub-stage-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    $ship = @("ImgHub.Desktop.exe") + $required
    foreach ($name in $ship) {
        $src = Join-Path $aotDir $name
        if (-not (Test-Path $src)) { Fail "missing required file: $name" }
        Copy-Item $src (Join-Path $stage $name) -Force
        Write-Host ("  ship {0,-24} {1,8:N2} MB" -f $name, ((Get-Item $src).Length / 1MB))
    }

    # Anything else in the folder is deliberately not shipped -- report it so a
    # surprise file is visible in the log rather than silently dropped.
    foreach ($f in (Get-ChildItem $aotDir -File)) {
        if ($ship -notcontains $f.Name) {
            Write-Host ("  skip {0,-24} {1,8:N2} MB" -f $f.Name, ($f.Length / 1MB)) -ForegroundColor DarkGray
        }
    }

    $zip = Join-Path $out "imghub-$ver-win-x64.zip"
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
    if ($LASTEXITCODE -ne 0 -and -not (Test-Path $zip)) { Fail "Compress-Archive failed" }
} finally {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
}

# Ship the APK only when it was requested and located.
if (-not $SkipAndroid -and $apk) {
    $apkDest = Join-Path $out "imghub-$ver-android.apk"
    Copy-Item $apk.FullName $apkDest -Force
}

# --------------------------------------------------------------- checksums
# Write both SHA256 (matching the bundled SHA256SUMS.txt format) and MD5,
# because users habitually check either one.
Step "Writing checksums"
$assets = @($zip)
if ($apkDest) { $assets += $apkDest }

$lines = @()
$md5Lines = @()
foreach ($f in $assets) {
    $leaf = Split-Path $f -Leaf
    $sha = (Get-FileHash $f -Algorithm SHA256).Hash.ToLower()
    $md5 = (Get-FileHash $f -Algorithm MD5).Hash.ToLower()
    $lines += "$sha  $leaf"
    $md5Lines += "$md5  $leaf"
    Write-Host ("  {0}" -f $leaf)
    Write-Host ("    MD5    {0}" -f $md5)
    Write-Host ("    SHA256 {0}" -f $sha)
}
$lines | Set-Content (Join-Path $out "SHA256SUMS.txt") -Encoding ASCII
$md5Lines | Set-Content (Join-Path $out "MD5SUMS.txt") -Encoding ASCII

# --------------------------------------------------------------- summary
Step "Done"
Get-ChildItem $out | ForEach-Object { Write-Host ("  {0,-34} {1,8:N2} MB" -f $_.Name, ($_.Length / 1MB)) }
Write-Host ""
Write-Host "Next: attach everything in $out to a GitHub Release for tag v$ver"
Write-Host "  gh release create v$ver --title `"ImgHub v$ver`" --notes-file .github\RELEASE_NOTES_v$ver.md (assets...)"
