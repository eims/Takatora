// Built-in task: git.pull
// Runs `git pull` against the project working dir. After the pull,
// captures the resulting HEAD commit so downstream steps can reference
// `${steps.<id>.outputs.commit_sha}`.
//
// Params:
//   remote?  string  — default "origin"
//   branch?  string  — default "" (whatever the current branch tracks)
//
// Outputs:
//   commit_sha  string  — `git rev-parse HEAD` after the pull
open Takatora.Tasks

let remote = Param.optional<string> "remote" "origin"
let branch = Param.optional<string> "branch" ""

let pullArgs =
    [ yield "pull"
      yield remote
      if branch <> "" then yield branch ]

Step.run "git pull" (fun () ->
    Cmd.execIn Project.workingDir "git" pullArgs
)

let result =
    Cmd.execCaptureWith
        { ExecOptions.empty with WorkingDir = Some Project.workingDir }
        "git"
        [ "rev-parse"; "HEAD" ]

if result.exitCode <> 0 then
    Task.fail<unit> (sprintf "git rev-parse failed: %s" (result.stderr.Trim()))

Output.set "commit_sha" (result.stdout.Trim())
