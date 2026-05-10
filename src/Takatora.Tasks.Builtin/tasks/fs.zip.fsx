// Built-in task: fs.zip
// Archive a directory into a .zip. Existing archive at `to` is
// overwritten. Use for build artifact packaging before upload.
//
// Params:
//   from   string  — source directory (must be a dir, not a file)
//   to     string  — output .zip path
//
// Outputs:
//   archive_path  string  — absolute path to the created .zip
//   size          int     — final archive size in bytes
open Takatora.Tasks
open System.IO
open System.IO.Compression

let fromParam = Param.required<string> "from"
let toParam   = Param.required<string> "to"

let resolve (p: string) =
    if Path.IsPathRooted p then p
    else Path.Combine(Project.workingDir, p)

let src = resolve fromParam
let dst = resolve toParam

Step.run "fs.zip" (fun () ->
    if not (Directory.Exists src) then
        Task.fail<unit> (sprintf "fs.zip: source dir '%s' does not exist" src)
    Directory.CreateDirectory(Path.GetDirectoryName dst) |> ignore
    if File.Exists dst then File.Delete dst
    ZipFile.CreateFromDirectory(src, dst, CompressionLevel.Optimal, includeBaseDirectory = false)
    Output.set "archive_path" dst
    Output.set "size"         (FileInfo dst).Length
)
