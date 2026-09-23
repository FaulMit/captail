[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Read-Text([string]$relativePath) {
    [IO.File]::ReadAllText((Join-Path $repoRoot $relativePath))
}

function Require([string]$text, [string]$pattern, [string]$message) {
    if ($text -notmatch $pattern) {
        throw $message
    }
}

$hotkeys = Read-Text "src\Captail\HotkeyManager.cs"
$overlay = Read-Text "src\Captail\OverlayNotificationWindow.xaml.cs"
$indicator = Read-Text "src\Captail\ReplayStatusIndicatorWindow.xaml.cs"
$app = Read-Text "src\Captail\App.xaml.cs"

Require $hotkeys 'WhKeyboardLl' `
    "F13-F24 need a low-level keyboard fallback for fullscreen applications."
Require $hotkeys 'IsExtendedFunctionKey' `
    "Extended function-key bindings must be routed through the fullscreen fallback."
Require $hotkeys 'CallNextHookEx' `
    "Fullscreen hotkey fallback must never swallow keyboard input."
Require $hotkeys 'RunExtendedFunctionKeyQa' `
    "F13-F24 parsing, modifier matching, and repeat suppression need runtime QA."

Require $overlay 'HwndTopmost' `
    "Overlay must explicitly enter native HWND_TOPMOST z-order."
Require $overlay 'SetWindowPos' `
    "Overlay must reassert native topmost state when a fullscreen app owns foreground."
Require $overlay '_topmostTimer' `
    "Visible notification must defend its z-order while fullscreen app is active."
Require $overlay 'MonitorFromWindow' `
    "Notification must use foreground game's monitor instead of primary monitor only."
Require $overlay 'RunFullscreenOverlayQa' `
    "Overlay native styles and z-order maintenance need runtime QA."

Require $indicator 'RunFullscreenIndicatorQa' `
    "Recording indicator needs runtime coverage for fullscreen z-order loss."
Require $indicator 'foregroundChanged[\s\S]{0,300}HwndTopmost' `
    "Recording indicator must recover topmost z-order after foreground changes."
Require $indicator 'IsCoveredByHigherWindow' `
    "Recording indicator must recover from non-activating topmost windows."
Require $app 'indicatorPassed' `
    "Combined fullscreen QA must fail when recording indicator loses z-order."

Require $app '--qa-fullscreen-input-overlay' `
    "Combined fullscreen hotkey/overlay QA entry point is missing."

Write-Host "FULLSCREEN_OVERLAY_HOTKEYS STATIC PASS"
