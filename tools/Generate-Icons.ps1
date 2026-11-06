<#
.SYNOPSIS
    Regenerates the app's PNG assets and AppIcon.ico from Assets/AppIcon.svg.

.DESCRIPTION
    Renders the SVGs with headless Microsoft Edge, then resizes with System.Drawing.
    Sizes 24 px and below use the simplified Assets/AppIcon.small.svg.
    Requires Windows with Microsoft Edge installed. Run from any directory:

        pwsh tools/Generate-Icons.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = Join-Path (Split-Path $PSScriptRoot -Parent) 'Assets'
$renderSize = 1024

function Find-Edge {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
    )
    $edge = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $edge) { throw 'Microsoft Edge not found.' }
    $edge
}

# Renders an SVG to a square transparent bitmap of $renderSize pixels.
function Render-Svg([string]$svgPath) {
    $work = Join-Path ([IO.Path]::GetTempPath()) "reorder-icons-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory $work | Out-Null
    try {
        $svgUri = ([Uri](Resolve-Path $svgPath).Path).AbsoluteUri
        $html = Join-Path $work 'render.html'
        $png = Join-Path $work 'render.png'
        Set-Content $html -Encoding utf8 @"
<!DOCTYPE html><html><head><style>html,body{margin:0;background:transparent}img{display:block;width:${renderSize}px;height:${renderSize}px}</style></head>
<body><img src="$svgUri"></body></html>
"@
        $edgeArgs = @(
            '--headless=new', '--disable-gpu', '--hide-scrollbars',
            '--default-background-color=00000000',
            "--window-size=$renderSize,$renderSize",
            "--user-data-dir=$work\profile",
            "--screenshot=$png",
            ([Uri]$html).AbsoluteUri
        )
        Start-Process (Find-Edge) -ArgumentList $edgeArgs -Wait -WindowStyle Hidden
        if (-not (Test-Path $png)) { throw "Edge failed to render $svgPath" }

        # Load into memory so the temp file can be deleted.
        $bytes = [IO.File]::ReadAllBytes($png)
        $image = [Drawing.Image]::FromStream([IO.MemoryStream]::new($bytes))
        [Drawing.Bitmap]::new($image)
    }
    finally {
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    }
}

function Resize-Image([Drawing.Image]$source, [int]$size) {
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.CompositingMode = 'SourceCopy'
    $g.CompositingQuality = 'HighQuality'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.SmoothingMode = 'HighQuality'
    $attributes = [Drawing.Imaging.ImageAttributes]::new()
    $attributes.SetWrapMode('TileFlipXY') # avoids dark fringes at the edges
    $g.DrawImage($source, [Drawing.Rectangle]::new(0, 0, $size, $size), 0, 0, $source.Width, $source.Height, 'Pixel', $attributes)
    $g.Dispose()
    $bitmap
}

# Places the icon at $iconSize, centered on a transparent canvas.
function New-Canvas([Drawing.Image]$source, [int]$width, [int]$height, [int]$iconSize) {
    $icon = Resize-Image $source $iconSize
    $canvas = [Drawing.Bitmap]::new($width, $height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($canvas)
    $g.DrawImage($icon, [int](($width - $iconSize) / 2), [int](($height - $iconSize) / 2), $iconSize, $iconSize)
    $g.Dispose()
    $icon.Dispose()
    $canvas
}

function Save-Png([Drawing.Image]$image, [string]$name) {
    $image.Save((Join-Path $assets $name), [Drawing.Imaging.ImageFormat]::Png)
    $image.Dispose()
    Write-Host "  $name"
}

# Writes a multi-resolution .ico with PNG-compressed entries.
function Save-Ico([Drawing.Image[]]$images, [string]$name) {
    $pngs = foreach ($image in $images) {
        $ms = [IO.MemoryStream]::new()
        $image.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
        , $ms.ToArray()
    }
    $out = [IO.MemoryStream]::new()
    $w = [IO.BinaryWriter]::new($out)
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    for ($i = 0; $i -lt $images.Count; $i++) {
        $size = $images[$i].Width
        $w.Write([byte]($size % 256)); $w.Write([byte]($size % 256)) # 256 is stored as 0
        $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($png in $pngs) { $w.Write($png) }
    $w.Flush()
    [IO.File]::WriteAllBytes((Join-Path $assets $name), $out.ToArray())
    Write-Host "  $name"
}

Write-Host 'Rendering SVGs...'
$large = Render-Svg (Join-Path $assets 'AppIcon.svg')
$small = Render-Svg (Join-Path $assets 'AppIcon.small.svg')
function Get-Icon([int]$size) { Resize-Image ($(if ($size -le 24) { $small } else { $large })) $size }

Write-Host 'Writing assets:'
Save-Png (Get-Icon 88) 'Square44x44Logo.scale-200.png'
Save-Png (Get-Icon 24) 'Square44x44Logo.targetsize-24_altform-unplated.png'
Save-Png (Get-Icon 48) 'Square44x44Logo.targetsize-48_altform-lightunplated.png'
Save-Png (Get-Icon 48) 'LockScreenLogo.scale-200.png'
Save-Png (Get-Icon 50) 'StoreLogo.png'
Save-Png (New-Canvas $large 300 300 200) 'Square150x150Logo.scale-200.png'
Save-Png (New-Canvas $large 620 300 200) 'Wide310x150Logo.scale-200.png'
Save-Png (New-Canvas $large 1240 600 320) 'SplashScreen.scale-200.png'

$icoImages = foreach ($size in 16, 20, 24, 32, 40, 48, 64, 128, 256) { Get-Icon $size }
Save-Ico $icoImages 'AppIcon.ico'
$icoImages | ForEach-Object Dispose

$large.Dispose()
$small.Dispose()
Write-Host 'Done.'
