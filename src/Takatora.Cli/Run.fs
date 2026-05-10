module Takatora.Cli.Run

open System
open System.IO
open Takatora.Core

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

// ─── Execute + format ─────────────────────────────────────────────

let private runResultToExitCode (outcome: RunOutcome) =
    match outcome.Result with
    | RunResult.Success   -> 0
    | RunResult.Failure   -> 1
    | RunResult.Cancelled -> 4

let private failureToExitCode (failure: RunFailure) =
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
/// render the outcome.
let invoke (workingDir: string) (flowId: string) (varRaw: string seq) (dryRun: bool) : int =
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
                Console.Out.Write(formatPlan plan)
                0
        else
            match Run.execute opts with
            | Error f ->
                Console.Error.Write(formatFailure f)
                failureToExitCode f
            | Ok outcome ->
                let stdout, stderr = formatOutcome outcome
                Console.Out.Write(stdout)
                if not (String.IsNullOrEmpty stderr) then Console.Error.Write(stderr)
                runResultToExitCode outcome
