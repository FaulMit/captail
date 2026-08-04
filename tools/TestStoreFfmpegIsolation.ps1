[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot
)

$ErrorActionPreference = "Stop"
$packageRoot = [IO.Path]::GetFullPath($PackageRoot)
$ffmpegRoot = Join-Path $packageRoot "ffmpeg"

if (-not (Test-Path -LiteralPath $ffmpegRoot -PathType Container)) {
    throw "FFmpeg directory not found: $ffmpegRoot"
}

$duplicateLibraries = @(
    Get-ChildItem -LiteralPath $ffmpegRoot -File -Filter *.dll |
        Where-Object {
            Test-Path -LiteralPath (Join-Path $packageRoot $_.Name) -PathType Leaf
        } |
        Select-Object -ExpandProperty Name
)
if ($duplicateLibraries.Count -ne 0) {
    throw (
        "Store FFmpeg DLL collision detected: " +
        ($duplicateLibraries -join ", ") +
        ". Packaged processes can resolve these names from the package root " +
        "and fail before FFmpeg starts."
    )
}

foreach ($toolName in @("ffmpeg.exe", "ffprobe.exe")) {
    $toolPath = Join-Path $ffmpegRoot $toolName
    if (-not (Test-Path -LiteralPath $toolPath -PathType Leaf)) {
        throw "Store FFmpeg tool not found: $toolPath"
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $toolPath
    $startInfo.WorkingDirectory = $ffmpegRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add("-version")

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start $toolName."
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) {
            $process.Kill($true)
            throw "$toolName did not exit within 15 seconds."
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "$toolName failed with exit code $($process.ExitCode): $stderr"
        }
        $expectedVersionPrefix =
            [IO.Path]::GetFileNameWithoutExtension($toolName) + " version"
        if (($stdout + $stderr) -notmatch [Regex]::Escape($expectedVersionPrefix)) {
            throw "$toolName returned unexpected version output."
        }
    }
    finally {
        $process.Dispose()
    }
}

Write-Host "PASS: Store FFmpeg is self-contained and starts without package-root DLL collisions."
