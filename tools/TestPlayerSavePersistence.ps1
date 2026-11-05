[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ClipPath,
    [ValidateRange(1, 10)]
    [int]$Iterations = 3,
    [string]$Dotnet = "dotnet",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$clip = (Resolve-Path -LiteralPath $ClipPath).Path
if ($clip.Contains('"')) { throw "Clip path must not contain a quote." }
if (-not $SkipBuild) {
    & $Dotnet build (Join-Path $repoRoot "src\Captail\Captail.csproj") -c Debug
    if ($LASTEXITCODE -ne 0) { throw "Player persistence QA build failed." }
}
$exe = Join-Path $repoRoot "src\Captail\bin\Debug\net10.0-windows10.0.22621.0\win-x64\Captail.exe"
for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
    # Use a clip of at least eight seconds with audio. The application checks
    # both save modes, track merging, repeated edits, playback, and clean exit.
    $process = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden `
        -ArgumentList ('--qa-trim-overwrite="' + $clip + '"')
    try {
        if (-not $process.WaitForExit(120000)) {
            Stop-Process -Id $process.Id -Force
            throw "Player persistence QA timed out on iteration $iteration."
        }
        if ($process.ExitCode -ne 0) {
            throw "Player persistence QA failed on iteration ${iteration}: exit $($process.ExitCode)."
        }
        Write-Output "PLAYER_SAVE_PERSISTENCE_QA PASS: iteration $iteration."
    }
    finally { $process.Dispose() }
}
