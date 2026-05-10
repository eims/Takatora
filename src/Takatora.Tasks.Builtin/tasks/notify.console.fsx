// Built-in task: notify.console
// Writes a message to the run log. Trivial; serves as the smoke-test
// that the .fsx execution pipeline works end-to-end.
//
// The runner injects the Takatora.Tasks SDK via a generated wrapper
// `#r`, so this script doesn't need its own reference. Authors writing
// project-local tasks under `.ci/tasks/` follow the same convention.
open Takatora.Tasks

let message = Param.required<string> "message"

Step.run "notify" (fun () ->
    Log.info message
)
