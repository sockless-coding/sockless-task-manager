<#
.SYNOPSIS
    Builds src/SocklessTaskManager/Features/Network/WorldMap.bin, the Network page's map outline.

.DESCRIPTION
    Country outlines come from Natural Earth's 1:110m countries and label points (where a country's
    dot goes when a database only knows the country) from its 1:50m countries, which include small
    countries the 110m set leaves out. Natural Earth is public domain. Antarctica is left out.

    Format, little-endian, coordinates as degrees x 100 in int16:
      "SLMAP1"
      u16 label count, then per label: 2 ASCII bytes ISO code, i16 longitude, i16 latitude
      u16 shape count, then per shape: 2 ASCII bytes ISO code ("--" if none), u16 ring count,
          then per ring: u16 point count, then i16 longitude, i16 latitude per point

.PARAMETER SourceFolder
    A folder with ne_110m.geojson and ne_50m.geojson already downloaded. Without it they're
    downloaded from the natural-earth-vector repository into a temporary folder.
#>
param(
    [string]$SourceFolder,
    [string]$Output = (Join-Path $PSScriptRoot '..\src\SocklessTaskManager\Features\Network\WorldMap.bin')
)

$ErrorActionPreference = 'Stop'

if (-not $SourceFolder) {
    $SourceFolder = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid())
    New-Item -ItemType Directory $SourceFolder | Out-Null
    foreach ($scale in '110m', '50m') {
        Invoke-WebRequest -Uri "https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/ne_${scale}_admin_0_countries.geojson" `
            -OutFile (Join-Path $SourceFolder "ne_$scale.geojson")
    }
}

function Read-Countries([string]$path) {
    (Get-Content -Raw -LiteralPath $path | ConvertFrom-Json).features
}

function Get-Code($properties) {
    $code = $properties.ISO_A2_EH
    if (-not $code -or $code -eq '-99' -or $code.Length -ne 2) { $code = $properties.ISO_A2 }
    if (-not $code -or $code -eq '-99' -or $code.Length -ne 2) { return '--' }
    return $code.ToUpperInvariant()
}

function Get-Fixed([double]$degrees) {
    # Double literals: with 180 PowerShell picks Math.Min(int, int) and rounds to whole degrees.
    [int16][Math]::Round([Math]::Max(-180.0, [Math]::Min(180.0, $degrees)) * 100)
}

$stream = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($stream)
$writer.Write([Text.Encoding]::ASCII.GetBytes('SLMAP1'))

# Label points: every country in the 50m set with a code.
$labels = Read-Countries (Join-Path $SourceFolder 'ne_50m.geojson') |
    ForEach-Object { [pscustomobject]@{ Code = Get-Code $_.properties; X = $_.properties.LABEL_X; Y = $_.properties.LABEL_Y } } |
    Where-Object { $_.Code -ne '--' -and $_.Code -ne 'AQ' } |
    Sort-Object Code -Unique
$writer.Write([uint16]$labels.Count)
foreach ($label in $labels) {
    $writer.Write([Text.Encoding]::ASCII.GetBytes($label.Code))
    $writer.Write((Get-Fixed $label.X))
    $writer.Write((Get-Fixed $label.Y))
}

# Shapes: the 110m outlines.
$shapes = Read-Countries (Join-Path $SourceFolder 'ne_110m.geojson') | Where-Object { (Get-Code $_.properties) -ne 'AQ' }
$writer.Write([uint16]@($shapes).Count)
foreach ($shape in $shapes) {
    $writer.Write([Text.Encoding]::ASCII.GetBytes((Get-Code $shape.properties)))
    # Assign inside the branches: an if expression's output is unrolled, which would turn a
    # single Polygon's rings into polygons and its points into rings.
    if ($shape.geometry.type -eq 'MultiPolygon') { $polygons = $shape.geometry.coordinates } else { $polygons = @(, $shape.geometry.coordinates) }
    $rings = [Collections.Generic.List[object]]::new()
    foreach ($polygon in $polygons) { foreach ($ring in $polygon) { $rings.Add($ring) } }
    $writer.Write([uint16]$rings.Count)
    foreach ($ring in $rings) {
        $writer.Write([uint16]$ring.Count)
        foreach ($point in $ring) {
            $writer.Write((Get-Fixed $point[0]))
            $writer.Write((Get-Fixed $point[1]))
        }
    }
}

$writer.Flush()
[IO.File]::WriteAllBytes((Resolve-Path -LiteralPath (Split-Path $Output) | Join-Path -ChildPath (Split-Path $Output -Leaf)), $stream.ToArray())
"Wrote $($labels.Count) labels and $(@($shapes).Count) shapes, $($stream.Length) bytes, to $Output"
