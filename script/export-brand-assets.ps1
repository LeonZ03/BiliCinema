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
