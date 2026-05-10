// Built-in task: godot.export
// Headless export via Godot's CLI. Requires the user-side
// `export_presets.cfg` to define a preset matching the `preset` param.
//
// Params:
//   preset    string  — preset name from export_presets.cfg
//                       (e.g. "Windows Desktop")
//   output    string  — output exe / app path
//   release?  bool    — true → --export-release, false → --export-debug
//                       (default true)
//
// Outputs:
//   exe_path  string  — same as `output`, absolutized
open Takatora.Tasks
open System.IO

let preset    = Param.required<string> "preset"
let output    = Param.required<string> "output"
let release   = Param.optional<bool>   "release" true

let outputAbs =
    if Path.IsPathRooted output then output
    else Path.Combine(Project.workingDir, output)

Directory.CreateDirectory(Path.GetDirectoryName outputAbs) |> ignore

// Godot's CLI wants the project dir as the last arg, or via
// `--path`. Use --path to be explicit and order-independent.
let exportFlag = if release then "--export-release" else "--export-debug"

let args = [
    "--path"; Project.workingDir
    exportFlag; preset
    outputAbs
]

Step.run "Godot export" (fun () ->
    Godot.runHeadless args
)

Output.set "exe_path" outputAbs
