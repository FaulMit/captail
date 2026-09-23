[CmdletBinding()]
param(
    [string]$ProjectRoot = (Join-Path $PSScriptRoot '..'),
    [int]$TimeoutSeconds = 20
)

$root = [IO.Path]::GetFullPath($ProjectRoot)
$exe = Join-Path $root `
    'src\Captail\bin\Debug\net10.0-windows10.0.22621.0\win-x64\Captail.exe'
$logPath = Join-Path $env:LOCALAPPDATA 'Captail\log.txt'

if (-not (Test-Path -LiteralPath $exe)) {
    throw "Debug Captail executable not found: $exe"
}

$token = [Guid]::NewGuid().ToString('N')
$started = Start-Process `
    -FilePath $exe `
    -ArgumentList "--qa-gpu-recovery-parent=$token" `
    -WindowStyle Hidden `
    -PassThru
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$passed = $false

try {
    do {
        Start-Sleep -Milliseconds 200
        if (Test-Path -LiteralPath $logPath) {
            $matching = Select-String `
                -LiteralPath $logPath `
                -SimpleMatch "OBS_GPU_RECOVERY_TEST PASS: token=$token" `
                -ErrorAction SilentlyContinue
            if ($matching) {
                $passed = $true
                break
            }

            $failure = Select-String `
                -LiteralPath $logPath `
                -SimpleMatch "OBS_GPU_RECOVERY_TEST FAIL: token=$token" `
                -ErrorAction SilentlyContinue
            if ($failure) {
                throw $failure.Line
            }
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    if (-not $passed) {
        throw "GPU recovery did not resume recording within $TimeoutSeconds seconds."
    }

    Write-Host "GPU_RECOVERY_TEST PASS: token=$token"
}
finally {
    $matchingProcesses = Get-CimInstance Win32_Process `
        -Filter "Name = 'Captail.exe'" |
        Where-Object { $_.CommandLine -like "*$token*" }
    foreach ($process in $matchingProcesses) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
}
