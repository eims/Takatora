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

    type private Channel = {
        InputRoot: JsonObject
        OutputPath: string option
        EventsPath: string option
    }

    let private syncRoot = obj ()
    let mutable private channelOpt: Channel option = None

    let private envOrEmpty (name: string) =
        match Environment.GetEnvironmentVariable(name) with
        | null -> ""
        | s -> s

    let private buildChannel () : Channel =
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
          EventsPath = optPath "TAKATORA_EVENTS_FILE" }

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
        lock syncRoot (fun () -> channelOpt <- None)

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

// ─── Public SDK surface ────────────────────────────────────────────

/// Read typed input parameters declared in the flow's TOML for this step.
/// Mismatched types or missing required keys raise `TaskFailure`.
[<RequireQualifiedAccess>]
module Param =

    let private taskFail msg : 'T = raise (TaskFailure msg)

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

    /// Required typed param. `'T` should be string / bool / int / int64 /
    /// float / double for primitives; arrays and lists work too.
    let required<'T> (name: string) : 'T =
        match Io.tryParam name with
        | Some node when not (isNull node) -> convert<'T> name node
        | _ -> taskFail $"Required param '{name}' is missing from task input"

    /// Optional typed param with a fallback default.
    let optional<'T> (name: string) (defaultValue: 'T) : 'T =
        match Io.tryParam name with
        | Some node when not (isNull node) -> convert<'T> name node
        | _ -> defaultValue

    /// True when the param is present and non-null.
    let has (name: string) : bool =
        match Io.tryParam name with
        | Some node when not (isNull node) -> true
        | _ -> false

    /// String param constrained to a fixed set of allowed values. Used for
    /// flow vars declared as `type = "enum"`.
    let requiredEnum (name: string) (values: string list) : string =
        let v = required<string> name
        if List.contains v values then v
        else
            let allowed = String.concat ", " values
            taskFail $"Param '{name}' must be one of [{allowed}], got '{v}'"

/// Surface step outputs to subsequent steps via NDJSON appended to
/// `TAKATORA_OUTPUT_FILE`. Visible as `${steps.<id>.outputs.<name>}` in
/// later flow steps.
[<RequireQualifiedAccess>]
module Output =
    let set (name: string) (value: obj) : unit = Io.writeOutput name value

/// Time + log a logical sub-section of a task. Substep events appear
/// in `TAKATORA_EVENTS_FILE` so the GUI can render a tree view.
[<RequireQualifiedAccess>]
module Step =

    let private elapsedSec (start: DateTimeOffset) =
        (DateTimeOffset.UtcNow - start).TotalSeconds

    /// Run an action wrapped in substep.start / substep.end events.
    /// Exceptions are logged as substep.end status=fail and re-raised.
    let run (name: string) (action: unit -> unit) : unit =
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
        Io.writeEvent "substep.skip" [
            "name", box name
            "reason", box reason
        ]

/// Leveled logging routed through `TAKATORA_EVENTS_FILE`.
[<RequireQualifiedAccess>]
module Log =
    let private emit (level: string) (message: string) =
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

/// Read-only project metadata as supplied by the runner.
/// Members are properties (not let-bound values) so they re-read the
/// input on each access; otherwise tests + GUI re-init scenarios would
/// pin to whatever the input was at module init time.
type Project =
    static member workingDir : string =
        Io.tryProjectField "working_dir" |> Option.defaultValue ""
    static member name : string =
        Io.tryProjectField "name" |> Option.defaultValue ""

/// Task-level control flow.
[<RequireQualifiedAccess>]
module Task =
    /// Abort the task with a clean message — runner won't print the F# stack.
    /// Use for user-facing failure reasons; let plain exceptions handle bugs.
    let fail<'T> (reason: string) : 'T = raise (TaskFailure reason)

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

    let private taskFail msg : 'T = raise (TaskFailure msg)

    let private buildPsi (exe: string) (args: string list) (opts: ExecOptions) : ProcessStartInfo =
        let psi = ProcessStartInfo(exe)
        for a in args do psi.ArgumentList.Add(a)
        psi.UseShellExecute <- false
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
        let psi = buildPsi exe args opts
        // No redirection — child inherits fsi's stdout/stderr, which
        // the runner is already capturing into log.txt.
        use proc = Process.Start(psi)
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
        let psi = buildPsi exe args opts
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
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
