# Run record schema (v1)

Every flow run Takatora executes leaves a **run record** on disk under the
project's `.takatora/runs/`. This is the durable, machine-readable history that
`takatora history` / `show-run`, the GUI's Run list/detail, and `replay-run`
all read back. This document is the **frozen contract** for that format and for
the CLI's `--output-format json` shapes built on top of it.

> Why freeze it now: history has to stay readable across upgrades. Pinning the
> layout — and making the reader tolerant of older/newer variants — is cheap
> insurance against a painful migration once test history (0.2.0) lands.

## Versioning

`schema_version` identifies the on-disk format. The current version is **1**
(`Version.RunSchemaVersion` in `src/Takatora.Core/Library.fs`). It is stamped
into every new `manifest.toml` and into the `run.start` line of
`events.ndjson`.

**Reader contract** (`RunHistory` in `src/Takatora.Core/History.fs`):

- **Missing `schema_version` → treat as v1.** Runs written before the field
  existed are v1-shaped; they must keep loading.
- **Unknown fields are ignored**; missing optional fields fall back to a
  default. A field's *meaning* never changes within a version — new
  information goes in a new field (or a version bump), never by repurposing an
  existing one.
- **A manifest that fails to parse is skipped, not fatal** — one broken run dir
  must never break the whole history list.
- A **higher** `schema_version` than the reader knows is still parsed
  best-effort on the v1-compatible fields. Bump the version only for a change
  that a v1 reader would *misread*, not merely one it can ignore.

## Run directory layout

```
.takatora/runs/<run-id>/
├── manifest.toml          # summary; written ONCE when the run finishes
├── events.ndjson          # append-only timeline; written live during the run
├── log.txt                # combined stdout+stderr of every step subprocess
└── outputs/
    └── <step-id>.ndjson   # one step's recorded outputs (name/value per line)
```

`<run-id>` is the run id (e.g. `r-2026051010-0100-aaaa`) and is the directory
name. A run **in progress** has `events.ndjson` + `log.txt` but **no
`manifest.toml`** — the manifest is written only on completion, so its presence
is the "this run finished" marker that `history`/`show-run` rely on.

## `manifest.toml`

The history-facing summary. Hand-written TOML (basic strings; backslashes and
quotes escaped — Windows paths are common in params). Secret var values are
replaced with a mask before writing; plaintext secrets never reach disk.

Top-level keys:

| Key | Type | Notes |
|---|---|---|
| `schema_version` | int | On-disk format version. **v1** today. Absent in pre-versioning runs (read as 1). |
| `flow_id` | string | The flow that ran. |
| `run_id` | string | Matches the directory name. |
| `started_at` | string | ISO-8601 (`o` round-trip, UTC). |
| `finished_at` | string | ISO-8601. |
| `trigger` | string | How the run was started. Currently always `"cli"`. Reserved values: `"gui"`, `"watch"`, `"schedule"`. |
| `result` | string | `"success"` \| `"failure"` \| `"cancelled"`. |
| `duration_sec` | float | Wall-clock seconds. |
| `project_name` | string | Project the run belongs to. |

`[params]` — a table of the run's effective var values (after `--var`
overrides), TOML-typed (string/int/float/bool/array). Secret vars appear masked.
Absent when the flow has no vars.

`[[step_summary]]` — one table per step, in execution order:

| Key | Type | Notes |
|---|---|---|
| `id` | string | Step id (explicit, or `<type>-<index>`). |
| `type` | string | Task type. |
| `status` | string | `"success"` \| `"failure"` \| `"skipped"` \| `"cancelled"`. |
| `duration_sec` | float | |
| `message` | string | Present only on `failure`. |
| `reason` | string | Present only on `skipped`. |

## `events.ndjson`

Append-only, one JSON object per line, written live as the run progresses (the
GUI tails this for status; `log.txt` for log text). Every line has the same
envelope:

```json
{ "ts": "<ISO-8601 UTC>", "kind": "<kind>", ... }
```

Event kinds and their extra fields:

