# Display topology regression QA

Run from the repository root on Windows x64:

```powershell
.\tools\TestDisplayTopology.ps1 -Dotnet <path-to-dotnet-10>
```

The harness uses the production `ObsReplayEngine` with an injected monitor
enumerator. It simulates retired monitor IDs, new handles and resolutions,
enumeration reordering, temporary empty topology, and switching back. Real OBS
captures a live desktop, keeps the original output and canvas, saves replay and
continuous-recording files, and FFmpeg verifies decoded frames contain pixels.
A small green fixture window appears during capture. Notification QA substitutes
an invalid monitor handle and checks recovery and on-screen placement.

The script exercises replay with explicit DXGI after failed borderless preflight
and continuous recording with WGC. It checks the native source method before and
after monitor replacement so OBS Auto cannot silently reselect WGC in fallback.
Results are saved in `.qa/issue-51/`. No physical display mode is changed.
Actual ASUS XG32UCWMG 4K/240 Hz to 1080p/480 Hz switching remains a hardware
acceptance check; simulated enumeration cannot prove driver-specific timing.

`dotnet run --project tools/DisplayTopologyQa -c Release -- --startup-permissions-only`
checks fresh replay defaults, saved preference loading, and foreground permission
preflight with replay disabled. It calls the application's startup preparation
method without creating OBS or changing the user's configuration. Background and
UI-only launches must not prompt for capture permission.
