// Project-local task: pkg.stamp
// Sample of the custom-task escape hatch. Does the SAME thing as the
// git.info + fs.write pair in the `package` flow — stamp a version file from
// the current commit — but in a single .fsx, so the formatting lives in code
// instead of flow params. Resolved from .ci/tasks, so the GUI step Inspector
// shows "source: project".
//
// Outputs:
//   hash      string  — the stamped commit SHA
//   modified  string  — "true"/"false" working-tree state
open Takatora.Tasks
open System
open System.IO

let git (args: string list) : string =
    try
        let r = Cmd.execCapture "git" args
        if r.exitCode = 0 then r.stdout.Trim() else ""
    with _ -> ""

Step.run "stamp version" (fun () ->
    let hash     = git [ "rev-parse"; "HEAD" ]
    let modified = if git [ "status"; "--porcelain" ] = "" then "false" else "true"
    let date     = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
    let content  = sprintf "commit=%s\nmodified=%s\ndate=%s\n" hash modified date
    let path     = Path.Combine(Project.workingDir, "Build", "VERSION.txt")
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, content)
    Log.info (sprintf "stamped %s @ %s (modified=%s)" path hash modified)
    Output.set "hash" hash
    Output.set "modified" modified
)
