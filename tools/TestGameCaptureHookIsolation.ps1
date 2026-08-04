$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $repoRoot `
    "native\ObsCaptureFixture\build\Release\ObsCaptureFixture.exe"
$appRoot = Join-Path $repoRoot `
    "src\Captail\bin\Debug\net9.0-windows10.0.22621.0\win-x64"
$captail = Join-Path $appRoot "Captail.exe"
$appHook = Join-Path $appRoot `
    "data\obs-plugins\win-capture\graphics-hook64.dll"
$probe = "$appHook.qa-unlocked"

foreach ($required in @($fixture, $captail, $appHook)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required QA file is missing: $required"
    }
}

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class CaptailHookQaWindow
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr window, int command);
}
"@

$fixtureProcess = $null
try {
    $fixtureProcess = Start-Process -FilePath $fixture -PassThru
    Start-Sleep -Seconds 1
    $fixtureProcess.Refresh()

    $shell = New-Object -ComObject WScript.Shell
    $shell.SendKeys("%")
    [void][CaptailHookQaWindow]::ShowWindow(
        $fixtureProcess.MainWindowHandle,
        5)
    [void][CaptailHookQaWindow]::BringWindowToTop(
        $fixtureProcess.MainWindowHandle)
    [void][CaptailHookQaWindow]::SetForegroundWindow(
        $fixtureProcess.MainWindowHandle)
    Start-Sleep -Seconds 1
    if ([CaptailHookQaWindow]::GetForegroundWindow() -ne `
        $fixtureProcess.MainWindowHandle) {
        throw "Could not focus D3D11 capture fixture."
    }

    $captailProcess = Start-Process -FilePath $captail -PassThru -Wait `
        -ArgumentList @(
            "--qa-game-capture=fixture",
            "--qa-game-codec=h264",
            "--qa-game-fps=60",
            "--qa-game-resolution=1920x1080"
        )
    if ($captailProcess.ExitCode -ne 0) {
        throw "Captail Game Capture QA failed: $($captailProcess.ExitCode)"
    }

    Start-Sleep -Milliseconds 500
    $hooks = @($fixtureProcess.Modules | Where-Object {
        $_.ModuleName -match '^graphics-hook(32|64)\.dll$'
    })
    if ($hooks.Count -eq 0) {
        throw "Capture fixture did not retain an OBS graphics hook."
    }
    if ($hooks | Where-Object {
            $_.FileName.StartsWith(
                $appRoot,
                [System.StringComparison]::OrdinalIgnoreCase)
        }) {
        throw "Captured process retained hook from Captail application folder."
    }

    Move-Item -LiteralPath $appHook -Destination $probe
    Move-Item -LiteralPath $probe -Destination $appHook
    $hooks | Select-Object ModuleName, FileName | Format-Table -AutoSize
    Write-Output `
        "PASS: captured process uses cached hook; application hook is unlocked."
}
finally {
    if (Test-Path -LiteralPath $probe) {
        Move-Item -LiteralPath $probe -Destination $appHook -Force
    }
    if ($fixtureProcess -and -not $fixtureProcess.HasExited) {
        Stop-Process -Id $fixtureProcess.Id -Force
    }
}
