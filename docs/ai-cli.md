# Driving Takatora from the CLI (for AI agents)

This guide is for an AI assistant operating an **existing** Takatora project
from the command line: discover what flows/tasks exist, inspect them, run a
flow, and read the results. (Authoring new flows and custom tasks is covered
separately.) Every command supports machine-readable output where it matters —
prefer `--output-format json` (or `describe`, which is always JSON) so you can
parse rather than scrape.

The CLI is `takatora` (the published exe) or, from a source checkout,
`dotnet run --project src/Takatora.Cli --`. Examples below use `takatora`.

> **Using Claude Code?** This guide also ships as a self-contained skill at
> `.claude/skills/takatora-run/` — active automatically inside the Takatora
> repo. To use it elsewhere, copy that folder into your project's
> `.claude/skills/` (or `~/.claude/skills/` for all your projects).

## Project resolution

Anywhere a command takes a `<project>` / `<path>`, you can pass **either**:

- a **registered name** (from `takatora project list`), or
- a **path** to a working directory that contains a `.takatora/` folder.

So `takatora run my-game smoke` and `takatora run ./path/to/game smoke` are
equivalent if `my-game` is registered at that path.

## 1. Inspect before you run

**List registered projects:**

```
takatora project list
takatora project info <name>     # registration + project.toml/flows.toml summary
```

**Validate + summarize a project's flows** (flows, step counts, var counts):

```
takatora validate <path>
```
```
sample-game (unreal): valid
  working_dir: .
  flows: 4
    - smoke — Smoke test (notify only) (1 step(s), 1 var(s))
    - package — Windows package — built-in tasks (4 step(s), 1 var(s))
    - package-custom — Windows package — custom-task stamp (3 step(s), 1 var(s))
    - clean — Clean (intermediate + binaries) (1 step(s), 0 var(s))
```

**Inspect a task's contract** (params + outputs) as JSON. Pass `--project` so
project-local (`.takatora/tasks`) and user task overrides resolve too:

```
takatora describe <task-type> --project <path>
```
```json
{
  "type": "notify.console",
  "params": [ { "name": "message", "kind": "string", "required": true } ],
  "outputs": []
}
```
`params[].kind` is the value kind (`string` / `int` / `float` / `bool` /
`enum` / `path` / `file` / `dir` / `multiline` / `secret` / `list`); enums also
carry their allowed `values`. `outputs` is the list of output names a step of
this type records (referenceable downstream as
`${steps.<id>.outputs.<name>}`).

**See a flow's resolved plan without executing anything** — this is the safe
way to understand what a flow *would* do (effective vars + ordered steps,
including which engine resolves):

```
takatora run <path> <flow> --dry-run
```
```
Flow: package
Project: sample-game
Engine: unreal 5.7.4 — C:\Program Files\Epic Games\UE_5.7

Vars (effective):
    configuration   = "Development"

Steps to execute:
  1. git (git.info)
  2. stamp (fs.write)
  3. build (ue.build_cook_run)
  4. zip (fs.zip)
```

## 2. Run a flow

```
takatora run <project> <flow> [--var KEY=VALUE]... [--output-format json]
```

- `--var KEY=VALUE` overrides a flow variable; repeatable. Names and kinds come
  from `describe` / `--dry-run` / `validate`.
- `--output-format json` prints a structured result you can parse:

```json
{
  "run_id": "r-2026060617-2327-3924",
  "flow_id": "smoke",
  "result": "success",
  "started_at": "2026-06-06T17:23:27.82+00:00",
  "finished_at": "2026-06-06T17:23:28.87+00:00",
  "duration_sec": 1.06,
  "run_dir": ".../.takatora/runs/r-2026060617-2327-3924",
  "steps": [
    { "id": "notify.console-1", "type": "notify.console",
      "status": "success", "duration_sec": 1.03, "outputs": {} }
  ]
}
```

Check `result` (`success` / `failure` / ...) and each `steps[].status`; the
process exit code reflects success/failure too. `run_dir` is where this run's
logs and outputs live (see below).

## 3. Read results

**List past runs** (newest first). Filter by flow, cap with `--limit`:

```
takatora history <project> [--flow <id>] [--limit N] [--output-format json]
```
```json
{ "runs": [ {
  "run_id": "r-2026060617-2327-3924", "flow_id": "smoke",
  "result": "success", "trigger": "cli",
  "duration_sec": 1.058, "run_dir": ".../.takatora/runs/r-..."
} ] }
```
`trigger` tells you how the run was started (e.g. `cli`).

**Show one run** in detail (adds the params it ran with + per-step summary):

```
takatora show-run <project> <run-id> [--output-format json]
```
```json
{
  "run_id": "r-...", "flow_id": "smoke", "result": "success",
  "params": { "message": "hello from takatora" },
  "step_summary": [ { "id": "notify.console-1", "type": "notify.console",
                      "status": "success", "duration_sec": 1.033,
                      "outputs": {} } ]
}
```

Each step carries an `outputs` object (empty when it recorded none) with the
recorded values typed as written — e.g. a packaging step's
`"outputs": { "archive_path": "…/Windows", "size": 12345 }`. This is the
programmatic hook for grabbing a run's artifacts from automation.

**Re-run with the exact params of a prior run** (secrets excluded — they're
never stored):

```
takatora replay-run <project> <run-id>
```

**On-disk artifacts.** Each run writes to `run_dir`
(`<project>/.takatora/runs/<run-id>/`):

- `log.txt` — full combined log
- `events.ndjson` — one JSON event per line (step start/end, run end) — best
  for parsing progress/results line by line
- `manifest.toml` — the run's recorded metadata + params
- `outputs/` — per-step output values

## 4. Engines

```
takatora detect-engines [--format json]
```
Lists detected UE / Unity / Godot installs. Useful before running a flow that
needs an engine — if the flow's engine isn't found, the run will fail at the
engine step.

## Known limitation: sandboxed hosts (MSIX)

If you are running Takatora from **inside a packaged / MSIX-sandboxed host**
(e.g. an AI tool installed from the Microsoft Store), engine builds can fail
in a confusing way: spawned engine tools like Unreal's **UAT write logs under
`%APPDATA%` / `%LOCALAPPDATA%`, and the sandbox blocks or virtualizes those
writes**, so the tool exits non-zero before doing useful work. Symptom: a
`ue.*` step fails early with an AppData/log write or path error.

The reliable fix is to **run the build outside the sandbox** (e.g. approve
external execution of the same Takatora command). This mostly affects engine
builds; lightweight flows (shell, fs.*, notify) are usually unaffected. It's a
host-sandbox limitation, not a Takatora or project misconfiguration.

## Quick recipe for an agent

1. `takatora project list` → find the project (or use a path).
2. `takatora validate <p>` → see the flows + their var counts.
3. `takatora run <p> <flow> --dry-run` → confirm the plan + effective vars.
4. `takatora describe <task> --project <p>` → check a step's params/outputs if
   you need to override a var.
5. `takatora run <p> <flow> --var k=v --output-format json` → run; parse
   `result` + `steps`.
6. On failure, read `run_dir/log.txt` (or `events.ndjson`), or
   `takatora show-run <p> <run-id> --output-format json`.
