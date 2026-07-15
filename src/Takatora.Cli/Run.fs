module Takatora.Cli.Run

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Takatora.Core

/// Output format for the `run` command. JSON is for CI of CI
/// scenarios that pipe the result into `jq` or similar.
type Format = Human | Json

let parseFormat (s: string) : Result<Format, string> =
    match s with
    | null | "" | "human" -> Ok Human
    | "json" -> Ok Json
    | other -> Error $"unknown format '{other}', expected human | json"

// ─── --var KEY=VALUE parsing ──────────────────────────────────────

let private parseVarArg (raw: string) : Result<string * TomlValue, string> =
    let idx = raw.IndexOf('=')
    if idx <= 0 then
        Error $"--var must be KEY=VALUE, got: {raw}"
    else
        let k = raw.Substring(0, idx).Trim()
        let v = raw.Substring(idx + 1)
        // Heuristic typing: bool > int > float > string. Matches common
        // CLI conventions (`--var clean_first=true`, `--var count=3`).
        let typed =
            match v.Trim() with
            | "true"  -> TBool true
            | "false" -> TBool false
            | s ->
                let mutable iv = 0L
                let mutable fv = 0.0
                if Int64.TryParse(s, System.Globalization.NumberStyles.Integer,
                                  System.Globalization.CultureInfo.InvariantCulture, &iv) then
                    TInt iv
                elif Double.TryParse(s, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, &fv) then
                    TFloat fv
                else
                    TString v
        Ok (k, typed)

let parseVars (raws: string seq) : Result<Map<string, TomlValue>, string> =
    let mutable acc = Map.empty
    let mutable err = None
    for raw in raws do
        if Option.isNone err then
            match parseVarArg raw with
            | Ok (k, v) -> acc <- Map.add k v acc
            | Error msg -> err <- Some msg
    match err with
    | Some e -> Error e
    | None -> Ok acc

// ─── Resolve dev/release SDK + builtin paths ──────────────────────

/// In a published `dotnet build` / `dotnet publish` layout, the SDK
/// dll and the `builtin-tasks/` folder live next to the runner exe
/// because Takatora.Cli ProjectReferences both Tasks projects.
let defaultSdkAssemblyPath () =
    Path.Combine(AppContext.BaseDirectory, "Takatora.Tasks.dll")

let defaultBuiltinTasksDir () =
    Path.Combine(AppContext.BaseDirectory, "builtin-tasks")

/// User-level task overrides: `%APPDATA%\Takatora\tasks`.
let defaultUserTasksDir () =
    Path.Combine(
        Environment.GetFolderPath Environment.SpecialFolder.ApplicationData,
        "Takatora", "tasks")

/// Accept a CLI `<project>` argument as either a registered name (see
/// `takatora project add`) or a filesystem path to a working dir
/// containing `.takatora/`. Returns the project root or None on miss.
/// Shared by `run`, `history`, `show-run`, `replay-run`.
let resolveProject (nameOrPath: string) : string option =
    match Takatora.Core.ProjectRegistry.find nameOrPath (Takatora.Core.ProjectRegistry.load ()) with
    | Some entry -> Some entry.Path
    | None ->
        let abs = Path.GetFullPath(nameOrPath)
        if Directory.Exists(Path.Combine(abs, ".takatora")) then Some abs
        else None

// ─── Execute + format ─────────────────────────────────────────────

let runResultToExitCode (outcome: RunOutcome) =
    match outcome.Result with
    | RunResult.Success   -> 0
    | RunResult.Failure   -> 1
    | RunResult.Cancelled -> 4

let failureToExitCode (failure: RunFailure) =
    match failure with
    | RunFailure.FlowNotFound _   -> 3
    | RunFailure.ConfigError _    -> 2
    | RunFailure.TaskNotFound _   -> 1
    | RunFailure.InternalError _  -> 5

