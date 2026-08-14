[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$assemblyPath = Join-Path $repoRoot `
    "src\Captail\bin\Debug\net9.0-windows10.0.22621.0\win-x64\Captail.dll"
if (-not (Test-Path -LiteralPath $assemblyPath)) {
    throw "Build Captail before running diagnostic-log QA."
}

[Reflection.Assembly]::LoadFrom($assemblyPath) | Out-Null
$exporter = [Captail.Config].Assembly.GetType(
    "Captail.DiagnosticLogExporter",
    $true)
$method = $exporter.GetMethod(
    "CreateExcerpt",
    [Reflection.BindingFlags]"Static,NonPublic",
    $null,
    [Type[]]@([string]),
    $null)
if ($null -eq $method) {
    throw "DiagnosticLogExporter.CreateExcerpt(string) was not found."
}

$tempPath = Join-Path ([IO.Path]::GetTempPath()) `
    "captail-diagnostic-export-$([Guid]::NewGuid().ToString('N')).log"
try {
    $sample = @"
17:00:00.000 OBS pipeline: version=0.1.10, gpu=NVIDIA GeForce RTX 5070, encoder=NVENC, fps=240
17:00:00.010 Replay save failed: C:\Users\$env:USERNAME\Videos\Private\clip.mp4
17:00:00.020 endpoint=10.0.0.7:443 email=test.user@example.com mac=AA:BB:CC:DD:EE:FF
17:00:00.030 token=super-secret password=hunter2
17:00:00.040 window_title="Private Telegram chat" device_id={12345678-1234-1234-1234-123456789ABC}
17:00:00.050 SID=S-1-5-21-111111111-222222222-333333333-1001 host=$env:COMPUTERNAME user=$env:USERNAME
17:00:00.060 More details at https://example.com/private?key=value
17:00:00.070 libobs[info]: uncontrolled third-party path C:\Users\$env:USERNAME\secret.txt
17:00:00.080 Watchdog: recovering capture pipeline after encoder failure
"@
    [IO.File]::WriteAllText($tempPath, $sample, [Text.UTF8Encoding]::new($false))
    $arguments = [object[]]::new(1)
    $arguments[0] = [string]$tempPath
    $excerpt = [string]$method.Invoke($null, $arguments)

    $forbidden = @(
        $env:USERNAME,
        $env:COMPUTERNAME,
        "super-secret",
        "hunter2",
        "test.user@example.com",
        "10.0.0.7",
        "AA:BB:CC:DD:EE:FF",
        "12345678-1234-1234-1234-123456789ABC",
        "S-1-5-21-111111111-222222222-333333333-1001",
        "Private Telegram chat",
        "example.com",
        "uncontrolled third-party"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    foreach ($value in $forbidden) {
        if ($excerpt.Contains($value, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Sensitive fixture value survived sanitization: $value"
        }
    }

    foreach ($expected in @(
        "OBS pipeline",
        "NVIDIA GeForce RTX 5070",
        "fps=240",
        "Replay save failed",
        "Watchdog",
        "encoder failure",
        "<path>",
        "<ip>",
        "<email>",
        "<redacted>"
    )) {
        if (-not $excerpt.Contains($expected, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Useful or redaction marker missing from excerpt: $expected"
        }
    }

    if ($excerpt.Length -gt 1800) {
        throw "Diagnostic excerpt exceeded URL budget: $($excerpt.Length) characters."
    }
    Write-Host "Diagnostic log exporter QA passed: $($excerpt.Length) characters."
}
finally {
    Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
}
