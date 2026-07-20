param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\Captail\Assets\Captail.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$pngImages = [System.Collections.Generic.List[byte[]]]::new()

foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

        $inset = [Math]::Max(1.0, $size * 0.035)
        $radius = $size * 0.22
        $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
        try {
            $diameter = $radius * 2
            $bounds = [System.Drawing.RectangleF]::new(
                [single]$inset,
                [single]$inset,
                [single]($size - 2 * $inset),
                [single]($size - 2 * $inset))
            $path.AddArc($bounds.Left, $bounds.Top, $diameter, $diameter, 180, 90)
            $path.AddArc($bounds.Right - $diameter, $bounds.Top, $diameter, $diameter, 270, 90)
            $path.AddArc($bounds.Right - $diameter, $bounds.Bottom - $diameter, $diameter, $diameter, 0, 90)
            $path.AddArc($bounds.Left, $bounds.Bottom - $diameter, $diameter, $diameter, 90, 90)
            $path.CloseFigure()

            $background = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 18, 23, 26))
            $border = [System.Drawing.Pen]::new(
                [System.Drawing.Color]::FromArgb(255, 40, 49, 54),
                [single][Math]::Max(1.0, $size * 0.022))
            try {
                $graphics.FillPath($background, $path)
                $graphics.DrawPath($border, $path)
            }
            finally {
                $background.Dispose()
                $border.Dispose()
            }
        }
        finally {
            $path.Dispose()
        }

        $mint = [System.Drawing.Color]::FromArgb(255, 99, 224, 189)
        $ringWidth = [single][Math]::Max(1.7, $size * 0.075)
        $ringPen = [System.Drawing.Pen]::new($mint, $ringWidth)
        $ringPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $ringPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        try {
            $ringInset = [single]($size * 0.235)
            $ringSize = [single]($size - 2 * $ringInset)
            $graphics.DrawArc($ringPen, $ringInset, $ringInset, $ringSize, $ringSize, -72, 304)
        }
        finally {
            $ringPen.Dispose()
        }

        $dotSize = [single][Math]::Max(2.2, $size * 0.145)
        $dotBrush = [System.Drawing.SolidBrush]::new($mint)
        try {
            $dotOffset = [single](($size - $dotSize) / 2)
            $graphics.FillEllipse($dotBrush, $dotOffset, $dotOffset, $dotSize, $dotSize)
        }
        finally {
            $dotBrush.Dispose()
        }

        $stream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $pngImages.Add($stream.ToArray())
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($resolvedOutput)) | Out-Null
$file = [System.IO.File]::Create($resolvedOutput)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)

    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $size = $sizes[$index]
        $image = $pngImages[$index]
        $iconDimension = if ($size -eq 256) { 0 } else { $size }
        $writer.Write([byte]$iconDimension)
        $writer.Write([byte]$iconDimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Length
    }

    foreach ($image in $pngImages) {
        $writer.Write($image)
    }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Output $resolvedOutput
