[CmdletBinding()]
param(
    [string]$Dotnet = "dotnet",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$qaProject = Join-Path $repoRoot "tools\InstantScreenshotQa\InstantScreenshotQa.csproj"
$qaExe = Join-Path $repoRoot "tools\InstantScreenshotQa\bin\Release\net10.0-windows10.0.22621.0\win-x64\InstantScreenshotQa.exe"
$bridge = Join-Path $repoRoot "native\ObsBridge\build\Release\CaptailObsBridge.dll"
$fixtureBuild = Join-Path $repoRoot "native\ObsCaptureFixture\build"
$fixture = Join-Path $fixtureBuild "Release\ObsCaptureFixture.exe"
$output = Join-Path $repoRoot ".qa\instant-screenshots"

Push-Location $repoRoot
try {
    if (-not $SkipBuild) {
        & $Dotnet build $qaProject -c Release -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Instant screenshot QA build failed." }
        if (-not (Test-Path -LiteralPath $fixtureBuild)) {
            cmake -S (Join-Path $repoRoot "native\ObsCaptureFixture") -B $fixtureBuild -A x64
            if ($LASTEXITCODE -ne 0) { throw "D3D11 fixture configuration failed." }
        }
        cmake --build $fixtureBuild --config Release --target ObsCaptureFixture
        if ($LASTEXITCODE -ne 0) { throw "D3D11 fixture build failed." }
    }
    & $qaExe $bridge $output $fixture
    if ($LASTEXITCODE -ne 0) { throw "Instant screenshot runtime QA failed ($LASTEXITCODE)." }
}
finally {
    Pop-Location
}
