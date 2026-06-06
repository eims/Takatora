namespace Takatora.Tasks

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

/// Raised by `Task.fail` to signal a clean failure with no stack trace.
/// Runner distinguishes this from other exceptions when reporting.
exception TaskFailure of reason: string

// ─── Internal I/O contract ─────────────────────────────────────────
//
// Three env vars cooperatively scope a task subprocess:
//
//   TAKATORA_TASK_INPUT   — JSON file with params/project/engine/prior_outputs
//   TAKATORA_OUTPUT_FILE  — NDJSON, one {"name","value"} per Output.set call
//   TAKATORA_EVENTS_FILE  — NDJSON, structured events from Step / Log
//
// All three are optional in isolation — when unset the SDK silently no-ops
// the missing channel so a `.fsx` author can run the script under `dotnet
// fsi` directly for local debugging.

[<RequireQualifiedAccess>]
module internal Io =

    // Pin this fsi process's stdout/stderr to UTF-8. The runner reads them
    // as UTF-8; without this, a GUI (windowed) host's spawned fsi (no
    // console attached) leaves Console.Out at the system code page (CP932),
    // so forwarded tool output — already decoded correctly — is re-encoded
    // wrong and lands in log.txt as mojibake. We swap the writers directly
    // over the raw streams instead of setting Console.OutputEncoding, which
    // silently throws in a console-less process.
    do
        try
            let utf8Writer (stream: System.IO.Stream) =
                let w = new System.IO.StreamWriter(stream, System.Text.UTF8Encoding(false))
                w.AutoFlush <- true
                w :> System.IO.TextWriter
            System.Console.SetOut(utf8Writer (System.Console.OpenStandardOutput()))
            System.Console.SetError(utf8Writer (System.Console.OpenStandardError()))
        with _ -> ()

    type private Channel = {
        InputRoot: JsonObject
        OutputPath: string option
        EventsPath: string option
        /// True when `TAKATORA_MODE=describe`. Param.* registers schema
        /// + returns safe defaults; Step.run skips its action; Cmd.exec
        /// is a no-op; Output.set just records the output name. The
        /// SDK writes the collected schema to DescribePath on process
        /// exit so the runner can read it back.
        DescribeMode: bool
        DescribePath: string option
        /// The task type being described (TAKATORA_TASK_TYPE), so the schema
        /// JSON carries a top-level "type" even via the process-exit flush.
        TaskType: string option
    }

    /// Captured schema entry for one Param.* call in describe mode.
    type ParamSchema = {
        Name: string
        Kind: string
        Required: bool
        Default: JsonNode option
        EnumValues: string list option
        /// File-picker filters for kind="file" (e.g. ["*.uproject"]); None otherwise.
        Filter: string list option
        /// Author-supplied human description (via `Param.note`); shown as a
        /// tooltip in the GUI. None until annotated.
        Description: string option
    }

    let private syncRoot = obj ()
    let mutable private channelOpt: Channel option = None
    // describe-mode collectors. Each is appended to as Param/Output run.
    let mutable private paramSchemas : ParamSchema list = []
    let mutable private outputNames  : string list = []
    let mutable private describeFlushed = false

    let private envOrEmpty (name: string) =
        match Environment.GetEnvironmentVariable(name) with
        | null -> ""
        | s -> s

    let private buildChannel () : Channel =
        let describeMode = (envOrEmpty "TAKATORA_MODE" = "describe")
        let inputPath = envOrEmpty "TAKATORA_TASK_INPUT"
        let inputRoot =
            if String.IsNullOrEmpty inputPath then
                JsonObject()
            elif not (File.Exists inputPath) then
                failwithf "TAKATORA_TASK_INPUT points to non-existent file: %s" inputPath
            else
                let text = File.ReadAllText(inputPath)
                match JsonNode.Parse(text) with
                | :? JsonObject as o -> o
                | _ -> failwithf "TAKATORA_TASK_INPUT must contain a JSON object: %s" inputPath
        let optPath name =
            match envOrEmpty name with
            | "" -> None
            | p -> Some p
        { InputRoot = inputRoot
          OutputPath = optPath "TAKATORA_OUTPUT_FILE"
          EventsPath = optPath "TAKATORA_EVENTS_FILE"
          DescribeMode = describeMode
          DescribePath = optPath "TAKATORA_DESCRIBE_OUTPUT"
          TaskType = optPath "TAKATORA_TASK_TYPE" }

    let private get () : Channel =
        match channelOpt with
        | Some c -> c
        | None ->
            lock syncRoot (fun () ->
                match channelOpt with
                | Some c -> c
                | None ->
                    let c = buildChannel ()
                    channelOpt <- Some c
                    c)

    /// Test hook — drops the cached channel so a subsequent call re-reads
    /// the env vars. Production code never needs this.
    let resetForTests () : unit =
        lock syncRoot (fun () ->
            channelOpt <- None
            paramSchemas <- []
            outputNames <- []
            describeFlushed <- false)

    let isDescribeMode () : bool = get().DescribeMode

    /// Test hook: snapshot the describe-mode schema collected so far.
    /// Production reads the JSON from disk via the DescribePath; tests
    /// pull it through this for direct assertions.
    let describeSnapshot () : ParamSchema list * string list =
        List.rev paramSchemas, List.rev outputNames

    let registerParam (entry: ParamSchema) : unit =
        lock syncRoot (fun () -> paramSchemas <- entry :: paramSchemas)

    /// Attach a description to an already-registered param (by name). Used by
    /// `Param.note`, called after the param's own declaration in describe mode.
    let annotateParam (name: string) (description: string) : unit =
        lock syncRoot (fun () ->
            paramSchemas <-
                paramSchemas
                |> List.map (fun p ->
                    if p.Name = name then { p with Description = Some description } else p))

    let registerOutput (name: string) : unit =
        lock syncRoot (fun () ->
            if not (List.contains name outputNames) then
                outputNames <- name :: outputNames)

    // ─── Input access ──────────────────────────────────────────────

    let private tryProperty (obj: JsonObject) (key: string) : JsonNode option =
        let mutable result : JsonNode = null
        if obj.TryGetPropertyValue(key, &result) then Some result else None

    let tryParam (name: string) : JsonNode option =
        match tryProperty (get().InputRoot) "params" with
        | Some (:? JsonObject as p) -> tryProperty p name
        | _ -> None

    let tryProjectField (key: string) : string option =
        match tryProperty (get().InputRoot) "project" with
        | Some (:? JsonObject as p) ->
            match tryProperty p key with
            | Some (:? JsonValue as v) ->
                match v.TryGetValue<string>() with
                | true, s -> Some s
                | _ -> None
            | _ -> None
        | _ -> None

    let tryEngineField (key: string) : string option =
        match tryProperty (get().InputRoot) "engine" with
        | Some (:? JsonObject as e) ->
            match tryProperty e key with
            | Some (:? JsonValue as v) ->
                match v.TryGetValue<string>() with
                | true, s -> Some s
                | _ -> None
            | _ -> None
        | _ -> None

    // ─── Append-only writes ────────────────────────────────────────

    let private nowIso () =
        DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)

    let private appendLine (path: string) (line: string) =
        // FileShare.ReadWrite so a tail-reader (GUI) can follow live.
        use stream =
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)
        use writer = new StreamWriter(stream)
        writer.WriteLine(line)

    let private valueToNode (v: obj) : JsonNode =
        // System.Text.Json handles the polymorphic case correctly for primitives,
        // strings, sequences, and POCOs. Going through Serialize → Parse keeps
        // behavior consistent regardless of caller-supplied runtime type.
        if isNull v then null
        else JsonNode.Parse(JsonSerializer.Serialize(v))

    let writeEvent (kind: string) (fields: (string * obj) seq) : unit =
        match get().EventsPath with
        | None -> ()
        | Some path ->
            let obj = JsonObject()
            obj.["ts"] <- JsonValue.Create(nowIso ())
            obj.["kind"] <- JsonValue.Create(kind)
            for k, v in fields do
                obj.[k] <- valueToNode v
            appendLine path (obj.ToJsonString())

    let writeOutput (name: string) (value: obj) : unit =
        match get().OutputPath with
        | None -> ()
        | Some path ->
            let obj = JsonObject()
            obj.["name"] <- JsonValue.Create(name)
            obj.["value"] <- valueToNode value
            appendLine path (obj.ToJsonString())

    /// Serialize the captured describe-mode schema to the configured
    /// DescribePath. Idempotent — flushing twice from both an explicit
    /// call and the ProcessExit hook is safe.
    let flushDescribe (taskTypeHint: string option) : unit =
        let chan = get ()
        if chan.DescribeMode then
            lock syncRoot (fun () ->
                if not describeFlushed then
                    match chan.DescribePath with
                    | None -> ()
                    | Some path ->
                        let root = JsonObject()
                        match taskTypeHint |> Option.orElse chan.TaskType with
                        | Some t -> root.["type"] <- JsonValue.Create(t)
                        | None -> ()
                        let paramsArr = JsonArray()
                        for p in List.rev paramSchemas do
                            let entry = JsonObject()
                            entry.["name"]     <- JsonValue.Create(p.Name)
                            entry.["kind"]     <- JsonValue.Create(p.Kind)
                            entry.["required"] <- JsonValue.Create(p.Required)
                            match p.Default with
                            | Some d -> entry.["default"] <- d
                            | None -> ()
                            match p.EnumValues with
                            | Some vs ->
                                let arr = JsonArray()
                                for v in vs do arr.Add(JsonValue.Create(v))
                                entry.["values"] <- arr
                            | None -> ()
                            match p.Filter with
                            | Some fs ->
                                let arr = JsonArray()
                                for f in fs do arr.Add(JsonValue.Create(f))
                                entry.["filter"] <- arr
                            | None -> ()
                            match p.Description with
                            | Some d -> entry.["description"] <- JsonValue.Create(d)
                            | None -> ()
                            paramsArr.Add(entry)
                        root.["params"] <- paramsArr
                        let outsArr = JsonArray()
                        for n in List.rev outputNames do outsArr.Add(JsonValue.Create(n))
                        root.["outputs"] <- outsArr
                        File.WriteAllText(
                            path,
                            root.ToJsonString(JsonSerializerOptions(WriteIndented = true)))
                    describeFlushed <- true)

    /// Hook the process exit so an .fsx that just declares params at the
    /// top level and doesn't bother with any explicit flush still gets
    /// the describe output written. Idempotent with explicit flushes.
    let private registerProcessExitHook () =
        AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> flushDescribe None)
    do registerProcessExitHook ()

