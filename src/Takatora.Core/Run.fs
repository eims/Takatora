namespace Takatora.Core

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

// ─── Result types ──────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type StepStatus =
    | Success
    | Failure of message: string
    | Skipped of reason: string
    /// In-flight when a CANCEL signal arrived; the process tree was
    /// killed mid-execution. Distinct from Skipped (which means "never
    /// started") so the audit trail shows where work was lost.
    | Cancelled

type StepRecord = {
    Id: string
    Type: string
    Status: StepStatus
    DurationSec: float
    Outputs: Map<string, TomlValue>
}

[<RequireQualifiedAccess>]
type RunResult =
    | Success
    | Failure
    | Cancelled

type RunOutcome = {
    RunId: string
    FlowId: string
    StartedAt: DateTimeOffset
    FinishedAt: DateTimeOffset
    Result: RunResult
    Steps: StepRecord list
    RunDir: string
}

[<RequireQualifiedAccess>]
type RunFailure =
    | FlowNotFound of flowId: string
    | TaskNotFound of stepType: string
    | ConfigError of source: string * message: string
    | InternalError of message: string

/// One step entry in a `--dry-run` plan. Same shape whether the step
/// would have run or been skipped — `SkipReason` is `Some` for skips
/// (when=false, missing .fsx) and `None` for runnable.
type PlannedStep = {
    Index: int
    Id: string
    Type: string
    TaskPath: string option
    SkipReason: string option
    ResolvedParams: Map<string, TomlValue>
}

/// Output of `Run.plan`. Mirrors the runtime view a real `execute`
/// would have at the start of step execution, minus the actual runs.
type RunPlan = {
    Project: Project
    FlowId: string
    Vars: Map<string, TomlValue>
    /// Which var keys came from `VarOverrides` rather than defaults.
    OverriddenKeys: Set<string>
    Steps: PlannedStep list
}

// ─── Run id ────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
module RunId =
    let private rng = Random.Shared
    /// Format: `r-YYYYMMDDHH-MMSS-rand4`. Sortable, human-skimmable.
    let generate (now: DateTimeOffset) : string =
        let datePart = now.ToString("yyyyMMddHH", CultureInfo.InvariantCulture)
        let timePart = now.ToString("mmss",       CultureInfo.InvariantCulture)
        let rand     = rng.Next(0x10000).ToString("x4")
        $"r-{datePart}-{timePart}-{rand}"

// ─── Task .fsx resolution ──────────────────────────────────────────

[<RequireQualifiedAccess>]
type TaskSource =
    | ProjectLocal
    | UserLocal
    | Builtin

type ResolvedTask = { Path: string; Source: TaskSource }

[<RequireQualifiedAccess>]
module TaskResolver =
    /// Look up `<type>.fsx` in the design's 3-tier order. First hit wins.
    let resolve (workingDir: string)
                (userTasksDir: string option)
                (builtinDir: string)
                (taskType: string) : ResolvedTask option =
        let projectLocal = Path.Combine(workingDir, ".ci", "tasks", $"{taskType}.fsx")
        let userLocal = userTasksDir |> Option.map (fun d -> Path.Combine(d, $"{taskType}.fsx"))
        let builtin = Path.Combine(builtinDir, $"{taskType}.fsx")
        if File.Exists projectLocal then
            Some { Path = projectLocal; Source = TaskSource.ProjectLocal }
        else
            match userLocal with
            | Some p when File.Exists p ->
                Some { Path = p; Source = TaskSource.UserLocal }
            | _ ->
                if File.Exists builtin then
                    Some { Path = builtin; Source = TaskSource.Builtin }
                else
                    None

// ─── Run orchestration ─────────────────────────────────────────────

