[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Read-Text([string]$relativePath) {
    return [IO.File]::ReadAllText((Join-Path $repoRoot $relativePath))
}

function Assert-Contains(
    [string]$text,
    [string]$pattern,
    [string]$message
) {
    if ($text -notmatch $pattern) {
        throw $message
    }
}

function Assert-NotContains(
    [string]$text,
    [string]$pattern,
    [string]$message
) {
    if ($text -match $pattern) {
        throw $message
    }
}

$app = Read-Text "src\Captail\App.xaml.cs"
$config = Read-Text "src\Captail\Config.cs"
$editor = Read-Text "src\Captail\ClipEditorWindow.xaml.cs"
$engine = Read-Text "src\Captail\ObsReplayEngine.cs"
$settingsXaml = Read-Text "src\Captail\SettingsWindow.xaml"
$settingsCode = Read-Text "src\Captail\SettingsWindow.xaml.cs"
$editorXaml = Read-Text "src\Captail\ClipEditorWindow.xaml"

Assert-NotContains $app '_activeTrayIcon|_inactiveTrayIcon' `
    "Tray icons must not be reused after TaskbarIcon disposes replaced instances."
Assert-Contains $app 'CreateIcon\(\s*active\s*\?[\s\S]{0,160}ThemeManager\.IconAssetName[\s\S]{0,160}:\s*"CaptailInactive\.ico"\)' `
    "Tray state changes must assign a fresh icon instance."
Assert-Contains $app '--qa-replay-toggle' `
    "Native replay stop/start needs an unattended regression loop."

Assert-Contains $config 'public bool Enabled \{ get; set; \} = true;' `
    "Per-app routes need a persistent enabled state."
Assert-Contains $engine 'Where\(route => route\.Enabled\)' `
    "Disabled per-app routes must not reach process audio capture."
Assert-Contains $settingsXaml 'x:Name="PerAppAudioSourcePanel"' `
    "Main screen needs per-app source chips."
Assert-Contains $settingsCode 'PerAppAudioSource_Click' `
    "Per-app source chips must toggle their capture source."
Assert-Contains $settingsXaml 'L\.Help\.ApplicationAudio' `
    "Per-app audio settings need contextual help."

Get-ChildItem (Join-Path $repoRoot "src\Captail\Languages") `
    -Filter "Strings.*.xaml" |
    ForEach-Object {
        $language = [IO.File]::ReadAllText($_.FullName)
        Assert-Contains $language 'x:Key="L\.Help\.ApplicationAudio"' `
            "$($_.Name) must localize per-app audio help."
    }

Assert-NotContains $settingsCode 'advancedAudioTrackLabels:\s*BuildAdvancedAudioTrackLabels' `
    "Replay labels must not come from current settings."
Assert-Contains $editor 'IsGenericAudioTitle' `
    "Editor must resolve labels from replay metadata, then use neutral fallback."
Assert-Contains $editorXaml 'x:Name="AudioTrackScrollViewer"' `
    "Editor audio rows need a bounded scroll viewport for files with many tracks."
Assert-Contains $editorXaml 'x:Name="AudioTrackCountText"' `
    "Editor must show the number of audio tracks found in the file."
Assert-Contains $editor 'UpdateAudioTrackLayout\(tracks\.Count\)' `
    "Editor window height must adapt after audio metadata is loaded."
Assert-Contains $editor 'BaseVisibleAudioTracks = 1' `
    "Base editor height only has room for one complete audio row above actions."
Assert-Contains $editorXaml '<ColumnDefinition Width="330"/>' `
    "Editor metadata must reserve enough width for the complete action button group."

Write-Host "ISSUE41_FOLLOWUP_TEST PASS"
