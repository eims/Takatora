module Takatora.Cli.Validate

open System.IO
open System.Text
open Takatora.Core

/// Outcome of running `validate` against a project working directory.
type Outcome =
    | Valid of Project * Flow list * ProjectParam list * warnings: string list
    | MissingFile of path: string
    | ConfigError of source: string * message: string

let private ciDir (workingDir: string) = Path.Combine(workingDir, ".takatora")
let private projectFile (workingDir: string) = Path.Combine(ciDir workingDir, "project.toml")
let private flowsFile (workingDir: string) = Path.Combine(ciDir workingDir, "flows.toml")

/// Non-fatal findings on the params/flows combination. Shadowing matters
/// because the TAKATORA_SECRET_<name> env namespace is flat: a flow var
/// and a param with the same name collide there (the flow var wins).
let private paramWarnings (flows: Flow list) (ps: ProjectParam list) : string list =
    let paramNames = ps |> List.map (fun p -> p.Name) |> Set.ofList
    let shadowing =
        [ for f in flows do
            for v in f.Vars do
                if Set.contains v.Name paramNames then
                    yield sprintf "flow '%s' var '%s' shadows a shared param of the same name (TAKATORA_SECRET_* env is flat; the flow var wins there)" f.Id v.Name ]
    let referenced =
        flows |> List.fold (fun acc f -> Set.union acc (Params.referencedIn f)) Set.empty
    let unused =
        ps
        |> List.filter (fun p -> not (Set.contains p.Name referenced))
        |> List.map (fun p -> sprintf "param '%s' is declared but no flow references it" p.Name)
    shadowing @ unused

/// Read project.toml + flows.toml (+ optional params.toml) under
/// `<workingDir>/.takatora/` and run them through `TomlConfig`. Pure with
/// respect to stdout/stderr — formatting is the caller's responsibility.
let run (workingDir: string) : Outcome =
    let projectPath = projectFile workingDir
    let flowsPath = flowsFile workingDir
    let paramsPath = Params.paramsPath workingDir
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
            let flowsResult =
                try Result.Ok (TomlConfig.loadFlows flowsPath)
                with TomlConfigError msg -> Result.Error (flowsPath, msg)
            match flowsResult with
            | Result.Error (src, msg) -> ConfigError (src, msg)
            | Result.Ok flows ->
                let paramsResult =
                    try Result.Ok (Params.load workingDir)
                    with TomlConfigError msg -> Result.Error (paramsPath, msg)
                match paramsResult with
                | Result.Error (src, msg) -> ConfigError (src, msg)
                | Result.Ok ps ->
                    // A `${params.X}` reference to an undeclared param is a
                    // hard error — the runner refuses it too.
                    let declared = ps |> List.map (fun p -> p.Name) |> Set.ofList
                    let undeclared =
                        [ for f in flows do
                            let missing = Set.difference (Params.referencedIn f) declared
                            if not (Set.isEmpty missing) then
                                yield f.Id, missing ]
                    match undeclared with
                    | (flowId, missing) :: _ ->
                        let names = missing |> Set.toList |> String.concat ", "
                        ConfigError (
                            paramsPath,
                            sprintf "flow '%s' references undeclared param(s): %s — declare them in .takatora/params.toml" flowId names)
                    | [] ->
                        Valid (project, flows, ps, paramWarnings flows ps)

/// Render an outcome to (stdout-text, stderr-text, exit-code) so the
/// caller can pipe to whichever writers they like. Exit codes follow the
/// runner-cli convention: 0 valid, 2 config error, 3 missing files.
let format (outcome: Outcome) : string * string * int =
    match outcome with
    | Valid (project, flows, ps, warnings) ->
        let sb = StringBuilder()
        let engine =
            match project.Engine.Kind with
            | EngineKind.Unreal -> "unreal"
            | EngineKind.Unity  -> "unity"
            | EngineKind.Godot  -> "godot"
        sb.AppendLine($"{project.Name} ({engine}): valid") |> ignore
        sb.AppendLine($"  working_dir: {project.WorkingDir}") |> ignore
        if not (List.isEmpty ps) then
            let secretCount = ps |> List.filter (fun p -> p.Kind = VarKind.Secret) |> List.length
            sb.AppendLine($"  params: {List.length ps} ({secretCount} secret)") |> ignore
        sb.AppendLine($"  flows: {List.length flows}") |> ignore
        for f in flows do
            let display =
                f.Name
                |> Option.map (fun n -> $"{f.Id} — {n}")
                |> Option.defaultValue f.Id
            sb.AppendLine(
                $"    - {display} ({List.length f.Steps} step(s), {List.length f.Vars} var(s))"
            ) |> ignore
        for w in warnings do
            sb.AppendLine($"  warning: {w}") |> ignore
        sb.ToString(), "", 0

    | MissingFile path ->
        "", $"validate: required config file not found: {path}{System.Environment.NewLine}", 3

    | ConfigError (source, msg) ->
        let sb = StringBuilder()
        sb.AppendLine($"validate: configuration error in {source}:") |> ignore
        sb.AppendLine($"  {msg}") |> ignore
        "", sb.ToString(), 2
