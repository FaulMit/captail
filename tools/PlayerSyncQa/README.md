# Native player seek synchronization QA

Runs the production `MpvHost` source in a standalone WPF host against the supplied
libmpv runtime. Audio remains enabled at zero volume. Four forward/backward seeks
use the pause/seek/resume sequence used by timeline and progress controls.

```powershell
dotnet run --project tools/PlayerSyncQa -- "Z:\Demos\Replay_2026-09-26_03-21-51.mp4" "D:\Captail-0.1.10\libmpv-2.dll"
```

Use a clip of at least eight seconds. Default selects three audio tracks; append
`single` for a clip with one track. Exit code 1 means synchronization failed;
2 means setup failed. After a one-second settling period, each seek must provide
at least five samples with both native `avsync` and the audio/video playback clock
difference below 100 ms. Missing playback clocks or a stalled seek fail the test.

The original 240 FPS AV1 replay reproduced 0.12–0.51 seconds of video lag after
seeks with software decoding. Retrying hardware decoding after the first valid
video output restored D3D11 decoding and passed the same seeks. This test checks
native playback timing; physical display/audio latency still requires listening
and watching on the affected output devices.
