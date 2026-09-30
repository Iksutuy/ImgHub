# 从 App 的 app-icon.png 生成 Android 全套图标资源
#   · 各密度 mipmap/ic_launcher.png（方图 + 圆图）
#   · mipmap-anydpi-v26/ic_launcher.xml（自适应图标）
#   · values/colors.xml（背景色）
# 用法：powershell -File tools\make-android-icons.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src\ImgHub.App\Assets\app-icon.png'
$resRoot = Join-Path $root 'src\ImgHub.Android\Resources'

if (-not (Test-Path $src)) { throw "source icon missing: $src" }

$srcImg = [System.Drawing.Image]::FromFile($src)
Write-Output ("source: " + $srcImg.Width + "x" + $srcImg.Height)

$names = @('mdpi', 'hdpi', 'xhdpi', 'xxhdpi', 'xxxhdpi')
$sizes = @(48, 72, 96, 144, 192)

function Save-Png($bmp, $path) {
    $dir = Split-Path -Parent $path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
}

for ($i = 0; $i -lt $names.Count; $i++) {
    $name = $names[$i]
    $size = $sizes[$i]
    $dir = Join-Path $resRoot ("mipmap-" + $name)

    # 方形图标
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($srcImg, 0, 0, $size, $size)
    $g.Dispose()
    Save-Png $bmp (Join-Path $dir 'ic_launcher.png')
    $bmp.Dispose()

    # 圆形图标
    $bmp2 = New-Object System.Drawing.Bitmap($size, $size)
    $g2 = [System.Drawing.Graphics]::FromImage($bmp2)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g2.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g2.Clear([System.Drawing.Color]::Transparent)
    $clip = New-Object System.Drawing.Drawing2D.GraphicsPath
    $clip.AddEllipse(0, 0, $size, $size)
    $g2.SetClip($clip)
    $g2.DrawImage($srcImg, 0, 0, $size, $size)
    $g2.Dispose()
    Save-Png $bmp2 (Join-Path $dir 'ic_launcher_round.png')
    $bmp2.Dispose()
    $clip.Dispose()

    Write-Output ("  " + $name + ": " + $size + "x" + $size)
}

# 自适应图标前景（108dp @ xxxhdpi = 432px，内容占中间 66% 安全区）
$fgSize = 432
$inner = [int]($fgSize * 0.66)
$off = [int](($fgSize - $inner) / 2)
$bmpFg = New-Object System.Drawing.Bitmap($fgSize, $fgSize)
$gFg = [System.Drawing.Graphics]::FromImage($bmpFg)
$gFg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$gFg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$gFg.Clear([System.Drawing.Color]::Transparent)
$gFg.DrawImage($srcImg, $off, $off, $inner, $inner)
$gFg.Dispose()
Save-Png $bmpFg (Join-Path $resRoot 'mipmap-xxxhdpi\ic_launcher_foreground.png')
$bmpFg.Dispose()

# anydpi-v26 自适应图标
$anyDir = Join-Path $resRoot 'mipmap-anydpi-v26'
if (-not (Test-Path $anyDir)) { New-Item -ItemType Directory -Force -Path $anyDir | Out-Null }
$adaptive = @'
<?xml version="1.0" encoding="utf-8"?>
<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
  <background android:drawable="@color/ic_launcher_background" />
  <foreground android:drawable="@mipmap/ic_launcher_foreground" />
</adaptive-icon>
'@
Set-Content -Path (Join-Path $anyDir 'ic_launcher.xml') -Value $adaptive -Encoding UTF8
Copy-Item (Join-Path $anyDir 'ic_launcher.xml') (Join-Path $anyDir 'ic_launcher_round.xml') -Force

# 背景色
$colorsDir = Join-Path $resRoot 'values'
if (-not (Test-Path $colorsDir)) { New-Item -ItemType Directory -Force -Path $colorsDir | Out-Null }
$colors = @'
<?xml version="1.0" encoding="utf-8"?>
<resources>
  <color name="ic_launcher_background">#1B6FE8</color>
</resources>
'@
Set-Content -Path (Join-Path $colorsDir 'colors.xml') -Value $colors -Encoding UTF8

# 清理旧资源
$oldIcon = Join-Path $root 'src\ImgHub.Android\Icon.png'
if (Test-Path $oldIcon) { Remove-Item $oldIcon -Force; Write-Output "removed old Icon.png" }
$oldDrawable = Join-Path $resRoot 'drawable'
if (Test-Path $oldDrawable) { Remove-Item $oldDrawable -Recurse -Force; Write-Output "removed old drawable/" }

$srcImg.Dispose()
Write-Output "DONE - mipmaps + adaptive icon generated"