let private describeStatus (s: StepStatus) =
    match s with
    | StepStatus.Success   -> "✓"
    | StepStatus.Failure _ -> "✗"
    | StepStatus.Skipped _ -> "⊘"
    | StepStatus.Cancelled -> "⊗"

let private engineKindString = function
    | EngineKind.Unreal -> "unreal"
    | EngineKind.Unity  -> "unity"
    | EngineKind.Godot  -> "godot"

let rec tomlValueToJson (v: TomlValue) : JsonNode =
    match v with
    | TString s -> JsonValue.Create(s)
    | TBool b   -> JsonValue.Create(b)
    | TInt i    -> JsonValue.Create(i)
    | TFloat f  -> JsonValue.Create(f)
    | TArray xs ->
        let arr = JsonArray()
        for x in xs do arr.Add(tomlValueToJson x)
        arr
    | TTable m ->
        let obj = JsonObject()
        for KeyValue (k, v) in m do obj.[k] <- tomlValueToJson v
        obj

let private outcomeToJson (outcome: RunOutcome) : string =
    let root = JsonObject()
    root.["schema_version"] <- JsonValue.Create(Takatora.Core.Version.RunSchemaVersion)
    root.["run_id"]   <- JsonValue.Create(outcome.RunId)
    root.["flow_id"]  <- JsonValue.Create(outcome.FlowId)
    root.["result"]   <-
        JsonValue.Create(
            match outcome.Result with
            | RunResult.Success -> "success"
            | RunResult.Failure -> "failure"
            | RunResult.Cancelled -> "cancelled")
    root.["started_at"]   <- JsonValue.Create(outcome.StartedAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture))
    root.["finished_at"]  <- JsonValue.Create(outcome.FinishedAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture))
    root.["duration_sec"] <- JsonValue.Create((outcome.FinishedAt - outcome.StartedAt).TotalSeconds)
    root.["run_dir"]      <- JsonValue.Create(outcome.RunDir)
    let stepsArr = JsonArray()
    for s in outcome.Steps do
        let step = JsonObject()
        step.["id"]   <- JsonValue.Create(s.Id)
        step.["type"] <- JsonValue.Create(s.Type)
        step.["status"] <-
            JsonValue.Create(
                match s.Status with
                | StepStatus.Success    -> "success"
                | StepStatus.Failure _  -> "failure"
                | StepStatus.Skipped _  -> "skipped"
                | StepStatus.Cancelled  -> "cancelled")
        step.["duration_sec"] <- JsonValue.Create(s.DurationSec)
        match s.Status with
        | StepStatus.Failure msg -> step.["message"] <- JsonValue.Create(msg)
        | StepStatus.Skipped r   -> step.["reason"]  <- JsonValue.Create(r)
        | _ -> ()
        let outs = JsonObject()
        for KeyValue (k, v) in s.Outputs do outs.[k] <- tomlValueToJson v
        step.["outputs"] <- outs
        stepsArr.Add(step)
    root.["steps"] <- stepsArr
    root.ToJsonString(JsonSerializerOptions(WriteIndented = true))

let private planToJson (plan: RunPlan) : string =
    let root = JsonObject()
    root.["flow_id"]      <- JsonValue.Create(plan.FlowId)
    root.["project_name"] <- JsonValue.Create(plan.Project.Name)
    let engine = JsonObject()
    engine.["type"] <- JsonValue.Create(engineKindString plan.Project.Engine.Kind)
    plan.Project.Engine.EnginePath
    |> Option.iter (fun p -> engine.["path"] <- JsonValue.Create(p))
    plan.Project.Engine.EngineVersion
    |> Option.iter (fun v -> engine.["version"] <- JsonValue.Create(v))
    root.["engine"] <- engine
    let vars = JsonObject()
    for KeyValue (k, v) in plan.Vars do vars.[k] <- tomlValueToJson v
    root.["vars"] <- vars
    let overridden = JsonArray()
    for k in plan.OverriddenKeys do overridden.Add(JsonValue.Create(k))
    root.["overridden_keys"] <- overridden
    let stepsArr = JsonArray()
    for s in plan.Steps do
        let step = JsonObject()
        step.["index"]      <- JsonValue.Create(s.Index)
        step.["id"]         <- JsonValue.Create(s.Id)
        step.["type"]       <- JsonValue.Create(s.Type)
        match s.TaskPath with
        | Some p -> step.["task_path"] <- JsonValue.Create(p)
        | None -> step.["task_path"] <- null
        match s.SkipReason with
        | Some r -> step.["skip_reason"] <- JsonValue.Create(r)
        | None -> step.["skip_reason"] <- null
        stepsArr.Add(step)
    root.["steps"] <- stepsArr
    root.ToJsonString(JsonSerializerOptions(WriteIndented = true))

