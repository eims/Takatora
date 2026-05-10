// Built-in task: unity.clean
// Selective Unity artifact cleanup. Same pattern as ue.clean.
//
// Params:
//   targets?      string[]  — any of: library, temp, obj, logs,
//                              build_output, project_files
//   preset?       string    — safe | pre_release | nuke
//   build_output? string    — path for `build_output` target
//                              (default "Build")
//
// Outputs:
//   bytes_freed     int
//   files_deleted   int
//
// Editor-running detection isn't done here; Unity will lock Library/
// while the editor is open and the recursive delete will fail loudly.
open Takatora.Tasks
open System.IO

let targetsParam   = Param.optional<string[]> "targets" [||]
let presetParam    = Param.optional<string>   "preset"  ""
let buildOutputDir = Param.optional<string>   "build_output" "Build"

let abs (p: string) =
    if Path.IsPathRooted p then p else Path.Combine(Project.workingDir, p)

let targetPaths (target: string) : string list =
    match target with
    | "library"       -> [ "Library" ]
    | "temp"          -> [ "Temp" ]
    | "obj"           -> [ "obj" ]
    | "logs"          -> [ "Logs" ]
    | "build_output"  -> [ buildOutputDir ]
    | "project_files" -> []  // glob-handled
    | other -> Task.fail<string list> (sprintf "unity.clean: unknown target '%s'" other)

let presetTargets = function
    | ""            -> []
    | "safe"        -> [ "temp"; "obj" ]
    | "pre_release" -> [ "temp"; "obj"; "build_output" ]
    // `nuke` excludes `library` — reimport on a 50GB project takes
    // hours. Authors who really mean it pass `library` explicitly.
    | "nuke"        -> [ "temp"; "obj"; "logs"; "build_output"; "project_files" ]
    | other -> Task.fail<string list> (sprintf "unity.clean: unknown preset '%s'" other)

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

Step.run "unity.clean" (fun () ->
    for target in allTargets do
        if target = "project_files" then
            for f in Directory.GetFiles(Project.workingDir, "*.csproj") do
                purgePath f
            purgePath (abs ".vs")
            purgePath (abs ".idea")
        else
            for relPath in targetPaths target do
                purgePath (abs relPath)
)

Output.set "bytes_freed"   bytesFreed
Output.set "files_deleted" filesDeleted
