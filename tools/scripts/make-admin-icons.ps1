# Cash desk app icons (PWA, apps/admin/public): the console's mark: an ice-blue diamond outline with a filled core, on
# its obsidian background. Windows PowerShell 5.1 (System.Drawing); run again after changing the mark.
param([string]$Out = (Join-Path $PSScriptRoot '..\..\apps\admin\public'))
Add-Type -AssemblyName System.Drawing
$bg = [System.Drawing.ColorTranslator]::FromHtml('#07090C')
$panel = [System.Drawing.ColorTranslator]::FromHtml('#0D1117')
$accent = [System.Drawing.ColorTranslator]::FromHtml('#9ADFFF')

function Diamond([float]$cx, [float]$cy, [float]$r) {
    [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF($cx, ($cy - $r))),
        (New-Object System.Drawing.PointF(($cx + $r), $cy)),
        (New-Object System.Drawing.PointF($cx, ($cy + $r))),
        (New-Object System.Drawing.PointF(($cx - $r), $cy)))
}

# $rounded: transparent corners (purpose "any"); otherwise full bleed (maskable, Apple). $scale: size of the mark.
function Icon([int]$size, [string]$name, [bool]$rounded, [float]$scale) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    if ($rounded) {
        $r = $size * 0.22
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
        $path.AddArc($size - 2 * $r, 0, 2 * $r, 2 * $r, 270, 90)
        $path.AddArc($size - 2 * $r, $size - 2 * $r, 2 * $r, 2 * $r, 0, 90)
        $path.AddArc(0, $size - 2 * $r, 2 * $r, 2 * $r, 90, 90)
        $path.CloseFigure()
        $g.FillPath((New-Object System.Drawing.SolidBrush($bg)), $path)
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(40, $accent), [Math]::Max(1, $size * 0.008))
        $g.DrawPath($pen, $path)
    } else {
        $g.Clear($bg)
    }
    $c = $size / 2
    $outer = $size * 0.30 * $scale
    $g.FillPolygon((New-Object System.Drawing.SolidBrush($panel)), (Diamond $c $c $outer))
    $stroke = New-Object System.Drawing.Pen($accent, [Math]::Max(1.5, $size * 0.045 * $scale))
    $stroke.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Miter
    $g.DrawPolygon($stroke, (Diamond $c $c $outer))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush($accent)), (Diamond $c $c ($size * 0.105 * $scale)))
    $g.Dispose()
    $bmp.Save((Join-Path $Out $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

New-Item -ItemType Directory -Force $Out | Out-Null
Icon 192 'icon-192.png' $true 1.0
Icon 512 'icon-512.png' $true 1.0
# Maskable: the platform crops to its own shape; the mark stays inside the central safe zone.
Icon 512 'icon-maskable-512.png' $false 0.78
Icon 180 'apple-touch-icon.png' $false 0.9
Icon 32 'favicon-32.png' $true 1.25
Get-ChildItem $Out | Select-Object Name, Length
