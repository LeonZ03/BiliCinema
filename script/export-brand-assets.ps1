[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourcePath = Join-Path $root 'docs/assets/logo-a-approved.png'
$resources = Join-Path $root 'src/DownKyi.Desktop/Resources'

# The approved sheet is the source of truth. Crop the existing pixels; do not
# redraw the mark, change its colors, stretch it or include the sheet's labels.
$source = [Drawing.Bitmap]::FromFile($sourcePath)
try {
    if ($source.Width -ne 1254 -or $source.Height -ne 1254) {
        throw 'The approved logo sheet must remain at its original 1254 x 1254 size.'
    }
    $mark = $source.Clone([Drawing.Rectangle]::new(327, 202, 600, 600), [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        # Remove only paper connected to the outside. The screen and seats are
        # enclosed white artwork and must remain opaque.
        $width = $mark.Width
        $height = $mark.Height
        $visited = [bool[]]::new($width * $height)
        $outside = [bool[]]::new($width * $height)
        $queue = [Collections.Generic.Queue[int]]::new()
        for ($x = 0; $x -lt $width; $x++) {
            $queue.Enqueue($x)
            $queue.Enqueue(($height - 1) * $width + $x)
        }
        for ($y = 1; $y -lt $height - 1; $y++) {
            $queue.Enqueue($y * $width)
            $queue.Enqueue($y * $width + $width - 1)
        }
        while ($queue.Count -gt 0) {
            $index = $queue.Dequeue()
            if ($visited[$index]) { continue }
            $visited[$index] = $true
            $x = $index % $width
            $y = [int][Math]::Floor($index / $width)
            $color = $mark.GetPixel($x, $y)
            if ($color.R -lt 240 -or $color.G -lt 240 -or $color.B -lt 240) { continue }
            $outside[$index] = $true
            if ($x -gt 0) { $queue.Enqueue($index - 1) }
            if ($x -lt $width - 1) { $queue.Enqueue($index + 1) }
            if ($y -gt 0) { $queue.Enqueue($index - $width) }
            if ($y -lt $height - 1) { $queue.Enqueue($index + $width) }
        }

        $edgePixels = [Collections.Generic.List[int]]::new()
        for ($y = 0; $y -lt $height; $y++) {
            for ($x = 0; $x -lt $width; $x++) {
                $index = $y * $width + $x
                if ($outside[$index]) { continue }
                if (($x -gt 0 -and $outside[$index - 1]) -or
                    ($x -lt $width - 1 -and $outside[$index + 1]) -or
                    ($y -gt 0 -and $outside[$index - $width]) -or
                    ($y -lt $height - 1 -and $outside[$index + $width])) {
                    $edgePixels.Add($index)
                }
            }
        }

        # Recover edge coverage from the white-matted source, rather than leaving
        # pale antialiasing pixels which become a white halo on gray/dark surfaces.
        foreach ($index in $edgePixels) {
            $x = $index % $width
            $y = [int][Math]::Floor($index / $width)
            $color = $mark.GetPixel($x, $y)
            $reference = $color
            for ($dy = -3; $dy -le 3; $dy++) {
                for ($dx = -3; $dx -le 3; $dx++) {
                    $nx = $x + $dx
                    $ny = $y + $dy
                    if ($nx -lt 0 -or $nx -ge $width -or $ny -lt 0 -or $ny -ge $height) { continue }
                    $candidate = $mark.GetPixel($nx, $ny)
                    if (($candidate.B - $candidate.R) -gt ($reference.B - $reference.R)) {
                        $reference = $candidate
                    }
                }
            }
            $alpha = [Math]::Min(1.0, (255.0 - $color.R) / (255.0 - $reference.R))
            if ($alpha -le 0 -or $alpha -ge 0.98) { continue }
            $channels = foreach ($component in @($color.R, $color.G, $color.B)) {
                [int][Math]::Round([Math]::Clamp(($component - 255 * (1 - $alpha)) / $alpha, 0, 255))
            }
            $mark.SetPixel($x, $y, [Drawing.Color]::FromArgb(
                [int][Math]::Round(255 * $alpha), $channels[0], $channels[1], $channels[2]))
        }
        for ($index = 0; $index -lt $outside.Length; $index++) {
            if ($outside[$index]) {
                $mark.SetPixel($index % $width, [int][Math]::Floor($index / $width), [Drawing.Color]::FromArgb(0, 0, 0, 0))
            }
        }

        $mark.Save((Join-Path $resources 'bilicinema-mark.png'), [Drawing.Imaging.ImageFormat]::Png)
        $frames = foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
            $bitmap = [Drawing.Bitmap]::new($size, $size)
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            $stream = [IO.MemoryStream]::new()
            try {
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.DrawImage($mark, [Drawing.Rectangle]::new(0, 0, $size, $size))
                $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
                [PSCustomObject]@{ Size = $size; Bytes = $stream.ToArray() }
            } finally {
                $stream.Dispose()
                $graphics.Dispose()
                $bitmap.Dispose()
            }
        }

        $iconStream = [IO.File]::Create((Join-Path $resources 'favicon.ico'))
        $writer = [IO.BinaryWriter]::new($iconStream)
        try {
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]$frames.Count)
            $offset = 6 + 16 * $frames.Count
            foreach ($frame in $frames) {
                $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
                $writer.Write([byte]$dimension)
                $writer.Write([byte]$dimension)
                $writer.Write([byte]0)
                $writer.Write([byte]0)
                $writer.Write([uint16]1)
                $writer.Write([uint16]32)
                $writer.Write([uint32]$frame.Bytes.Length)
                $writer.Write([uint32]$offset)
                $offset += $frame.Bytes.Length
            }
            foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
        } finally {
            $writer.Dispose()
            $iconStream.Dispose()
        }
    } finally { $mark.Dispose() }
} finally { $source.Dispose() }

Write-Host 'Exported the approved A logo to the shared PNG and Windows icon.'
