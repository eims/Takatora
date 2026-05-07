module Takatora.Cli.Validate

open System.IO
open System.Text
open Takatora.Core

/// Outcome of running `validate` against a project working directory.
type Outcome =
    | Valid of Project * Flow list
    | MissingFile of path: string
    | ConfigError of source: string * message: string

let private ciDir (workingDir: string) = Path.Combine(workingDir, ".ci")
let private projectFile (workingDir: string) = Path.Combine(ciDir workingDir, "project.toml")
let private flowsFile (workingDir: string) = Path.Combine(ciDir workingDir, "flows.toml")

/// Read project.toml + flows.toml under `<workingDir>/.ci/` and run them
/// through `TomlConfig`. Pure with respect to stdout/stderr — formatting is
/// the caller's responsibility.
let run (workingDir: string) : Outcome =
    let projectPath = projectFile workingDir
    let flowsPath = flowsFile workingDir
    if not (File.Exists projectPath) then
        MissingFile projectPath
    elif not (File.Exists flowsPath) then
        MissingFile flowsPath
    else
        let projectResult =
            try Result.Ok (TomlConfig.loadProject projectPath)
            with TomlConfigError msg -> Result.Error (projectPath, msg)
        match projectResult with
        | Result.Error (src, msg) -> ConfigError (src, msg)
        | Result.Ok project ->
            try
                let flows = TomlConfig.loadFlows flowsPath
                Valid (project, flows)
            with TomlConfigError msg ->
                ConfigError (flowsPath, msg)

/// Render an outcome to (stdout-text, stderr-text, exit-code) so the
/// caller can pipe to whichever writers they like. Exit codes follow the
/// runner-cli convention: 0 valid, 2 config error, 3 missing files.
let format (outcome: Outcome) : string * string * int =
    match outcome with
    | Valid (project, flows) ->
        let sb = StringBuilder()
        let engine =
            match project.Engine.Kind with
            | EngineKind.Unreal -> "unreal"
            | EngineKind.Unity  -> "unity"
            | EngineKind.Godot  -> "godot"
        sb.AppendLine($"{project.Name} ({engine}): valid") |> ignore
        sb.AppendLine($"  working_dir: {project.WorkingDir}") |> ignore
        sb.AppendLine($"  flows: {List.length flows}") |> ignore
        for f in flows do
            let display =
                f.Name
                |> Option.map (fun n -> $"{f.Id} — {n}")
                |> Option.defaultValue f.Id
            sb.AppendLine(
                $"    - {display} ({List.length f.Steps} step(s), {List.length f.Vars} var(s))"
            ) |> ignore
        sb.ToString(), "", 0

    | MissingFile path ->
        "", $"validate: required config file not found: {path}{System.Environment.NewLine}", 3

    | ConfigError (source, msg) ->
        let sb = StringBuilder()
        sb.AppendLine($"validate: configuration error in {source}:") |> ignore
        sb.AppendLine($"  {msg}") |> ignore
        "", sb.ToString(), 2
