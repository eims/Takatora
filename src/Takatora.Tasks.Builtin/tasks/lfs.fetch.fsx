// Built-in task: lfs.fetch
// Pre-warms the LFS cache without checkout — useful as an early step
// before a `git.checkout` so the actual checkout's smudge filter is
// instant.
//
// Outputs: none
open Takatora.Tasks

Step.run "git lfs fetch" (fun () ->
    Cmd.execIn Project.workingDir "git" [ "lfs"; "fetch" ]
)
