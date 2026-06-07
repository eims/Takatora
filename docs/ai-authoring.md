# Authoring Takatora flows & custom tasks (for AI agents)

This guide is for an AI assistant **writing** Takatora config: defining flows
in `flows.toml` and, when a step's logic doesn't fit flow params, writing a
custom `.fsx` task. (Operating/running is covered in [ai-cli.md](ai-cli.md).)

Workflow: write → `takatora validate <path>` → `takatora run <path> <flow>
--dry-run` → run. For custom tasks also `takatora describe <type> --project
<path>` to confirm the schema you authored.

## 1. Flows (`<project>/.takatora/flows.toml`)

A project has one or more flows. Each is an ordered list of steps.

```toml
[[flow]]
id = "package"                 # referenced by `takatora run <project> package`
name = "Windows package"       # human label

[flow.vars]                    # optional inputs, overridable with --var / the GUI
configuration = { type = "enum", values = ["Development", "Shipping"], default = "Development", description = "Client config." }
clean = { type = "bool", default = false, description = "Clean before building." }

[[flow.steps]]
id = "git"                     # optional; needed if a later step references its outputs
type = "git.info"              # the task to run (built-in or a .takatora/tasks/*.fsx)

[[flow.steps]]
id = "stamp"
type = "fs.write"
path = "Build/VERSION.txt"
content = "commit=${steps.git.outputs.hash}\n"   # templating, see below

[[flow.steps]]
id = "build"
type = "ue.build_cook_run"
when = "${vars.clean}"         # bool gate (see grammar)
configuration = "${vars.configuration}"
platform = "Win64"
```

### Variable types (`[flow.vars]`)
`type = ` one of: `string`, `int`, `float`, `bool`, `enum` (requires a
`values = [...]` array), `path`, `file`, `dir`, `secret`, `multiline`, or
`list` (with `item = "string"|"int"|"float"|"bool"`, default `string`). Each
var may carry a `default` and a `description` (shown as a GUI tooltip).
`path/file/dir/secret/multiline` are string-valued but make the GUI render the
right widget; `secret` is kept out of manifests/logs.

```toml
maps = { type = "list", item = "string", default = ["Main"] }
api_key = { type = "secret" }
```

### Templating (in step param values, as strings)
- `${vars.<name>}` — a flow variable.
- `${steps.<id>.outputs.<name>}` — an output recorded by an earlier step (that
  step needs an explicit `id`).
- `${project.<field>}` / `${env.<NAME>}` — project field / environment var.

### `when` (step gating) — bool-only grammar
A step's `when` is `${vars.<boolVar>}` or `!${vars.<boolVar>}` only. There is
**no** enum/string comparison. If you need to branch on an enum value (e.g.
do X only when `target == "Steam"`), that doesn't fit `when` — write a custom
task that takes the value and branches in code (see §3).

Validate + preview anytime: `takatora validate <path>`, then
`takatora run <path> <flow> --dry-run` to see effective vars + ordered steps.

## 2. Built-in tasks

Prefer composing built-ins before writing a custom task. Families:
`shell`, `notify.console`, `fs.clean|copy|write|zip`, `git.info|checkout|pull`,
`lfs.fetch|pull`, `ue.clean|build_cook_run|build_nonunity`,
`unity.build|clean`, `godot.export|clean`, `artifact.collect`.
Inspect any one's params/outputs with `takatora describe <type> --project <p>`.

## 3. Custom tasks (`.fsx`)

Write one when logic is clearer in code than in flow params: conditionals
(e.g. branching on an enum), formatting, or collapsing a multi-step
composition into one named step.

**Where it goes / how it's named.** Put `<task-type>.fsx` in
`<project>/.takatora/tasks/` (project-local), or `%APPDATA%\Takatora\tasks` (user),
or it's a built-in. Resolution order: **project → user → built-in**. The step's
`type` is the filename without `.fsx` (so `.takatora/tasks/pkg.stamp.fsx` → `type =
"pkg.stamp"`).

**Skeleton:**

```fsharp
open Takatora.Tasks
open System.IO

// Declare params at TOP LEVEL — `describe` harvests the schema from these.
let target        = Param.optional<string> "target" "Game"
let configuration = Param.requiredEnum "configuration" [ "Development"; "Shipping" ]
Param.note "target" "Build target (Game / Editor / ...)."

// Do ALL work inside Step.run — describe mode skips the action, so any side
// effect (Cmd, file IO) MUST live here or it would run during `describe`.
Step.run "do the work" (fun () ->
    let r = Cmd.execCapture "git" [ "rev-parse"; "HEAD" ]
    let hash = if r.exitCode = 0 then r.stdout.Trim() else ""
    File.WriteAllText(Path.Combine(Project.workingDir, "Build", "VERSION.txt"), hash)
    Log.info (sprintf "stamped %s" hash)
    Output.set "hash" hash)        // → ${steps.<id>.outputs.hash} downstream
```

