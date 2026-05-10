// Built-in task: ue.clean
// Selective UE artifact cleanup per the design's target catalog.
// Use `targets` for explicit choice or `preset` for shorthand.
//
// Params:
//   targets?  string[]  — any of: intermediate, binaries, saved,
//                          cooked, derived_data, project_files
//   preset?   string    — safe | pre_release | nuke
//                          (resolved BEFORE targets; both can be set,
//                          they're unioned)
//
// Outputs:
//   bytes_freed     int
//   files_deleted   int
open Takatora.Tasks
open System.IO

let targetsParam = Param.optional<string[]> "targets" [||]
let presetParam  = Param.optional<string>   "preset"  ""

// Target → list of paths (relative to Project.workingDir). Some targets
// expand to multiple paths (e.g. cooked = Saved/Cooked + Build).
let targetPaths (target: string) : string list =
    match target with
    | "intermediate"  -> [ "Intermediate" ]
    | "binaries"      -> [ "Binaries" ]
    | "saved"         -> [ "Saved" ]
    | "cooked"        -> [ "Saved/Cooked"; "Build" ]
    | "derived_data"  -> [ "DerivedDataCache" ]
    | "project_files" -> []  // glob-handled below
    | other -> Task.fail<string list> (sprintf "ue.clean: unknown target '%s'" other)

let presetTargets = function
    | ""             -> []
    | "safe"         -> [ "intermediate"; "binaries" ]
    | "pre_release"  -> [ "intermediate"; "binaries"; "cooked"; "saved" ]
    // `nuke` deliberately excludes `derived_data` — DDC rebuild can take
    // hours. Authors who really mean it pass `derived_data` explicitly.
    | "nuke"         -> [ "intermediate"; "binaries"; "saved"; "cooked"; "project_files" ]
    | other -> Task.fail<string list> (sprintf "ue.clean: unknown preset '%s'" other)

let allTargets =
    Set.union (Set.ofArray targetsParam) (Set.ofList (presetTargets presetParam))

let abs (p: string) =
    if Path.IsPathRooted p then p else Path.Combine(Project.workingDir, p)

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

Step.run "ue.clean" (fun () ->
    for target in allTargets do
        if target = "project_files" then
            // *.sln files at the project root + .vs/
            for f in Directory.GetFiles(Project.workingDir, "*.sln") do
                purgePath f
            purgePath (abs ".vs")
        else
            for relPath in targetPaths target do
                purgePath (abs relPath)
)

Output.set "bytes_freed"   bytesFreed
Output.set "files_deleted" filesDeleted
