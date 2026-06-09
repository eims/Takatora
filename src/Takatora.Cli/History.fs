module Takatora.Cli.History

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json.Nodes
open Takatora.Core

type Format = Human | Json

let parseFormat (s: string) : Result<Format, string> =
    match s with
    | null | "" | "human" -> Ok Human
    | "json" -> Ok Json
    | other -> Error (sprintf "unknown format '%s', expected human | json" other)

// Project resolution is shared with Run.fs — keep one implementation.
let resolveProject = Run.resolveProject

let private resultMark = function
    | "success"   -> "✓"
    | "failure"   -> "✗"
    | "cancelled" -> "⊗"
    | _           -> "?"

// ─── history ──────────────────────────────────────────────────────

let private historyToHuman (entries: RunHistoryEntry list) : string =
    if List.isEmpty entries then
        "(no runs yet)\n"
    else
        let sb = StringBuilder()
        for e in entries do
            sb.AppendLine(
                sprintf "%s %s  %s  %-10s  %.1fs  %s"
                    (resultMark e.Result)
                    (e.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss",
                                                         CultureInfo.InvariantCulture))
                    e.RunId
                    e.FlowId
                    e.DurationSec
                    e.Trigger)
            |> ignore
        sb.ToString()

let private historyToJson (entries: RunHistoryEntry list) : string =
    let root = JsonObject()
    let arr = JsonArray()
    for e in entries do
        let item = JsonObject()
        item.["schema_version"] <- JsonValue.Create(e.SchemaVersion)
        item.["run_id"]       <- JsonValue.Create(e.RunId)
        item.["flow_id"]      <- JsonValue.Create(e.FlowId)
        item.["started_at"]   <- JsonValue.Create(e.StartedAt.ToString("o", CultureInfo.InvariantCulture))
        match e.FinishedAt with
        | Some f -> item.["finished_at"] <- JsonValue.Create(f.ToString("o", CultureInfo.InvariantCulture))
        | None -> ()
        item.["duration_sec"] <- JsonValue.Create(e.DurationSec)
        item.["result"]       <- JsonValue.Create(e.Result)
        item.["trigger"]      <- JsonValue.Create(e.Trigger)
        item.["run_dir"]      <- JsonValue.Create(e.RunDir)
        arr.Add(item)
    root.["runs"] <- arr
    root.ToJsonString(System.Text.Json.JsonSerializerOptions(WriteIndented = true))

let invokeHistory (project: string) (flowFilter: string option) (limit: int) (format: Format) : int =
    match resolveProject project with
    | None ->
        Console.Error.WriteLine(sprintf "history: '%s' is not a registered name or a directory with .takatora/" project)
        3
    | Some root ->
        let entries =
            RunHistory.load root
            |> match flowFilter with
               | Some f -> List.filter (fun e -> e.FlowId = f)
               | None -> id
            |> List.truncate limit
        let text =
            match format with
            | Human -> historyToHuman entries
            | Json  -> historyToJson  entries + Environment.NewLine
        Console.Out.Write(text)
        0

// ─── show-run ─────────────────────────────────────────────────────

let private showToHuman (entry: RunHistoryEntry) (steps: StepSummary list) : string =
    let sb = StringBuilder()
    sb.AppendLine(sprintf "Run:        %s" entry.RunId) |> ignore
    sb.AppendLine(sprintf "Flow:       %s" entry.FlowId) |> ignore
    sb.AppendLine(sprintf "Result:     %s %s" (resultMark entry.Result) entry.Result) |> ignore
    sb.AppendLine(sprintf "Started:    %s"
                    (entry.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss zzz",
                                                             CultureInfo.InvariantCulture)))
    |> ignore
    match entry.FinishedAt with
    | Some f ->
        sb.AppendLine(sprintf "Finished:   %s"
                        (f.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss zzz",
                                                   CultureInfo.InvariantCulture)))
        |> ignore
    | None -> ()
    sb.AppendLine(sprintf "Duration:   %.2fs" entry.DurationSec) |> ignore
    sb.AppendLine(sprintf "Trigger:    %s" entry.Trigger) |> ignore
    sb.AppendLine(sprintf "Run dir:    %s" entry.RunDir) |> ignore
    sb.AppendLine(sprintf "  log:      %s" (Path.Combine(entry.RunDir, "log.txt"))) |> ignore
    sb.AppendLine(sprintf "  events:   %s" (Path.Combine(entry.RunDir, "events.ndjson"))) |> ignore
    sb.AppendLine() |> ignore
    if not (Map.isEmpty entry.Params) then
        sb.AppendLine("Params:") |> ignore
        for KeyValue (k, v) in entry.Params do
            let lit =
                match v with
                | TString s -> sprintf "\"%s\"" s
                | TInt i -> string i
                | TFloat f -> sprintf "%g" f
                | TBool b -> if b then "true" else "false"
                | _ -> sprintf "%A" v
            sb.AppendLine(sprintf "  %-15s = %s" k lit) |> ignore
        sb.AppendLine() |> ignore
    if not (List.isEmpty steps) then
        sb.AppendLine(sprintf "Steps (%d):" (List.length steps)) |> ignore
        for s in steps do
            let detail =
                match s.Status with
                | "success"   -> sprintf "%.2fs" s.DurationSec
                | "skipped"   -> sprintf "skipped — %s" (Option.defaultValue "" s.Reason)
                | "failure"   -> sprintf "failed — %s" (Option.defaultValue "" s.Message)
                | "cancelled" -> sprintf "cancelled after %.2fs" s.DurationSec
                | other       -> other
            sb.AppendLine(sprintf "  %s %s (%s) — %s" (resultMark s.Status) s.Id s.Type detail) |> ignore
    sb.ToString()