// ─── Public SDK surface ────────────────────────────────────────────

/// Read typed input parameters declared in the flow's TOML for this step.
/// Mismatched types or missing required keys raise `TaskFailure`. In
/// describe mode (`TAKATORA_MODE=describe`), `required` / `optional` /
/// `requiredEnum` register schema instead of returning real values, and
/// hand back a benign default of the requested type so the .fsx keeps
/// running and reaches subsequent Param declarations.
[<RequireQualifiedAccess>]
module Param =

    let private taskFail msg : 'T = raise (TaskFailure msg)

    let private kindOfType (t: System.Type) : string =
        if t = typeof<string> then "string"
        elif t = typeof<bool> then "bool"
        elif t = typeof<int> || t = typeof<int64> then "int"
        elif t = typeof<float> || t = typeof<double> then "float"
        elif t.IsArray then sprintf "list<%s>" (
            if isNull (t.GetElementType()) then "object"
            else
                let et = t.GetElementType()
                if et = typeof<string> then "string"
                elif et = typeof<bool> then "bool"
                elif et = typeof<int> || et = typeof<int64> then "int"
                elif et = typeof<float> || et = typeof<double> then "float"
                else "object")
        else "object"

    /// Safe default for a type — used as the return value in describe
    /// mode so the .fsx keeps running through subsequent Param calls.
    let private safeDefault<'T> () : 'T =
        let t = typeof<'T>
        if t = typeof<string> then box "" :?> 'T
        elif t = typeof<bool> then box false :?> 'T
        elif t = typeof<int> then box 0 :?> 'T
        elif t = typeof<int64> then box 0L :?> 'T
        elif t = typeof<float> || t = typeof<double> then box 0.0 :?> 'T
        elif t.IsArray then box (System.Array.CreateInstance(t.GetElementType(), 0)) :?> 'T
        else Unchecked.defaultof<'T>

    let private convert<'T> (name: string) (node: JsonNode) : 'T =
        let t = typeof<'T>
        try
            match node with
            | :? JsonValue as v ->
                if t = typeof<string> then box (v.GetValue<string>()) :?> 'T
                elif t = typeof<bool>     then box (v.GetValue<bool>())     :?> 'T
                elif t = typeof<int>      then box (v.GetValue<int>())      :?> 'T
                elif t = typeof<int64>    then box (v.GetValue<int64>())    :?> 'T
                elif t = typeof<float>    then box (v.GetValue<double>())   :?> 'T
                elif t = typeof<double>   then box (v.GetValue<double>())   :?> 'T
                else
                    JsonSerializer.Deserialize<'T>(node.ToJsonString())
            | _ ->
                JsonSerializer.Deserialize<'T>(node.ToJsonString())
        with ex ->
            taskFail $"Param '{name}' expected type {t.Name}: {ex.Message}"

    let private defaultToNode<'T> (defaultValue: 'T) : JsonNode option =
        try Some (JsonNode.Parse(JsonSerializer.Serialize(defaultValue))) with _ -> None

    /// Required typed param. `'T` should be string / bool / int / int64 /
    /// float / double for primitives; arrays and lists work too.
    let required<'T> (name: string) : 'T =
        if Io.isDescribeMode () then
            Io.registerParam {
                Name = name
                Kind = kindOfType typeof<'T>
                Required = true
                Default = None
                EnumValues = None
                Filter = None
                Description = None
            }
            safeDefault<'T> ()
        else
            match Io.tryParam name with
            | Some node when not (isNull node) -> convert<'T> name node
            | _ -> taskFail $"Required param '{name}' is missing from task input"

    /// Optional typed param with a fallback default.
    let optional<'T> (name: string) (defaultValue: 'T) : 'T =
        if Io.isDescribeMode () then
            Io.registerParam {
                Name = name
                Kind = kindOfType typeof<'T>
                Required = false
                Default = defaultToNode defaultValue
                EnumValues = None
                Filter = None
                Description = None
            }
            defaultValue
        else
            match Io.tryParam name with
            | Some node when not (isNull node) -> convert<'T> name node
            | _ -> defaultValue

    /// True when the param is present and non-null.
    let has (name: string) : bool =
        if Io.isDescribeMode () then
            // Register as optional bool-flag-style schema entry; the
            // GUI can decide how to surface it (typically a checkbox).
            Io.registerParam {
                Name = name
                Kind = "bool"
                Required = false
                Default = None
                EnumValues = None
                Filter = None
                Description = None
            }
            false
        else
            match Io.tryParam name with
            | Some node when not (isNull node) -> true
            | _ -> false

    /// String param constrained to a fixed set of allowed values. Used for
    /// flow vars declared as `type = "enum"`.
    let requiredEnum (name: string) (values: string list) : string =
        if Io.isDescribeMode () then
            Io.registerParam {
                Name = name
                Kind = "enum"
                Required = true
                Default = None
                EnumValues = Some values
                Filter = None
                Description = None
            }
            match values with
            | v :: _ -> v
            | [] -> ""
        else
            let v = required<string> name
            if List.contains v values then v
            else
                let allowed = String.concat ", " values
                taskFail $"Param '{name}' must be one of [{allowed}], got '{v}'"

    // ─── kind-hinted string params ─────────────────────────────────
    //
    // Value behaviour is identical to required/optional<string>; the only
    // difference is the `kind` registered in describe mode, so a GUI renders
    // the right widget (picker / mask / textarea). Per task-sdk.md.

    let private requiredOfKind (kind: string) (filter: string list option) (name: string) : string =
        if Io.isDescribeMode () then
            Io.registerParam {
                Name = name; Kind = kind; Required = true
                Default = None; EnumValues = None; Filter = filter; Description = None
            }
            ""
        else
            match Io.tryParam name with
            | Some node when not (isNull node) -> convert<string> name node
            | _ -> taskFail $"Required param '{name}' is missing from task input"

    let private optionalOfKind (kind: string) (filter: string list option) (name: string) (defaultValue: string) : string =
        if Io.isDescribeMode () then
            Io.registerParam {
                Name = name; Kind = kind; Required = false
                Default = defaultToNode defaultValue; EnumValues = None; Filter = filter; Description = None
            }
            defaultValue
        else
            match Io.tryParam name with
            | Some node when not (isNull node) -> convert<string> name node
            | _ -> defaultValue

    /// A filesystem path (GUI: a path picker).
    let requiredPath (name: string) : string = requiredOfKind "path" None name
    let optionalPath (name: string) (def: string) : string = optionalOfKind "path" None name def
    /// A directory (GUI: a folder picker).
    let requiredDir (name: string) : string = requiredOfKind "dir" None name
    let optionalDir (name: string) (def: string) : string = optionalOfKind "dir" None name def
    /// An existing file, optionally constrained by filters like ["*.uproject"].
    let requiredFile (name: string) (filter: string list option) : string = requiredOfKind "file" filter name
    let optionalFile (name: string) (filter: string list option) (def: string) : string = optionalOfKind "file" filter name def
    /// A secret (GUI: a masked field; the runner keeps it out of manifests/logs).
    let requiredSecret (name: string) : string = requiredOfKind "secret" None name
    let optionalSecret (name: string) (def: string) : string = optionalOfKind "secret" None name def
    /// Multi-line text (GUI: a text area).
    let requiredMultiline (name: string) : string = requiredOfKind "multiline" None name
    let optionalMultiline (name: string) (def: string) : string = optionalOfKind "multiline" None name def

    /// Optional list param. Value behaves like `optional<'T[]>` but is typed
    /// as an F# list and registers kind `list<elem>` in describe mode.
    let optionalList<'T> (name: string) (defaultValue: 'T list) : 'T list =
        if Io.isDescribeMode () then
            Io.registerParam {
                Name = name
                Kind = kindOfType typeof<'T[]>          // → "list<elem>"
                Required = false
                Default = defaultToNode (List.toArray defaultValue)
                EnumValues = None
                Filter = None
                Description = None
            }
            defaultValue
        else
            match Io.tryParam name with
            | Some node when not (isNull node) -> convert<'T[]> name node |> List.ofArray
            | _ -> defaultValue

    /// Attach a human description to a previously-declared param — surfaced as
    /// a tooltip in the GUI (Inspector / run dialog). Call it right after the
    /// param's declaration. describe-mode only; a no-op at run time.
    let note (name: string) (description: string) : unit =
        if Io.isDescribeMode () then Io.annotateParam name description

