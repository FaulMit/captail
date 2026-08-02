# Third-party notices

Captail dynamically uses and may redistribute selected components from OBS Studio 32.1.2.

## OBS Studio / libobs

- Project: https://github.com/obsproject/obs-studio
- Source release: https://github.com/obsproject/obs-studio/tree/32.1.2
- License: GNU General Public License, version 2 or later
- Local license text: `LICENSE`

`tools/AcquireObsRuntime.ps1` prepares the runtime. Distributions contain only components required for libobs, Replay Buffer, Windows capture, WASAPI audio, and supported hardware encoders.

## FFmpeg

- Project: https://ffmpeg.org/
- Windows build: https://github.com/BtbN/FFmpeg-Builds
- Build: `n7.1.5-12-g1fdbca85aa`, LGPL shared variant
- License: GNU Lesser General Public License 2.1 or later; optional components retain their own licenses

`tools/AcquireFfmpegRuntime.ps1` downloads a pinned, SHA-256-verified build. Captail uses it for clip metadata, thumbnails, and non-destructive trimming.

## NuGet dependencies

- NAudio — MIT License: https://github.com/naudio/NAudio
- H.NotifyIcon — MIT License: https://github.com/HavenDV/H.NotifyIcon
- System.Drawing.Common — MIT License: https://github.com/dotnet/runtime

Licenses and copyright notices from these projects remain applicable to their respective components.
