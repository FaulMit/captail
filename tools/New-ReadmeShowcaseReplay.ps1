param(
    [Parameter(Mandatory = $true)]
    [string]$Source,
    [string]$Output,
    [double]$StartSeconds = 5,
    [double]$DurationSeconds = 4,
    [ValidateRange(0, 63)]
    [int]$Crf = 32
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = (Resolve-Path -LiteralPath $Source -ErrorAction Stop).Path
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $repoRoot `
        "artifacts\readme-media\captail-showcase-av1-4k-240.mkv"
}
$outputPath = [System.IO.Path]::GetFullPath($Output)

function Find-BundledTool {
    param([string]$Name)

    $tool = Get-ChildItem -Path (Join-Path $repoRoot "artifacts") `
        -Filter $Name -File -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $tool) {
        throw "$Name not found under artifacts. Build or extract Captail first."
    }
    return $tool.FullName
}

function Get-FrameRate {
    param([string]$Rate)

    $parts = $Rate -split "/"
    if ($parts.Count -ne 2 -or [double]$parts[1] -eq 0) {
        return 0
    }
    return [double]$parts[0] / [double]$parts[1]
}

$ffmpeg = Find-BundledTool -Name "ffmpeg.exe"
$ffprobe = Find-BundledTool -Name "ffprobe.exe"

$sourceJson = & $ffprobe -v error -show_entries `
    stream=index,codec_type,codec_name,width,height,avg_frame_rate `
    -of json -- $sourcePath | ConvertFrom-Json
$sourceVideo = $sourceJson.streams |
    Where-Object codec_type -eq "video" |
    Select-Object -First 1
if ($null -eq $sourceVideo) {
    throw "Source has no readable video stream."
}
$sourceFps = Get-FrameRate -Rate ([string]$sourceVideo.avg_frame_rate)
if ($sourceFps -lt 239) {
    throw "Source must contain real 240 FPS video. Found $([Math]::Round($sourceFps, 3)) FPS."
}

$audioStreams = @($sourceJson.streams | Where-Object codec_type -eq "audio")
if ($audioStreams.Count -eq 0) {
    throw "Source must contain at least one real audio stream."
}

$encoderList = & $ffmpeg -hide_banner -encoders 2>$null
if (-not ($encoderList -match "libsvtav1")) {
    throw "Bundled FFmpeg does not expose libsvtav1."
}

New-Item -ItemType Directory -Path (Split-Path -Parent $outputPath) `
    -Force | Out-Null
$temporaryPath = "$outputPath.partial.mkv"
Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue

$arguments = @(
    "-y",
    "-hide_banner",
    "-loglevel", "warning",
    "-stats",
    "-ss", ([string]::Format(
        [Globalization.CultureInfo]::InvariantCulture,
        "{0:0.###}",
        $StartSeconds)),
    "-t", ([string]::Format(
        [Globalization.CultureInfo]::InvariantCulture,
        "{0:0.###}",
        $DurationSeconds)),
    "-i", $sourcePath,
    "-map", "0:v:0",
    "-map", "0:a?",
    "-vf", "scale=3840:2160:flags=lanczos",
    "-fps_mode", "passthrough",
    "-c:v", "libsvtav1",
    "-preset", "13",
    "-crf", $Crf.ToString([Globalization.CultureInfo]::InvariantCulture),
    "-g", "240",
    "-pix_fmt", "yuv420p",
    "-c:a", "copy",
    "-metadata", "title=Captail README showcase fixture",
    "-metadata", "comment=Upscaled from real Captail 240 FPS replay; documentation fixture, not native 4K capture evidence"
)
if ($audioStreams.Count -ge 1) {
    $arguments += @("-metadata:s:a:0", "title=System audio")
}
if ($audioStreams.Count -ge 2) {
    $arguments += @("-metadata:s:a:1", "title=Microphone")
}
$arguments += $temporaryPath

Write-Host "Generating AV1 3840x2160 240 FPS fixture..."
& $ffmpeg @arguments
if ($LASTEXITCODE -ne 0) {
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    throw "FFmpeg failed with exit code $LASTEXITCODE."
}

$resultJson = & $ffprobe -v error -show_entries `
    stream=index,codec_type,codec_name,width,height,avg_frame_rate,duration:format=duration `
    -of json -- $temporaryPath | ConvertFrom-Json
$resultVideo = $resultJson.streams |
    Where-Object codec_type -eq "video" |
    Select-Object -First 1
$resultAudio = @($resultJson.streams | Where-Object codec_type -eq "audio")
$resultFps = Get-FrameRate -Rate ([string]$resultVideo.avg_frame_rate)
$valid = $resultVideo.codec_name -eq "av1" -and
    [int]$resultVideo.width -eq 3840 -and
    [int]$resultVideo.height -eq 2160 -and
    $resultFps -ge 239 -and
    $resultAudio.Count -eq $audioStreams.Count
if (-not $valid) {
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    throw "Generated fixture failed metadata validation."
}

Move-Item -LiteralPath $temporaryPath -Destination $outputPath -Force
$file = Get-Item -LiteralPath $outputPath
Write-Host "Created: $outputPath"
Write-Host "Video: AV1 3840x2160 $([Math]::Round($resultFps, 3)) FPS"
Write-Host "Audio tracks: $($resultAudio.Count)"
Write-Host "Size: $([Math]::Round($file.Length / 1MB, 1)) MB"
