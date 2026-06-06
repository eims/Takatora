---
name: takatora-run
description: Run, inspect, and debug Takatora CI flows from the command line. Use when the user wants to run a Takatora flow, list or validate flows, inspect a task's params/outputs, dry-run a plan, override flow variables, or read run history/results for a project that has a `.takatora/` directory (Takatora is a local CI for game builds — UE/Unity/Godot).
---

# Operating Takatora from the CLI

You are driving an existing Takatora project. Prefer machine-readable output
(`--output-format json`, or `describe` which is always JSON) so you parse
rather than scrape.

The CLI is `takatora`. If it's vendored into a project it may be at e.g.
`Tools/Takatora/Takatora.Cli.exe`; from a Takatora source checkout use
`dotnet run --project src/Takatora.Cli --`. Substitute accordingly.

A `<project>` argument is **either** a registered name (`takatora project list`)
**or** a path to a working dir containing `.takatora/`.

## Inspect before running

- `takatora project list` / `takatora project info <name>` — registry + summary.
- `takatora validate <path>` — lists flows with their step and var counts.
- `takatora describe <task-type> --project <path>` — a task's params + outputs
  as JSON. `params[]` = `{name, kind, required, default?}` (kind ∈ string / int
  / float / bool / enum(+values) / path / file / dir / multiline / secret /
  list). `outputs` = output names, referenced downstream as
  `${steps.<id>.outputs.<name>}`. **Always describe a task before overriding
  its vars** so you use real names/kinds.
- `takatora run <path> <flow> --dry-run` — resolve effective vars + ordered
  steps + the engine, and print the plan **without executing**. Do this to
  understand a flow before running it.

## Run

```
takatora run <project> <flow> [--var KEY=VALUE]... [--output-format json]
```

- `--var KEY=VALUE` overrides a flow variable (repeatable).
- With `--output-format json` you get: `run_id`, `flow_id`, `result`
  (`success`/`failure`/...), `started_at`, `finished_at`, `duration_sec`,
  `run_dir`, and `steps[]` = `{id, type, status, duration_sec, outputs}`.
- Check `result` and each `steps[].status`; the process exit code also
  reflects success/failure.

## Read results

- `takatora history <project> [--flow <id>] [--limit N] [--output-format json]`
  — past runs, newest first. Each entry includes `result` and `trigger`
  (e.g. `cli`).
- `takatora show-run <project> <run-id> [--output-format json]` — one run in
  detail: the `params` it ran with + per-step summary.
- `takatora replay-run <project> <run-id>` — re-run with a prior run's exact
  params (secrets excluded).
- On-disk under `run_dir` = `<project>/.takatora/runs/<run-id>/`: `log.txt` (full
  log), `events.ndjson` (one JSON event per line — best for parsing
  step/run progress), `manifest.toml` (metadata + params), `outputs/`.

## Engines

`takatora detect-engines [--format json]` — detected UE/Unity/Godot installs.
If a flow's engine isn't found, the run fails at the engine step.

## Recipe

1. `project list` → find the project (or use a path).
2. `validate <p>` → see the flows + var counts.
3. `run <p> <flow> --dry-run` → confirm the plan + effective vars.
4. `describe <task> --project <p>` → check params before overriding a var.
5. `run <p> <flow> --var k=v --output-format json` → run; parse `result` +
   `steps`.
6. On failure: read `run_dir/log.txt` (or `events.ndjson`), or
   `show-run <p> <run-id> --output-format json`.

## Note: sandboxed hosts (MSIX)

If you're running inside a packaged/MSIX-sandboxed host (e.g. a Store-installed
AI tool), engine builds may fail because spawned tools like Unreal's UAT can't
write logs under `%APPDATA%`/`%LOCALAPPDATA%` (the sandbox blocks/virtualizes
those writes) — a `ue.*` step fails early with an AppData/log path error. Fix:
**run the build outside the sandbox** (approve external execution). Lightweight
flows (shell, fs.*, notify) are usually fine. This is a host limitation, not a
Takatora/config problem.

(Fuller human-facing docs, if this skill ships inside the Takatora repo:
`docs/ai-cli.md`.)
