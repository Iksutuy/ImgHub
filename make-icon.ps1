Add-Type -AssemblyName System.Drawing

$src = 'D:\Project\imagagent\Imagagent\avalonia\src\Imgagent.App\Assets\app-icon.png'
$outIco = 'D:\Project\imagagent\Imagagent\avalonia\src\Imgagent.Desktop\app.ico'
$appIco = 'D:\Project\imagagent\Imagagent\avalonia\src\Imgagent.App\Assets\app.ico'

if (-not (Test-Path $src)) { Write-Output "SRC MISSING"; exit 1 }

$srcImg = [System.Drawing.Image]::FromFile($src)
Write-Output ("source: " + $srcImg.Width + "x" + $srcImg.Height)

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngBlobs = New-Object System.Collections.ArrayList

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($srcImg, 0, 0, $s, $s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    [void]$pngBlobs.Add(@{ Size = $s; Bytes = $ms.ToArray() })
    $bmp.Dispose()
    $ms.Dispose()
}

# 组装 ICO
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($out)

# ICONDIR: reserved(2) type(2) count(2)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$pngBlobs.Count)

# 目录项 16 字节 * N，数据偏移从头部之后开始
$dataOffset = 6 + (16 * $pngBlobs.Count)
foreach ($blob in $pngBlobs) {
    $s = $blob.Size
    $dim = if ($s -ge 256) { 0 } else { $s }
    $writer.Write([byte]$dim)              # width
    $writer.Write([byte]$dim)              # height
    $writer.Write([byte]0)                 # color count
    $writer.Write([byte]0)                 # reserved
    $writer.Write([uint16]1)               # planes
    $writer.Write([uint16]32)              # bit count
    $writer.Write([uint32]$blob.Bytes.Length)
    $writer.Write([uint32]$dataOffset)
    $dataOffset += $blob.Bytes.Length
}

# 图像数据
foreach ($blob in $pngBlobs) { $writer.Write($blob.Bytes) }
$writer.Flush()

$all = $out.ToArray()
[System.IO.File]::WriteAllBytes($outIco, $all)
[System.IO.File]::WriteAllBytes($appIco, $all)
Write-Output ("ICO written: " + $all.Length + " bytes")

# 校验能被系统 Icon 类加载
$test = New-Object System.Drawing.Icon($outIco)
Write-Output ("ICON LOAD OK: " + $test.Width + "x" + $test.Height)
$test.Dispose()

$srcImg.Dispose()
$writer.Dispose()
$out.Dispose()
Write-Output "DONE"