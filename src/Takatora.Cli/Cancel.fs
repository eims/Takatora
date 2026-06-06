module Takatora.Cli.Cancel

open System
open System.IO

/// Drop a CANCEL flag in `<workingDir>/.takatora/runs/<runId>/CANCEL`. The
/// runner polls for it during step execution and kills the spawned
/// fsi process tree. This is presence-only — the file's content is
/// ignored, so any process (CLI / GUI / external script) can request
/// cancellation through the same mechanism.
let invoke (workingDir: string) (runId: string) : int =
    let runsRoot = Path.Combine(workingDir, ".takatora", "runs")
    let runDir = Path.Combine(Path.GetFullPath workingDir, ".takatora", "runs", runId)
    if not (Directory.Exists runDir) then
        Console.Error.WriteLine($"cancel: run '{runId}' not found under {runsRoot}")
        3
    else
        let cancelPath = Path.Combine(runDir, "CANCEL")
        try
            File.WriteAllText(cancelPath, "")
            Console.Out.WriteLine($"cancel requested for {runId}")
            0
        with ex ->
            Console.Error.WriteLine($"cancel: failed to write {cancelPath}: {ex.Message}")
            5
