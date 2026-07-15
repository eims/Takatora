namespace Takatora.Core

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Tomlyn.Model

/// One line of run history, derived from `<wd>/.takatora/runs/<id>/manifest.toml`.
/// Captures enough for listing + replay; full details still live in the
/// run dir for show-run to render.
type RunHistoryEntry = {
    /// On-disk run-record schema version (`manifest.toml`'s `schema_version`).
    /// Runs written before the field existed report `1` (the original layout).
    SchemaVersion: int
    RunId: string
    FlowId: string
    StartedAt: DateTimeOffset
    FinishedAt: DateTimeOffset option
    DurationSec: float
    Result: string
    Trigger: string
    Params: Map<string, TomlValue>
    RunDir: string
}

/// Per-step record as serialized in manifest.toml's `[[step_summary]]`.
/// Re-read here rather than reusing Run.StepRecord because the on-disk
/// representation is simpler (just the fields we wrote).
type StepSummary = {
    Id: string
    Type: string
    Status: string
    DurationSec: float
    Message: string option
    Reason: string option
}

[<RequireQualifiedAccess>]
module RunHistory =

    let private tryStr (tbl: TomlTable) (key: string) =
        match tbl.TryGetValue(key) with
        | true, (:? string as s) -> Some s
        | _ -> None

    let private tryFloat (tbl: TomlTable) (key: string) =
        match tbl.TryGetValue(key) with
        | true, (:? double as d) -> Some d
        | true, (:? int64 as i)  -> Some (float i)
        | _ -> None

    let private tryInt (tbl: TomlTable) (key: string) =
        match tbl.TryGetValue(key) with
        | true, (:? int64 as i)  -> Some (int i)
        | true, (:? double as d) -> Some (int d)
        | _ -> None

    let private parseDate (s: string) : DateTimeOffset option =
        match DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
        | true, dt -> Some dt
        | _ -> None

    let rec private convertValue (v: obj) : TomlValue =
        match v with
        | :? string as s -> TString s
        | :? bool as b -> TBool b
        | :? int64 as i -> TInt i
        | :? double as d -> TFloat d
        | :? TomlArray as arr -> arr |> Seq.map convertValue |> List.ofSeq |> TArray
        | :? TomlTable as t ->
            t |> Seq.map (fun kv -> kv.Key, convertValue kv.Value) |> Map.ofSeq |> TTable
        | _ -> TString (string v)

    let private parseManifest (runDir: string) (text: string) : RunHistoryEntry option =
        try
            let table =
                Tomlyn.TomlSerializer.Deserialize<TomlTable>(
                    text, Tomlyn.TomlSerializerOptions())
            match tryStr table "run_id", tryStr table "flow_id", tryStr table "started_at" with
            | Some runId, Some flowId, Some startedRaw ->
                match parseDate startedRaw with
                | None -> None
                | Some started ->
                    let finished = tryStr table "finished_at" |> Option.bind parseDate
                    let duration = tryFloat table "duration_sec" |> Option.defaultValue 0.0
                    let result   = tryStr table "result"   |> Option.defaultValue "unknown"
                    let trigger  = tryStr table "trigger"  |> Option.defaultValue "unknown"
                    let paramMap =
                        match table.TryGetValue("params") with
                        | true, (:? TomlTable as p) ->
                            p
                            |> Seq.map (fun kv -> kv.Key, convertValue kv.Value)
                            |> Map.ofSeq
                        | _ -> Map.empty
                    // Missing field → a pre-versioning run, which is v1-shaped.
                    let schemaVersion = tryInt table "schema_version" |> Option.defaultValue 1
                    Some {
                        SchemaVersion = schemaVersion
                        RunId = runId
                        FlowId = flowId
                        StartedAt = started
                        FinishedAt = finished
                        DurationSec = duration
                        Result = result
                        Trigger = trigger
                        Params = paramMap
                        RunDir = runDir
                    }
            | _ -> None
        with _ -> None

    let private parseStepSummaries (text: string) : StepSummary list =
        try
            let table =
                Tomlyn.TomlSerializer.Deserialize<TomlTable>(
                    text, Tomlyn.TomlSerializerOptions())
            match table.TryGetValue("step_summary") with
            | true, (:? TomlTableArray as arr) ->
                arr
                |> Seq.choose (fun t ->
                    match tryStr t "id", tryStr t "type", tryStr t "status" with
                    | Some id, Some ty, Some st ->
                        Some {
                            Id = id
                            Type = ty
                            Status = st
                            DurationSec = tryFloat t "duration_sec" |> Option.defaultValue 0.0
                            Message = tryStr t "message"
                            Reason  = tryStr t "reason"
                        }
                    | _ -> None)
                |> List.ofSeq
            | _ -> []
        with _ -> []

    /// All run entries for a project, newest first. Malformed manifests
    /// are silently skipped — a broken run dir shouldn't break the list.
    let load (projectRoot: string) : RunHistoryEntry list =
        let runsDir = Path.Combine(projectRoot, ".takatora", "runs")
        if not (Directory.Exists runsDir) then []
        else
            Directory.GetDirectories(runsDir)
            |> Array.choose (fun runDir ->
                let manifest = Path.Combine(runDir, "manifest.toml")
                if not (File.Exists manifest) then None
                else parseManifest runDir (File.ReadAllText manifest))
            |> Array.sortByDescending (fun e -> e.StartedAt)
            |> List.ofArray

    /// Find one entry by run id. Returns the entry + its step summaries
    /// so `show-run` can render the full picture in one pass.
    let findRun (projectRoot: string) (runId: string)
            : (RunHistoryEntry * StepSummary list) option =
        let runDir = Path.Combine(projectRoot, ".takatora", "runs", runId)
        let manifest = Path.Combine(runDir, "manifest.toml")
        if not (File.Exists manifest) then None
        else
            let text = File.ReadAllText manifest
            match parseManifest runDir text with
            | None -> None
            | Some entry -> Some (entry, parseStepSummaries text)

    /// A run's full `log.txt` (so a past run is reviewable in RunDetail —
    /// a tail would hide errors that don't land at the very end). Capped at
    /// `maxLines` only as a memory guard for a runaway log (keeping the
    /// start, where a failure usually originates). Empty if absent.
    let readLog (projectRoot: string) (runId: string) (maxLines: int) : string list =
        let p = Path.Combine(projectRoot, ".takatora", "runs", runId, "log.txt")
        if not (File.Exists p) then []
        else
            try
                let lines = File.ReadAllLines p
                if lines.Length <= maxLines then List.ofArray lines
                else lines.[.. maxLines - 1] |> List.ofArray
            with _ -> []

    /// Step outputs a run recorded under `<runDir>/outputs/<stepId>.ndjson`
    /// (each line `{"name":…,"value":…}`), keyed by step id → (name → value
    /// rendered as a string). Used by the GUI to surface e.g. a UE package's
    /// `archive_path` so it can be opened. Missing/empty → empty map.
    let runOutputs (projectRoot: string) (runId: string) : Map<string, Map<string, string>> =
        let outDir = Path.Combine(projectRoot, ".takatora", "runs", runId, "outputs")
        if not (Directory.Exists outDir) then Map.empty
        else
            Directory.GetFiles(outDir, "*.ndjson")
            |> Array.choose (fun f ->
                let stepId = Path.GetFileNameWithoutExtension f
                let outs =
                    File.ReadAllLines f
                    |> Array.choose (fun line ->
                        if String.IsNullOrWhiteSpace line then None
                        else
                            try
                                match JsonNode.Parse(line) with
                                | null -> None
                                | node ->
                                    let name = node.["name"].GetValue<string>()
                                    let value =
                                        match node.["value"] with
                                        | null -> ""
                                        | v ->
                                            match v.GetValueKind() with
                                            | JsonValueKind.String -> v.GetValue<string>()
                                            | _ -> v.ToJsonString()
                                    Some (name, value)
                            with _ -> None)
                    |> Map.ofArray
                if Map.isEmpty outs then None else Some (stepId, outs))
            |> Map.ofArray

    let rec private jsonNodeToToml (node: JsonNode) : TomlValue =
        match node with
        | null -> TString ""
        | _ ->
            match node.GetValueKind() with
            | JsonValueKind.String -> TString (node.GetValue<string>())
            | JsonValueKind.True   -> TBool true
            | JsonValueKind.False  -> TBool false
            | JsonValueKind.Number ->
                // Integral JSON numbers stay ints (int64 fidelity); a value
                // written with a fractional part becomes a float.
                match node.AsValue().TryGetValue<int64>() with
                | true, i -> TInt i
                | _       -> TFloat (node.GetValue<double>())
            | JsonValueKind.Array  ->
                node.AsArray() |> Seq.map jsonNodeToToml |> List.ofSeq |> TArray
            | JsonValueKind.Object ->
                node.AsObject() |> Seq.map (fun kv -> kv.Key, jsonNodeToToml kv.Value) |> Map.ofSeq |> TTable
            | _ -> TString (node.ToJsonString())

    /// Like `runOutputs`, but preserves each value's JSON type — a number
    /// stays a number, an array an array — instead of flattening to strings.
    /// For the `show-run --output-format json` contract, which mirrors the
    /// live `run` json's typed `outputs`. Same map shape (step id → name →
    /// value); missing/empty → empty map.
    let runOutputsTyped (projectRoot: string) (runId: string) : Map<string, Map<string, TomlValue>> =
        let outDir = Path.Combine(projectRoot, ".takatora", "runs", runId, "outputs")
        if not (Directory.Exists outDir) then Map.empty
        else
            Directory.GetFiles(outDir, "*.ndjson")
            |> Array.choose (fun f ->
                let stepId = Path.GetFileNameWithoutExtension f
                let outs =
                    File.ReadAllLines f
                    |> Array.choose (fun line ->
                        if String.IsNullOrWhiteSpace line then None
                        else
                            try
                                match JsonNode.Parse(line) with
                                | null -> None
                                | node ->
                                    let name = node.["name"].GetValue<string>()
                                    Some (name, jsonNodeToToml node.["value"])
                            with _ -> None)
                    |> Map.ofArray
                if Map.isEmpty outs then None else Some (stepId, outs))
            |> Map.ofArray
