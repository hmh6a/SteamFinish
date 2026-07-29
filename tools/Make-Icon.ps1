<#
.SYNOPSIS
    Generates src/SteamFinish/Assets/app.ico (the window, taskbar and tray icon).

.DESCRIPTION
    Draws a power glyph on a rounded Steam-blue tile at several sizes and packs the
    PNGs into a single multi-resolution .ico. Re-run after changing the artwork.
#>
[CmdletBinding()]
param(
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\src\SteamFinish\Assets\app.ico')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-IconBitmap {
    param([int] $Size)

    $scale = $Size / 256.0
    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded tile in Steam's dark blue.
    $radius = [Math]::Max(2, [int](48 * $scale))
    $inset = 2 * $scale
    # Each argument is parenthesised: inside New-Object's argument array, the comma binds
    # tighter than arithmetic, so a bare "$Size - 2 * $inset" would be parsed as an array.
    $side = $Size - (2 * $inset)
    $rect = New-Object System.Drawing.RectangleF($inset, $inset, $side, $side)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.Left, $rect.Top, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Top, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.Left, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 30, 48, 71),
        [System.Drawing.Color]::FromArgb(255, 22, 32, 48),
        90.0)
    $g.FillPath($brush, $path)

    # Power symbol: a ring with a gap at the top plus a vertical stroke.
    $stroke = [Math]::Max(1.6, 26 * $scale)
    $pen = New-Object System.Drawing.Pen(
        [System.Drawing.Color]::FromArgb(255, 102, 192, 244), $stroke)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $ring = New-Object System.Drawing.RectangleF(
        (68 * $scale), (72 * $scale), (120 * $scale), (120 * $scale))
    $g.DrawArc($pen, $ring, 305, 290)
    $g.DrawLine($pen, (128 * $scale), (52 * $scale), (128 * $scale), (128 * $scale))

    $pen.Dispose(); $brush.Dispose(); $path.Dispose(); $g.Dispose()
    return $bitmap
}

function Get-PngBytes {
    param([System.Drawing.Bitmap] $Bitmap)

    $stream = New-Object System.IO.MemoryStream
    $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    return , $bytes
}

# Classic BITMAPINFOHEADER payload. GDI+ cannot decode PNG-compressed entries at small
# sizes, so anything below 128px is stored as a raw 32bpp DIB with a (zeroed) AND mask.
function Get-DibBytes {
    param([System.Drawing.Bitmap] $Bitmap)

    $w = $Bitmap.Width
    $h = $Bitmap.Height
    $maskStride = [int]([Math]::Floor(($w + 31) / 32) * 4)
    $maskSize = $maskStride * $h

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    $writer.Write([UInt32]40)                    # biSize
    $writer.Write([Int32]$w)                     # biWidth
    $writer.Write([Int32]($h * 2))               # biHeight: XOR image plus AND mask
    $writer.Write([UInt16]1)                     # biPlanes
    $writer.Write([UInt16]32)                    # biBitCount
    $writer.Write([UInt32]0)                     # biCompression = BI_RGB
    $writer.Write([UInt32]($w * $h * 4 + $maskSize))
    $writer.Write([Int32]0); $writer.Write([Int32]0)
    $writer.Write([UInt32]0); $writer.Write([UInt32]0)

    for ($y = $h - 1; $y -ge 0; $y--) {          # DIB rows run bottom-up
        for ($x = 0; $x -lt $w; $x++) {
            $pixel = $Bitmap.GetPixel($x, $y)
            $writer.Write([byte]$pixel.B)
            $writer.Write([byte]$pixel.G)
            $writer.Write([byte]$pixel.R)
            $writer.Write([byte]$pixel.A)
        }
    }

    $writer.Write((New-Object byte[] $maskSize)) # AND mask, unused for 32bpp
    $writer.Flush()
    $bytes = $stream.ToArray()
    $writer.Dispose(); $stream.Dispose()
    return , $bytes
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($size in $sizes) {
    $bitmap = New-IconBitmap -Size $size
    if ($size -ge 128) {
        $images += , (Get-PngBytes -Bitmap $bitmap)
    }
    else {
        $images += , (Get-DibBytes -Bitmap $bitmap)
    }
    $bitmap.Dispose()
}

$directory = Split-Path -Parent $OutputPath
if (-not (Test-Path $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }

$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($out)
$writer.Write([UInt16]0)              # reserved
$writer.Write([UInt16]1)              # type: icon
$writer.Write([UInt16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $writer.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))   # width  (0 means 256)
    $writer.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))   # height (0 means 256)
    $writer.Write([byte]0)            # palette size
    $writer.Write([byte]0)            # reserved
    $writer.Write([UInt16]1)          # colour planes
    $writer.Write([UInt16]32)         # bits per pixel
    $writer.Write([UInt32]$images[$i].Length)
    $writer.Write([UInt32]$offset)
    $offset += $images[$i].Length
}

foreach ($image in $images) { $writer.Write($image) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Resolve-Path -LiteralPath $directory).Path + '\' + (Split-Path -Leaf $OutputPath), $out.ToArray())
$writer.Dispose(); $out.Dispose()

Write-Output "Wrote $OutputPath ($($sizes.Count) sizes)"
