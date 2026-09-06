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

$settings = Read-Text "src\Captail\SettingsWindow.xaml.cs"
$app = Read-Text "src\Captail\App.xaml.cs"
$engine = Read-Text "src\Captail\ObsReplayEngine.cs"
$launcher = Read-Text "src\Captail\ExternalLinkLauncher.cs"
$player = Read-Text "src\Captail\MpvHost.cs"
$editor = Read-Text "src\Captail\ClipEditorWindow.xaml.cs"
$editorXaml = Read-Text "src\Captail\ClipEditorWindow.xaml"

Require $settings 'L\.Status\.RecordingDisabled' `
    "Recording mode needs its own inactive status title."
Require $settings 'L\.Status\.RecordingIdle' `
    "Recording mode needs its own inactive status detail."
Require $app 'L\.Notify\.RecordingOffGameTitle' `
    "Game-start warning must describe disabled Recording mode."

Require $engine '_recordingGameExecutable' `
    "Continuous recording must preserve detected game identity for save routing."
Require $engine 'IsContinuousRecording[\s\S]{0,220}IsAutomaticCapture' `
    "Continuous Desktop recording must keep automatic game detection enabled."

Require $launcher 'Windows\.System\.Launcher\.LaunchUriAsync' `
    "Packaged and portable builds must use Windows URI launcher."
Require $launcher 'ShellExecuteW' `
    "External links need native shell activation in MSIX builds."
Require $settings 'AboutPopupPanel\.IsAncestorOf' `
    "About popup buttons must receive clicks before outside-click dismissal."

Require $player 'NativeMouseWheel' `
    "Embedded native video host must forward mouse-wheel input."
Require $player 'await StopAsync\(' `
    "Replacing a loaded clip must drain old end-file event before loading next clip."
Require $editor 'PreviewPlayer\.NativeMouseWheel' `
    "Replay player must consume wheel events forwarded by native video host."

Require $editorXaml 'x:Name="SidebarColumn"' `
    "Fullscreen layout needs direct control over recent-clips column."
Require $editor 'SidebarColumn\.Width\s*=\s*new GridLength\(0\)' `
    "Fullscreen must remove recent-clips column from layout."
Require $editor 'Grid\.SetColumnSpan\(EditorWorkspace, 2\)' `
    "Fullscreen video must span complete window width."
Require $editor 'ResizeMode\s*=\s*ResizeMode\.NoResize' `
    "Fullscreen must remove WPF resize frame."
Require $editor 'SwpFrameChanged' `
    "Fullscreen must notify Windows after changing native frame style."

Get-ChildItem (Join-Path $repoRoot "src\Captail\Languages") `
    -Filter "Strings.*.xaml" |
    ForEach-Object {
        $language = [IO.File]::ReadAllText($_.FullName)
        Require $language 'x:Key="L\.Status\.RecordingDisabled"' `
            "$($_.Name) is missing Recording inactive title."
        Require $language 'x:Key="L\.Status\.RecordingIdle"' `
            "$($_.Name) is missing Recording inactive detail."
        Require $language 'x:Key="L\.Notify\.RecordingOffGameTitle"' `
            "$($_.Name) is missing Recording game-start warning."
        Require $language 'x:Key="L\.Notify\.RecordingOffGameDetail"' `
            "$($_.Name) is missing Recording game-start warning detail."
    }

Write-Host "REPORTED_UI_RUNTIME_REGRESSIONS PASS"
