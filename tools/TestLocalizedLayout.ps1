[CmdletBinding()]
param(
    [double]$StatusTitleWidth = 240.0
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationCore

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$languageRoot = Join-Path $repoRoot "src\Captail\Languages"
$settingsPath = Join-Path $repoRoot "src\Captail\SettingsWindow.xaml"
$statusTitleKeys = @(
    "L.Status.Enabled",
    "L.Status.Disabled",
    "L.Status.Recovering"
)
$settingsLayoutContracts = @(
    @{
        Name = "BufferLimitLabelText"
        Key = "L.Replay.BufferLimit"
        Width = 110.0
        MaxLines = 2
        FontSize = 13.0
        Weight = [Windows.FontWeights]::SemiBold
    },
    @{
        Name = "BufferLimitHintText"
        Key = "L.Replay.BufferLimitHint"
        Width = 131.0
        MaxLines = 2
        FontSize = 10.0
        Weight = [Windows.FontWeights]::Normal
    },
    @{
        Name = "ResolutionLabelText"
        Key = "L.Video.Resolution"
        Width = 110.0
        MaxLines = 2
        FontSize = 13.0
        Weight = [Windows.FontWeights]::SemiBold
    }
)

$fontFamily = [Windows.Media.FontFamily]::new(
    "Segoe UI Variable Text, Segoe UI")
$typeface = [Windows.Media.Typeface]::new(
    $fontFamily,
    [Windows.FontStyles]::Normal,
    [Windows.FontWeights]::Bold,
    [Windows.FontStretches]::Normal)
$failures = [Collections.Generic.List[string]]::new()
$files = @(Get-ChildItem -LiteralPath $languageRoot -Filter "Strings.*.xaml")

[xml]$settings = Get-Content -LiteralPath $settingsPath -Encoding utf8
$namespaces = [Xml.XmlNamespaceManager]::new($settings.NameTable)
$namespaces.AddNamespace(
    "x",
    "http://schemas.microsoft.com/winfx/2006/xaml")
foreach ($contract in $settingsLayoutContracts) {
    $node = $settings.SelectSingleNode(
        "//*[@x:Name='$($contract.Name)']",
        $namespaces)
    if ($null -eq $node) {
        $failures.Add("SettingsWindow.xaml: missing $($contract.Name)")
    }
    elseif ($node.TextWrapping -ne "Wrap") {
        $failures.Add(
            "SettingsWindow.xaml: $($contract.Name) must wrap localized text")
    }
}

foreach ($file in $files) {
    [xml]$dictionary = Get-Content -LiteralPath $file.FullName -Encoding utf8
    $values = @{}
    foreach ($entry in $dictionary.ResourceDictionary.String) {
        $values[$entry.Key] = [string]$entry.'#text'
    }

    $language = $file.BaseName.Split('.')[-1]
    $culture = [Globalization.CultureInfo]::GetCultureInfo($language)
    foreach ($key in $statusTitleKeys) {
        if (-not $values.ContainsKey($key)) {
            $failures.Add("$($file.Name): missing $key")
            continue
        }

        $text = $values[$key]
        $formatted = [Windows.Media.FormattedText]::new(
            $text,
            $culture,
            [Windows.FlowDirection]::LeftToRight,
            $typeface,
            14.5,
            [Windows.Media.Brushes]::White,
            1.0)
        $width = [math]::Ceiling($formatted.WidthIncludingTrailingWhitespace)
        if ($width -gt $StatusTitleWidth) {
            $failures.Add(
                "$($file.Name): $key uses ${width}px of ${StatusTitleWidth}px: '$text'")
        }
    }

    foreach ($contract in $settingsLayoutContracts) {
        $key = $contract.Key
        if (-not $values.ContainsKey($key)) {
            $failures.Add("$($file.Name): missing $key")
            continue
        }

        $contractTypeface = [Windows.Media.Typeface]::new(
            $fontFamily,
            [Windows.FontStyles]::Normal,
            $contract.Weight,
            [Windows.FontStretches]::Normal)
        $text = $values[$key]
        $formatted = [Windows.Media.FormattedText]::new(
            $text,
            $culture,
            [Windows.FlowDirection]::LeftToRight,
            $contractTypeface,
            $contract.FontSize,
            [Windows.Media.Brushes]::White,
            1.0)
        $width = [math]::Ceiling($formatted.WidthIncludingTrailingWhitespace)
        $availableWidth = $contract.Width * $contract.MaxLines
        if ($width -gt $availableWidth) {
            $failures.Add(
                "$($file.Name): $key needs more than $($contract.MaxLines) lines at $($contract.Width)px: '$text'")
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    throw "Localized Captail layout exceeds available space."
}

Write-Host "$($files.Count) localization dictionaries fit protected UI regions."
