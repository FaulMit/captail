param(
    [string]$CaptailExe,
    [string]$ReplayFile,
    [string]$OutputDirectory,
    [string]$ExpectedVersion,
    [switch]$SkipEditor,
    [switch]$EditorOnly,
    [switch]$AllowNonShowcaseReplay
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "docs"
}

function Resolve-CaptailExecutable {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = Resolve-Path -LiteralPath $RequestedPath -ErrorAction Stop
        return $resolved.Path
    }

    $candidates = @(
        Get-ChildItem -Path (Join-Path $repoRoot "artifacts") -Filter Captail.exe -File -Recurse -ErrorAction SilentlyContinue
        Get-ChildItem -Path (Join-Path $repoRoot "src\Captail\bin\Release") -Filter Captail.exe -File -Recurse -ErrorAction SilentlyContinue
    ) | Sort-Object LastWriteTimeUtc -Descending

    if ($candidates.Count -eq 0) {
        throw "Captail.exe not found. Pass -CaptailExe with exact release-candidate executable."
    }

    return $candidates[0].FullName
}

function Wait-Until {
    param(
        [scriptblock]$Condition,
        [int]$TimeoutSeconds = 20,
        [string]$FailureMessage = "Timed out waiting for Captail."
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $value = & $Condition
        if ($null -ne $value -and $value -ne $false) {
            return $value
        }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)

    throw $FailureMessage
}

function Find-AutomationElement {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [int]$TimeoutSeconds = 10
    )

    $propertyCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)

    return Wait-Until -TimeoutSeconds $TimeoutSeconds `
        -FailureMessage "Automation element '$AutomationId' was not found." `
        -Condition {
            $element = $Root.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                $propertyCondition)
            if ($null -ne $element) { $element }
        }
}

function Invoke-AutomationElement {
    param([System.Windows.Automation.AutomationElement]$Element)

    $pattern = $Element.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}

function Set-SettingsScrollPercent {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [double]$Percent
    )

    $viewer = Find-AutomationElement -Root $Root -AutomationId "SettingsScrollViewer"
    $pattern = [System.Windows.Automation.ScrollPattern]$viewer.GetCurrentPattern(
        [System.Windows.Automation.ScrollPattern]::Pattern)
    $pattern.SetScrollPercent(
        [System.Windows.Automation.ScrollPattern]::NoScroll,
        [Math]::Min(100, [Math]::Max(0, $Percent)))
    Start-Sleep -Milliseconds 450
}

function Get-CaptailRoot {
    param([System.Diagnostics.Process]$Process)

    $handle = Wait-Until -TimeoutSeconds 30 `
        -FailureMessage "Captail did not create a main window." `
        -Condition {
            $Process.Refresh()
            if ($Process.HasExited) {
                throw "Captail exited before its window was ready (exit code $($Process.ExitCode))."
            }
            if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
                $Process.MainWindowHandle
            }
        }

    return [System.Windows.Automation.AutomationElement]::FromHandle($handle)
}

function Capture-Window {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Path
    )

    $handle = [IntPtr]$Root.Current.NativeWindowHandle
    if ($handle -eq [IntPtr]::Zero) {
        throw "Cannot capture window without native handle."
    }

    [NativeMethods]::ShowWindow($handle, 9) | Out-Null
    [NativeMethods]::SetForegroundWindow($handle) | Out-Null
    Start-Sleep -Milliseconds 250

    $rect = New-Object NativeMethods+RECT
    if (-not [NativeMethods]::GetWindowRect($handle, [ref]$rect)) {
        throw "GetWindowRect failed for Captail window."
    }

    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -lt 100 -or $height -lt 100) {
        throw "Invalid Captail window bounds: ${width}x${height}."
    }

    [NativeMethods]::SetCursorPos(0, 0) | Out-Null
    $bitmap = New-Object System.Drawing.Bitmap(
        $width,
        $height,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen(
                $rect.Left,
                $rect.Top,
                0,
                0,
                (New-Object System.Drawing.Size($width, $height)),
                [System.Drawing.CopyPixelOperation]::SourceCopy)
        }
        finally {
            $graphics.Dispose()
        }

        $parent = Split-Path -Parent $Path
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }

    Write-Host "Captured $Path"
}

