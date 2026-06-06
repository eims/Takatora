module Takatora.Cli.Describe

open System
open System.Diagnostics
open System.IO
open Takatora.Core

let invoke (taskType: string) (project: string option) : int =
    let builtinDir = Run.defaultBuiltinTasksDir ()
    let sdkPath    = Run.defaultSdkAssemblyPath ()
    // With --project, resolve project-local (.takatora/tasks) + user-level
    // overrides against that root; otherwise builtin-only (cwd).
    let resolvedRoot =
        match project with
        | None -> Ok "."
        | Some p ->
            match Run.resolveProject p with
            | Some root -> Ok root
            | None -> Error (sprintf "describe: project '%s' not found (registered name or a dir with .takatora/)" p)
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
        // a temp dir, so a project-local task resolved as ".\.takatora\tasks\…"
        // wouldn't be found relative to that temp dir.
        match Takatora.Core.Describe.spawnJson sdkPath (Path.GetFullPath resolved.Path) taskType with
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
