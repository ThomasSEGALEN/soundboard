# Draws the Soundboard icon (music notes on a rounded gradient tile) into app\Soundboard.ico
# Multi-size ICO (16..256 px, PNG-compressed entries), regenerate with:
#   powershell -ExecutionPolicy Bypass -File scripts\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\app\Soundboard.ico'

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)

    # rounded tile with a blue -> violet gradient
    $m = [Math]::Max(1, [int]($size * 0.04))
    $r = [single]($size * 0.22)
    $rect = New-Object System.Drawing.RectangleF $m, $m, ($size - 2 * $m), ($size - 2 * $m)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(255, 0, 140, 255)), ([System.Drawing.Color]::FromArgb(255, 124, 58, 237)), 45.0
    $g.FillPath($brush, $path)

    # two beamed eighth notes, drawn as shapes so they look the same at every size
    $white = [System.Drawing.Brushes]::White
    $s = $size / 64.0
    $head1 = New-Object System.Drawing.RectangleF (16 * $s), (38 * $s), (13 * $s), (10 * $s)
    $head2 = New-Object System.Drawing.RectangleF (36 * $s), (33 * $s), (13 * $s), (10 * $s)
    foreach ($h in @($head1, $head2)) {
        $st = $g.Save()
        $g.TranslateTransform($h.X + $h.Width / 2, $h.Y + $h.Height / 2)
        $g.RotateTransform(-20)
        $g.FillEllipse($white, -$h.Width / 2, -$h.Height / 2, $h.Width, $h.Height)
        $g.Restore($st)
    }
    $stemW = [single][Math]::Max(1.5, 3.2 * $s)
    $g.FillRectangle($white, [single](27.2 * $s), [single](16 * $s), $stemW, [single](26 * $s))
    $g.FillRectangle($white, [single](47.2 * $s), [single](11 * $s), $stemW, [single](26 * $s))
    $beam = New-Object System.Drawing.Drawing2D.GraphicsPath
    $beam.AddPolygon([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF (27.2 * $s), (16 * $s)),
        (New-Object System.Drawing.PointF (50.4 * $s), (11 * $s)),
        (New-Object System.Drawing.PointF (50.4 * $s), (18 * $s)),
        (New-Object System.Drawing.PointF (27.2 * $s), (23 * $s))))
    $g.FillPath($white, $beam)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        # large size: PNG entry (standard for 256 px icons)
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        if ($size -eq 256) { $bmp.Save((Join-Path $PSScriptRoot '..\app\Soundboard-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    } else {
        # small sizes: classic 32-bit DIB (BITMAPINFOHEADER + bottom-up BGRA + AND mask), read by every API
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([uint32]40); $bw.Write([int32]$size); $bw.Write([int32]($size * 2))
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
        $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
        $bw.Write([uint32]($size * $size * 4 + $maskStride * $size))
        $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $bw.Write((New-Object byte[] ($maskStride * $size))) # alpha channel is used, mask all zero
        $bw.Flush()
    }
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($sz in $sizes) { ,(New-IconPng $sz) }

$fs = [System.IO.File]::Create((Resolve-Path (Split-Path $out)).Path + '\Soundboard.ico')
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Dispose()
Write-Host "Icon created: $out"
