[CmdletBinding()]
param(
    [string]$Dotnet = "dotnet",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$project = Join-Path $PSScriptRoot "DisplayTopologyQa\DisplayTopologyQa.csproj"
$exe = Join-Path $PSScriptRoot "DisplayTopologyQa\bin\Release\net10.0-windows10.0.22621.0\win-x64\DisplayTopologyQa.exe"
$output = Join-Path $repoRoot ".qa\issue-51"
New-Item -ItemType Directory -Force -Path $output | Out-Null
if (-not $SkipBuild) {
    & $Dotnet build $project -c Release -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Display topology QA build failed." }
}
& $exe *> (Join-Path $output "replay-auto.log")
Get-Content -LiteralPath (Join-Path $output "replay-auto.log")
if ($LASTEXITCODE -ne 0) { throw "Replay/Auto display topology QA failed." }
& $exe --recording --wgc *> (Join-Path $output "recording-wgc.log")
Get-Content -LiteralPath (Join-Path $output "recording-wgc.log")
if ($LASTEXITCODE -ne 0) { throw "Recording/WGC display topology QA failed." }
