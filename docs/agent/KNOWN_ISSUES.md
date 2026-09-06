# Known constraints and verification gaps

This is a compact engineering checklist, not release history. Verify current
code and open issues before claiming a problem still exists.

## Platform and capture

- Supported target is Windows 10 build 19041+ or Windows 11, x64 only.
- Hardware H.264, HEVC, or AV1 encoding depends on GPU and driver capability.
- Some games and anti-cheat configurations can block OBS Game Capture.
- DRM-protected content may be black.
- Desktop capture cannot create unique frames beyond desktop presentation rate.
- WPF topmost notifications are tested against composed fullscreen windows.
  True exclusive fullscreen can bypass desktop composition and still requires
  real-game verification.

## Hardware validation

- RTX 40/50 paths have public testing.
- Older NVIDIA, AMD, and Intel paths need broader real-world coverage.
- Do not turn capability detection into a compatibility claim without a real
  recording and playback test.

## Audio and media

- Both AAC/MP4 and Opus/MKV expose at most six recording tracks.
- Advanced per-app routing requires Windows process-loopback support and the
  bundled Captail OBS source.
- Preview loads all selected audio tracks through a libmpv mix graph; trim can
  preserve selected tracks or merge them for one-track players.
- Preview sidebar loads every clip from the configured output directory. Keep
  scrolling virtualized when expanding clip-card content.

## Distribution

- Public release binaries are not Authenticode-signed.
- Store and Portable state paths differ; resolve package identity dynamically.
- Store package identity uses four numeric fields ending in `.0`; visible
  Captail version remains three-part.

## Documentation hygiene

- `docs/SCREENSHOTS.md` is authoritative for screenshot truthfulness.
- Never carry temporary branch names, local paths, issue assumptions, or
  certification state into durable architecture docs.
- Update this file only for a durable constraint or an explicitly tracked
  verification gap.