| `kind` | Extra fields |
|---|---|
| `run.start` | `schema_version` (int), `run_id`, `flow_id`, `project` |
| `run.end` | `run_id`, `status` (`success`\|`failure`\|`cancelled`), `duration_sec` |
| `step.start` | `step_id`, `type`, `step_index`, `task_path` |
| `step.end` | `step_id`, `type`, `status`, `duration_sec`, `exit_code` |
| `step.skip` | `step_id`, `type`, `reason` |
| `step.cancel` | `step_id`, `type` |
| `mutex.wait` | `step_id`, `mutex` (engine mutex name) |
| `mutex.acquired` | `step_id`, `mutex` |

> **Known quirk (frozen in v1):** `step.end`'s `status` uses `"fail"`, whereas
> `manifest.toml` and the CLI JSON use `"failure"`. Consumers reading
> `events.ndjson` should accept `"fail"`. This is left as-is in v1; a future
> version may normalize it (and would bump `schema_version`).

`events.ndjson` is the **audit trail**; `manifest.toml` is the **summary**.
They overlap deliberately — the manifest can be rebuilt conceptually from the
events, but is written once so the common "list past runs" path is a single
small read per run.

## `log.txt`

Plain text: the interleaved stdout + stderr of every step's `.fsx` subprocess,
line-buffered. This is the human log shown in the GUI and by tooling. SDK
`Log.*` calls go to `events.ndjson`, **not** here; `log.txt` is purely the
task subprocess's console output (which includes `Progress`/`Zip` heartbeats,
since those write to stdout).

## `outputs/<step-id>.ndjson`

One file per step that recorded outputs. Each line:

```json
{ "name": "<output-name>", "value": <json-value> }
```

Read back into `${steps.<id>.outputs.<name>}` for downstream steps, and surfaced
by the GUI (e.g. a package's `archive_path` becomes an "open" link). `value`
may be any JSON scalar/array; tooling that needs a string renders it.

## CLI `--output-format json`

The machine-readable command outputs are part of this contract (the AI-CLI
guide and any automation depend on them). All carry `schema_version`.

**`takatora run … --output-format json`** (also describes a just-finished run):

```json
{
  "schema_version": 1,
  "run_id": "…", "flow_id": "…",
  "result": "success",
  "started_at": "…", "finished_at": "…", "duration_sec": 12.3,
  "run_dir": "…/.takatora/runs/…",
  "steps": [
    { "id": "…", "type": "…", "status": "success", "duration_sec": 1.2,
      "message": "…", "reason": "…", "outputs": { "name": value } }
  ]
}
```

`message`/`reason` appear only on failure/skip respectively.

**`takatora history … --output-format json`**:

```json
{ "runs": [
  { "schema_version": 1, "run_id": "…", "flow_id": "…",
    "started_at": "…", "finished_at": "…", "duration_sec": 1.5,
    "result": "success", "trigger": "cli", "run_dir": "…" }
] }
```

`finished_at` is omitted for a run with no recorded finish.

**`takatora show-run … --output-format json`**:

```json
{
  "schema_version": 1,
  "run_id": "…", "flow_id": "…", "result": "success", "trigger": "cli",
  "started_at": "…", "finished_at": "…", "duration_sec": 1.5,
  "run_dir": "…",
  "params": { "name": value },
  "step_summary": [
    { "id": "…", "type": "…", "status": "success", "duration_sec": 1.2,
      "message": "…", "reason": "…" }
  ]
}
```

## Forward-look: test history (0.2.0)

The most likely schema-breaker on the roadmap is the test runner / test history
(0.2.0). The decision, recorded here so it isn't relitigated later:

**Test results extend the run record — they are not a separate record type.**
A test run is just a flow whose steps run tests, so its results are an
*additional artifact of the same run*, attached as:

- a reserved manifest section (e.g. `[[test_summary]]` per suite, or a
  `[tests]` rollup with totals/pass/fail/skip), and
- new `test.*` event kinds in `events.ndjson` (e.g. `test.start`, `test.case`,
  `test.end`), under the existing envelope.

Adding those is a **`schema_version` bump to 2**. v1 readers will ignore the
new section/events (per the reader contract) and still render the run; a v2
reader gains the test view. No `kind`/type discriminator is added to the
manifest now — runs are uniform, and reserving the namespace via the version
field is enough.

When that lands: bump `Version.RunSchemaVersion`, extend
`writeManifest`/`writeEvent`, teach `RunHistory` the new fields (keeping the
v1 fallbacks), and add the new shapes here.
