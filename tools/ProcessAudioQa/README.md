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
.\tools\ProcessAudioQa\bin\Release\net10.0-windows10.0.22621.0\win-x64\ProcessAudioQa.exe `
  --source target=ProcessAudioQaTarget.exe --duration 15
```

PID-aware example:

```powershell
.\tools\ProcessAudioQa\bin\Release\net10.0-windows10.0.22621.0\win-x64\ProcessAudioQa.exe `
  --watch-executable ProcessAudioQaTarget.exe `
  --plugin .\native\ProcessAudio\build\Release\captail-process-audio.dll `
  --duration 15
```

`--creation-time-offset 1` deliberately supplies a stale identity and verifies
that the native source refuses to activate or retarget the PID.

`--exclude-target true` verifies the complementary system capture used by
Separate mode. Run two independent Target/Child executables at distinct
frequencies (for example 3137 and 6173 Hz), watch only Target, and compare
include/exclude reports in different output directories. Include must retain
the watched tone; exclude must retain the other tone and suppress the watched
tone. This validates native audio separation independently of game hooking.

All reports, recordings, and OBS configuration are written below
`.qa/process-audio`, which is ignored by Git.

`--separate-game executable.exe` exercises the same automatic mixer routing
planner and identity-qualified reconciliation as Separate mode. Each probe is
labelled `game-<PID>-N` or `system-<PID>-N`; ordinary output uses a
`system-global-N` exclusion probe. Pass the native plugin with `--plugin`.
Use independent known tones for the game and another application, both routed
through Voicemeeter. Assert that the game tone remains on Game and the other
tone remains on System without the game tone. This harness does not alter
Windows device routing or Captail configuration.

`ProcessAudioFoundationQa --separate-capture=<directory>` additionally exercises
the production three-track recording graph, gains, encoders, muxer and teardown.
It uses `Issue53AudioSignal.exe` as the detected game fixture, independently of
game hook recognition. Check the resulting `capture.json` path with FFprobe and
decode each track. Real-game recognition and NGENUITY still need separate QA.

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
