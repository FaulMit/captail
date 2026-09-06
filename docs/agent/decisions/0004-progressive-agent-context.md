# ADR 0004: Agent context uses progressive disclosure

Status: accepted

## Decision

`AGENTS.md` contains only stable rules and routes tasks through
`docs/agent/INDEX.md`. Area documents and ADRs are loaded only when relevant.
`tools/GetAgentContext.ps1` generates bounded live Git/task context.

## Why

Large conversation history and repeated broad repository scans waste tokens and
can preserve stale decisions. Code, focused docs, and executable tests retain
knowledge with less repeated context.

## Consequences

- Do not copy release history or all module details into `AGENTS.md`.
- New tasks start from one area and expand only when blocked.
- Durable decisions receive ADRs; current state comes from Git and the script.
- Context-system changes are verified by `tools/TestAgentContext.ps1`.
