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

## Discord Quest Completer game catalog

- Project: https://github.com/markterence/discord-quest-completer
- Source revision: `53191138e647304dc7dce6a6ace77ae19f8309c3`
- Source file: `src/assets/gamelist.json`
- License: MIT License
- Copyright: Mark Terence Tiglao, 2025

Captail embeds a reduced Windows executable-to-game-name index generated from
this catalog. `tools/UpdateGameCatalog.ps1` filters launcher and non-Windows
entries and records the exact source revision in the generated asset.

MIT License

Copyright (c) 2025 Mark Terence Tiglao

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
