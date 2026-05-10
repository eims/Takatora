// Built-in task: fs.copy
// File-or-directory copy. If `from` is a file, `to` is treated as the
// destination file path. If `from` is a directory, `to` is the
// destination directory; existing files there are overwritten.
//
// Params:
//   from   string  — source (file or directory)
//   to     string  — destination
//
// Outputs: none
open Takatora.Tasks
open System.IO

let fromParam = Param.required<string> "from"
let toParam   = Param.required<string> "to"

let resolve (p: string) =
    if Path.IsPathRooted p then p
    else Path.Combine(Project.workingDir, p)

let src = resolve fromParam
let dst = resolve toParam

let rec copyDir (s: string) (d: string) =
    Directory.CreateDirectory d |> ignore
    for f in Directory.GetFiles s do
        File.Copy(f, Path.Combine(d, Path.GetFileName f), overwrite = true)
    for sd in Directory.GetDirectories s do
        copyDir sd (Path.Combine(d, Path.GetFileName sd))

Step.run "fs.copy" (fun () ->
    if File.Exists src then
        Directory.CreateDirectory(Path.GetDirectoryName dst) |> ignore
        File.Copy(src, dst, overwrite = true)
    elif Directory.Exists src then
        copyDir src dst
    else
        Task.fail<unit> (sprintf "fs.copy: source '%s' does not exist" src)
)