**The describe-mode contract (important):** the runner executes the `.fsx` in
*describe mode* to harvest its schema. So: declare every `Param.*` at top
level (they register the schema), and keep every side effect inside
`Step.run` / `Step.runResult` (their actions are skipped in describe mode).
Use `Task.fail "reason"` for clean user-facing aborts.

### SDK reference (`open Takatora.Tasks`)

**Param** (declare at top level):
- `Param.required<'T> "name"`, `Param.optional<'T> "name" default` — `'T` =
  `string` / `int` / `int64` / `float` / `bool` / arrays.
- `Param.requiredEnum "name" [ "a"; "b" ]` — constrained string.
- `Param.has "name"` — bool presence check.
- `Param.requiredPath|optionalPath`, `requiredDir|optionalDir`,
  `requiredFile|optionalFile` (filter `string list option`, e.g.
  `Some ["*.uproject"]`), `requiredSecret|optionalSecret`,
  `requiredMultiline|optionalMultiline` — string-valued, with GUI kind hints.
- `Param.optionalList<'T> "name" [ ... ]` — list input.
- `Param.note "name" "tooltip"` — describe-only annotation.

**Output:** `Output.set "name" value` — surfaces as
`${steps.<id>.outputs.name}`.

**Step:** `Step.run "label" (fun () -> ...)`,
`Step.runResult "label" (fun () -> value)`, `Step.skip "label" "reason"`.

**Log:** `Log.info|warn|error|debug "msg"`, `Log.section "heading"`.

**Progress:** `Progress.during "label" everySec (fun () -> ...)` — run a long
blocking op while emitting a heartbeat every `everySec` seconds, so the log
doesn't go silent.

**Zip:** `Zip.createFromDirectory src dst` — zip a directory's contents (files
at the archive root) with visible start/finish + %-progress lines. Prefer over
`System.IO.Compression.ZipFile.CreateFromDirectory` for large outputs.

**Task:** `Task.fail<'T> "reason"` — abort cleanly (no F# stack dump).

**Project / Engine** (read-only): `Project.workingDir`, `Project.name`;
`Engine.kind|path|version|projectFile|executable`. Engine tasks should
`Task.fail` early if a needed field is empty.

**Cmd** (external processes; cwd defaults to `Project.workingDir`):
- `Cmd.exec "exe" [args]` — stream stdout/stderr to the run log; **throws on
  non-zero exit** (unless ignored).
- `Cmd.execIn "wd" "exe" [args]`, `Cmd.execWith opts "exe" [args]`.
- `Cmd.execCapture "exe" [args]` → `{| stdout; stderr; exitCode |}` — **does
  not throw**; you decide what's success. Use for short outputs (hashes,
  versions).
- `ExecOptions = { WorkingDir: string option; Env: Map<string,string>;
  IgnoreExitCodes: int list; Timeout: TimeSpan option }`, `ExecOptions.empty`.

**Engine helpers** (resolve the right tool from `Engine.path`):
- `UE.runUAT [args]` / `UE.runUBT [args]` (+ `uatBatPath`/`ubtBatPath`/
  `editorPath`). A `ue.*` task auto-acquires the editor mutex (won't fight a
  concurrent build in the same workspace).
- `Unity.runBatch [args]` (prefixes `-batchmode -nographics -quit`).
- `Godot.runHeadless [args]` (prefixes `--headless`).

### Built-in composition vs custom task — pick deliberately
Two worked examples live in the repo sample
(`samples/sample-game/.takatora/flows.toml`): the `package` flow stamps a version
file with built-ins (`git.info` + `fs.write`); `package-custom` does the same
stamp with one project-local `.fsx` (`pkg.stamp`). Use built-ins when a short
composition reads clearly in TOML; reach for a `.fsx` when conditionals or
formatting are clearer in code (and remember `when` can't branch on enums).

### Verify what you wrote
```
takatora describe <task-type> --project <path>   # confirm params + outputs
takatora run <path> <flow> --dry-run             # confirm the plan
takatora run <path> <flow>                        # run for real
```
