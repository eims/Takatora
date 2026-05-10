// Built-in task: lfs.pull
// Runs `git lfs pull` in the project working dir. Separate from
// `git.pull` because the smudge filter is slow and gets its own task
// for explicit scheduling / parallel sequencing.
//
// Outputs: none (lfs progress streams through to log.txt verbatim)
open Takatora.Tasks

Step.run "git lfs pull" (fun () ->
    Cmd.execIn Project.workingDir "git" [ "lfs"; "pull" ]
)
