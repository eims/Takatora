module Takatora.Cli.Describe

open System
open System.Diagnostics
open System.IO
open Takatora.Core

/// Spawn `dotnet fsi wrapper.fsx` with `TAKATORA_MODE=describe` and a
/// designated output file, then return the JSON the SDK wrote on exit.
///
/// The wrapper script is the same shape the runner uses for execution:
///   #r @"<sdk dll>"
///   #load @"<task .fsx>"
/// Describe-mode .fsx files reach Param.* / Output.set declarations the
/// same way they would under a real run, but the SDK switches Step.run
/// + Cmd.* into no-ops so describe doesn't actually do work.
let private spawnAndCapture
        (sdkAssemblyPath: string)
        (taskPath: string)
        (taskType: string)
        : Result<string, string> =
    let tempDir =
        Path.Combine(
            Path.GetTempPath(),
            "takatora-describe",
            Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tempDir) |> ignore
    let wrapperPath = Path.Combine(tempDir, "wrapper.fsx")
    let outputPath  = Path.Combine(tempDir, "describe.json")
    let escape (p: string) = p.Replace("\\", "\\\\").Replace("\"", "\\\"")
    File.WriteAllText(
        wrapperPath,
        sprintf "#r @\"%s\"\n#load @\"%s\"\n" (escape sdkAssemblyPath) (escape taskPath))
    try
        let psi = ProcessStartInfo("dotnet")
        psi.ArgumentList.Add("fsi")
        psi.ArgumentList.Add(wrapperPath)
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true   // no console pop when spawned from a GUI host
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.Environment.["TAKATORA_MODE"]            <- "describe"
        psi.Environment.["TAKATORA_DESCRIBE_OUTPUT"] <- outputPath
        psi.Environment.["TAKATORA_TASK_TYPE"]       <- taskType
        use proc = Process.Start(psi)
        let stdoutTask = proc.StandardOutput.ReadToEndAsync()
        let stderrTask = proc.StandardError.ReadToEndAsync()
        proc.WaitForExit()
        if proc.ExitCode <> 0 then
            Error (
                sprintf "fsi exited %d while inspecting %s:%s%s%s%s"
                    proc.ExitCode taskPath Environment.NewLine
                    stdoutTask.Result Environment.NewLine stderrTask.Result)
        elif not (File.Exists outputPath) then
            Error (sprintf "describe wrote no output for %s (SDK process exit hook didn't fire?)" taskPath)
        else
            Ok (File.ReadAllText outputPath)
    finally
        try Directory.Delete(tempDir, recursive = true) with _ -> ()

let invoke (taskType: string) (project: string option) : int =
    let builtinDir = Run.defaultBuiltinTasksDir ()
    let sdkPath    = Run.defaultSdkAssemblyPath ()
    // With --project, resolve project-local (.ci/tasks) + user-level
    // overrides against that root; otherwise builtin-only (cwd).
    let resolvedRoot =
        match project with
        | None -> Ok "."
        | Some p ->
            match Run.resolveProject p with
            | Some root -> Ok root
            | None -> Error (sprintf "describe: project '%s' not found (registered name or a dir with .ci/)" p)
    match resolvedRoot with
    | Error msg -> Console.Error.WriteLine msg; 2
    | Ok projectRoot ->
    let userTasksDir = if Option.isSome project then Some (Run.defaultUserTasksDir ()) else None
    match TaskResolver.resolve projectRoot userTasksDir builtinDir taskType with
    | None ->
        Console.Error.WriteLine(
            sprintf "describe: no task .fsx found for type '%s' under %s" taskType builtinDir)
        3
    | Some resolved ->
        // Absolute path: the describe wrapper is written to (and #loads from)
        // a temp dir, so a project-local task resolved as ".\.ci\tasks\…"
        // wouldn't be found relative to that temp dir.
        match spawnAndCapture sdkPath (Path.GetFullPath resolved.Path) taskType with
        | Error msg ->
            Console.Error.WriteLine(sprintf "describe: %s" msg)
            5
        | Ok json ->
            // The SDK writes JSON without a trailing newline; add one
            // so terminal output is clean. The fsi process may have
            // also written warnings to its stdout; we ignore those.
            Console.Out.Write(json)
            if not (json.EndsWith("\n")) then Console.Out.WriteLine()
            0
