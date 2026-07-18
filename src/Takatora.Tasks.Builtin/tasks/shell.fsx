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
//   encoding?          string         — how to decode the command's output
//                                       for the log. Defaults to the OS
//                                       native code page (right for the
//                                       engine toolchain). Set "utf-8" for
//                                       tools that always emit UTF-8 (e.g.
//                                       butler / itch upload) so their
//                                       output isn't mojibaked. Also accepts
//                                       a code-page number or name (932,
//                                       shift_jis, …).
//
// Outputs: none (use shell.capture in the future for stdout-bearing variants)
open Takatora.Tasks
open System.Runtime.InteropServices

let command         = Param.required<string>   "command"
let workingDir      = Param.optional<string>    "working_dir" Project.workingDir
// `int array` not `int list`: System.Text.Json doesn't deserialize
// FSharpList without a custom converter; arrays go through cleanly.
let ignoreExitCodes = Param.optional<int array> "ignore_exit_codes" [||]
let encoding        = Param.optional<string>    "encoding" ""

let isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
let exe, args =
    if isWindows then "cmd.exe", [ "/c"; command ]
    else "/bin/sh",   [ "-c"; command ]

Step.run "shell" (fun () ->
    let opts =
        { ExecOptions.empty with
            WorkingDir = Some workingDir
            IgnoreExitCodes = List.ofArray ignoreExitCodes
            // "" → native default; parsed here so an unknown name fails the step.
            Encoding = (if encoding = "" then None else Some (Cmd.encodingByName encoding)) }
    Cmd.execWith opts exe args
)
