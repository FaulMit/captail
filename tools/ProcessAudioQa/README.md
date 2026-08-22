# Process audio QA harness

This directory contains development/QA infrastructure for process-audio
capture. It is intentionally excluded from `Captail.sln` and does not change
Captail's configuration, UI, encoders, or recording pipeline.

`ProcessAudioQa` can load either bundled `win-wasapi.dll` sources or Captail's
PID-aware source. In PID-aware mode it takes process snapshots off the QA OBS
thread, reconciles independent roots by executable name on that thread, and
records each root's PID and creation time. It collects float audio blocks,
attach timing and optional OBS bridge logs, then writes a JSON report plus one
mono float WAV file per source. `Target` is a controllable tone generator for
process lifecycle, process-tree, same-name, and windowless tests.
`tone.html` provides a user-gesture-controlled Web Audio tone for a real
Chromium process tree.

Example:

```powershell
dotnet build .\tools\ProcessAudioQa\Target\ProcessAudioQaTarget.csproj -c Release
dotnet build .\tools\ProcessAudioQa\Target\Child\ProcessAudioQaChild.csproj -c Release
dotnet build .\tools\ProcessAudioQa\ProcessAudioQa.csproj -c Release
.\tools\ProcessAudioQa\bin\Release\net9.0-windows10.0.22621.0\win-x64\ProcessAudioQa.exe `
  --source target=ProcessAudioQaTarget.exe --duration 15
```

PID-aware example:

```powershell
.\tools\ProcessAudioQa\bin\Release\net9.0-windows10.0.22621.0\win-x64\ProcessAudioQa.exe `
  --watch-executable ProcessAudioQaTarget.exe `
  --plugin .\native\ProcessAudio\build\Release\captail-process-audio.dll `
  --duration 15
```

`--creation-time-offset 1` deliberately supplies a stale identity and verifies
that the native source refuses to activate or retarget the PID.

All reports, recordings, and OBS configuration are written below
`.qa/process-audio`, which is ignored by Git.

The Debug Captail build also accepts backend-only mux QA arguments. They do
not add product UI or persisted test settings:

```powershell
Captail.exe --qa-codecs --qa-codec=h264 --qa-audio-codec=aac `
  --qa-advanced-audio=ProcessAudioQaTarget.exe:2,ProcessAudioQaChild.exe:5 `
  --qa-record-seconds=10
```

`--qa-advanced-mic-track=N` enables the default microphone on advanced track
`N`. Use `ffprobe` on the resulting MP4/AAC or MKV/Opus replay to verify that
audio encoders are continuous from track 1 through the highest configured
track. Executable names supplied to these QA-only command-line arguments are
not written by the product process-audio diagnostics.
