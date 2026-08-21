# Third-party notices

Captail dynamically uses and may redistribute selected components from OBS Studio 32.1.2.

## OBS Studio / libobs

- Project: https://github.com/obsproject/obs-studio
- Source release: https://github.com/obsproject/obs-studio/tree/32.1.2
- License: GNU General Public License, version 2 or later
- Local license text: `LICENSE`

`tools/AcquireObsRuntime.ps1` prepares the runtime. Distributions contain only components required for libobs, Replay Buffer, Windows capture, WASAPI audio, and supported hardware encoders.

Captail's `native/ProcessAudio` module narrowly adapts the process-loopback
activation, audio format, silent-buffer, timestamp, and packet-delivery portions
of OBS Studio 32.1.2 `plugins/win-wasapi/win-wasapi.cpp`. Window matching,
device capture, OBS UI properties, and other unrelated `win-wasapi`
functionality are not copied. `tools/AcquireObsPluginSdk.ps1` downloads the
pinned OBS 32.1.2 source archive with SHA-256 verification and extracts the
public libobs headers used to compile the module.

## FFmpeg

- Project: https://ffmpeg.org/
- Windows build: https://github.com/BtbN/FFmpeg-Builds
- Build: `n7.1.5-12-g1fdbca85aa`, LGPL shared variant
- License: GNU Lesser General Public License 2.1 or later; optional components retain their own licenses

`tools/AcquireFfmpegRuntime.ps1` downloads a pinned, SHA-256-verified build. Captail uses it for clip metadata, thumbnails, and non-destructive trimming.

## mpv / libmpv

- Project: https://mpv.io/
- Source release: https://github.com/mpv-player/mpv/tree/v0.41.0
- Native Windows package: `Endpne.LibMPV.Windows` 0.41.0
- License: GNU Lesser General Public License 2.1 or later

Captail dynamically loads the replaceable `libmpv-2.dll` for embedded editor playback, hardware decoding, seeking, and local mixing of selected audio tracks. Captail does not bundle or launch the standalone `mpv.exe` player.

## NuGet dependencies

- NAudio — MIT License: https://github.com/naudio/NAudio
- Endpne.LibMPV.Windows — LGPL-2.1-or-later: https://www.nuget.org/packages/Endpne.LibMPV.Windows/0.41.0
- H.NotifyIcon — MIT License: https://github.com/HavenDV/H.NotifyIcon
- System.Drawing.Common — MIT License: https://github.com/dotnet/runtime

Licenses and copyright notices from these projects remain applicable to their respective components.
