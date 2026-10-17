<#
.SYNOPSIS
  Regenerates src/SocklessTaskManager/Assets/app.ico.

.DESCRIPTION
  iconography.md → Favicons and app icons: a web app icon is a rounded tile (rx 7 at 32 px) in
  --sp-iron-3 with the product glyph in copper-light. The glyph is the Sockless gear mark
  (Theme/Icons.axaml → GearMark). Writes a PNG-compressed .ico with 16, 24, 32, 48 and 256 px images.
  Colours are the Workshop token values; keep them in step with Theme/Tokens.axaml.
#>
param(
    [string]$OutFile = (Join-Path $PSScriptRoot '..\src\SocklessTaskManager\Assets\app.ico')
)

Add-Type -AssemblyName System.Drawing

$iron3 = [System.Drawing.ColorTranslator]::FromHtml('#1a1a1a')       # --sp-iron-3
$copperLight = [System.Drawing.ColorTranslator]::FromHtml('#cd7f32') # --sp-copper-light
$copperDark = [System.Drawing.ColorTranslator]::FromHtml('#8b5a2b')  # --sp-copper-dark (tile rim)

function New-GearPath([float]$size) {
    # The gear mark on a 100-unit grid: ring 26–34 plus twelve 10×16 teeth.
    $s = $size / 100.0
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath ([System.Drawing.Drawing2D.FillMode]::Winding)
    $path.AddEllipse(16 * $s, 16 * $s, 68 * $s, 68 * $s)
    for ($k = 0; $k -lt 12; $k++) {
        $a = [math]::PI * 30 * $k / 180
        $points = foreach ($p in @(@(45, 2), @(55, 2), @(55, 18), @(45, 18))) {
            $dx = $p[0] - 50; $dy = $p[1] - 50
            New-Object System.Drawing.PointF ((50 + $dx * [math]::Cos($a) - $dy * [math]::Sin($a)) * $s), ((50 + $dx * [math]::Sin($a) + $dy * [math]::Cos($a)) * $s)
        }
        $path.AddPolygon([System.Drawing.PointF[]]$points)
    }
    return $path
}

function New-IconPng([int]$px) {
    $bmp = New-Object System.Drawing.Bitmap $px, $px
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded tile.
    $r = [float]($px * 7 / 32); $d = 2 * $r; $e = [float]($px - 1)
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc(0, 0, $d, $d, 180, 90); $tile.AddArc($e - $d, 0, $d, $d, 270, 90)
    $tile.AddArc($e - $d, $e - $d, $d, $d, 0, 90); $tile.AddArc(0, $e - $d, $d, $d, 90, 90)
    $tile.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $iron3), $tile)
    if ($px -ge 32) {
        $g.DrawPath((New-Object System.Drawing.Pen $copperDark, ([float][math]::Max(1, $px / 64))), $tile)
    }

    # Gear, inset to ~72% of the tile, with the ring's hole cut out.
    $gearSize = [float]($px * 0.72); $offset = [float](($px - $gearSize) / 2)
    $gear = New-GearPath $gearSize
    $hole = New-Object System.Drawing.Drawing2D.GraphicsPath
    $hole.AddEllipse([float](24 * $gearSize / 100), [float](24 * $gearSize / 100), [float](52 * $gearSize / 100), [float](52 * $gearSize / 100))
    $region = New-Object System.Drawing.Region $gear
    $region.Exclude($hole)
    $g.TranslateTransform($offset, $offset)
    $g.FillRegion((New-Object System.Drawing.SolidBrush $copperLight), $region)

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 256
$images = foreach ($size in $sizes) { , (New-IconPng $size) }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$dataOffset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$dataOffset)
    $dataOffset += $images[$i].Length
}
foreach ($image in $images) { $w.Write([byte[]]$image) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Resolve-Path -LiteralPath (Split-Path $OutFile) | Join-Path -ChildPath (Split-Path $OutFile -Leaf)), $out.ToArray())
"Wrote $OutFile"
