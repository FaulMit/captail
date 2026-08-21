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
function Test-TransparentIcon {
    param(
        [Parameter(Mandatory)]
        [Drawing.Bitmap]$Bitmap,

        [Parameter(Mandatory)]
        [string]$Name,

        [int]$ExpectedSize = 0
    )

    if ($ExpectedSize -gt 0 -and
        ($Bitmap.Width -ne $ExpectedSize -or $Bitmap.Height -ne $ExpectedSize)) {
        throw "Store taskbar icon has invalid dimensions: $Name is $($Bitmap.Width)x$($Bitmap.Height)."
    }

    $visiblePixels = 0
    $totalPixels = $Bitmap.Width * $Bitmap.Height
    $minX = $Bitmap.Width
    $minY = $Bitmap.Height
    $maxX = -1
    $maxY = -1
    for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            $alpha = $Bitmap.GetPixel($x, $y).A
            if ($alpha -gt 0) {
                $visiblePixels++
            }
            if ($alpha -gt 8) {
                $minX = [Math]::Min($minX, $x)
                $minY = [Math]::Min($minY, $y)
                $maxX = [Math]::Max($maxX, $x)
                $maxY = [Math]::Max($maxY, $y)
            }
        }
    }

    $visibleRatio = $visiblePixels / $totalPixels
    if ($visibleRatio -ge 0.5) {
        throw "Store taskbar icon appears plated: $Name has $([Math]::Round($visibleRatio * 100, 1))% visible pixels."
    }

    if ($maxX -lt $minX -or $maxY -lt $minY) {
        throw "Store taskbar icon is empty: $Name."
    }

    $widthCoverage = ($maxX - $minX + 1) / $Bitmap.Width
    $heightCoverage = ($maxY - $minY + 1) / $Bitmap.Height
    $maximumCoverage = [Math]::Max($widthCoverage, $heightCoverage)
    if ($maximumCoverage -lt 0.78) {
        throw "Store taskbar icon appears undersized: $Name occupies only $([Math]::Round($maximumCoverage * 100, 1))% of its canvas."
    }
}

$targetForms = @("", "_altform-unplated", "_altform-lightunplated")
foreach ($size in $targetSizes) {
    foreach ($form in $targetForms) {
        $name = "Square44x44Logo.targetsize-${size}${form}.png"
        $path = Join-Path $assetsDirectory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Store taskbar icon asset not found: $name"
        }

        $bitmap = [Drawing.Bitmap]::new($path)
        try {
            Test-TransparentIcon -Bitmap $bitmap -Name $name -ExpectedSize $size
        }
        finally {
            $bitmap.Dispose()
        }
    }
}

$baseBitmap = [Drawing.Bitmap]::new($baseLogo)
try {
    Test-TransparentIcon -Bitmap $baseBitmap -Name "Square44x44Logo.png" -ExpectedSize 44
}
finally {
    $baseBitmap.Dispose()
}

$executablePath = Join-Path $resolvedPackageRoot "Captail.exe"
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "Store package executable not found: $executablePath"
}

$associatedIcon = [Drawing.Icon]::ExtractAssociatedIcon($executablePath)
if ($null -eq $associatedIcon) {
    throw "Captail.exe does not contain an associated icon."
}
try {
    $executableBitmap = $associatedIcon.ToBitmap()
    try {
        Test-TransparentIcon -Bitmap $executableBitmap -Name "Captail.exe embedded icon"
    }
    finally {
        $executableBitmap.Dispose()
    }
}
finally {
    $associatedIcon.Dispose()
}

Write-Host "Store taskbar icon assets validated: $($targetSizes.Count * $targetForms.Count) transparent target-size variants plus transparent base and executable icons."