/// Surface step outputs to subsequent steps via NDJSON appended to
/// `TAKATORA_OUTPUT_FILE`. Visible as `${steps.<id>.outputs.<name>}` in
/// later flow steps. In describe mode, just record the output's name
/// so the GUI knows what downstream references will resolve.
[<RequireQualifiedAccess>]
module Output =
    let set (name: string) (value: obj) : unit =
        if Io.isDescribeMode () then Io.registerOutput name
        else Io.writeOutput name value

/// Time + log a logical sub-section of a task. Substep events appear
/// in `TAKATORA_EVENTS_FILE` so the GUI can render a tree view.
[<RequireQualifiedAccess>]
module Step =

    let private elapsedSec (start: DateTimeOffset) =
        (DateTimeOffset.UtcNow - start).TotalSeconds

    /// Run an action wrapped in substep.start / substep.end events.
    /// Exceptions are logged as substep.end status=fail and re-raised.
    /// In describe mode, the action body is SKIPPED — convention is
    /// that all side effects (Cmd, file IO, etc.) live inside Step.run,
    /// so skipping the action stops describe mode from doing actual work.
    let run (name: string) (action: unit -> unit) : unit =
        if Io.isDescribeMode () then () else
        Io.writeEvent "substep.start" [ "name", box name ]
        let start = DateTimeOffset.UtcNow
        try
            action ()
            Io.writeEvent "substep.end" [
                "name", box name
                "status", box "success"
                "duration_sec", box (elapsedSec start)
            ]
        with ex ->
            Io.writeEvent "substep.end" [
                "name", box name
                "status", box "fail"
                "duration_sec", box (elapsedSec start)
                "message", box ex.Message
            ]
            reraise ()

    /// Run an action that returns a value, otherwise identical to `run`.
    let runResult (name: string) (action: unit -> 'T) : 'T =
        let mutable result : 'T = Unchecked.defaultof<'T>
        run name (fun () -> result <- action ())
        result

    /// Skip a substep with a human-readable reason.
    let skip (name: string) (reason: string) : unit =
        if Io.isDescribeMode () then () else
        Io.writeEvent "substep.skip" [
            "name", box name
            "reason", box reason
        ]

/// Leveled logging routed through `TAKATORA_EVENTS_FILE`.
/// No-op in describe mode (event file isn't even open).
[<RequireQualifiedAccess>]
module Log =
    let private emit (level: string) (message: string) =
        if Io.isDescribeMode () then () else
        Io.writeEvent "log" [ "level", box level; "message", box message ]

    let info  (msg: string) : unit = emit "info"  msg
    let warn  (msg: string) : unit = emit "warn"  msg
    let error (msg: string) : unit = emit "error" msg
    let debug (msg: string) : unit = emit "debug" msg

    /// Visual section break — surfaces as a heading-style line in the GUI's
    /// log view. Functionally just an info-level event with a section flag.
    let section (msg: string) : unit =
        Io.writeEvent "log" [
            "level", box "info"
            "section", box true
            "message", box msg
        ]

/// Heartbeat for long, blocking operations so the run log doesn't go
/// silent (e.g. a large zip). Runs `action`, emitting an info line
/// "<label> … (Ns)" every `everySec` seconds on a background thread until
/// it returns. No-op heartbeat in describe mode.
[<RequireQualifiedAccess>]
module Progress =
    let during (label: string) (everySec: float) (action: unit -> 'T) : 'T =
        if Io.isDescribeMode () then action ()
        else
            let mutable running = true
            let sw = System.Diagnostics.Stopwatch.StartNew()
            let t =
                System.Threading.Thread(fun () ->
                    while running do
                        System.Threading.Thread.Sleep(max 250 (int (everySec * 1000.0)))
                        if running then
                            Log.info (sprintf "%s … (%.0fs)" label sw.Elapsed.TotalSeconds))
            t.IsBackground <- true
            t.Start()
            try action () finally running <- false

/// Read-only project metadata as supplied by the runner.
/// Members are properties (not let-bound values) so they re-read the
/// input on each access; otherwise tests + GUI re-init scenarios would
/// pin to whatever the input was at module init time.
type Project =
    static member workingDir : string =
        Io.tryProjectField "working_dir" |> Option.defaultValue ""
    static member name : string =
        Io.tryProjectField "name" |> Option.defaultValue ""

/// Read-only engine metadata. Same property pattern as `Project`.
/// Empty strings when the runner hasn't supplied a field — engine
/// tasks should call `Task.fail` early if a required field is blank.
type Engine =
    static member kind : string =
        Io.tryEngineField "type" |> Option.defaultValue ""
    static member path : string =
        Io.tryEngineField "path" |> Option.defaultValue ""
    static member version : string =
        Io.tryEngineField "version" |> Option.defaultValue ""
    static member projectFile : string =
        Io.tryEngineField "project_file" |> Option.defaultValue ""
    static member executable : string =
        Io.tryEngineField "executable" |> Option.defaultValue ""

/// Task-level control flow.
[<RequireQualifiedAccess>]
module Task =
    /// Abort the task with a clean message — runner won't print the F#
    /// stack. Use for user-facing failure reasons; let plain exceptions
    /// handle bugs. In describe mode this returns the type default
    /// instead of throwing so the .fsx keeps reaching subsequent Param
    /// / Output declarations; describe doesn't care about the value.
    let fail<'T> (reason: string) : 'T =
        if Io.isDescribeMode () then Unchecked.defaultof<'T>
        else raise (TaskFailure reason)

// ─── External process invocation (Cmd) ─────────────────────────────

/// Configurable knobs for `Cmd.execWith` / `Cmd.execCaptureWith`.
/// `Cmd.exec` / `execCapture` use `ExecOptions.empty`.
type ExecOptions = {
    /// Override the child's working directory. `None` inherits the
    /// task's cwd (which is `Project.workingDir`).
    WorkingDir: string option
    /// Extra environment variables to set on the child process.
    Env: Map<string, string>
    /// Non-zero exit codes that should NOT throw (`robocopy` returns 1
    /// for "files copied, no errors" — that's success there).
    IgnoreExitCodes: int list
    /// Hard wall-clock cap. On expiry the runner kills the entire
    /// process tree and the task fails with a timeout message.
    Timeout: TimeSpan option
}

[<RequireQualifiedAccess>]
module ExecOptions =
    let empty = {
        WorkingDir = None
        Env = Map.empty
        IgnoreExitCodes = []
        Timeout = None
    }

/// Spawn external processes from a task .fsx. By default stdout/stderr
/// are inherited from the parent fsi process, which the runner pipes to
/// `<run-dir>/log.txt` — so engine tools (UE/Unity/git) appear with
/// their output verbatim. Use the `Capture` variants when the task
/// itself needs the bytes (e.g. `git rev-parse HEAD` → output value).
[<RequireQualifiedAccess>]
module Cmd =

    open System.Diagnostics
    open System.Text

    let private taskFail msg : 'T = raise (TaskFailure msg)

    /// Console tools (UBT, cl.exe, git, …) emit bytes in the OS's native
    /// (ANSI/console) code page, not UTF-8 — e.g. CP932 on Japanese
    /// Windows. Decode captured/streamed output with that code page so
    /// localized messages don't turn into mojibake in log.txt. Registering
    /// the code-pages provider is required for GetEncoding(932) on .NET.
    let private nativeEncoding : Encoding =
        try
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)
            Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage)
        with _ -> Encoding.UTF8

    let private buildPsi (exe: string) (args: string list) (opts: ExecOptions) : ProcessStartInfo =
        let psi = ProcessStartInfo(exe)
        for a in args do psi.ArgumentList.Add(a)
        psi.UseShellExecute <- false
        // Don't let console child tools pop their own window when the host
        // is a GUI app (they'd grab the foreground). Output is captured /
        // inherited by the runner regardless.
        psi.CreateNoWindow <- true
        opts.WorkingDir |> Option.iter (fun wd -> psi.WorkingDirectory <- wd)
        for KeyValue (k, v) in opts.Env do
            psi.Environment.[k] <- v
        psi

    let private waitWithTimeout (proc: Process) (timeout: TimeSpan option) : bool =
        match timeout with
        | None -> proc.WaitForExit(); true
        | Some t ->
            let ms = int t.TotalMilliseconds
            if proc.WaitForExit(ms) then true
            else
                try proc.Kill(entireProcessTree = true) with _ -> ()
                false

    let private runStreaming (exe: string) (args: string list) (opts: ExecOptions) : int =
        // Describe mode never spawns external processes. Step.run skips
        // its action and Cmd.* is normally called from inside one, so we
        // shouldn't reach here under the convention. Guard defensively
        // anyway — a top-level Cmd outside Step.run would otherwise run
        // during describe.
        if Io.isDescribeMode () then 0 else
        let psi = buildPsi exe args opts
        // Redirect and forward to this process's stdout/stderr (which the
        // runner captures into log.txt). We can't rely on bare handle
        // inheritance any more: buildPsi sets CreateNoWindow (so console
        // child tools don't pop a window under a GUI host), and an
        // un-redirected console child loses its output in that mode.
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        // Decode the tool's native-codepage bytes correctly (then re-emit as
        // UTF-8 via Console.Out, which the runner reads as UTF-8).
        psi.StandardOutputEncoding <- nativeEncoding
        psi.StandardErrorEncoding <- nativeEncoding
        use proc = new Process()
        proc.StartInfo <- psi
        proc.OutputDataReceived.Add(fun e -> if not (isNull e.Data) then Console.Out.WriteLine(e.Data))
        proc.ErrorDataReceived.Add(fun e -> if not (isNull e.Data) then Console.Error.WriteLine(e.Data))
        proc.Start() |> ignore
        proc.BeginOutputReadLine()
        proc.BeginErrorReadLine()
        if not (waitWithTimeout proc opts.Timeout) then
            taskFail (sprintf "Cmd '%s' timed out after %.1fs" exe opts.Timeout.Value.TotalSeconds)
        let exitCode = proc.ExitCode
        if exitCode <> 0 && not (List.contains exitCode opts.IgnoreExitCodes) then
            taskFail $"Cmd '{exe}' exited with code {exitCode}"
        exitCode

    /// Run `exe args` with stdout/stderr streaming through to the run log.
    /// Throws `TaskFailure` on non-zero exit unless the code is in
    /// `IgnoreExitCodes`.
    let exec (exe: string) (args: string list) : unit =
        runStreaming exe args ExecOptions.empty |> ignore

    /// As `exec`, but with an explicit working directory.
    let execIn (workingDir: string) (exe: string) (args: string list) : unit =
        runStreaming exe args { ExecOptions.empty with WorkingDir = Some workingDir }
        |> ignore

    /// Full-knob variant. Use for ignore_exit_codes, timeout, or env vars.
    let execWith (opts: ExecOptions) (exe: string) (args: string list) : unit =
        runStreaming exe args opts |> ignore

    /// Capture stdout/stderr into strings instead of streaming. Returns
    /// the exit code instead of throwing — caller decides what's
    /// success. Use for short outputs (commit hashes, version strings).
    let execCaptureWith (opts: ExecOptions) (exe: string) (args: string list) =
        if Io.isDescribeMode () then
            {| stdout = ""; stderr = ""; exitCode = 0 |}
        else
        let psi = buildPsi exe args opts
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.StandardOutputEncoding <- nativeEncoding
        psi.StandardErrorEncoding <- nativeEncoding
        use proc = Process.Start(psi)
        // Read both streams concurrently; reading sequentially can
        // deadlock if the child fills the unread pipe.
        let stdoutTask = proc.StandardOutput.ReadToEndAsync()
        let stderrTask = proc.StandardError.ReadToEndAsync()
        if not (waitWithTimeout proc opts.Timeout) then
            taskFail (sprintf "Cmd '%s' timed out after %.1fs" exe opts.Timeout.Value.TotalSeconds)
        {| stdout = stdoutTask.Result
           stderr = stderrTask.Result
           exitCode = proc.ExitCode |}

    /// Capture variant with default options.
    let execCapture (exe: string) (args: string list) =
        execCaptureWith ExecOptions.empty exe args