[<RequireQualifiedAccess>]
module Run =

    type Options = {
        /// Project working directory containing `.ci/`.
        WorkingDir: string
        /// Flow id from `flows.toml`.
        FlowId: string
        /// Override values for flow vars (typed). Keys not declared on
        /// the flow are accepted; runner doesn't enforce schema yet.
        VarOverrides: Map<string, TomlValue>
        /// Absolute path to `Takatora.Tasks.dll` for the wrapper `#r`.
        SdkAssemblyPath: string
        /// Directory holding the built-in `<type>.fsx` files (typically
        /// `<install>/builtin-tasks/`).
        BuiltinTasksDir: string
        /// Optional user-level task overrides (typically
        /// `%APPDATA%/Takatora/tasks/`). Pass `None` to disable that tier.
        UserTasksDir: string option
    }

    // ─── Internal helpers ─────────────────────────────────────────

    let private nowIso () =
        DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)

    let private appendLine (path: string) (line: string) =
        use stream =
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)
        use writer = new StreamWriter(stream)
        writer.WriteLine(line)

    let private writeEvent (eventsPath: string) (kind: string) (fields: (string * obj) seq) =
        let obj = JsonObject()
        obj.["ts"] <- JsonValue.Create(nowIso ())
        obj.["kind"] <- JsonValue.Create(kind)
        for k, v in fields do
            obj.[k] <-
                if isNull v then null
                else JsonNode.Parse(JsonSerializer.Serialize(v))
        appendLine eventsPath (obj.ToJsonString())

    /// Convert a TomlValue to a System.Text.Json node so it can sit
    /// inside the input.json the runner hands to the .fsx subprocess.
    let rec private toJsonNode (v: TomlValue) : JsonNode =
        match v with
        | TString s -> JsonValue.Create(s)
        | TInt i    -> JsonValue.Create(i)
        | TFloat f  -> JsonValue.Create(f)
        | TBool b   -> JsonValue.Create(b)
        | TArray xs ->
            let arr = JsonArray()
            for x in xs do arr.Add(toJsonNode x)
            arr
        | TTable m ->
            let obj = JsonObject()
            for KeyValue (k, vv) in m do obj.[k] <- toJsonNode vv
            obj

    /// Inverse of `toJsonNode`. Used when reading back a step's outputs
    /// so prior_outputs can flow into subsequent steps' resolve contexts.
    let rec private fromJsonNode (node: JsonNode) : TomlValue =
        match node with
        | :? JsonValue as v ->
            match v.GetValue<JsonElement>().ValueKind with
            | JsonValueKind.String -> TString (v.GetValue<string>())
            | JsonValueKind.True -> TBool true
            | JsonValueKind.False -> TBool false
            | JsonValueKind.Number ->
                match v.TryGetValue<int64>() with
                | true, i -> TInt i
                | _ -> TFloat (v.GetValue<double>())
            | _ -> TString (v.ToJsonString())
        | :? JsonArray as a -> TArray [ for x in a -> fromJsonNode x ]
        | :? JsonObject as o ->
            TTable (o |> Seq.map (fun kv -> kv.Key, fromJsonNode kv.Value) |> Map.ofSeq)
        | _ -> TString (node.ToJsonString())

    /// Build the input.json object the runner hands to a .fsx subprocess.
    /// `effectiveWorkingDir` is the absolute path the .fsx will see as
    /// `Project.workingDir`; runner has already resolved
    /// `project.WorkingDir` against the project root.
    let private buildInputJson (project: Project)
                                (effectiveWorkingDir: string)
                                (resolvedParams: Map<string, TomlValue>)
                                (priorOutputs: Map<string, Map<string, TomlValue>>)
                                : string =
        let root = JsonObject()
        let paramsObj = JsonObject()
        for KeyValue (k, v) in resolvedParams do paramsObj.[k] <- toJsonNode v
        root.["params"] <- paramsObj

        let projectObj = JsonObject()
        projectObj.["name"] <- JsonValue.Create(project.Name)
        projectObj.["working_dir"] <- JsonValue.Create(effectiveWorkingDir)
        root.["project"] <- projectObj

        let engineObj = JsonObject()
        engineObj.["type"] <-
            JsonValue.Create(
                match project.Engine.Kind with
                | EngineKind.Unreal -> "unreal"
                | EngineKind.Unity  -> "unity"
                | EngineKind.Godot  -> "godot")
        project.Engine.ProjectFile
        |> Option.iter (fun p -> engineObj.["project_file"] <- JsonValue.Create(p))
        project.Engine.EnginePath
        |> Option.iter (fun p -> engineObj.["path"] <- JsonValue.Create(p))
        project.Engine.EngineVersion
        |> Option.iter (fun p -> engineObj.["version"] <- JsonValue.Create(p))
        project.Engine.Executable
        |> Option.iter (fun p -> engineObj.["executable"] <- JsonValue.Create(p))
        root.["engine"] <- engineObj

        let priorObj = JsonObject()
        for KeyValue (stepId, outs) in priorOutputs do
            let stepOut = JsonObject()
            for KeyValue (k, v) in outs do stepOut.[k] <- toJsonNode v
            priorObj.[stepId] <- stepOut
        root.["prior_outputs"] <- priorObj

        root.ToJsonString(JsonSerializerOptions(WriteIndented = true))

    /// Read a `<step-id>.ndjson` outputs file back into a key-value map.
    let private readStepOutputs (path: string) : Map<string, TomlValue> =
        if not (File.Exists path) then Map.empty
        else
            File.ReadAllLines path
            |> Array.choose (fun line ->
                if String.IsNullOrWhiteSpace line then None
                else
                    let node = JsonNode.Parse(line) :?> JsonObject
                    let mutable nameNode : JsonNode = null
                    let mutable valueNode : JsonNode = null
                    if node.TryGetPropertyValue("name", &nameNode)
                       && node.TryGetPropertyValue("value", &valueNode) then
                        Some (nameNode.GetValue<string>(), fromJsonNode valueNode)
                    else None)
            |> Map.ofArray

    /// Default flow var values, with overrides applied on top.
    let private effectiveVars (flow: Flow) (overrides: Map<string, TomlValue>) : Map<string, TomlValue> =
        let baseMap =
            flow.Vars
            |> List.choose (fun v -> v.Default |> Option.map (fun d -> v.Name, d))
            |> Map.ofList
        overrides
        |> Map.fold (fun acc k v -> Map.add k v acc) baseMap

    /// Compose the wrapper script the runner spawns under `dotnet fsi`.
    /// The wrapper supplies the SDK reference so authors don't need
    /// `#r "nuget: Takatora.Tasks"` (which would require a published
    /// package or local feed).
    let private wrapperScript (sdkPath: string) (taskPath: string) : string =
        let escape (p: string) = p.Replace("\\", "\\\\").Replace("\"", "\\\"")
        $"#r @\"{escape sdkPath}\"\n#load @\"{escape taskPath}\"\n"

    /// Pick a stable id for a step. If the user didn't provide `id`, fall
    /// back to `<type>-<index>` (1-based) to stay deterministic across
    /// reruns of the same flow definition.
    let private stepId (index: int) (step: Step) : string =
        match step.Id with
        | Some id -> id
        | None -> $"{step.Type}-{index}"

    let private engineKindString = function
        | EngineKind.Unreal -> "unreal"
        | EngineKind.Unity  -> "unity"
        | EngineKind.Godot  -> "godot"

    /// Path where a cancel request lives, relative to a run dir.
    let cancelFlagPath (runDir: string) = Path.Combine(runDir, "CANCEL")

    /// Spawn `dotnet fsi <wrapper>` for one step. Stdout/stderr get
    /// pumped into log.txt; the .fsx itself writes its own events +
    /// outputs through the env var paths. While the subprocess runs,
    /// the runner polls `<run-dir>/CANCEL` and terminates the entire
    /// process tree on appearance — UE's UAT cooks spawn long chains of
    /// children, so a plain Process.Kill leaks them.
    let private runStep
            (opts: Options)
            (project: Project)
            (projectRoot: string)
            (effectiveWorkingDir: string)
            (runDir: string)
            (eventsPath: string)
            (logPath: string)
            (priorOutputs: Map<string, Map<string, TomlValue>>)
            (resolveCtx: ResolveContext)
            (index: int)
            (step: Step)
            : StepRecord =

        let id = stepId index step
        let stepStart = DateTimeOffset.UtcNow

        // 1) `when` short-circuits before we touch the filesystem
        let skipReason =
            match step.When with
            | None -> None
            | Some expr ->
                if Vars.evalWhen resolveCtx expr then None
                else Some $"when expression evaluated false: {expr}"

        match skipReason with
        | Some reason ->
            writeEvent eventsPath "step.skip" [
                "step_id", box id
                "type", box step.Type
                "reason", box reason
            ]
            { Id = id
              Type = step.Type
              Status = StepStatus.Skipped reason
              DurationSec = 0.0
              Outputs = Map.empty }
        | None ->

        // 2) Resolve task .fsx, fail step if not found.
        // Lookup is anchored at the project root (the dir containing
        // `.ci/`), which may differ from `effectiveWorkingDir` when
        // project.toml's `working_dir` is non-".".
        match TaskResolver.resolve projectRoot opts.UserTasksDir opts.BuiltinTasksDir step.Type with
        | None ->
            let msg = $"no task .fsx found for type '{step.Type}'"
            writeEvent eventsPath "step.start" [ "step_id", box id; "type", box step.Type; "step_index", box index ]
            writeEvent eventsPath "step.end"   [ "step_id", box id; "type", box step.Type; "status", box "fail"; "duration_sec", box 0.0; "message", box msg ]
            { Id = id
              Type = step.Type
              Status = StepStatus.Failure msg
              DurationSec = 0.0
              Outputs = Map.empty }
        | Some resolved ->

        // 3) Resolve params, write input.json + wrapper.fsx
        // Per design ("省略時は vars 同名キーを暗黙参照"): a flow var with
        // the same name as a task-expected param flows in automatically.
        // Step-level entries always win over the auto-merged var. Without
        // describe-mode schemas yet, we merge ALL vars into the param bag;
        // unused keys are harmless because Param.required only fetches
        // what the .fsx actually asks for.
        let mergedParams =
            let baseMap =
                resolveCtx.Vars |> Map.fold (fun acc k v -> Map.add k v acc) Map.empty
            step.Params |> Map.fold (fun acc k v -> Map.add k v acc) baseMap
        let resolvedParams =
            mergedParams |> Map.map (fun _ v -> Vars.resolve resolveCtx v)
        let inputPath  = Path.Combine(runDir, "inputs",   $"{id}.json")
        let outputPath = Path.Combine(runDir, "outputs",  $"{id}.ndjson")
        let wrapperPath = Path.Combine(runDir, "_wrapper", $"{id}.fsx")
        Directory.CreateDirectory(Path.GetDirectoryName inputPath)   |> ignore
        Directory.CreateDirectory(Path.GetDirectoryName outputPath)  |> ignore
        Directory.CreateDirectory(Path.GetDirectoryName wrapperPath) |> ignore
        File.WriteAllText(inputPath, buildInputJson project effectiveWorkingDir resolvedParams priorOutputs)
        File.WriteAllText(wrapperPath, wrapperScript opts.SdkAssemblyPath resolved.Path)
        // Touch the output file so the SDK can `FileMode.Append` it cleanly.
        File.WriteAllText(outputPath, "")

        writeEvent eventsPath "step.start" [
            "step_id", box id
            "type", box step.Type
            "step_index", box index
            "task_path", box resolved.Path
        ]

        // 4) Spawn fsi
        let psi = ProcessStartInfo("dotnet")
        psi.ArgumentList.Add("fsi")
        psi.ArgumentList.Add(wrapperPath)
        psi.UseShellExecute <- false
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.WorkingDirectory <- effectiveWorkingDir
        psi.Environment.["TAKATORA_TASK_INPUT"]  <- inputPath
        psi.Environment.["TAKATORA_OUTPUT_FILE"] <- outputPath
        psi.Environment.["TAKATORA_EVENTS_FILE"] <- eventsPath

        use proc = new Process()
        proc.StartInfo <- psi
        let logLock = obj()
        let appendLog (line: string) =
            if not (isNull line) then
                lock logLock (fun () ->
                    File.AppendAllText(logPath, line + Environment.NewLine))
        proc.OutputDataReceived.Add(fun e -> appendLog e.Data)
        proc.ErrorDataReceived.Add(fun e -> appendLog e.Data)
        proc.Start() |> ignore
        proc.BeginOutputReadLine()
        proc.BeginErrorReadLine()

        // CANCEL polling: cheap (200ms cadence, single File.Exists), so we
        // don't reach for FileSystemWatcher's threading complexity.
        let cancelPath = cancelFlagPath runDir
        let mutable cancelled = false
        while not (proc.HasExited) do
            if not cancelled && File.Exists cancelPath then
                writeEvent eventsPath "step.cancel" [
                    "step_id", box id
                    "type", box step.Type
                ]
                try proc.Kill(entireProcessTree = true)
                with _ -> ()
                cancelled <- true
            else
                Threading.Thread.Sleep(200)
        // Drain stdout/stderr handlers before reading exit code.
        proc.WaitForExit()
        let exitCode = proc.ExitCode
        let durationSec = (DateTimeOffset.UtcNow - stepStart).TotalSeconds

        // 5) Read outputs back, record the step
        let outputs = readStepOutputs outputPath
        let status =
            if cancelled then StepStatus.Cancelled
            elif exitCode = 0 then StepStatus.Success
            else StepStatus.Failure $"task exited with code {exitCode}"
        let endStatus =
            match status with
            | StepStatus.Success -> "success"
            | StepStatus.Cancelled -> "cancelled"
            | _ -> "fail"
        writeEvent eventsPath "step.end" [
            "step_id", box id
            "type", box step.Type
            "status", box endStatus
            "duration_sec", box durationSec
            "exit_code", box exitCode
        ]
        { Id = id
          Type = step.Type
          Status = status
          DurationSec = durationSec
          Outputs = outputs }

    let private writeManifest (path: string) (outcome: RunOutcome) (project: Project) (flow: Flow) (vars: Map<string, TomlValue>) =
        // Tomlyn write-back via DocumentSyntax is overkill here; emit a
        // hand-written TOML string. Fields match runner-cli.md's example.
        let sb = StringBuilder()
        let writeStr key (v: string) =
            sb.AppendFormat("{0} = \"{1}\"\n", key, v.Replace("\"", "\\\"")) |> ignore
        let writeRaw key (v: string) =
            sb.AppendFormat("{0} = {1}\n", key, v) |> ignore
        writeStr "flow_id" flow.Id
        writeStr "run_id" outcome.RunId
        writeStr "started_at" (outcome.StartedAt.ToString("o", CultureInfo.InvariantCulture))
        writeStr "finished_at" (outcome.FinishedAt.ToString("o", CultureInfo.InvariantCulture))
        writeStr "trigger" "cli"
        writeStr "result" (
            match outcome.Result with
            | RunResult.Success -> "success"
            | RunResult.Failure -> "failure"
            | RunResult.Cancelled -> "cancelled")
        writeRaw "duration_sec" ((outcome.FinishedAt - outcome.StartedAt).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture))
        writeStr "project_name" project.Name
        sb.AppendLine() |> ignore

        if not (Map.isEmpty vars) then
            sb.AppendLine("[params]") |> ignore
            for KeyValue (k, v) in vars do
                let lit =
                    match v with
                    | TString s -> sprintf "\"%s\"" (s.Replace("\"", "\\\""))
                    | TBool b -> if b then "true" else "false"
                    | TInt i -> string i
                    | TFloat f -> f.ToString("R", CultureInfo.InvariantCulture)
                    | _ -> sprintf "\"%s\"" (sprintf "%A" v)
                sb.AppendFormat("{0} = {1}\n", k, lit) |> ignore
            sb.AppendLine() |> ignore

        for s in outcome.Steps do
            sb.AppendLine("[[step_summary]]") |> ignore
            writeStr "id" s.Id
            writeStr "type" s.Type
            writeStr "status" (
                match s.Status with
                | StepStatus.Success    -> "success"
                | StepStatus.Failure _  -> "failure"
                | StepStatus.Skipped _  -> "skipped"
                | StepStatus.Cancelled  -> "cancelled")
            writeRaw "duration_sec" (s.DurationSec.ToString("0.###", CultureInfo.InvariantCulture))
            match s.Status with
            | StepStatus.Failure msg -> writeStr "message" msg
            | StepStatus.Skipped reason -> writeStr "reason" reason
            | _ -> ()
            sb.AppendLine() |> ignore

        File.WriteAllText(path, sb.ToString())

    /// Resolve a flow into the same view `execute` would see at start
    /// of step execution, but stop short of actually spawning anything.
    /// Used by `takatora run --dry-run` to preview what would happen.
    let plan (opts: Options) : Result<RunPlan, RunFailure> =
        let projectRoot = Path.GetFullPath(opts.WorkingDir)
        let projectPath = Path.Combine(projectRoot, ".ci", "project.toml")
        let flowsPath   = Path.Combine(projectRoot, ".ci", "flows.toml")
        let load () =
            try
                let p  = TomlConfig.loadProject projectPath
                let fs = TomlConfig.loadFlows  flowsPath
                Ok (p, fs)
            with
            | TomlConfigError msg -> Error (RunFailure.ConfigError (projectPath, msg))
            | :? FileNotFoundException as ex ->
                Error (RunFailure.ConfigError (ex.FileName, ex.Message))
        load ()
        |> Result.bind (fun (project, flows) ->
            match flows |> List.tryFind (fun f -> f.Id = opts.FlowId) with
            | None -> Error (RunFailure.FlowNotFound opts.FlowId)
            | Some flow ->
                // Same engine auto-detect path as `execute` so the
                // plan reflects the runner's actual resolution.
                let project =
                    match project.Engine.EnginePath with
                    | Some _ -> project
                    | None ->
                        match Engines.pick project.Engine.Kind project.Engine.EngineVersion with
                        | Some d ->
                            { project with
                                Engine =
                                    { project.Engine with
                                        EnginePath = Some d.Path
                                        EngineVersion =
                                            project.Engine.EngineVersion
                                            |> Option.orElse (Some d.Version)
                                        Executable = d.Executable } }
                        | None -> project

                let vars = effectiveVars flow opts.VarOverrides
                let envReader name =
                    match Environment.GetEnvironmentVariable(name: string) with
                    | null -> None
                    | s -> Some s
                let ctx : ResolveContext = {
                    Vars = vars
                    StepOutputs = Map.empty
                    Project = project
                    Env = envReader
                }

                let steps =
                    flow.Steps
                    |> List.indexed
                    |> List.map (fun (i, step) ->
                        let id = stepId (i + 1) step
                        let skip =
                            match step.When with
                            | Some expr when not (Vars.evalWhen ctx expr) ->
                                Some (sprintf "when=%s evaluated false" expr)
                            | _ -> None
                        let taskPath =
                            TaskResolver.resolve projectRoot opts.UserTasksDir opts.BuiltinTasksDir step.Type
                            |> Option.map (fun r -> r.Path)
                        let skipReason =
                            match skip, taskPath with
                            | Some r, _ -> Some r
                            | _, None -> Some (sprintf "no .fsx for type '%s'" step.Type)
                            | _ -> None
                        let merged =
                            let baseMap =
                                vars |> Map.fold (fun acc k v -> Map.add k v acc) Map.empty
                            step.Params |> Map.fold (fun acc k v -> Map.add k v acc) baseMap
                        let resolved =
                            try merged |> Map.map (fun _ v -> Vars.resolve ctx v)
                            with _ -> merged  // if resolution fails, return raw — plan shouldn't crash
                        {
                            Index = i + 1
                            Id = id
                            Type = step.Type
                            TaskPath = taskPath
                            SkipReason = skipReason
                            ResolvedParams = resolved
                        })

                Ok {
                    Project = project
                    FlowId = flow.Id
                    Vars = vars
                    OverriddenKeys = Map.toSeq opts.VarOverrides |> Seq.map fst |> Set.ofSeq
                    Steps = steps
                })

    /// Execute a flow end-to-end. Returns either an outcome with the
    /// per-step record and final result, or a setup-time failure
    /// (unknown flow, malformed config, missing builtin task dir, ...).
    let execute (opts: Options) : Result<RunOutcome, RunFailure> =
        // Anchor everything in absolute paths so behavior doesn't depend
        // on the parent process's CWD. `projectRoot` is the dir
        // containing `.ci/`; `effectiveWorkingDir` (resolved later) is
        // where .fsx subprocesses run, which may differ if project.toml
        // declares a non-"." working_dir.
        let projectRoot = Path.GetFullPath(opts.WorkingDir)
        let projectPath = Path.Combine(projectRoot, ".ci", "project.toml")
        let flowsPath   = Path.Combine(projectRoot, ".ci", "flows.toml")

        let load () =
            try
                let p  = TomlConfig.loadProject projectPath
                let fs = TomlConfig.loadFlows  flowsPath
                Ok (p, fs)
            with
            | TomlConfigError msg -> Error (RunFailure.ConfigError (projectPath, msg))
            | :? FileNotFoundException as ex -> Error (RunFailure.ConfigError (ex.FileName, ex.Message))

        load ()
        |> Result.bind (fun (project, flows) ->
            match flows |> List.tryFind (fun f -> f.Id = opts.FlowId) with
            | None -> Error (RunFailure.FlowNotFound opts.FlowId)
            | Some flow ->

            let effectiveWorkingDir =
                Path.GetFullPath(Path.Combine(projectRoot, project.WorkingDir))

            // Engine auto-detection: only fills in path/version/executable
            // if the user didn't specify them in project.toml. Detection
            // failure is non-fatal — the .fsx may still get by (e.g. a
            // pure git/fs flow doesn't need engine info), and tasks
            // that DO need it raise their own clear error.
            let project =
                match project.Engine.EnginePath with
                | Some _ -> project
                | None ->
                    match Engines.pick project.Engine.Kind project.Engine.EngineVersion with
                    | Some detected ->
                        { project with
                            Engine =
                                { project.Engine with
                                    EnginePath = Some detected.Path
                                    EngineVersion =
                                        project.Engine.EngineVersion
                                        |> Option.orElse (Some detected.Version)
                                    Executable = detected.Executable } }
                    | None -> project

            let runId = RunId.generate DateTimeOffset.UtcNow
            let runDir = Path.Combine(projectRoot, ".ci", "runs", runId)
            Directory.CreateDirectory(runDir) |> ignore
            let eventsPath = Path.Combine(runDir, "events.ndjson")
            let logPath    = Path.Combine(runDir, "log.txt")
            File.WriteAllText(eventsPath, "")
            File.WriteAllText(logPath, "")

            let started = DateTimeOffset.UtcNow
            writeEvent eventsPath "run.start" [
                "run_id", box runId
                "flow_id", box flow.Id
                "project", box project.Name
            ]

            let vars = effectiveVars flow opts.VarOverrides

            let envReader (name: string) =
                match Environment.GetEnvironmentVariable(name) with
                | null -> None
                | s -> Some s

            let mutable priorOutputs : Map<string, Map<string, TomlValue>> = Map.empty
            let mutable steps : StepRecord list = []
            // `halted` is one of: `None` (keep running), `Some "failed"`,
            // `Some "cancelled"`. Both terminal states skip remaining
            // steps; the reason string differs in the audit trail.
            let mutable halted : string option = None

            for i, step in flow.Steps |> List.indexed do
                match halted with
                | Some reason ->
                    let id = stepId (i + 1) step
                    let skipReason =
                        match reason with
                        | "cancelled" -> "cancelled"
                        | _ -> "earlier step failed"
                    writeEvent eventsPath "step.skip" [
                        "step_id", box id
                        "type", box step.Type
                        "reason", box skipReason
                    ]
                    steps <-
                        { Id = id
                          Type = step.Type
                          Status = StepStatus.Skipped skipReason
                          DurationSec = 0.0
                          Outputs = Map.empty } :: steps
                | None ->
                    let ctx : ResolveContext = {
                        Vars = vars
                        StepOutputs = priorOutputs
                        Project = project
                        Env = envReader
                    }
                    let record = runStep opts project projectRoot effectiveWorkingDir runDir eventsPath logPath priorOutputs ctx (i + 1) step
                    steps <- record :: steps
                    match record.Status with
                    | StepStatus.Success ->
                        priorOutputs <- Map.add record.Id record.Outputs priorOutputs
                    | StepStatus.Failure _ -> halted <- Some "failed"
                    | StepStatus.Cancelled -> halted <- Some "cancelled"
                    | StepStatus.Skipped _ -> ()

            let stepsOrdered = List.rev steps
            let result =
                let anyCancelled =
                    stepsOrdered |> List.exists (fun s ->
                        match s.Status with StepStatus.Cancelled -> true | _ -> false)
                let anyFailed =
                    stepsOrdered |> List.exists (fun s ->
                        match s.Status with StepStatus.Failure _ -> true | _ -> false)
                if anyCancelled then RunResult.Cancelled
                elif anyFailed then RunResult.Failure
                else RunResult.Success
            let finished = DateTimeOffset.UtcNow

            writeEvent eventsPath "run.end" [
                "run_id", box runId
                "status", box (match result with RunResult.Success -> "success" | RunResult.Failure -> "failure" | RunResult.Cancelled -> "cancelled")
                "duration_sec", box (finished - started).TotalSeconds
            ]

            let outcome : RunOutcome = {
                RunId = runId
                FlowId = flow.Id
                StartedAt = started
                FinishedAt = finished
                Result = result
                Steps = stepsOrdered
                RunDir = runDir
            }

            writeManifest (Path.Combine(runDir, "manifest.toml")) outcome project flow vars
            Ok outcome)
