// Built-in task: ue.build_nonunity
// Compiles a UE target with the unity (jumbo) build DISABLED so the
// compiler sees each .cpp on its own — surfacing missing #includes and
// stale forward-declarations that a unity build silently hides. It's a
// pure correctness gate: UBT returns non-zero on the first compile error,
// which fails the step.
//
// Params:
//   target?         string   — UBT target name (default "<project name>Editor")
//   platform?       string   — default "Win64"
//   configuration?  string   — Development (default) | DebugGame | Shipping | Test
//   extra_ubt_args? string[] — pass-through (e.g. -NoPCH to also defeat PCH-
//                              hidden includes)
//
// Outputs: none — success/failure is the result.
//
// Note: as a `ue.*` task the runner auto-acquires the UE editor mutex, so
// this won't fight a build/cook running in the same workspace.
open Takatora.Tasks
open System.IO

let target        = Param.optional<string>   "target" (sprintf "%sEditor" Project.name)
let platform      = Param.optional<string>   "platform" "Win64"
let configuration = Param.optional<string>   "configuration" "Development"
let extraUbtArgs  = Param.optional<string[]>  "extra_ubt_args" [||]

if Engine.projectFile = "" then
    Task.fail<unit> "ue.build_nonunity: project.toml [engine] needs project_file (e.g. SampleGame.uproject)"

let projectAbs =
    let p = Engine.projectFile
    if Path.IsPathRooted p then p else Path.Combine(Project.workingDir, p)

// UBT positional args are: <Target> <Platform> <Configuration> [options].
let ubtArgs = [
    yield target
    yield platform
    yield configuration
    yield sprintf "-Project=%s" projectAbs
    yield "-DisableUnity"
    yield! Array.toList extraUbtArgs
]

Step.run "BuildNonUnity" (fun () ->
    UE.runUBT ubtArgs
)
