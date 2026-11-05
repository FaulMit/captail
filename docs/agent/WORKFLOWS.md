# Development and release workflows

Commands run from repository root in PowerShell.

## Build

```powershell
dotnet restore .\src\Captail\Captail.csproj --locked-mode
dotnet build .\Captail.sln -c Release --no-restore
```

Do not restore on every edit when the lock file and SDK are unchanged.

## Validation by area

| Area | Commands |
| --- | --- |
| Capture | `TestCapturePlayerRegression.ps1`, `TestIssue41Fixes.ps1`, `TestFullscreenOverlayHotkeys.ps1` |
| Audio | `TestIssue41Fixes.ps1`, `TestIssue41Followup.ps1` plus relevant process-audio QA |
| Player | `TestCapturePlayerRegression.ps1`, `TestNextFeatureRegression.ps1` |
| UI | `TestReportedUiRuntimeRegressions.ps1`, `TestStartupUiSurfaces.ps1`, `TestLocalizedLayout.ps1` |
| Distribution | Store isolation tests and `TestStoreReleaseWorkflow.ps1` |
| Context docs | `TestAgentContext.ps1` |
| Diagnostics | `dotnet run --project .\tools\DiagnosticsQa -c Release`; capture saves also require disk runtime QA |

Run scripts as `./tools/<name>`. Runtime `--qa-*` entry points live in
`App.xaml.cs`; use them when a static contract cannot prove behavior.

For monitor hotplug or dual-mode changes, run `TestDisplayTopology.ps1`. This
injects topology changes into the production engine while using real OBS desktop
capture, checks replay/Auto and recording/WGC, decodes saved frames, and verifies
notification placement. It does not switch physical display modes; specific
monitor/driver acceptance still requires that hardware.

## Installed test build

Only when the user asks to test the current local work:

```powershell
.\tools\DeployTestBuild.ps1 -Version 0.1.10
```

This safely replaces `D:\Captail-0.1.10` with a Portable build and validates
required runtime files. It does not publish or modify Microsoft Store state.

## GitHub release

Follow `docs/RELEASING.md`. Required before dispatch:

- version and dated changelog section;
- generated release notes preview;
- preserve the existing README video and do not add application screenshots;
- validate changed workflows on the release candidate;
- local build/package validation.

GitHub release binaries come from the pinned Actions workflow. Never replace
assets under an existing tag.

## Microsoft Store

Follow `docs/MICROSOFT-STORE-RELEASES.md` and `packaging/msix/README.md`.
Store release is separate from GitHub release. Normal submissions synchronize
EN/RU listing text and screenshots from `store-listing/`.

Do not call Partner Center, submit certification, or alter a draft without an
explicit user request.

## Screenshots

The README uses a showcase video. Preserve it during releases and do not add
application screenshots. Store listing images are maintained separately;
`docs/SCREENSHOTS.md` describes the tooling when new images are requested.
