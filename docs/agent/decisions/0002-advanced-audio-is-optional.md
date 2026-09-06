# ADR 0002: Advanced audio routing is optional

Status: accepted

## Decision

Simple device audio remains default. Advanced routing is an explicit third
audio-track mode backed by Captail's process-loopback OBS source. UI and config
remain usable when advanced capability is unavailable.

## Why

Per-application routing is powerful but depends on Windows capability, process
topology, and a bundled native source. It must not make basic replay unreliable.

## Consequences

- Capability detection gates advanced mode.
- Routes are persisted by executable and reconciled across process restarts.
- Container/codec capability determines available tracks; current maximum is six.
- Failure is explicit and does not silently change what gets recorded.
