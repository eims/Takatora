// Built-in task: fs.write
// Write text to a file (creating parent dirs). `content` is a normal step
// param, so it can interpolate ${steps.X.outputs.Y} / ${vars.X} — e.g.
// stamp a build with a git.info hash. Use append to add to an existing file.
//
// Params:
//   path     string   — destination file (relative → under the working dir)
//   content  string?  — text to write (default empty)
//   append   bool?    — append instead of overwrite (default false)
//
// Outputs:
//   path  string  — the absolute path written
//   bytes int     — number of bytes written (the content length, UTF-8)
open Takatora.Tasks
open System.IO
open System.Text

let pathParam = Param.required<string> "path"
let content   = Param.optional<string> "content" ""
let append    = Param.optional<bool>   "append" false

let dest =
    if Path.IsPathRooted pathParam then pathParam
    else Path.Combine(Project.workingDir, pathParam)

Step.run "fs.write" (fun () ->
    Directory.CreateDirectory(Path.GetDirectoryName dest) |> ignore
    if append then File.AppendAllText(dest, content)
    else File.WriteAllText(dest, content)
)

Output.set "path"  dest
Output.set "bytes" (Encoding.UTF8.GetByteCount content)
