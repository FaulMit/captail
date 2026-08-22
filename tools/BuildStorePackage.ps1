[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [ValidatePattern('^\d+\.\d+\.\d+\.0$')]
    [string]$IdentityVersion = "",

    [string]$OutputDirectory = "",

    [string]$MakeAppxPath = "",

    [string]$MakePriPath = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\store\$Version"
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$repoPrefix = $repoRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $outputRoot.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Store output must stay inside repository: $outputRoot"
}

$packageVersion = if ($IdentityVersion) { $IdentityVersion } else { "$Version.0" }
$packageName = "Captail-$packageVersion-x64"
$stagingRoot = Join-Path $outputRoot "staging"
$dotnetArtifacts = Join-Path $stagingRoot "dotnet"
$packageRoot = Join-Path $stagingRoot "package"
$validationRoot = Join-Path $stagingRoot "validation"
$msixPath = Join-Path $outputRoot "$packageName.msix"
$uploadPath = Join-Path $outputRoot "$packageName.msixupload"
$uploadZipPath = Join-Path $outputRoot "$packageName.zip"
$checksumPath = Join-Path $outputRoot "SHA256SUMS.txt"
$project = Join-Path $repoRoot "src\Captail\Captail.csproj"
$storeFfmpegRoot = Join-Path $repoRoot "runtime\ffmpeg-store-static"
$acquireFfmpeg = Join-Path $repoRoot "tools\AcquireFfmpegRuntime.ps1"
$testFfmpegIsolation = Join-Path $repoRoot "tools\TestStoreFfmpegIsolation.ps1"
$testNativeDependencies =
    Join-Path $repoRoot "tools\TestStoreNativeDependencies.ps1"
$testStoreLifecycle =
    Join-Path $repoRoot "tools\TestStoreLifecycleIsolation.ps1"
$testStoreIconAssets =
    Join-Path $repoRoot "tools\TestStoreIconAssets.ps1"
$manifestTemplate = Join-Path $repoRoot "packaging\msix\AppxManifest.xml.template"
$iconPath = Join-Path $repoRoot "src\Captail\Assets\Captail.ico"

