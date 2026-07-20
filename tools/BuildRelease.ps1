[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$OutputDirectory = "",

    [string]$InnoSetupCompiler = "",

    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\release\$Version"
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$repoPrefix = $repoRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $outputRoot.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release output must stay inside repository: $outputRoot"
}

$stagingRoot = Join-Path $outputRoot "staging"
$portableName = "Captail-$Version"
$publishDirectory = Join-Path $stagingRoot $portableName
$portableArchive = Join-Path $outputRoot "$portableName-Portable-win-x64.zip"
$setupName = "Captail-$Version-Setup-win-x64.exe"
$setupPath = Join-Path $outputRoot $setupName
$checksumPath = Join-Path $outputRoot "SHA256SUMS.txt"
$project = Join-Path $repoRoot "src\InstantReplay\InstantReplay.csproj"
$installerScript = Join-Path $repoRoot "installer\Captail.iss"

foreach ($path in @($stagingRoot, $portableArchive, $setupPath, $checksumPath)) {
    $resolved = [IO.Path]::GetFullPath($path)
    if (-not $resolved.StartsWith($outputRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean path outside release output: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null

Write-Host "Publishing Captail $Version..."
dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $publishDirectory `
    -p:Version=$Version `
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

$captailExe = Join-Path $publishDirectory "Captail.exe"
if (-not (Test-Path -LiteralPath $captailExe)) {
    throw "Published Captail.exe not found."
}
if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory "obs.dll"))) {
    throw "Published OBS runtime not found."
}

Write-Host "Creating Portable archive..."
Compress-Archive -LiteralPath $publishDirectory `
    -DestinationPath $portableArchive `
    -CompressionLevel Optimal

if (-not $SkipInstaller) {
    if (-not $InnoSetupCompiler) {
        $candidates = @(
            (Join-Path $env:ProgramFiles "Inno Setup 7\ISCC.exe"),
            (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
            (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 7\ISCC.exe")
        )
        $InnoSetupCompiler = $candidates |
            Where-Object { $_ -and (Test-Path -LiteralPath $_) } |
            Select-Object -First 1
    }
    if (-not $InnoSetupCompiler -or
        -not (Test-Path -LiteralPath $InnoSetupCompiler)) {
        throw "Inno Setup ISCC.exe not found. Pass -InnoSetupCompiler or use -SkipInstaller."
    }

    Write-Host "Creating installer..."
    & $InnoSetupCompiler `
        "/DMyAppVersion=$Version" `
        "/DSourceDir=$publishDirectory" `
        "/DOutputDir=$outputRoot" `
        $installerScript
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $setupPath)) {
        throw "Installer output not found: $setupPath"
    }
}

$assets = @($portableArchive)
if (Test-Path -LiteralPath $setupPath) {
    $assets += $setupPath
}
$hashLines = foreach ($asset in $assets) {
    $hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($asset))"
}
Set-Content -LiteralPath $checksumPath -Value $hashLines -Encoding ascii

Write-Host ""
Write-Host "Release ready: $outputRoot"
Get-ChildItem -LiteralPath $outputRoot -File |
    Select-Object Name, Length
