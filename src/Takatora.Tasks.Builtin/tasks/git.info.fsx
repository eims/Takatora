// Built-in task: git.info
// Capture the working repo's state as step outputs, for downstream steps
// (e.g. stamping a build with its commit hash via fs.write). Reads the repo
// at the project's working dir. Missing values come through as empty.
//
// Params:
//   date_format  string?  — .NET format for the `date` output
//                           (default "yyyy-MM-dd HH:mm:ss")
//
// Outputs:
//   hash      string  — full commit SHA (HEAD)
//   short     string  — short commit SHA
//   branch    string  — current branch (or "HEAD" when detached)
//   modified  string  — "true" if the working tree has changes, else "false"
//   message   string  — HEAD's subject line
//   date      string  — now, formatted with date_format
open Takatora.Tasks
open System

let dateFormat = Param.optional<string> "date_format" "yyyy-MM-dd HH:mm:ss"

Step.run "git.info" (fun () ->
    let git (args: string list) : string =
        try
            let r = Cmd.execCapture "git" args
            if r.exitCode = 0 then r.stdout.Trim() else ""
        with _ -> ""
    Output.set "hash"     (git [ "rev-parse"; "HEAD" ])
    Output.set "short"    (git [ "rev-parse"; "--short"; "HEAD" ])
    Output.set "branch"   (git [ "rev-parse"; "--abbrev-ref"; "HEAD" ])
    Output.set "modified" (if git [ "status"; "--porcelain" ] = "" then "false" else "true")
    Output.set "message"  (git [ "log"; "-1"; "--pretty=%s" ])
    Output.set "date"     (DateTime.Now.ToString(dateFormat))
)
