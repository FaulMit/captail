[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BannerSource,

    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\store-assets"
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
$artifactsPrefix = $artifactsRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $outputRoot.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Store asset output must stay inside repository artifacts: $outputRoot"
}

$resolvedBannerSource = [IO.Path]::GetFullPath($BannerSource)
if (-not (Test-Path -LiteralPath $resolvedBannerSource -PathType Leaf)) {
    throw "Banner source not found: $resolvedBannerSource"
}

if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}

$masterDirectory = Join-Path $outputRoot "Master"
$storeDirectory = Join-Path $outputRoot "StoreListing"
$msixDirectory = Join-Path $outputRoot "MSIX"
$windowsDirectory = Join-Path $outputRoot "Windows"
$previewDirectory = Join-Path $outputRoot "Preview"
foreach ($directory in @(
    $masterDirectory,
    $storeDirectory,
    $msixDirectory,
    $windowsDirectory,
    $previewDirectory)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

Add-Type -AssemblyName System.Drawing.Common

function New-RoundedRectanglePath {
    param(
        [Drawing.RectangleF]$Bounds,
        [single]$Radius
    )

    $diameter = $Radius * 2
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($Bounds.Left, $Bounds.Top, $diameter, $diameter, 180, 90)
    $path.AddArc($Bounds.Right - $diameter, $Bounds.Top, $diameter, $diameter, 270, 90)
    $path.AddArc($Bounds.Right - $diameter, $Bounds.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Bounds.Left, $Bounds.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function Set-HighQualityGraphics {
    param([Drawing.Graphics]$Graphics)

    $Graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceOver
    $Graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
    $Graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $Graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $Graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
}

function Draw-CaptailMark {
    param(
        [Drawing.Graphics]$Graphics,
        [Drawing.RectangleF]$Bounds,
        [Drawing.Color]$Color
    )

    $ringWidth = [single]($Bounds.Width * 0.108)
    $ringInset = [single]($Bounds.Width * 0.135)
    $ringBounds = [Drawing.RectangleF]::new(
        [single]($Bounds.Left + $ringInset),
        [single]($Bounds.Top + $ringInset),
        [single]($Bounds.Width - 2 * $ringInset),
        [single]($Bounds.Height - 2 * $ringInset))
    $pen = [Drawing.Pen]::new($Color, $ringWidth)
    $pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    try {
        $Graphics.DrawArc($pen, $ringBounds, -72, 304)
    }
    finally {
        $pen.Dispose()
    }

    $dotSize = [single]($Bounds.Width * 0.145)
    $dotBrush = [Drawing.SolidBrush]::new($Color)
    try {
        $Graphics.FillEllipse(
            $dotBrush,
            [single]($Bounds.Left + ($Bounds.Width - $dotSize) / 2),
            [single]($Bounds.Top + ($Bounds.Height - $dotSize) / 2),
            $dotSize,
            $dotSize)
    }
    finally {
        $dotBrush.Dispose()
    }
}

function New-CaptailMaster {
    param(
        [int]$Size,
        [bool]$Transparent
    )

    $bitmap = [Drawing.Bitmap]::new(
        $Size,
        $Size,
        [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        Set-HighQualityGraphics $graphics
        $graphics.Clear([Drawing.Color]::Transparent)

        if (-not $Transparent) {
            $inset = [single]($Size * 0.022)
            $tileBounds = [Drawing.RectangleF]::new(
                $inset,
                $inset,
                [single]($Size - 2 * $inset),
                [single]($Size - 2 * $inset))
            $tilePath = New-RoundedRectanglePath $tileBounds ([single]($Size * 0.21))
            $background = [Drawing.SolidBrush]::new(
                [Drawing.Color]::FromArgb(255, 18, 23, 26))
            $border = [Drawing.Pen]::new(
                [Drawing.Color]::FromArgb(255, 40, 49, 54),
                [single]($Size * 0.018))
            try {
                $graphics.FillPath($background, $tilePath)
                $graphics.DrawPath($border, $tilePath)
            }
            finally {
                $border.Dispose()
                $background.Dispose()
                $tilePath.Dispose()
            }
        }

        $markInset = if ($Transparent) { 0 } else { $Size * 0.185 }
        $markBounds = [Drawing.RectangleF]::new(
            [single]$markInset,
            [single]$markInset,
            [single]($Size - 2 * $markInset),
            [single]($Size - 2 * $markInset))
        $markColor = if ($Transparent) {
            [Drawing.Color]::FromArgb(255, 69, 201, 167)
        }
        else {
            [Drawing.Color]::FromArgb(255, 99, 224, 189)
        }
        Draw-CaptailMark `
            -Graphics $graphics `
            -Bounds $markBounds `
            -Color $markColor
    }
    finally {
        $graphics.Dispose()
    }
    return $bitmap
}

function Save-ScaledPng {
    param(
        [Drawing.Image]$Source,
        [string]$Path,
        [int]$Width,
        [int]$Height
    )

    $bitmap = [Drawing.Bitmap]::new(
        $Width,
        $Height,
        [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        Set-HighQualityGraphics $graphics
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.DrawImage($Source, [Drawing.Rectangle]::new(0, 0, $Width, $Height))
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Save-CoverPng {
    param(
        [Drawing.Image]$Source,
        [string]$Path,
        [int]$Width,
        [int]$Height
    )

    $targetRatio = $Width / [double]$Height
    $sourceRatio = $Source.Width / [double]$Source.Height
    if ($sourceRatio -gt $targetRatio) {
        $cropHeight = $Source.Height
        $cropWidth = [int][Math]::Round($cropHeight * $targetRatio)
        $cropX = [int](($Source.Width - $cropWidth) / 2)
        $cropY = 0
    }
    else {
        $cropWidth = $Source.Width
        $cropHeight = [int][Math]::Round($cropWidth / $targetRatio)
        $cropX = 0
        $cropY = [int](($Source.Height - $cropHeight) / 2)
    }

    $bitmap = [Drawing.Bitmap]::new(
        $Width,
        $Height,
        [Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        Set-HighQualityGraphics $graphics
        $graphics.Clear([Drawing.Color]::FromArgb(17, 23, 25))
        $graphics.DrawImage(
            $Source,
            [Drawing.Rectangle]::new(0, 0, $Width, $Height),
            $cropX,
            $cropY,
            $cropWidth,
            $cropHeight,
            [Drawing.GraphicsUnit]::Pixel)
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function New-WideLogo {
    param([string]$Path)

    $bitmap = [Drawing.Bitmap]::new(
        310,
        150,
        [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        Set-HighQualityGraphics $graphics
        $graphics.Clear([Drawing.Color]::FromArgb(255, 18, 23, 26))
        Draw-CaptailMark `
            -Graphics $graphics `
            -Bounds ([Drawing.RectangleF]::new(91, 16, 128, 118)) `
            -Color ([Drawing.Color]::FromArgb(255, 99, 224, 189))
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$tileMaster = New-CaptailMaster -Size 1024 -Transparent $false
$markMaster = New-CaptailMaster -Size 1024 -Transparent $true
try {
    $tileMaster.Save(
        (Join-Path $masterDirectory "Captail-AppTile-1024x1024.png"),
        [Drawing.Imaging.ImageFormat]::Png)
    $markMaster.Save(
        (Join-Path $masterDirectory "Captail-Mark-1024x1024.png"),
        [Drawing.Imaging.ImageFormat]::Png)

    foreach ($size in @(300, 150, 71)) {
        Save-ScaledPng `
            -Source $tileMaster `
            -Path (Join-Path $storeDirectory ("Captail-AppTile-{0}x{0}.png" -f $size)) `
            -Width $size `
            -Height $size
    }

    foreach ($size in @(16, 20, 24, 32, 40, 44, 48, 50, 64, 71, 96, 128, 150, 256, 300, 310, 512, 1024)) {
        Save-ScaledPng `
            -Source $tileMaster `
            -Path (Join-Path $windowsDirectory ("Captail-AppTile-{0}x{0}.png" -f $size)) `
            -Width $size `
            -Height $size
        Save-ScaledPng `
            -Source $markMaster `
            -Path (Join-Path $windowsDirectory ("Captail-Mark-{0}x{0}.png" -f $size)) `
            -Width $size `
            -Height $size
    }

    Save-ScaledPng $markMaster (Join-Path $msixDirectory "Square44x44Logo.png") 44 44
    Save-ScaledPng $tileMaster (Join-Path $msixDirectory "StoreLogo.png") 50 50
    Save-ScaledPng $tileMaster (Join-Path $msixDirectory "Square150x150Logo.png") 150 150
    Save-ScaledPng $tileMaster (Join-Path $msixDirectory "Square310x310Logo.png") 310 310
    New-WideLogo (Join-Path $msixDirectory "Wide310x150Logo.png")

    foreach ($size in @(16, 20, 24, 30, 32, 36, 40, 44, 48, 60, 64, 72, 80, 96, 256)) {
        foreach ($form in @("", "_altform-unplated", "_altform-lightunplated")) {
            Save-ScaledPng `
                -Source $markMaster `
                -Path (Join-Path $msixDirectory ("Square44x44Logo.targetsize-{0}{1}.png" -f $size, $form)) `
                -Width $size `
                -Height $size
        }
    }
}
finally {
    $markMaster.Dispose()
    $tileMaster.Dispose()
}

$bannerMasterPath = Join-Path $masterDirectory "Captail-SuperBanner-Generated-Source.png"
Copy-Item -LiteralPath $resolvedBannerSource -Destination $bannerMasterPath
$banner = [Drawing.Image]::FromFile($resolvedBannerSource)
try {
    Save-CoverPng `
        -Source $banner `
        -Path (Join-Path $storeDirectory "Captail-SuperBanner-1920x1080.png") `
        -Width 1920 `
        -Height 1080
    Save-CoverPng `
        -Source $banner `
        -Path (Join-Path $storeDirectory "Captail-SuperBanner-3840x2160.png") `
        -Width 3840 `
        -Height 2160
}
finally {
    $banner.Dispose()
}

& (Join-Path $PSScriptRoot "GenerateAppIcon.ps1") `
    -OutputPath (Join-Path $windowsDirectory "Captail.ico") | Out-Null

$previewPath = Join-Path $previewDirectory "Captail-Store-Asset-Pack-Preview.png"
$preview = [Drawing.Bitmap]::new(
    1440,
    1360,
    [Drawing.Imaging.PixelFormat]::Format24bppRgb)
$previewGraphics = [Drawing.Graphics]::FromImage($preview)
try {
    Set-HighQualityGraphics $previewGraphics
    $previewGraphics.Clear([Drawing.Color]::FromArgb(255, 12, 16, 18))
    $titleFont = [Drawing.Font]::new("Segoe UI Semibold", 34)
    $labelFont = [Drawing.Font]::new("Segoe UI", 18)
    $mutedBrush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 151, 163, 169))
    $whiteBrush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 236, 241, 243))
    try {
        $previewGraphics.DrawString("Captail Store Asset Pack", $titleFont, $whiteBrush, 54, 35)
        $previewGraphics.DrawString("Store listing logos", $labelFont, $mutedBrush, 58, 102)
        $x = 58
        foreach ($size in @(300, 150, 71)) {
            $path = Join-Path $storeDirectory ("Captail-AppTile-{0}x{0}.png" -f $size)
            $image = [Drawing.Image]::FromFile($path)
            try {
                $displaySize = if ($size -eq 300) { 260 } elseif ($size -eq 150) { 180 } else { 120 }
                $previewGraphics.DrawImage($image, $x, 155, $displaySize, $displaySize)
                $previewGraphics.DrawString("${size}x${size}", $labelFont, $mutedBrush, $x, 432)
                $x += $displaySize + 60
            }
            finally {
                $image.Dispose()
            }
        }

        $previewGraphics.DrawString("Windows and Xbox super banner", $labelFont, $mutedBrush, 58, 510)
        $bannerPreview = [Drawing.Image]::FromFile(
            (Join-Path $storeDirectory "Captail-SuperBanner-1920x1080.png"))
        try {
            $previewGraphics.DrawImage($bannerPreview, 58, 560, 1324, 745)
        }
        finally {
            $bannerPreview.Dispose()
        }
    }
    finally {
        $whiteBrush.Dispose()
        $mutedBrush.Dispose()
        $labelFont.Dispose()
        $titleFont.Dispose()
    }
    $preview.Save($previewPath, [Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $previewGraphics.Dispose()
    $preview.Dispose()
}

$readme = @'
# Captail Store Asset Pack

Ready-to-upload graphics for Microsoft Partner Center and Windows packaging.

## Partner Center fields

| Partner Center field | File |
| --- | --- |
| App tile icon 1:1, 300 x 300 | `StoreListing/Captail-AppTile-300x300.png` |
| App tile icon 1:1, 150 x 150 | `StoreListing/Captail-AppTile-150x150.png` |
| App tile icon 1:1, 71 x 71 | `StoreListing/Captail-AppTile-71x71.png` |
| Windows and Xbox super banner | `StoreListing/Captail-SuperBanner-1920x1080.png` |
| Windows and Xbox super banner, 4K | `StoreListing/Captail-SuperBanner-3840x2160.png` |

Use either super banner, not both. Both versions contain no product name, as required by Partner Center.

## Other folders

- `Master`: 1024 x 1024 tile, transparent mark, and generated banner source.
- `MSIX`: package logos plus default, dark-theme, and light-theme target-size variants.
- `Windows`: common PNG sizes and multi-resolution `Captail.ico`.
- `Preview`: contact sheet for quick visual review.

All files are PNG except `Captail.ico`, use the Captail graphite/mint palette, and are generated without third-party marks.
'@
[IO.File]::WriteAllText(
    (Join-Path $outputRoot "README.md"),
    $readme,
    [Text.UTF8Encoding]::new($false))

$promptNotes = @'
# Generation Notes

Mode: built-in image generation, followed by deterministic local resizing and icon rendering.

## Banner generation prompt

Create a premium abstract 16:9 Microsoft Store super banner for Captail, a lightweight instant replay recorder. Use a deep graphite matte background, subtle rounded layers, a horizontal replay-buffer timeline, restrained waveform details, and mint-teal frame trails. Convey continuous capture, stability, and smooth high-frame-rate motion. Keep important elements inside the central 80% safe area. Use Captail colors `#111719`, `#182024`, `#283238`, `#63E0BD`, and muted teal. No text, product name, letters, numbers, third-party logos, game screenshots, people, devices, cyberpunk, excessive glow, rainbow colors, noisy particles, fake UI text, or watermark.

## Targeted symbol edit prompt

Replace only the central circular arrow with Captail's brand motif: a thick mint circular ring with one clean gap near the upper-right, rounded ends, and a solid mint capture dot centered inside. Remove the arrowhead. Preserve composition, graphite background, timeline, waveform, frame trails, lighting, colors, and all other elements. No text or watermark.
'@
[IO.File]::WriteAllText(
    (Join-Path $outputRoot "GENERATION-NOTES.md"),
    $promptNotes,
    [Text.UTF8Encoding]::new($false))

$manifestRows = foreach ($file in Get-ChildItem -LiteralPath $outputRoot -Recurse -File |
    Where-Object { $_.Extension -in @(".png", ".ico") }) {
    $width = ""
    $height = ""
    if ($file.Extension -eq ".png") {
        $image = [Drawing.Image]::FromFile($file.FullName)
        try {
            $width = $image.Width
            $height = $image.Height
        }
        finally {
            $image.Dispose()
        }
    }
    [pscustomobject]@{
        File = [IO.Path]::GetRelativePath($outputRoot, $file.FullName).Replace("\", "/")
        Width = $width
        Height = $height
        Bytes = $file.Length
        SHA256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$manifestRows |
    Sort-Object File |
    Export-Csv -LiteralPath (Join-Path $outputRoot "asset-manifest.csv") -NoTypeInformation -Encoding utf8

$checksumLines = foreach ($file in Get-ChildItem -LiteralPath $outputRoot -Recurse -File |
    Where-Object { $_.Name -notin @("SHA256SUMS.txt") } |
    Sort-Object FullName) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $relative = [IO.Path]::GetRelativePath($outputRoot, $file.FullName).Replace("\", "/")
    "$hash  $relative"
}
[IO.File]::WriteAllLines(
    (Join-Path $outputRoot "SHA256SUMS.txt"),
    $checksumLines,
    [Text.Encoding]::ASCII)

$zipPath = Join-Path $artifactsRoot "Captail-Store-Assets.zip"
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $outputRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Captail Store asset pack ready: $outputRoot"
Write-Host "ZIP: $zipPath"
Get-ChildItem -LiteralPath $storeDirectory -File |
    Select-Object Name, Length
