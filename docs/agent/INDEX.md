# Captail agent context index

Use this page to route work. Read one area document, not the whole set.

## Task router

| Area | Use for | Read | Primary validation |
| --- | --- | --- | --- |
| `overview` | unfamiliar task, cross-module change | `CURRENT.md`, `ARCHITECTURE.md`, `KNOWN_ISSUES.md` | Release build |
| `capture` | replay/recording lifecycle, desktop/game capture, encoder behavior | `CAPTURE.md`, ADR 0001 | capture/player and issue regressions |
| `audio` | devices, mixed/separate tracks, per-app routing | `AUDIO.md`, ADR 0002 | issue and process-audio QA |
| `player` | preview, recent clips, fullscreen, trim, tracks | `PLAYER.md` | capture/player regressions |
| `ui` | dashboard, settings, localization, theme, overlays | `UI.md` | UI/layout regressions |
| `distribution` | config paths, Portable/Setup/Store behavior, updates | `DISTRIBUTION.md`, ADR 0003 | Store lifecycle/package tests |
| `release` | GitHub release, screenshots, Microsoft Store | `WORKFLOWS.md`, ADR 0003 | release/store validators |

Generate a bounded live snapshot:

```powershell
.\tools\GetAgentContext.ps1 -Area player
```

Use `-NoDocumentText` when the area document is already present in the current
task; the command then returns only live state, routes, and validation.

Valid areas: `overview`, `capture`, `audio`, `player`, `ui`, `distribution`,
and `release`.

## Source of truth order

1. Current code and executable tests.
2. Area document and accepted ADRs.
3. Public product/release documentation.
4. Conversation history.

When sources disagree, verify behavior and update stale documentation with the
same change. Never treat generated artifacts or an old test installation as
source truth.

## Stable external docs

- Product behavior: `README.md`
- Release procedure: `docs/RELEASING.md`
- Release-note policy: `docs/RELEASE_NOTES.md`
- Screenshot policy: `docs/SCREENSHOTS.md`
- Microsoft Store submission: `docs/MICROSOFT-STORE-RELEASES.md`
- Package identity: `packaging/msix/README.md`
- Store listing data: `store-listing/listing.json`

## Maintenance rule

Keep area documents compact. Add durable architectural choices as ADRs. Put
user-visible history in `CHANGELOG.md`, not here. Run
`./tools/TestAgentContext.ps1` after editing this context system.