let private showToJson (entry: RunHistoryEntry) (steps: StepSummary list) : string =
    let root = JsonObject()
    root.["schema_version"] <- JsonValue.Create(entry.SchemaVersion)
    root.["run_id"]       <- JsonValue.Create(entry.RunId)
    root.["flow_id"]      <- JsonValue.Create(entry.FlowId)
    root.["result"]       <- JsonValue.Create(entry.Result)
    root.["trigger"]      <- JsonValue.Create(entry.Trigger)
    root.["started_at"]   <- JsonValue.Create(entry.StartedAt.ToString("o", CultureInfo.InvariantCulture))
    match entry.FinishedAt with
    | Some f -> root.["finished_at"] <- JsonValue.Create(f.ToString("o", CultureInfo.InvariantCulture))
    | None -> ()
    root.["duration_sec"] <- JsonValue.Create(entry.DurationSec)
    root.["run_dir"]      <- JsonValue.Create(entry.RunDir)
    let paramsObj = JsonObject()
    for KeyValue (k, v) in entry.Params do
        let node : JsonNode =
            match v with
            | TString s -> JsonValue.Create(s)
            | TInt i    -> JsonValue.Create(i)
            | TFloat f  -> JsonValue.Create(f)
            | TBool b   -> JsonValue.Create(b)
            | _         -> JsonValue.Create(sprintf "%A" v)
        paramsObj.[k] <- node
    root.["params"] <- paramsObj
    let stepsArr = JsonArray()
    for s in steps do
        let item = JsonObject()
        item.["id"]           <- JsonValue.Create(s.Id)
        item.["type"]         <- JsonValue.Create(s.Type)
        item.["status"]       <- JsonValue.Create(s.Status)
        item.["duration_sec"] <- JsonValue.Create(s.DurationSec)
        s.Message |> Option.iter (fun m -> item.["message"] <- JsonValue.Create(m))
        s.Reason  |> Option.iter (fun r -> item.["reason"]  <- JsonValue.Create(r))
        stepsArr.Add(item)
    root.["step_summary"] <- stepsArr
    root.ToJsonString(System.Text.Json.JsonSerializerOptions(WriteIndented = true))

let invokeShowRun (project: string) (runId: string) (format: Format) : int =
    match resolveProject project with
    | None ->
        Console.Error.WriteLine(sprintf "show-run: '%s' is not a registered name or a directory with .takatora/" project)
        3
    | Some root ->
        match RunHistory.findRun root runId with
        | None ->
            Console.Error.WriteLine(sprintf "show-run: run '%s' not found under %s" runId root)
            3
        | Some (entry, steps) ->
            let text =
                match format with
                | Human -> showToHuman entry steps
                | Json  -> showToJson  entry steps + Environment.NewLine
            Console.Out.Write(text)
            0

// ─── replay-run ───────────────────────────────────────────────────

/// Re-run the same flow with the exact var values from a prior run.
/// "Same params" semantics — current flow var defaults are bypassed
/// in favor of the historical record, so a flow whose defaults changed
/// since doesn't quietly drift on replay.
let invokeReplay (project: string) (runId: string) : int =
    match resolveProject project with
    | None ->
        Console.Error.WriteLine(sprintf "replay-run: '%s' is not a registered name or a directory with .takatora/" project)
        3
    | Some root ->
        match RunHistory.findRun root runId with
        | None ->
            Console.Error.WriteLine(sprintf "replay-run: run '%s' not found under %s" runId root)
            3
        | Some (entry, _) ->
            Console.Out.WriteLine(
                sprintf "Replaying %s (flow %s) with %d param(s) from the original run"
                    runId entry.FlowId (Map.count entry.Params))
            let opts : Run.Options = {
                WorkingDir = root
                FlowId = entry.FlowId
                VarOverrides = entry.Params
                SdkAssemblyPath = Run.defaultSdkAssemblyPath ()
                BuiltinTasksDir = Run.defaultBuiltinTasksDir ()
                UserTasksDir = None
            }
            match Run.execute opts with
            | Error f ->
                Console.Error.Write(Run.formatFailure f)
                Run.failureToExitCode f
            | Ok outcome ->
                let stdout, stderr = Run.formatOutcome outcome
                Console.Out.Write(stdout)
                if not (String.IsNullOrEmpty stderr) then Console.Error.Write(stderr)
                Run.runResultToExitCode outcome
