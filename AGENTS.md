# Captail agent guide

This file is a router, not a project encyclopedia. Keep it short.

## Start here

1. Read `docs/agent/INDEX.md`.
2. Select one task area.
3. Read only documents listed for that area.
4. Run `./tools/GetAgentContext.ps1 -Area <area>` for current Git state,
   relevant paths, and validation commands.

Do not preload every file under `docs/agent`.

## Project rules

- Captail is a Windows x64 WPF/.NET 9 application with native OBS components.
- Use PowerShell commands. Prefer `rg` for search and `apply_patch` for edits.
- Preserve user changes in a dirty worktree. Never reset or rewrite unrelated work.
- Public repository text, code comments, diagnostics, and release metadata use
  English. Russian belongs only in `Strings.ru.xaml`.
- Keep existing architecture unless the task requires a seam change. Do not
  rewrite working modules to make a small fix.
- Read `artifacts/`, `runtime/`, `bin/`, `obj/`, or `.qa/` only when the task
  explicitly concerns generated output or runtime dependencies.
- Avoid unbounded `git diff`, recursive file dumps, and broad command output.
  Filter by task paths and cap output.

## Action boundaries

- Explain, review, or diagnose: inspect and report; do not implement silently.
- Change, build, or fix: make scoped local changes and run relevant validation.
- Do not push, publish, create releases, submit Store packages, or comment on
  issues/PRs unless the user explicitly requests that external action.
- Do not change version numbers or release notes during ordinary feature work.

## Baseline validation

```powershell
dotnet build .\Captail.sln -c Release --no-restore
```

Run area-specific tests from `docs/agent/WORKFLOWS.md`. When the user requests
an installed test build, deploy the ordinary Portable build with:

```powershell
.\tools\DeployTestBuild.ps1 -Version 0.1.10
```

Target must remain `D:\Captail-0.1.10`; this is not a Microsoft Store build.
