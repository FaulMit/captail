[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageRoot
)

$ErrorActionPreference = "Stop"
$resolvedPackageRoot = [IO.Path]::GetFullPath($PackageRoot)
if (-not (Test-Path -LiteralPath $resolvedPackageRoot -PathType Container)) {
    throw "Store package root not found: $resolvedPackageRoot"
}

$assetsDirectory = Join-Path $resolvedPackageRoot "Assets"
$baseLogo = Join-Path $assetsDirectory "Square44x44Logo.png"
if (-not (Test-Path -LiteralPath $baseLogo -PathType Leaf)) {
    throw "Store package base app-list logo not found: $baseLogo"
}

Add-Type -AssemblyName System.Drawing.Common
$targetSizes = @(16, 20, 24, 30, 32, 36, 40, 44, 48, 60, 64, 72, 80, 96, 256)
foreach ($size in $targetSizes) {
    $name = "Square44x44Logo.targetsize-${size}_altform-unplated.png"
    $path = Join-Path $assetsDirectory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Store taskbar icon asset not found: $name"
    }

    $bitmap = [Drawing.Bitmap]::new($path)
    try {
        if ($bitmap.Width -ne $size -or $bitmap.Height -ne $size) {
            throw "Store taskbar icon has invalid dimensions: $name is $($bitmap.Width)x$($bitmap.Height)."
        }

        $visiblePixels = 0
        $totalPixels = $bitmap.Width * $bitmap.Height
        for ($y = 0; $y -lt $bitmap.Height; $y++) {
            for ($x = 0; $x -lt $bitmap.Width; $x++) {
                if ($bitmap.GetPixel($x, $y).A -gt 0) {
                    $visiblePixels++
                }
            }
        }

        $visibleRatio = $visiblePixels / $totalPixels
        if ($visibleRatio -ge 0.5) {
            throw "Store taskbar icon appears plated: $name has $([Math]::Round($visibleRatio * 100, 1))% visible pixels."
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

Write-Host "Store taskbar icon assets validated: $($targetSizes.Count) transparent target-size variants."
