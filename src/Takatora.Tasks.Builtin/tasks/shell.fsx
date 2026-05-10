// Built-in task: shell
// Escape hatch for any command not covered by a dedicated task type.
// Stdout/stderr stream to the run's log.txt verbatim.
//
// Params:
//   command            string         — passed to cmd.exe /c on Windows,
//                                       /bin/sh -c elsewhere
//   working_dir?       string         — defaults to Project.workingDir
//   ignore_exit_codes? list<int>      — exit codes to treat as success
//                                       (e.g. [1] for robocopy)
//
// Outputs: none (use shell.capture in the future for stdout-bearing variants)
open Takatora.Tasks
open System.Runtime.InteropServices

let command         = Param.required<string>   "command"
let workingDir      = Param.optional<string>    "working_dir" Project.workingDir
// `int array` not `int list`: System.Text.Json doesn't deserialize
// FSharpList without a custom converter; arrays go through cleanly.
let ignoreExitCodes = Param.optional<int array> "ignore_exit_codes" [||]

let isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
let exe, args =
    if isWindows then "cmd.exe", [ "/c"; command ]
    else "/bin/sh",   [ "-c"; command ]

Step.run "shell" (fun () ->
    let opts =
        { ExecOptions.empty with
            WorkingDir = Some workingDir
            IgnoreExitCodes = List.ofArray ignoreExitCodes }
    Cmd.execWith opts exe args
)