// ─── Engine-family helpers ─────────────────────────────────────────
//
// These are thin wrappers that turn `Engine.path` (set by the runner
// from detection) into the right tool path and invoke it through Cmd.
// Tasks use these to avoid hardcoding paths like
// `<engine>/Engine/Build/BatchFiles/RunUAT.bat` in every .fsx.

/// Unreal Engine helpers. Resolved against `Engine.path`.
[<RequireQualifiedAccess>]
module UE =
    open System.IO

    let private taskFail msg : 'T = raise (TaskFailure msg)

    let private engineRoot () =
        match Engine.path with
        | "" -> taskFail "UE.* helpers require engine.path; runner did not detect Unreal Engine. Set [engine] engine_path in project.toml or install Unreal."
        | p -> p

    /// Path to `RunUAT.bat` under the resolved engine root.
    let uatBatPath () : string =
        Path.Combine(engineRoot (), "Engine", "Build", "BatchFiles", "RunUAT.bat")

    /// Path to `Build.bat` (UnrealBuildTool driver) under the engine root.
    let ubtBatPath () : string =
        Path.Combine(engineRoot (), "Engine", "Build", "BatchFiles", "Build.bat")

    /// Path to UnrealEditor.exe — falls back to the conventional
    /// location if the runner didn't fill in `engine.executable`.
    let editorPath () : string =
        match Engine.executable with
        | "" -> Path.Combine(engineRoot (), "Engine", "Binaries", "Win64", "UnrealEditor.exe")
        | p -> p

    /// Run UAT (`RunUAT.bat <args>`). Stdout streams into the run log.
    let runUAT (args: string list) : unit =
        Cmd.exec (uatBatPath ()) args

    /// Run UBT (`Build.bat <args>`).
    let runUBT (args: string list) : unit =
        Cmd.exec (ubtBatPath ()) args