function Stop-CaptailInstance {
    param([string]$Executable)

    $running = @(Get-Process Captail -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) {
        return
    }

    $shutdown = Start-Process -FilePath $Executable `
        -ArgumentList "--shutdown-existing" -PassThru -WindowStyle Hidden
    $shutdown.WaitForExit(15000) | Out-Null
    Wait-Until -TimeoutSeconds 15 -FailureMessage "Captail did not shut down cleanly." `
        -Condition { if (-not (Get-Process Captail -ErrorAction SilentlyContinue)) { $true } } | Out-Null
}

function Resolve-ReplayFile {
    param(
        [string]$RequestedPath,
        [string]$ConfiguredOutputDirectory
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        return (Resolve-Path -LiteralPath $RequestedPath -ErrorAction Stop).Path
    }

    if (-not (Test-Path -LiteralPath $ConfiguredOutputDirectory -PathType Container)) {
        return $null
    }

    $candidate = Get-ChildItem -LiteralPath $ConfiguredOutputDirectory -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object Extension -In ".mkv", ".mp4" |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $candidate) { return $null }
    return $candidate.FullName
}

function Assert-ShowcaseReplay {
    param(
        [string]$Path,
        [string]$Executable,
        [bool]$AllowMismatch
    )

    $ffprobe = Get-ChildItem -Path (Split-Path -Parent $Executable) `
        -Filter ffprobe.exe -File -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $ffprobe) {
        if ($AllowMismatch) { return }
        throw "ffprobe.exe not found beside release build; replay metadata cannot be verified."
    }

    $json = & $ffprobe.FullName -v error -select_streams v:0 `
        -show_entries stream=codec_name,width,height,avg_frame_rate `
        -of json -- $Path | ConvertFrom-Json
    $stream = $json.streams | Select-Object -First 1
    if ($null -eq $stream) {
        throw "Replay has no readable video stream: $Path"
    }

    $rateParts = [string]$stream.avg_frame_rate -split "/"
    $fps = if ($rateParts.Count -eq 2 -and [double]$rateParts[1] -ne 0) {
        [double]$rateParts[0] / [double]$rateParts[1]
    }
    else { 0 }

    $valid = $stream.codec_name -eq "av1" -and
        [int]$stream.width -eq 3840 -and
        [int]$stream.height -eq 2160 -and
        $fps -ge 239
    if (-not $valid -and -not $AllowMismatch) {
        throw "Editor screenshot requires real AV1 3840x2160 240 FPS replay. Found $($stream.codec_name) $($stream.width)x$($stream.height) $([Math]::Round($fps, 2)) FPS. Pass -ReplayFile with showcase media or use -AllowNonShowcaseReplay intentionally."
    }
}

function Open-FirstReplayEditor {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$ReplayPath
    )

    $list = Find-AutomationElement -Root $Root -AutomationId "RecentReplaysList" -TimeoutSeconds 30
    $targetName = [System.IO.Path]::GetFileNameWithoutExtension($ReplayPath)
    $textCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $texts = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition)
    $target = $null
    for ($index = 0; $index -lt $texts.Count; $index++) {
        $text = $texts.Item($index)
        if ($text.Current.Name -eq $targetName -or
            $text.Current.Name -eq [System.IO.Path]::GetFileName($ReplayPath)) {
            $target = $text
            break
        }
    }
    if ($null -eq $target -and $texts.Count -gt 0) {
        $target = $texts.Item(0)
    }
    if ($null -eq $target) {
        throw "No replay card is available on dashboard."
    }

    $rowElement = $target
    while ($null -ne $rowElement -and
        $rowElement.Current.ControlType -ne [System.Windows.Automation.ControlType]::DataItem) {
        $rowElement = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent(
            $rowElement)
    }
    $row = if ($null -ne $rowElement) {
        $rowElement.Current.BoundingRectangle
    }
    else {
        $textBounds = $target.Current.BoundingRectangle
        New-Object System.Windows.Rect(
            $textBounds.Left,
            ($textBounds.Top - 20),
            $textBounds.Width,
            ($textBounds.Height + 40))
    }
    $window = $Root.Current.BoundingRectangle
    [NativeMethods]::SetCursorPos(
        [int]($window.Right - 55),
        [int]($row.Top + ($row.Height / 2))) | Out-Null
    Start-Sleep -Milliseconds 500

    $buttonCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $buttons = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    $rowButtons = @()
    for ($index = 0; $index -lt $buttons.Count; $index++) {
        $button = $buttons.Item($index)
        $bounds = $button.Current.BoundingRectangle
        if (-not $button.Current.IsOffscreen -and $button.Current.IsEnabled -and
            $bounds.Top -ge ($row.Top - 10) -and $bounds.Bottom -le ($row.Bottom + 10) -and
            $bounds.Left -gt ($window.Left + 230)) {
            $rowButtons += $button
        }
    }
    $rowButtons = @($rowButtons | Sort-Object { $_.Current.BoundingRectangle.Left })
    if ($rowButtons.Count -lt 3) {
        throw "Replay action buttons did not appear after hover."
    }

    Invoke-AutomationElement -Element $rowButtons[1]
    [NativeMethods]::SetCursorPos(0, 0) | Out-Null
}

Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, ref RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);
}
"@

$CaptailExe = Resolve-CaptailExecutable -RequestedPath $CaptailExe
if ($SkipEditor -and $EditorOnly) {
    throw "-SkipEditor and -EditorOnly cannot be used together."
}
$actualVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($CaptailExe).ProductVersion
if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and
    -not $actualVersion.StartsWith($ExpectedVersion, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Expected Captail $ExpectedVersion, found '$actualVersion' in $CaptailExe"
}

$runningBefore = @(Get-Process Captail -ErrorAction SilentlyContinue | Select-Object -First 1)
$runningPath = if ($runningBefore.Count -gt 0) { $runningBefore[0].Path } else { $null }
$configDirectory = Join-Path $env:APPDATA "Captail"
$configPath = Join-Path $configDirectory "config.json"
$configBackupPath = Join-Path $configDirectory "config.json.bak"
$hadConfig = Test-Path -LiteralPath $configPath
$hadConfigBackup = Test-Path -LiteralPath $configBackupPath
$originalConfig = if ($hadConfig) { [System.IO.File]::ReadAllBytes($configPath) } else { $null }
$originalConfigBackup = if ($hadConfigBackup) { [System.IO.File]::ReadAllBytes($configBackupPath) } else { $null }
$originalCursor = New-Object System.Drawing.Point
[System.Windows.Forms.Cursor]::Position | ForEach-Object { $originalCursor = $_ }
$automationProcess = $null

try {
    Stop-CaptailInstance -Executable $CaptailExe
    if (-not $hadConfig) {
        throw "Captail config not found at $configPath. Launch Captail once before screenshot capture."
    }

    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $originalOutputDirectory = [string]$config.OutputDirectory
    $resolvedReplay = Resolve-ReplayFile -RequestedPath $ReplayFile `
        -ConfiguredOutputDirectory $originalOutputDirectory

    $config.Language = "en"
    $config.Codec = "av1"
    $config.RecordingResolution = "2160p"
    $config.FrameRate = 240
    $config.BitrateMbps = 80
    $config.BufferSeconds = 600
    $config.ReplayEnabled = $true
    $config.CaptureSystemAudio = $true
    $config.CaptureMicrophone = $true
    $config.SeparateAudioTracks = $true
    if ($null -ne $resolvedReplay) {
        $config.OutputDirectory = Split-Path -Parent $resolvedReplay
    }
    $config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configPath -Encoding UTF8

    Write-Host "Capturing Captail $actualVersion from $CaptailExe"
    $automationProcess = Start-Process -FilePath $CaptailExe -PassThru
    $root = Get-CaptailRoot -Process $automationProcess
    Start-Sleep -Seconds 4

    if (-not $EditorOnly) {
        Capture-Window -Root $root -Path (Join-Path $OutputDirectory "captail-main.png")

        $settingsButton = Find-AutomationElement -Root $root -AutomationId "SettingsButton"
        Invoke-AutomationElement -Element $settingsButton
        Find-AutomationElement -Root $root -AutomationId "DoneButton" | Out-Null
        Start-Sleep -Milliseconds 600

        Set-SettingsScrollPercent -Root $root -Percent 34
        Capture-Window -Root $root -Path (Join-Path $OutputDirectory "captail-settings-video.png")

        Set-SettingsScrollPercent -Root $root -Percent 66
        Capture-Window -Root $root -Path (Join-Path $OutputDirectory "captail-settings-audio.png")
    }

    if (-not $SkipEditor) {
        if ($null -eq $resolvedReplay) {
            throw "No replay found. Pass -ReplayFile or use -SkipEditor."
        }
        Assert-ShowcaseReplay -Path $resolvedReplay -Executable $CaptailExe `
            -AllowMismatch $AllowNonShowcaseReplay.IsPresent

        if (-not $EditorOnly) {
            $doneButton = Find-AutomationElement -Root $root -AutomationId "DoneButton"
            Invoke-AutomationElement -Element $doneButton
            Start-Sleep -Seconds 3
        }
        Open-FirstReplayEditor -Root $root -ReplayPath $resolvedReplay

        $editorProcess = Get-Process -Id $automationProcess.Id
        $editorRoot = Wait-Until -TimeoutSeconds 30 `
            -FailureMessage "Clip editor window did not open." `
            -Condition {
                $processCondition = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
                    $editorProcess.Id)
                $windowCondition = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Window)
                $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.AndCondition(
                        $processCondition,
                        $windowCondition)))
                $matches = @()
                for ($index = 0; $index -lt $windows.Count; $index++) {
                    $candidate = $windows.Item($index)
                    $play = $candidate.FindFirst(
                        [System.Windows.Automation.TreeScope]::Descendants,
                        (New-Object System.Windows.Automation.PropertyCondition(
                            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
                            "PlayButton")))
                    if ($null -ne $play) { $matches += $candidate }
                }
                if ($matches.Count -gt 0) {
                    return $matches |
                        Sort-Object {
                            $bounds = $_.Current.BoundingRectangle
                            -($bounds.Width * $bounds.Height)
                        } |
                        Select-Object -First 1
                }
            }
        Start-Sleep -Seconds 4
        Capture-Window -Root $editorRoot -Path (Join-Path $OutputDirectory "captail-editor.png")
    }
}
finally {
    try {
        Stop-CaptailInstance -Executable $CaptailExe
    }
    catch {
        Write-Warning "Captail shutdown during cleanup failed: $($_.Exception.Message)"
    }

    New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null
    if ($hadConfig) {
        [System.IO.File]::WriteAllBytes($configPath, $originalConfig)
    }
    else {
        Remove-Item -LiteralPath $configPath -Force -ErrorAction SilentlyContinue
    }
    if ($hadConfigBackup) {
        [System.IO.File]::WriteAllBytes($configBackupPath, $originalConfigBackup)
    }
    else {
        Remove-Item -LiteralPath $configBackupPath -Force -ErrorAction SilentlyContinue
    }
    [NativeMethods]::SetCursorPos($originalCursor.X, $originalCursor.Y) | Out-Null

    if (-not [string]::IsNullOrWhiteSpace($runningPath) -and
        (Test-Path -LiteralPath $runningPath)) {
        Start-Process -FilePath $runningPath | Out-Null
    }
}

Write-Host "README screenshot capture complete. User config restored."
