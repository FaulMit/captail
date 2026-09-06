# Audio area

## Modes

Simple mixed mode captures device loopback and optional microphone.
Separate mode uses track 1 for system audio excluding the detected game root,
track 2 for that game process tree, and track 3 for the enabled microphone.
Without a microphone, only tracks 1 and 2 are encoded. Without system capture,
microphone-only capture uses track 1.

Separate mode requires the process-loopback capability and captures across
output devices. With no detected game it excludes Captail itself and keeps
track 2 silent. For multiple independent instances of the detected executable,
the oldest root is used consistently for inclusion and exclusion; other
instances remain on the system track. PID creation time protects restarts.

In explicit Game Capture, mixed mode routes the executable detected by the
video hook through process loopback. This follows games that use a non-default
Windows output device. If process loopback is unavailable, Captail keeps the
device-loopback fallback.

Advanced mode maps executable roots to recording tracks. Selected processes,
currently audible processes, and other processes are presented separately in
the routing UI. Microphone receives its own configurable track assignment.

AAC uses fragmented MP4; Opus uses MKV. Current format capability model allows
up to six tracks for both.

## Flow and seams

- `Config`: persistent mode, devices, levels, routes, and microphone track.
- `AudioDevices`: endpoint enumeration.
- `ProcessAudioSessionMonitor`: current sessions and live levels.
- `ProcessTopology`: executable roots and child-process grouping.
- `ProcessAudioReconciler`: desired routes versus active OBS sources.
- `ProcessAudioMonitor`: health/recovery cadence.
- `ProcessAudioRoutingWindow`: selection and track UI.
- `native/ProcessAudio`: Captail OBS process-loopback source.
- `ObsReplayEngine`: mixers, track names, and source ownership.

## Invariants

- Normalize routes by executable; duplicate valid routes use one deterministic
  assignment.
- Unchecked applications are not recorded in advanced mode.
- Process restarts should reconnect without rebuilding unrelated capture state.
- Track names describe actual sources and remain short enough for containers.
- Track count follows highest assigned application/microphone track, up to six.
- Unsupported Windows/source states fall back to an explicit unavailable state,
  never silent mixed capture.

## Validation

```powershell
.\tools\TestIssue41Fixes.ps1
.\tools\TestIssue41Followup.ps1
dotnet build .\Captail.sln -c Release --no-restore
```

For native routing changes, also run `tools/ProcessAudioFoundationQa` and the
appropriate `--qa-advanced-audio` recording case described in its README.