/// Pretty-print a run's outcome to stdout. Stderr stays empty for the
/// success path; failures put a one-line summary there too so CI of CI
/// can grep.
let formatOutcome (outcome: RunOutcome) : string * string =
    let sb = System.Text.StringBuilder()
    let resultStr =
        match outcome.Result with
        | RunResult.Success -> "success"
        | RunResult.Failure -> "failure"
        | RunResult.Cancelled -> "cancelled"
    let totalSec = (outcome.FinishedAt - outcome.StartedAt).TotalSeconds
    sb.AppendLine($"Run: {outcome.RunId}") |> ignore
    sb.AppendLine($"Flow: {outcome.FlowId}") |> ignore
    sb.AppendLine($"Result: {resultStr}") |> ignore
    sb.AppendLine(sprintf "Duration: %.2fs" totalSec) |> ignore
    sb.AppendLine($"Run dir: {outcome.RunDir}") |> ignore
    sb.AppendLine() |> ignore
    sb.AppendLine("Steps:") |> ignore
    for s in outcome.Steps do
        let mark = describeStatus s.Status
        let line =
            match s.Status with
            | StepStatus.Success     -> sprintf "  %s %s (%s) — %.2fs" mark s.Id s.Type s.DurationSec
            | StepStatus.Failure msg -> sprintf "  %s %s (%s) — %s" mark s.Id s.Type msg
            | StepStatus.Skipped r   -> sprintf "  %s %s (%s) — skipped (%s)" mark s.Id s.Type r
            | StepStatus.Cancelled   -> sprintf "  %s %s (%s) — cancelled after %.2fs" mark s.Id s.Type s.DurationSec
        sb.AppendLine(line) |> ignore
    let stdout = sb.ToString()
    let stderr =
        match outcome.Result with
        | RunResult.Failure -> $"run {outcome.RunId} failed{Environment.NewLine}"
        | _ -> ""
    stdout, stderr

let formatFailure (failure: RunFailure) : string =
    match failure with
    | RunFailure.FlowNotFound flowId   -> $"run: flow '{flowId}' not found in flows.toml{Environment.NewLine}"
    | RunFailure.TaskNotFound stepType -> $"run: no task .fsx for type '{stepType}'{Environment.NewLine}"
    | RunFailure.ConfigError (src, m)  -> $"run: configuration error in {src}:{Environment.NewLine}  {m}{Environment.NewLine}"
    | RunFailure.InternalError m       -> $"run: internal error: {m}{Environment.NewLine}"

let private renderTomlValue (v: TomlValue) : string =
    match v with
    | TString s -> sprintf "\"%s\"" s
    | TBool b -> if b then "true" else "false"
    | TInt i -> string i
    | TFloat f -> sprintf "%g" f
    | TArray xs -> "[" + (xs |> List.map (fun x ->
        match x with
        | TString s -> sprintf "\"%s\"" s
        | TBool b -> if b then "true" else "false"
        | TInt i -> string i
        | TFloat f -> sprintf "%g" f
        | _ -> "...") |> String.concat ", ") + "]"
    | TTable _ -> "{...}"

