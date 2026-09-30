# Generates app.ico for the Windows exe (and for the Avalonia window icon).
#
# WHY THIS FILE IS ASCII-ONLY (no Chinese comments):
#   Windows PowerShell 5.1 reads UTF-8 files WITHOUT a BOM as ANSI.
#   Non-ASCII comments then corrupt parsing (this repo got bitten by that before).
#   Keep every comment in English.
#
# WHY BMP FRAMES, NOT PNG-IN-ICO:
#   The previous version embedded PNG blobs inside the .ico. Explorer / Shell32
#   do not reliably render PNG-in-ICO for the *exe* icon, so the binary showed
#   the generic application icon. BMP (DIB) frames are the universally
#   supported format. See docs/CONSTRAINTS.md D4.
#
# Usage:  powershell -NoProfile -File make-icon.ps1

Add-Type -AssemblyName System.Drawing

$src = Join-Path $PSScriptRoot 'src\ImgHub.App\Assets\app-icon.png'
$outDesktop = Join-Path $PSScriptRoot 'src\ImgHub.Desktop\app.ico'
$outApp = Join-Path $PSScriptRoot 'src\ImgHub.App\Assets\app.ico'

if (-not (Test-Path $src)) { Write-Output "SRC MISSING: $src"; exit 1 }

$srcImg = [System.Drawing.Image]::FromFile($src)
Write-Output ("source PNG: {0}x{1}" -f $srcImg.Width, $srcImg.Height)

# 16/24/32/48/64/128/256 are the standard sizes Windows asks for.
$sizes = @(16, 24, 32, 48, 64, 128, 256)

# ---------------------------------------------------------------- build DIB frames
$frames = New-Object System.Collections.ArrayList
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($srcImg, 0, 0, $s, $s)
    $g.Dispose()

    # ICO DIB frame layout: BITMAPINFOHEADER (40 bytes) whose biHeight is DOUBLED
    # (XOR image + AND mask), then BGRA pixels bottom-up, then the 1bpp AND mask
    # (each row padded to a 4-byte boundary).
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    $bw.Write([uint32]40)              # biSize
    $bw.Write([int32]$s)               # biWidth
    $bw.Write([int32]($s * 2))         # biHeight = XOR + AND
    $bw.Write([uint16]1)               # biPlanes
    $bw.Write([uint16]32)              # biBitCount
    $bw.Write([uint32]0)               # biCompression = BI_RGB
    $bw.Write([uint32]($s * $s * 4))   # biSizeImage (XOR part)
    $bw.Write([int32]0); $bw.Write([int32]0)     # x/y pixels-per-meter
    $bw.Write([uint32]0); $bw.Write([uint32]0)   # palette

    # XOR bitmap: bottom-up BGRA
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G)
            $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }

    # AND mask: 0 = let the XOR alpha decide (1 = fully transparent).
    $maskRowBytes = [int]([Math]::Ceiling($s / 8.0))
    if ($maskRowBytes % 4 -ne 0) { $maskRowBytes += (4 - ($maskRowBytes % 4)) }
    for ($y = 0; $y -lt $s; $y++) {
        for ($i = 0; $i -lt $maskRowBytes; $i++) { $bw.Write([byte]0) }
    }

    $bw.Flush()
    [void]$frames.Add(@{ Size = $s; Bytes = $ms.ToArray() })
    $bw.Dispose(); $ms.Dispose(); $bmp.Dispose()
}

# ---------------------------------------------------------------- assemble ICO
$outMs = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($outMs)

# ICONDIR: reserved(0) type(1 = icon) count
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)

# CRITICAL: the offset base is the ICONDIR plus the whole directory table.
# Getting this wrong makes every frame unreadable (the file still "looks" fine
# byte-count-wise, but Windows shows the generic icon).
$dirLen = 6 + (16 * $frames.Count)
$offset = $dirLen
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim)                  # width (0 means 256)
    $w.Write([byte]$dim)                  # height
    $w.Write([byte]0)                     # palette size
    $w.Write([byte]0)                     # reserved
    $w.Write([uint16]1)                   # planes
    $w.Write([uint16]32)                  # bits per pixel
    $w.Write([uint32]$f.Bytes.Length)     # frame length
    $w.Write([uint32]$offset)             # ABSOLUTE offset from file start
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $w.Write($f.Bytes) }
$w.Flush()

$all = $outMs.ToArray()
[System.IO.File]::WriteAllBytes($outDesktop, $all)
[System.IO.File]::WriteAllBytes($outApp, $all)

$w.Dispose(); $outMs.Dispose(); $srcImg.Dispose()

Write-Output ("ICO written: {0} bytes ({1} frames)" -f $all.Length, $frames.Count)

# ---------------------------------------------------------------- verify
# 1) must load through System.Drawing.Icon
$loaded = $false
try {
    $test = New-Object System.Drawing.Icon($outDesktop)
    Write-Output ("LOAD OK: {0}x{1}" -f $test.Width, $test.Height)
    $test.Dispose()
    $loaded = $true
} catch {
    Write-Output ("LOAD FAILED: " + $_.Exception.Message)
}

# 2) every directory offset must point inside the file, past the directory table
$bad = 0
$raw = [System.IO.File]::ReadAllBytes($outDesktop)
$n = [BitConverter]::ToUInt16($raw, 4)
for ($i = 0; $i -lt $n; $i++) {
    $o = 6 + ($i * 16)
    $off = [BitConverter]::ToUInt32($raw, $o + 12)
    if ($off -lt $dirLen -or $off -ge $raw.Length) { $bad++ }
}
if ($bad -eq 0) { Write-Output "OFFSETS OK (all >= $dirLen)" } else { Write-Output "OFFSETS BAD: $bad entries" }

if (-not $loaded -or $bad -ne 0) { Write-Output "RESULT: FAILED"; exit 1 }
Write-Output "DONE"
