# Quick panel and capture shutdown QA

Run from the repository root with the pinned .NET SDK on `PATH` and `DOTNET_ROOT`.

```powershell
dotnet run --project tools/QuickPanelQa -c Release
dotnet run --project tools/QuickPanelQa -c Release -- --hotkeys
dotnet run --project tools/QuickPanelQa -c Release -- --preview --ru
dotnet run --project tools/QuickPanelQa -c Release -- --capture --wgc-release-twice
dotnet run --project tools/QuickPanelQa -c Release -- --capture --user-config --toggle-only
dotnet run --project tools/QuickPanelQa -c Release -- --capture --user-config --source-cycle
```

- Default: real WPF toggle/save click handlers, successful start/stop, rejected
  start/stop, restored control state, and absence/presence of error dialogs.
  Delegates model App's contract: toggle returns resulting active state.
  Only dialogs belonging to this QA thread are automatically dismissed.
  Recording uses the actual start/stop action button; replay retains its toggle.
  `--preview` renders the capture card in both recording states with a violet
  accent under `.qa/recording-action`; `--ru` uses the Russian dictionary.
- Hotkeys: real production routing for ordinary combinations (including the
  reported Ctrl+Shift+F) without WM_HOTKEY delivery, deferred UI work, repeat and
  late-message suppression, modifier matching, rebind/disposal cancellation,
  and F13-F24 compatibility. Does not inject input into a running game or prove
  compatibility with a game's actual exclusive-fullscreen input handling.
- Repeated release: requires initialized WGC capture, releases its resources
  twice on the OBS graphics thread, then shuts the engine down normally.
  The original OBS DLL faults on its second release.
- Toggle stress: twelve starts/stops per mode on an MTA worker, with delays from
  zero to 500 ms. Rejects leaked/double-destroyed capture sources and a retained
  Windows capture DLL after shutdown. Old scene ownership fails this assertion
  on the first stop; old module retention also permits native worker crashes.
- Save/source cycle: five saves per mode, three synthetic game/desktop graph
  transitions per save, followed by shutdown and decoded non-black desktop-frame
  verification. This exercises graph ownership without a real game hook; it does
  not establish compatibility with DOOM or another actual game.

Native runs capture the desktop and use `.qa/overlay-save/capture` for output.
`--user-config` clones current settings, including enabled audio devices, without
saving configuration changes. Without it, use H.264 at 720p/30 FPS with audio
devices disabled. Application diagnostics remain in the ordinary local log.
