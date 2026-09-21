# =====================================================================
#  build.ps1 - imgagent Avalonia cross-platform build
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
#        %USERPROFILE%\android-sdk-imgagent
#        %USERPROFILE%\jdk-imgagent
#    If missing, run once:
#        dotnet build src\Imgagent.Android -t:InstallAndroidDependencies `
#            -f net10.0-android -p:AcceptAndroidSDKLicenses=True `
#            -p:AndroidSdkDirectory=$env:USERPROFILE\android-sdk-imgagent `
#            -p:JavaSdkDirectory=$env:USERPROFILE\jdk-imgagent
# =====================================================================
param(
    [ValidateSet("all", "desktop", "android")]
    [string]$Target = "all",
    [string]$Configuration = "Release",
    [string]$OutDir = "..\release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# Android toolchain paths (user dirs -> no admin rights needed)
$sdk = Join-Path $env:USERPROFILE "android-sdk-imgagent"
$jdk = Join-Path $env:USERPROFILE "jdk-imgagent"

function Build-Desktop {
    Write-Host ""
    Write-Host "=== Build Windows desktop ($Configuration) ===" -ForegroundColor Cyan
    $out = Join-Path $root "$OutDir\desktop"
    dotnet publish "$root\src\Imgagent.Desktop\Imgagent.Desktop.csproj" `
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

    dotnet build "$root\src\Imgagent.Android\Imgagent.Android.csproj" `
        -c $Configuration `
        -p:AndroidSdkDirectory="$sdk" `
        -p:JavaSdkDirectory="$jdk"
    if ($LASTEXITCODE -ne 0) { throw "android build failed" }

    $apk = Join-Path $root "src\Imgagent.Android\bin\$Configuration\net10.0-android\com.imgagent.app-Signed.apk"
    if (-not (Test-Path $apk)) { throw "APK not produced: $apk" }

    $out = Join-Path $root "$OutDir\android"
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    $dest = Join-Path $out "imgagent-5.22.0-android.apk"
    Copy-Item $apk $dest -Force
    $mb = [math]::Round((Get-Item $dest).Length / 1MB, 2)
    Write-Host ("  -> " + $dest + " (" + $mb + " MB)") -ForegroundColor Green
}

Write-Host ("imgagent Avalonia build - target=" + $Target + " config=" + $Configuration)

if ($Target -eq "all" -or $Target -eq "desktop") { Build-Desktop }
if ($Target -eq "all" -or $Target -eq "android") { Build-Android }

Write-Host ""
Write-Host "All done." -ForegroundColor Green
