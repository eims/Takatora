---
name: takatora-authoring
description: Write or edit Takatora CI config — define flows in .takatora/flows.toml and author custom .fsx tasks with the Takatora.Tasks SDK. Use when the user wants to add/change a flow, add a step, declare flow variables, or write a custom task for a Takatora project (local CI for game builds — UE/Unity/Godot). For running/inspecting existing flows, use takatora-run instead.
---

# Authoring Takatora flows & custom tasks

After any change: `takatora validate <path>`, then `takatora run <path>
<flow> --dry-run`. For a custom task also `takatora describe <type>
--project <path>`. (`takatora` may be a vendored exe, e.g.
`Tools/Takatora/Takatora.Cli.exe`, or `dotnet run --project src/Takatora.Cli
--` from source.)

## Flows — `<project>/.takatora/flows.toml`

```toml
[[flow]]
id = "package"
name = "Windows package"

[flow.vars]
configuration = { type = "enum", values = ["Development", "Shipping"], default = "Development", description = "Client config." }
clean = { type = "bool", default = false }

[[flow.steps]]
id = "git"                 # give an id only if a later step uses its outputs
type = "git.info"

[[flow.steps]]
id = "stamp"
type = "fs.write"
path = "Build/VERSION.txt"
content = "commit=${steps.git.outputs.hash}\n"

[[flow.steps]]
type = "ue.build_cook_run"
when = "${vars.clean}"
configuration = "${vars.configuration}"
platform = "Win64"
```

**Var types:** `string`, `int`, `float`, `bool`, `enum` (needs
`values=[...]`), `path`, `file`, `dir`, `secret`, `multiline`, `list` (with
`item="string"|...`). Each can have `default` and `description`.

**Templating** (in step param string values): `${vars.<name>}`,
`${steps.<id>.outputs.<name>}`, `${project.<field>}`, `${env.<NAME>}`.

**`when` is bool-only:** `${vars.<bool>}` or `!${vars.<bool>}`. No enum/string
comparison — to branch on an enum value, pass it to a custom task and branch
in code.

**Built-in tasks** (compose these before writing custom): `shell`,
`notify.console`, `fs.clean|copy|write|zip`, `git.info|checkout|pull`,
`lfs.fetch|pull`, `ue.clean|build_cook_run|build_nonunity`,
`unity.build|clean`, `godot.export|clean`, `artifact.collect`. Inspect any
with `takatora describe <type> --project <p>`.

## Custom task — a `.fsx`

Put `<type>.fsx` in `<project>/.takatora/tasks/` (project) or
`%APPDATA%\Takatora\tasks` (user); resolution is project → user → built-in.
The step `type` is the filename without `.fsx`.

```fsharp
open Takatora.Tasks
open System.IO

// Params at TOP LEVEL — `describe` harvests the schema from these.
let target        = Param.optional<string> "target" "Game"
let configuration = Param.requiredEnum "configuration" [ "Development"; "Shipping" ]
Param.note "target" "Build target."

// ALL side effects inside Step.run — describe mode skips the action, so a
// top-level Cmd/file write would wrongly run during `describe`.
Step.run "do the work" (fun () ->
    let r = Cmd.execCapture "git" [ "rev-parse"; "HEAD" ]
    let hash = if r.exitCode = 0 then r.stdout.Trim() else ""
    File.WriteAllText(Path.Combine(Project.workingDir, "Build", "VERSION.txt"), hash)
    Log.info (sprintf "stamped %s" hash)
    Output.set "hash" hash)
```

**describe-mode contract:** declare every `Param.*` at top level; keep every
side effect inside `Step.run`/`Step.runResult`; use `Task.fail "reason"` to
abort. This is what keeps `takatora describe` able to harvest the schema.

### SDK (`open Takatora.Tasks`)
- **Param** (top level): `required<'T>`, `optional<'T> _ default`,
  `requiredEnum name [vals]`, `has name`, `requiredPath/optionalPath`,
  `requiredDir/optionalDir`, `requiredFile/optionalFile` (filter
  `string list option`), `requiredSecret/optionalSecret`,
  `requiredMultiline/optionalMultiline`, `optionalList<'T> name [..]`,
  `note name desc`. `'T` = string/int/int64/float/bool/arrays.
- **Output:** `Output.set "name" value` → `${steps.<id>.outputs.name}`.
- **Step:** `Step.run "label" (fun () -> ..)`, `Step.runResult`, `Step.skip`.
- **Log:** `Log.info|warn|error|debug`, `Log.section`.
- **Progress:** `Progress.during "label" everySec (fun () -> ..)` — heartbeat
  around a long blocking op so the log doesn't go silent.
- **Zip:** `Zip.createFromDirectory src dst` — zip a dir's contents (files at
  the archive root) with start/finish + %-progress logs; prefer over
  `ZipFile.CreateFromDirectory` for large outputs.
- **Task:** `Task.fail<'T> "reason"`.
- **Project/Engine:** `Project.workingDir|name`;
  `Engine.kind|path|version|projectFile|executable`.
- **Cmd:** `Cmd.exec "exe" [args]` (streams to log, throws on non-zero),
  `Cmd.execIn wd ..`, `Cmd.execWith opts ..`, `Cmd.execCapture "exe" [args]`
  → `{| stdout; stderr; exitCode |}` (no throw). `ExecOptions = { WorkingDir;
  Env; IgnoreExitCodes; Timeout }`, `ExecOptions.empty`.
- **Engine helpers:** `UE.runUAT|runUBT [args]` (ue.* auto-locks the editor
  mutex), `Unity.runBatch [args]` (adds `-batchmode -nographics -quit`),
  `Godot.runHeadless [args]` (adds `--headless`).

## Built-in vs custom — choose deliberately
Compose built-ins when a short chain reads clearly in TOML (e.g. `git.info` +
`fs.write` to stamp). Write a `.fsx` when conditionals/formatting are clearer
in code, or to branch on an enum (`when` can't). The repo sample
(`samples/sample-game/.takatora/flows.toml`) shows both: `package` (built-ins) vs
`package-custom` (a `pkg.stamp` .fsx) doing the same job.

(Fuller human-facing docs, if this skill ships inside the Takatora repo:
`docs/ai-authoring.md`.)