/// Unity helpers. `Engine.executable` for Unity points at
/// `<engine>/Editor/Unity.exe`; runBatch prefixes the standard flags
/// every CI invocation needs.
[<RequireQualifiedAccess>]
module Unity =
    open System.IO

    let private taskFail msg : 'T = raise (TaskFailure msg)

    let editorPath () : string =
        match Engine.executable with
        | "" ->
            match Engine.path with
            | "" -> taskFail "Unity.* helpers require engine.path/executable; runner did not detect Unity. Set [engine] engine_path in project.toml or install Unity Hub."
            | p -> Path.Combine(p, "Editor", "Unity.exe")
        | p -> p

    /// `-batchmode -nographics -quit` plus user args. `-quit` is
    /// essential — without it Unity's batch process sits open waiting
    /// for input and the runner times out / hangs.
    let runBatch (args: string list) : unit =
        let prefix = [ "-batchmode"; "-nographics"; "-quit" ]
        Cmd.exec (editorPath ()) (prefix @ args)

/// Godot helpers. `Engine.executable` IS the Godot binary path
/// (Godot has no install layout to peer into).
[<RequireQualifiedAccess>]
module Godot =

    let private taskFail msg : 'T = raise (TaskFailure msg)

    let editorPath () : string =
        match Engine.executable with
        | "" -> taskFail "Godot.* helpers require engine.executable; runner did not detect Godot. Set [engine] engine_path in project.toml or place godot.exe on PATH."
        | p -> p

    /// Prepends `--headless` so the export doesn't try to open a window
    /// on a CI box. Godot 4.x convention; the flag became a hard
    /// requirement somewhere in the 4.0 release line.
    let runHeadless (args: string list) : unit =
        Cmd.exec (editorPath ()) ("--headless" :: args)
