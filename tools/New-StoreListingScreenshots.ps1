param(
    [string]$SourceDirectory,
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    $SourceDirectory = Join-Path $repoRoot "docs"
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "store-listing\images"
}

$screenshots = @(
    "captail-main.png",
    "captail-settings-video.png",
    "captail-settings-audio.png",
    "captail-player.png",
    "captail-editor.png"
)

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

foreach ($name in $screenshots) {
    $sourcePath = Join-Path $SourceDirectory $name
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Screenshot source not found: $sourcePath"
    }

    $source = [System.Drawing.Image]::FromFile($sourcePath)
    try {
        $canvas = New-Object System.Drawing.Bitmap(
            1920,
            1080,
            [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($canvas)
            try {
                $graphics.Clear([System.Drawing.Color]::FromArgb(10, 15, 17))
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

                $maxWidth = 1740.0
                $maxHeight = 920.0
                $scale = [Math]::Min($maxWidth / $source.Width, $maxHeight / $source.Height)
                $width = [int][Math]::Round($source.Width * $scale)
                $height = [int][Math]::Round($source.Height * $scale)
                $left = [int][Math]::Round((1920 - $width) / 2.0)
                $top = [int][Math]::Round((1080 - $height) / 2.0)

                $graphics.DrawImage(
                    $source,
                    (New-Object System.Drawing.Rectangle($left, $top, $width, $height)),
                    0,
                    0,
                    $source.Width,
                    $source.Height,
                    [System.Drawing.GraphicsUnit]::Pixel)
            }
            finally {
                $graphics.Dispose()
            }

            $outputPath = Join-Path $OutputDirectory $name
            $canvas.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Png)
            Write-Host "Generated Store screenshot: $outputPath"
        }
        finally {
            $canvas.Dispose()
        }
    }
    finally {
        $source.Dispose()
    }
}