let formatPlan (plan: RunPlan) : string =
    let sb = System.Text.StringBuilder()
    sb.AppendLine($"Flow: {plan.FlowId}") |> ignore
    sb.AppendLine($"Project: {plan.Project.Name}") |> ignore
    let engine = plan.Project.Engine
    let kindStr =
        match engine.Kind with
        | EngineKind.Unreal -> "unreal"
        | EngineKind.Unity  -> "unity"
        | EngineKind.Godot  -> "godot"
    sb.AppendLine(sprintf "Engine: %s%s%s" kindStr
                    (engine.EngineVersion |> Option.map (fun v -> $" {v}") |> Option.defaultValue "")
                    (engine.EnginePath    |> Option.map (fun p -> $" — {p}") |> Option.defaultValue " (not detected)"))
    |> ignore
    sb.AppendLine() |> ignore

    if Map.isEmpty plan.Vars then
        sb.AppendLine("Vars: (none)") |> ignore
    else
        sb.AppendLine("Vars (effective):") |> ignore
        for KeyValue (k, v) in plan.Vars do
            let mark =
                if Set.contains k plan.OverriddenKeys then "*" else " "
            sb.AppendLine(sprintf "  %s %-15s = %s" mark k (renderTomlValue v)) |> ignore
        if not (Set.isEmpty plan.OverriddenKeys) then
            sb.AppendLine("  (* = overridden via --var)") |> ignore
    sb.AppendLine() |> ignore

    let runnable, skipped =
        plan.Steps |> List.partition (fun s -> Option.isNone s.SkipReason)

    if List.isEmpty runnable then
        sb.AppendLine("Steps to execute: (none)") |> ignore
    else
        sb.AppendLine("Steps to execute:") |> ignore
        for s in runnable do
            sb.AppendLine(sprintf "  %d. %s (%s)" s.Index s.Id s.Type) |> ignore

    if not (List.isEmpty skipped) then
        sb.AppendLine() |> ignore
        sb.AppendLine("Steps to skip:") |> ignore
        for s in skipped do
            sb.AppendLine(sprintf "  - %s (%s) — %s" s.Id s.Type
                            (s.SkipReason |> Option.defaultValue "")) |> ignore

    sb.ToString()

/// Glue: build Run.Options, call Run.execute (or Run.plan if --dry-run),
/// render the outcome in the requested format. `projectArg` accepts a
/// registered name OR a filesystem path.
let invoke
        (projectArg: string)
        (flowId: string)
        (varRaw: string seq)
        (dryRun: bool)
        (format: Format)
        : int =
    match resolveProject projectArg with
    | None ->
        Console.Error.WriteLine(
            sprintf "run: '%s' is not a registered name and does not contain a .takatora/ directory"
                projectArg)
        3
    | Some workingDir ->

    match parseVars varRaw with
    | Error msg ->
        Console.Error.WriteLine($"run: {msg}")
        2
    | Ok overrides ->
        let opts : Run.Options = {
            WorkingDir = workingDir
            FlowId = flowId
            VarOverrides = overrides
            SdkAssemblyPath = defaultSdkAssemblyPath ()
            BuiltinTasksDir = defaultBuiltinTasksDir ()
            UserTasksDir = None
        }
        if dryRun then
            match Run.plan opts with
            | Error f ->
                Console.Error.Write(formatFailure f)
                failureToExitCode f
            | Ok plan ->
                let text =
                    match format with
                    | Human -> formatPlan plan
                    | Json  -> planToJson plan + Environment.NewLine
                Console.Out.Write(text)
                0
        else
            match Run.execute opts with
            | Error f ->
                Console.Error.Write(formatFailure f)
                failureToExitCode f
            | Ok outcome ->
                match format with
                | Human ->
                    let stdout, stderr = formatOutcome outcome
                    Console.Out.Write(stdout)
                    if not (String.IsNullOrEmpty stderr) then Console.Error.Write(stderr)
                | Json ->
                    Console.Out.WriteLine(outcomeToJson outcome)
                runResultToExitCode outcome