foreach ($path in @(
    $stagingRoot,
    $msixPath,
    $uploadPath,
    $uploadZipPath,
    $checksumPath)) {
    $resolved = [IO.Path]::GetFullPath($path)
    if (-not $resolved.StartsWith($outputRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean path outside Store output: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null

if (-not $MakeAppxPath) {
    $sdkBinRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    $MakeAppxPath = Get-ChildItem -LiteralPath $sdkBinRoot -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [Version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\makeappx.exe" } |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
}
if (-not $MakeAppxPath -or -not (Test-Path -LiteralPath $MakeAppxPath)) {
    throw "MakeAppx.exe not found. Install Windows SDK or pass -MakeAppxPath."
}
if (-not $MakePriPath) {
    $MakePriPath = Join-Path (Split-Path -Parent $MakeAppxPath) "makepri.exe"
}
if (-not (Test-Path -LiteralPath $MakePriPath -PathType Leaf)) {
    throw "MakePri.exe not found. Install Windows SDK or pass -MakePriPath."
}

Write-Host "Publishing Captail $Version for Microsoft Store..."
& $testStoreLifecycle
& $acquireFfmpeg -Destination $storeFfmpegRoot -Flavor Static
dotnet restore $project `
    --locked-mode `
    --runtime win-x64 `
    --artifacts-path $dotnetArtifacts
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}
dotnet publish $project `
    -c Release `
    -r win-x64 `
    --no-restore `
    --artifacts-path $dotnetArtifacts `
    --self-contained true `
    -o $packageRoot `
    -p:Version=$Version `
    -p:MicrosoftStoreBuild=true `
    -p:FfmpegRuntimeRoot=$storeFfmpegRoot `
    -p:AcquireFfmpegRuntimeOnBuild=false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

foreach ($requiredPath in @(
    (Join-Path $packageRoot "Captail.exe"),
    (Join-Path $packageRoot "CaptailObsBridge.dll"),
    (Join-Path $packageRoot "obs.dll"),
    (Join-Path $packageRoot "obs-plugins\64bit\captail-process-audio.dll"),
    (Join-Path $packageRoot "libmpv-2.dll"),
    (Join-Path $packageRoot "ffmpeg\ffmpeg.exe"),
    (Join-Path $packageRoot "ffmpeg\ffprobe.exe"))) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Store package dependency not found: $requiredPath"
    }
}

Write-Host "Generating MSIX assets and manifest..."
$assetsDirectory = Join-Path $packageRoot "Assets"
New-Item -ItemType Directory -Force -Path $assetsDirectory | Out-Null
Add-Type -AssemblyName System.Drawing.Common
$sourceIcon = [Drawing.Icon]::new($iconPath, 256, 256)
$sourceBitmap = $sourceIcon.ToBitmap()
try {
    foreach ($asset in @(
        @{ Name = "Square44x44Logo.png"; Size = 44 },
        @{ Name = "StoreLogo.png"; Size = 50 },
        @{ Name = "Square150x150Logo.png"; Size = 150 })) {
        $bitmap = [Drawing.Bitmap]::new($asset.Size, $asset.Size)
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([Drawing.Color]::Transparent)
                $graphics.CompositingQuality =
                    [Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode =
                    [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.SmoothingMode =
                    [Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.DrawImage(
                    $sourceBitmap,
                    [Drawing.Rectangle]::new(0, 0, $asset.Size, $asset.Size))
            }
            finally {
                $graphics.Dispose()
            }
            $bitmap.Save(
                (Join-Path $assetsDirectory $asset.Name),
                [Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $bitmap.Dispose()
        }
    }
}
finally {
    $sourceBitmap.Dispose()
    $sourceIcon.Dispose()
}

$taskbarIconSizes = @(16, 20, 24, 30, 32, 36, 40, 44, 48, 60, 64, 72, 80, 96, 256)
foreach ($size in $taskbarIconSizes) {
    $bitmap = [Drawing.Bitmap]::new(
        $size,
        $size,
        [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.CompositingMode =
                [Drawing.Drawing2D.CompositingMode]::SourceOver
            $graphics.CompositingQuality =
                [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode =
                [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode =
                [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode =
                [Drawing.Drawing2D.SmoothingMode]::AntiAlias

            $markInset = [single]0
            $markBounds = [Drawing.RectangleF]::new(
                $markInset,
                $markInset,
                [single]($size - 2 * $markInset),
                [single]($size - 2 * $markInset))
            $ringWidth = [single]($markBounds.Width * 0.108)
            $ringInset = [single]($markBounds.Width * 0.135)
            $ringBounds = [Drawing.RectangleF]::new(
                [single]($markBounds.Left + $ringInset),
                [single]($markBounds.Top + $ringInset),
                [single]($markBounds.Width - 2 * $ringInset),
                [single]($markBounds.Height - 2 * $ringInset))
            $mint = [Drawing.Color]::FromArgb(255, 69, 201, 167)
            $pen = [Drawing.Pen]::new($mint, $ringWidth)
            $pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
            $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
            try {
                $graphics.DrawArc($pen, $ringBounds, -72, 304)
            }
            finally {
                $pen.Dispose()
            }

            $dotSize = [single]($markBounds.Width * 0.145)
            $dotBrush = [Drawing.SolidBrush]::new($mint)
            try {
                $graphics.FillEllipse(
                    $dotBrush,
                    [single]($markBounds.Left + ($markBounds.Width - $dotSize) / 2),
                    [single]($markBounds.Top + ($markBounds.Height - $dotSize) / 2),
                    $dotSize,
                    $dotSize)
            }
            finally {
                $dotBrush.Dispose()
            }
        }
        finally {
            $graphics.Dispose()
        }

        foreach ($form in @("", "_altform-unplated", "_altform-lightunplated")) {
            $name = "Square44x44Logo.targetsize-${size}${form}.png"
            $bitmap.Save(
                (Join-Path $assetsDirectory $name),
                [Drawing.Imaging.ImageFormat]::Png)
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

$manifest = [IO.File]::ReadAllText($manifestTemplate)
$manifest = $manifest.Replace("__PACKAGE_VERSION__", $packageVersion)
$manifestPath = Join-Path $packageRoot "AppxManifest.xml"
[IO.File]::WriteAllText(
    $manifestPath,
    $manifest,
    [Text.UTF8Encoding]::new($false))

$priConfigPath = Join-Path $packageRoot "priconfig.xml"
$resourceIndexPath = Join-Path $packageRoot "resources.pri"
Write-Host "Generating package resource index..."
& $MakePriPath createconfig /cf $priConfigPath /dq en-US /o
if ($LASTEXITCODE -ne 0) {
    throw "MakePri createconfig failed with exit code $LASTEXITCODE."
}
& $MakePriPath new `
    /pr $packageRoot `
    /cf $priConfigPath `
    /in "faulmit.Captail" `
    /of $resourceIndexPath `
    /o
if ($LASTEXITCODE -ne 0) {
    throw "MakePri new failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $resourceIndexPath -PathType Leaf)) {
    throw "MakePri did not create resources.pri."
}
Remove-Item -LiteralPath $priConfigPath -Force

Write-Host "Creating MSIX..."
& $MakeAppxPath pack /d $packageRoot /p $msixPath /o
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx pack failed with exit code $LASTEXITCODE."
}

Write-Host "Validating generated package..."
& $MakeAppxPath unpack /p $msixPath /d $validationRoot /o
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx unpack failed with exit code $LASTEXITCODE."
}
[xml]$validatedManifest = Get-Content -Raw (
    Join-Path $validationRoot "AppxManifest.xml")
$identity = $validatedManifest.Package.Identity
if ($identity.Name -ne "faulmit.Captail" -or
    $identity.Publisher -ne "CN=1BD9448E-C83F-401D-B530-ED5258C6319A" -or
    $identity.Version -ne $packageVersion -or
    $identity.ProcessorArchitecture -ne "x64") {
    throw "Generated MSIX identity does not match Partner Center."
}
& $testFfmpegIsolation -PackageRoot $validationRoot
& $testNativeDependencies -PackageRoot $validationRoot
& $testStoreIconAssets -PackageRoot $validationRoot

Write-Host "Creating Partner Center upload archive..."
Compress-Archive -LiteralPath $msixPath `
    -DestinationPath $uploadZipPath `
    -CompressionLevel NoCompression
Move-Item -LiteralPath $uploadZipPath -Destination $uploadPath

$hashLines = foreach ($asset in @($msixPath, $uploadPath)) {
    $hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($asset))"
}
Set-Content -LiteralPath $checksumPath -Value $hashLines -Encoding ascii

Write-Host ""
Write-Host "Microsoft Store package ready: $outputRoot"
Get-ChildItem -LiteralPath $outputRoot -File |
    Select-Object Name, Length
