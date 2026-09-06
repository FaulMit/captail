# ADR 0003: Release channels are independent

Status: accepted

## Decision

GitHub Portable/Setup releases and Microsoft Store releases are separate build,
identity, update, and publishing channels.

## Why

Microsoft signs, installs, and updates MSIX packages through Partner Center.
GitHub assets use Captail's ordinary updater and must remain self-contained.
Combining the channels caused identity and submission mistakes.

## Consequences

- Store packages are never attached to GitHub Releases.
- Store internal identity may advance independently while visible semantic
  version stays unchanged.
- Listing synchronization and certification are Store-only external actions.
- Both channels share source and user-facing release intent, not package state.
