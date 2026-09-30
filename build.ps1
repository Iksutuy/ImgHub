# =====================================================================
#  build.ps1 - imghub Avalonia cross-platform build
#              (Windows desktop + Android APK)
#
#  Usage:
#      powershell -ExecutionPolicy Bypass -File build.ps1
#      powershell -ExecutionPolicy Bypass -File build.ps1 -Target desktop
#      powershell -ExecutionPolicy Bypass -File build.ps1 -Target android
#
#  NOTE: ASCII-only on purpose. Windows PowerShell 5.1 reads UTF-8-no-BOM
#        as ANSI, which would corrupt CJK comments and break parsing.
#
#  Android prerequisites (already configured on this machine):
#    * dotnet workloads: android + wasm-tools
#    * Android SDK / JDK live in USER paths (no admin needed):
#        %USERPROFILE%\android-sdk-imghub
#        %USERPROFILE%\jdk-imghub
#      (renamed from android-sdk-imgagent / jdk-imgagent; the script auto-detects
#       either name so an existing installation keeps working)
#    If missing, run once:
#        dotnet build src\ImgHub.Android -t:InstallAndroidDependencies `
#            -f net10.0-android -p:AcceptAndroidSDKLicenses=True `
#            -p:AndroidSdkDirectory=$env:USERPROFILE\android-sdk-imghub `
#            -p:JavaSdkDirectory=$env:USERPROFILE\jdk-imghub
# =====================================================================
param(
    [ValidateSet("all", "desktop", "android")]
    [string]$Target = "all",
    [string]$Configuration = "Release",
    # Relative to the REPO ROOT (not to the current directory).
    # Empty = default, i.e. <repo>\release. See Resolve-OutDir below.
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# FIX (v0.5.28): the old default was "..\release", which is relative to the
# CURRENT WORKING DIRECTORY. When invoked from the repo root it wrote OUTSIDE
# the repo (D:\Project\imagagent\release), so the artifacts documented in
# docs/DELIVERY.md could not be found. Everything is now resolved against the
# repo root, so the output always lands inside the repo.
function Resolve-OutDir {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return (Join-Path $root "release") }
    if ([System.IO.Path]::IsPathRooted($Value)) { return $Value }
    return (Join-Path $root $Value)
}

$outDir = Resolve-OutDir $OutDir

# Android toolchain paths (user dirs -> no admin rights needed).
# Accept both the new (imghub) and the pre-rename (imgagent) directory names,
# so an already-installed SDK/JDK is found without any manual moving.
function Resolve-ToolDir {
    param([string]$NewName, [string]$LegacyName)
    $new = Join-Path $env:USERPROFILE $NewName
    if (Test-Path $new) { return $new }
    $legacy = Join-Path $env:USERPROFILE $LegacyName
    if (Test-Path $legacy) { return $legacy }
    return $new
}

$sdk = Resolve-ToolDir "android-sdk-imghub" "android-sdk-imgagent"
$jdk = Resolve-ToolDir "jdk-imghub" "jdk-imgagent"

function Build-Desktop {
    Write-Host ""
    Write-Host "=== Build Windows desktop ($Configuration) ===" -ForegroundColor Cyan
    $out = Join-Path $outDir "desktop"
    dotnet publish "$root\src\ImgHub.Desktop\ImgHub.Desktop.csproj" `
        -c $Configuration -r win-x64 --self-contained false -o $out
    if ($LASTEXITCODE -ne 0) { throw "desktop build failed" }
    Write-Host ("  -> " + $out) -ForegroundColor Green
}

function Build-Android {
    Write-Host ""
    Write-Host "=== Build Android APK ($Configuration) ===" -ForegroundColor Cyan
    if (-not (Test-Path $sdk)) { throw "Android SDK not found: $sdk (see header notes)" }
    if (-not (Test-Path $jdk)) { throw "JDK not found: $jdk (see header notes)" }

    $env:ANDROID_HOME = $sdk
    $env:ANDROID_SDK_ROOT = $sdk

    dotnet build "$root\src\ImgHub.Android\ImgHub.Android.csproj" `
        -c $Configuration `
        -p:AndroidSdkDirectory="$sdk" `
        -p:JavaSdkDirectory="$jdk"
    if ($LASTEXITCODE -ne 0) { throw "android build failed" }

    $apk = Join-Path $root "src\ImgHub.Android\bin\$Configuration\net10.0-android\com.imghub.app-Signed.apk"
    if (-not (Test-Path $apk)) { throw "APK not produced: $apk" }

    $out = Join-Path $outDir "android"
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    # Read the version from Directory.Build.props (single source of truth)
    # instead of hardcoding it: the old value "0.5.26" silently drifted out of
    # sync whenever the version was bumped.
    $ver = "unknown"
    $props = Join-Path $root "Directory.Build.props"
    if (Test-Path $props) {
        # Avoid Select-String .Matches (awkward to parse under PS 5.1);
        # a plain string scan is enough here.
        foreach ($line in (Get-Content $props)) {
            $t = $line.Trim()
            if ($t.StartsWith("<Version>") -and $t.EndsWith("</Version>")) {
                $ver = $t.Substring(9, $t.Length - 19)
                break
            }
        }
    }
    $dest = Join-Path $out ("imghub-" + $ver + "-android.apk")
    Copy-Item $apk $dest -Force
    $mb = [math]::Round((Get-Item $dest).Length / 1MB, 2)
    Write-Host ("  -> " + $dest + " (" + $mb + " MB)") -ForegroundColor Green
}

Write-Host ("imghub Avalonia build - target=" + $Target + " config=" + $Configuration)

if ($Target -eq "all" -or $Target -eq "desktop") { Build-Desktop }
if ($Target -eq "all" -or $Target -eq "android") { Build-Android }

Write-Host ""
Write-Host "All done." -ForegroundColor Green
