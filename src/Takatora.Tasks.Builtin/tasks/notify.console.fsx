// Built-in task: notify.console
// Writes a message to the run log. Trivial; serves as the smoke-test
// that the .fsx execution pipeline works end-to-end.

#r "nuget: Takatora.Tasks"
open Takatora.Tasks

let message = Param.required<string> "message"

Step.run "notify" (fun () ->
    Log.info message
)
