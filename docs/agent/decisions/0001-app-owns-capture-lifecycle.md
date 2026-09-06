# ADR 0001: App owns capture lifecycle

Status: accepted

## Decision

`App.xaml.cs` owns capture start, stop, restart, save coordination, and recovery.
`ObsReplayEngine` owns one libobs graph but does not decide application policy.
OBS graph operations are serialized through the dedicated scheduler/gate.

## Why

Capture actions arrive from UI, tray, hotkeys, startup, recovery, and Store
lifecycle events. One orchestrator prevents parallel disposal/restart races and
keeps UI state consistent.

## Consequences

- UI windows request actions through delegates.
- Engine code exposes state and bounded operations, not global app behavior.
- New capture modes extend the existing lifecycle rather than creating a second
  independent OBS owner.
