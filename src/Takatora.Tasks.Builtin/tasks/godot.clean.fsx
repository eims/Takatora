// Built-in task: godot.clean
// Smaller catalog than UE/Unity — Godot's footprint is essentially
// `.godot/` (4.x) or `.import/` (3.x) + the user's chosen build dir.
//
// Params:
//   targets?      string[]  — any of: cache, build_output
//   preset?       string    — safe | pre_release | nuke
//   build_output? string    — path for `build_output` target
//                              (default "Build")
//
// Outputs:
//   bytes_freed     int
//   files_deleted   int
open Takatora.Tasks
open System.IO

let targetsParam   = Param.optional<string[]> "targets" [||]
let presetParam    = Param.optional<string>   "preset"  ""
let buildOutputDir = Param.optional<string>   "build_output" "Build"

let abs (p: string) =
    if Path.IsPathRooted p then p else Path.Combine(Project.workingDir, p)

let targetPaths (target: string) : string list =
    match target with
    // Both 4.x and 3.x cache dirs — whichever exists gets purged.
    | "cache"        -> [ ".godot"; ".import" ]
    | "build_output" -> [ buildOutputDir ]
    | other -> Task.fail<string list> (sprintf "godot.clean: unknown target '%s'" other)

let presetTargets = function
    | ""            -> []
    | "safe"        -> []
    | "pre_release" -> [ "cache"; "build_output" ]
    | "nuke"        -> [ "cache"; "build_output" ]
    | other -> Task.fail<string list> (sprintf "godot.clean: unknown preset '%s'" other)

let allTargets =
    Set.union (Set.ofArray targetsParam) (Set.ofList (presetTargets presetParam))

let mutable bytesFreed = 0L
let mutable filesDeleted = 0

let purgePath (path: string) =
    if File.Exists path then
        bytesFreed <- bytesFreed + (FileInfo path).Length
        filesDeleted <- filesDeleted + 1
        File.Delete path
    elif Directory.Exists path then
        let rec sizeAndCount (d: string) =
            for f in Directory.GetFiles d do
                bytesFreed <- bytesFreed + (FileInfo f).Length
                filesDeleted <- filesDeleted + 1
            for sd in Directory.GetDirectories d do sizeAndCount sd
        sizeAndCount path
        Directory.Delete(path, recursive = true)

Step.run "godot.clean" (fun () ->
    for target in allTargets do
        for relPath in targetPaths target do
            purgePath (abs relPath)
)

Output.set "bytes_freed"   bytesFreed
Output.set "files_deleted" filesDeleted
