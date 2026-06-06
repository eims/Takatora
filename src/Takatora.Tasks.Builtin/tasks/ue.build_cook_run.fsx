// Built-in task: ue.build_cook_run
// Wraps Unreal's BuildCookRun via UAT — the canonical "do everything
// to ship a build" entry point. Mirrors the design's I/O contract.
//
// Params:
//   configuration   enum     — Development | DebugGame | Shipping | Test
//   platform        string   — Win64 / Mac / Linux / etc.
//   target          string?  — Game | Client | Server | Editor (default Game)
//   maps            string[] — maps to cook (default: project's defaults)
//   archive_dir     string?  — output dir, default Build/<platform>-<config>
//   extra_uat_args  string[] — pass-through extras
//
// Outputs:
//   archive_path   string  — same as archive_dir but absolute
//   exe_path       string  — guessed primary exe under archive_dir
open Takatora.Tasks
open System.IO

let configuration =
    Param.requiredEnum "configuration"
        [ "Development"; "DebugGame"; "Shipping"; "Test" ]

let platform      = Param.required<string>     "platform"
let target        = Param.optional<string>     "target" "Game"
let maps          = Param.optional<string[]>   "maps" [||]
let archiveDir    =
    Param.optional<string> "archive_dir" (sprintf "Build/%s-%s" platform configuration)
let extraUatArgs  = Param.optional<string[]>   "extra_uat_args" [||]

// Descriptions surface as GUI tooltips (Inspector / run dialog).
Param.note "configuration"  "Build configuration passed to UAT (-clientconfig)."
Param.note "platform"       "Target platform, e.g. Win64 / Mac / Linux."
Param.note "target"         "Build target (Game / Client / Server / Editor)."
Param.note "maps"           "Maps to cook; empty uses the project's defaults."
Param.note "archive_dir"    "Output directory for the staged/archived build."
Param.note "extra_uat_args" "Extra args passed through to RunUAT verbatim."

let archiveAbs =
    if Path.IsPathRooted archiveDir then archiveDir
    else Path.Combine(Project.workingDir, archiveDir)

if Engine.projectFile = "" then
    Task.fail<unit> "ue.build_cook_run: project.toml [engine] needs project_file (e.g. SampleGame.uproject)"

let projectAbs =
    let p = Engine.projectFile
    if Path.IsPathRooted p then p
    else Path.Combine(Project.workingDir, p)

let mapArgs = maps |> Array.map (sprintf "-map=%s") |> Array.toList

let uatArgs = [
    yield "BuildCookRun"
    yield sprintf "-project=%s" projectAbs
    yield sprintf "-platform=%s" platform
    yield sprintf "-clientconfig=%s" configuration
    yield sprintf "-target=%s" target
    yield! mapArgs
    yield "-build"; yield "-cook"; yield "-stage"; yield "-archive"
    yield sprintf "-archivedirectory=%s" archiveAbs
    // NOTE: deliberately NOT -utf8output. It makes UAT/UBT emit UTF-8 for
    // their own lines while sub-tools (e.g. MSVC link.exe) still emit the
    // OS code page (CP932 on JP Windows) — a mixed-encoding stream the
    // runner can't decode cleanly, producing mojibake. Without it the whole
    // stream is the native code page, which the SDK decodes correctly.
    yield! Array.toList extraUatArgs
]

Step.run "BuildCookRun" (fun () ->
    UE.runUAT uatArgs
)

Output.set "archive_path" archiveAbs
Output.set "exe_path"     (Path.Combine(archiveAbs, sprintf "%s.exe" Project.name))
// NOTE: no "log_path" — UAT writes its master log under
// %APPDATA%\Unreal Engine\AutomationTool\Logs\… (not the archive dir), and
// the full UAT output is already captured in the run's own log.txt. A
// guessed archive/UAT_Log.txt path was misleading (the file isn't there).
