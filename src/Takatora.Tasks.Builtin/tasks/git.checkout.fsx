// Built-in task: git.checkout
// Switch to a branch / tag / commit by ref. Surfaces the resolved
// commit sha for downstream steps.
//
// Params:
//   ref  string  — branch name, tag, or commit hash
//
// Outputs:
//   commit_sha  string  — HEAD after checkout
open Takatora.Tasks

let ref = Param.required<string> "ref"

Step.run "git checkout" (fun () ->
    Cmd.execIn Project.workingDir "git" [ "checkout"; ref ]
)

let result =
    Cmd.execCaptureWith
        { ExecOptions.empty with WorkingDir = Some Project.workingDir }
        "git"
        [ "rev-parse"; "HEAD" ]

if result.exitCode <> 0 then
    Task.fail<unit> (sprintf "git rev-parse failed: %s" (result.stderr.Trim()))

Output.set "commit_sha" (result.stdout.Trim())
