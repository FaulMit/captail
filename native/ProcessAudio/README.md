# Captail process audio source

This module implements the private `captail_process_audio_capture` libobs input
source used by Captail's PID-aware audio foundation. It accepts only a target
process ID and the target's Windows process creation time. Discovery, process
tree selection, routing policy, and persistence remain outside the module.

The process-loopback activation, libobs audio format construction, silent
buffer handling, timestamps, and packet delivery are narrowly adapted from:

- OBS Studio 32.1.2
- `plugins/win-wasapi/win-wasapi.cpp`
- https://github.com/obsproject/obs-studio/tree/32.1.2

Window matching, device capture, default-device notification, UI properties,
localization, rerouting, and executable-name logging were intentionally not
copied. OBS Studio and this adaptation are licensed under GNU GPL version 2 or
later. Captail's repository `LICENSE` contains the applicable GPL text.

The build generates a minimal `obs.lib` from `Obs.def`, containing only the
libobs exports used by this source. It links against Captail's existing bundled
`obs.dll`; libobs and `win-wasapi` are not rebuilt or replaced.

A catch-all "everything else" route is intentionally not implemented. Mixing
the system loopback with routed process sources would record routed applications
twice. A safe catch-all needs an exclusion-capable mixer before it can be added.
