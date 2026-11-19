<#
  Generates Rimlight's icon: a dark rounded-square "screen" with a glowing purple→cyan gradient rim.
  Writes a multi-resolution .ico (16–256 px) plus a 256 px PNG preview.
  Usage: powershell -File installer/tools/make-icon.ps1 [-OutDir src/Rimlight/Assets]
  Runs on stock Windows PowerShell 5.1 (uses System.Drawing / GDI+).
#>
param([string]$OutDir = (Join-Path $PSScriptRoot '..\..\src\Rimlight\Assets'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$purple = [System.Drawing.Color]::FromArgb(255, 0x8A, 0x5C, 0xFF)
$cyan   = [System.Drawing.Color]::FromArgb(255, 0x22, 0xD3, 0xEE)

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Max(0.5, 2 * $r)
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-GradientPen([float]$n, [float]$width, [int]$alpha) {
    $a = [System.Drawing.Color]::FromArgb($alpha, $purple)
    $b = [System.Drawing.Color]::FromArgb($alpha, $cyan)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF($n, $n)), $a, $b)
    $pen = New-Object System.Drawing.Pen($brush, $width)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    return $pen
}

function Render-Icon([int]$n) {
    $bmp = New-Object System.Drawing.Bitmap($n, $n, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $small = $n -le 24
    $inset = if ($small) { [Math]::Max(1.0, $n * 0.08) } else { $n * 0.15 }
    $rim   = if ($small) { [Math]::Max(1.6, $n * 0.13) } else { $n * 0.065 }
    $size  = $n - 2 * $inset
    $rad   = $size * 0.22
    $path  = New-RoundRect $inset $inset $size $size $rad

    if (-not $small) {
        # Outer glow: progressively wider, fainter strokes approximate a blur.
        $steps = 10
        for ($i = $steps; $i -ge 1; $i--) {
            $w = $rim + ($n * 0.22) * ($i / $steps)
            $alpha = [int](70 * [Math]::Pow(1 - ($i / ($steps + 1)), 2.2))
            $pen = New-GradientPen $n $w $alpha
            $g.DrawPath($pen, $path); $pen.Dispose()
        }
    }

    # Screen
    $fill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0x14, 0x14, 0x1B))
    $g.FillPath($fill, $path); $fill.Dispose()

    if (-not $small) {
        # Inner glow: light spilling inward from the rim.
        $clip = New-Object System.Drawing.Region($path)
        $g.SetClip($clip, [System.Drawing.Drawing2D.CombineMode]::Replace)
        $steps = 8
        for ($i = $steps; $i -ge 1; $i--) {
            $w = $rim + ($n * 0.20) * ($i / $steps)
            $alpha = [int](55 * [Math]::Pow(1 - ($i / ($steps + 1)), 2.0))
            $pen = New-GradientPen $n $w $alpha
            $g.DrawPath($pen, $path); $pen.Dispose()
        }
        $g.ResetClip()
    }

    # Rim
    $pen = New-GradientPen $n $rim 255
    $g.DrawPath($pen, $path); $pen.Dispose()

    $g.Dispose()
    return $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $n = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle(0, 0, $n, $n)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $raw = New-Object byte[] ($stride * $n)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
    $bmp.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $maskRow = [int]([Math]::Ceiling($n / 32.0) * 4)
    # BITMAPINFOHEADER (height is doubled: XOR image + AND mask)
    $bw.Write([int]40); $bw.Write([int]$n); $bw.Write([int]($n * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]($n * $n * 4 + $maskRow * $n)); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $n - 1; $y -ge 0; $y--) { $bw.Write($raw, $y * $stride, $n * 4) }   # bottom-up BGRA
    $bw.Write((New-Object byte[] ($maskRow * $n)))                                # AND mask (alpha does the work)
    $bw.Flush()
    return ,$ms.ToArray()   # leading comma: stop PowerShell unrolling the byte[] into object[]
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @()
foreach ($s in $sizes) {
    $bmp = Render-Icon $s
    if ($s -eq 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
        $bmp.Save((Join-Path $OutDir 'Rimlight-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        $bytes = Get-DibBytes $bmp
    }
    $bmp.Dispose()
    $images += ,@($s, $bytes)
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $s = $img[0]; $len = $img[1].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$len); $w.Write([int]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write([byte[]]$img[1]) }
$w.Flush()
$icoPath = Join-Path $OutDir 'Rimlight.ico'
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
Write-Host "Wrote $icoPath ($($out.Length) bytes)"
