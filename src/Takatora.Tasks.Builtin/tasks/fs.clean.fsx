// Built-in task: fs.clean
// Recursively delete a path. Reports how much was reclaimed so flow
// authors can sanity-check / log cleanup magnitude.
//
// Params:
//   path     string  — absolute or relative to Project.workingDir
//   keep?    bool    — if true and `path` is a directory, delete its
//                      contents but keep the dir itself. Default false.
//
// Outputs:
//   bytes_freed     int  — total size of files removed
//   files_deleted   int  — count of files removed
open Takatora.Tasks
open System.IO

let pathParam = Param.required<string> "path"
let keep      = Param.optional<bool>   "keep" false

let resolved =
    if Path.IsPathRooted pathParam then pathParam
    else Path.Combine(Project.workingDir, pathParam)

let countAndSize (root: string) =
    let mutable bytes = 0L
    let mutable files = 0
    let rec walk (d: string) =
        for f in Directory.GetFiles(d) do
            bytes <- bytes + (FileInfo f).Length
            files <- files + 1
        for sd in Directory.GetDirectories(d) do
            walk sd
    walk root
    bytes, files

Step.run "fs.clean" (fun () ->
    if File.Exists resolved then
        let bytes = (FileInfo resolved).Length
        File.Delete resolved
        Output.set "bytes_freed"   bytes
        Output.set "files_deleted" 1
    elif Directory.Exists resolved then
        let bytes, files = countAndSize resolved
        if keep then
            for f  in Directory.GetFiles resolved      do File.Delete f
            for sd in Directory.GetDirectories resolved do Directory.Delete(sd, recursive = true)
        else
            Directory.Delete(resolved, recursive = true)
        Output.set "bytes_freed"   bytes
        Output.set "files_deleted" files
    else
        Log.info (sprintf "fs.clean: '%s' does not exist, nothing to do" resolved)
        Output.set "bytes_freed"   0L
        Output.set "files_deleted" 0
)
