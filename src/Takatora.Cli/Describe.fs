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
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.Environment.["TAKATORA_MODE"]            <- "describe"
        psi.Environment.["TAKATORA_DESCRIBE_OUTPUT"] <- outputPath
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

let invoke (taskType: string) : int =
    let builtinDir = Run.defaultBuiltinTasksDir ()
    let sdkPath    = Run.defaultSdkAssemblyPath ()
    // Project-local + user-level lookups need a project context, which
    // describe doesn't take. Future enhancement: accept `--project` to
    // resolve project-local task overrides. For now, builtin-only.
    match TaskResolver.resolve "." None builtinDir taskType with
    | None ->
        Console.Error.WriteLine(
            sprintf "describe: no task .fsx found for type '%s' under %s" taskType builtinDir)
        3
    | Some resolved ->
        match spawnAndCapture sdkPath resolved.Path with
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
