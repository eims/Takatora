namespace Takatora.Core

open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Tomlyn.Model

/// How the Toolbox list is ordered. A user preference, persisted per
/// machine (not shared): the sort you like is about *your* workflow, not
/// the project. `ByLastRun` puts recently-run tools on top.
type ToolSort =
    | ByName
    | ByLastRun
    | ByExtension

/// Project-side toolbox config, read from `<root>/.takatora/toolbox.toml`.
/// Committed with the project and shared via VCS: the directories a repo's
/// helper scripts live in are a property of the repo, not the developer.
type ToolboxConfig = {
    /// Directories scanned for scripts. Each is either relative to the
    /// project root or an absolute path.
    ScriptDirs: string list
}

/// One discovered script.
type ToolEntry = {
    /// Stable identity: path relative to the project root, '/'-separated
    /// (absolute, '/'-separated, when the script lives outside the root).
    /// Disambiguates same-named scripts in different dirs, and survives
    /// across machines so the shared config's toggles line up.
    Key: string
    /// File name incl. extension, for display.
    Name: string
    /// Lowercase extension with the dot, e.g. ".ps1".
    Extension: string
    /// Absolute path to the script.
    FullPath: string
    /// Absolute containing directory — the working dir a run uses, so a
    /// script's relative references (`.\config`, `./venv`) resolve.
    Dir: string
}

/// Machine-local toolbox state (`state.toml` under the per-project state
/// dir). Never committed — the ON/OFF toggles and sort order are how one
/// developer uses the tools, not a project fact.
type ToolboxLocalState = {
    /// Tool keys the user switched OFF. OFF tools sort below ON tools and
    /// can't be run until re-enabled. Stale keys (script since deleted)
    /// are inert.
    Disabled: Set<string>
    Sort: ToolSort
}

/// One line of `history.ndjson` — a single completed tool run. Kept out
/// of `.takatora/runs/` on purpose so Toolbox runs never pollute the
/// flow (Job) history.
type ToolRunRecord = {
    ToolKey: string
    StartedAt: DateTimeOffset
    DurationSec: float
    ExitCode: int
    /// Absolute path to the captured stdout/stderr log.
    LogPath: string
}

/// Discover, persist, and run project "toolbox" scripts.
///
/// Layout:
///   - Project side  : `<root>/.takatora/toolbox.toml` (scan dirs — shared).
///   - Machine side  : `%APPDATA%/Takatora/toolbox/<slug>/`
///                       state.toml, history.ndjson, logs/  (per developer).
///
/// All logic lives here (not in the GUI) so a future `takatora toolbox`
/// CLI can reuse it verbatim. `runTool` blocks; the GUI wraps it on a
/// worker thread and marshals the result back to the UI thread.
///
/// A tool spawned right before the app exits keeps running and its record
/// is not written — same contract as flow runs (closing a run doesn't kill
/// it). Acceptable: the log still lands, only the history line is missed.
[<RequireQualifiedAccess>]
module Toolbox =

    // ----- test hook (redirect the state root off %APPDATA%) -----

    let mutable private stateRootOverride : string option = None
    let internal setStateRootForTests (path: string) = stateRootOverride <- Some path
    let internal resetStateRoot () = stateRootOverride <- None

    // ----- shared helpers -----

    /// Minimal TOML string literal — backslash + quote escaping is enough
    /// for the paths/names we write (mirrors AppSettings/ProjectRegistry).
    let private esc (s: string) =
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

    let private tomlStrList (table: TomlTable) (key: string) : string list =
        match table.TryGetValue key with
        | true, (:? TomlArray as arr) ->
            [ for v in arr do
                match v with
                | :? string as s -> yield s
                | _ -> () ]
        | _ -> []

    // ----- paths -----

    let configPath (projectRoot: string) : string =
        Path.Combine(projectRoot, ".takatora", "toolbox.toml")

    let private stateRoot () : string =
        match stateRootOverride with
        | Some p -> p
        | None -> Path.Combine(AppData.baseDir (), "toolbox")

    let private sanitizeFileName (s: string) : string =
        let invalid = Set.ofArray (Path.GetInvalidFileNameChars())
        String(s |> Seq.map (fun c -> if invalid.Contains c || c = ' ' then '_' else c) |> Seq.toArray)

    /// Per-project state dir name: readable project name + 8 hex of the
    /// root path's hash. The registry keys projects by Name, but a name can
    /// collide after remove/re-add at a different path — the path hash keeps
    /// the dir collision-proof while the name stays debuggable.
    let slug (projectName: string) (projectRoot: string) : string =
        let normRoot = (Path.GetFullPath projectRoot).ToLowerInvariant()
        use sha = SHA256.Create()
        let hash8 =
            sha.ComputeHash(Encoding.UTF8.GetBytes normRoot)
            |> Array.take 4
            |> Array.map (sprintf "%02x")
            |> String.concat ""
        let namePart =
            let n = sanitizeFileName projectName
            if String.IsNullOrWhiteSpace n then "project" else n
        namePart + "-" + hash8

    let stateDir (projectName: string) (projectRoot: string) : string =
        Path.Combine(stateRoot (), slug projectName projectRoot)

    // ----- config (toolbox.toml) -----

    /// Missing/malformed file → empty (no dirs configured yet).
    let loadConfig (projectRoot: string) : ToolboxConfig =
        let path = configPath projectRoot
        if not (File.Exists path) then { ScriptDirs = [] }
        else
            try
                let table =
                    Tomlyn.TomlSerializer.Deserialize<TomlTable>(
                        File.ReadAllText path, Tomlyn.TomlSerializerOptions())
                { ScriptDirs = tomlStrList table "script_dirs" }
            with _ -> { ScriptDirs = [] }

    let saveConfig (projectRoot: string) (cfg: ToolboxConfig) : unit =
        let path = configPath projectRoot
        let dir = Path.GetDirectoryName path
        if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore
        let sb = StringBuilder()
        sb.AppendLine("# Takatora toolbox — directories scanned for runnable scripts.") |> ignore
        sb.AppendLine("# Committed with the project; shared via VCS. Edit via the GUI Settings tab.") |> ignore
        if not (List.isEmpty cfg.ScriptDirs) then
            let arr = cfg.ScriptDirs |> List.map esc |> String.concat ", "
            sb.AppendLine(sprintf "script_dirs = [%s]" arr) |> ignore
        File.WriteAllText(path, sb.ToString())

    // ----- local state (state.toml) -----

    let private sortToString = function
        | ByName -> "name"
        | ByLastRun -> "last_run"
        | ByExtension -> "extension"

    let private sortOfString = function
        | "last_run" -> ByLastRun
        | "extension" -> ByExtension
        | _ -> ByName

    let loadState (stateDir: string) : ToolboxLocalState =
        let path = Path.Combine(stateDir, "state.toml")
        if not (File.Exists path) then { Disabled = Set.empty; Sort = ByName }
        else
            try
                let table =
                    Tomlyn.TomlSerializer.Deserialize<TomlTable>(
                        File.ReadAllText path, Tomlyn.TomlSerializerOptions())
                let sort =
                    match table.TryGetValue "sort" with
                    | true, (:? string as s) -> sortOfString s
                    | _ -> ByName
                { Disabled = tomlStrList table "disabled" |> Set.ofList
                  Sort = sort }
            with _ -> { Disabled = Set.empty; Sort = ByName }

    let saveState (stateDir: string) (st: ToolboxLocalState) : unit =
        Directory.CreateDirectory stateDir |> ignore
        let sb = StringBuilder()
        sb.AppendLine("# Takatora toolbox state — machine-local, never committed.") |> ignore
        sb.AppendLine(sprintf "sort = %s" (esc (sortToString st.Sort))) |> ignore
        if not (Set.isEmpty st.Disabled) then
            let arr = st.Disabled |> Set.toList |> List.map esc |> String.concat ", "
            sb.AppendLine(sprintf "disabled = [%s]" arr) |> ignore
        File.WriteAllText(Path.Combine(stateDir, "state.toml"), sb.ToString())

    // ----- history (history.ndjson) -----

    // Single process, but two tools can finish at once; serialize appends.
    let private historyLock = obj ()

    let appendHistory (stateDir: string) (record: ToolRunRecord) : unit =
        Directory.CreateDirectory stateDir |> ignore
        // Store the log path relative to the state dir so the whole toolbox
        // dir stays relocatable; resolved back to absolute on load.
        let logRel =
            try Path.GetRelativePath(stateDir, record.LogPath).Replace('\\', '/')
            with _ -> record.LogPath
        let node = JsonObject()
        node["tool"] <- JsonValue.Create record.ToolKey
        node["started_at"] <- JsonValue.Create (record.StartedAt.ToString("o", CultureInfo.InvariantCulture))
        node["duration_sec"] <- JsonValue.Create record.DurationSec
        node["exit_code"] <- JsonValue.Create record.ExitCode
        node["log"] <- JsonValue.Create logRel
        let line = node.ToJsonString()
        lock historyLock (fun () ->
            File.AppendAllText(Path.Combine(stateDir, "history.ndjson"), line + "\n"))

    /// Newest first. Malformed lines are skipped (a broken line shouldn't
    /// break the list — same tolerance as RunHistory). Only the last
    /// `maxEntries` are returned; the file itself stays small in practice.
    let loadHistory (stateDir: string) (maxEntries: int) : ToolRunRecord list =
        let path = Path.Combine(stateDir, "history.ndjson")
        if not (File.Exists path) then []
        else
            try
                File.ReadAllLines path
                |> Array.choose (fun line ->
                    if String.IsNullOrWhiteSpace line then None
                    else
                        try
                            match JsonNode.Parse line with
                            | null -> None
                            | node ->
                                let started =
                                    match DateTimeOffset.TryParse(
                                            node["started_at"].GetValue<string>(),
                                            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
                                    | true, dt -> dt
                                    | _ -> DateTimeOffset.MinValue
                                let logRel = node["log"].GetValue<string>()
                                let logAbs =
                                    if Path.IsPathRooted logRel then logRel
                                    else Path.GetFullPath(Path.Combine(stateDir, logRel))
                                Some {
                                    ToolKey = node["tool"].GetValue<string>()
                                    StartedAt = started
                                    DurationSec = node["duration_sec"].GetValue<float>()
                                    ExitCode = node["exit_code"].GetValue<int>()
                                    LogPath = logAbs
                                }
                        with _ -> None)
                |> Array.rev
                |> Array.truncate maxEntries
                |> List.ofArray
            with _ -> []

    /// Most-recent run per tool key.
    let lastRuns (records: ToolRunRecord list) : Map<string, ToolRunRecord> =
        (Map.empty, records)
        ||> List.fold (fun acc r ->
            match Map.tryFind r.ToolKey acc with
            | Some existing when existing.StartedAt >= r.StartedAt -> acc
            | _ -> Map.add r.ToolKey r acc)

    // ----- scan -----

    let supportedExtensions : Set<string> =
        Set.ofList [ ".bat"; ".cmd"; ".ps1"; ".sh" ]

    // Directories never worth walking into for user scripts.
    let private skipDirNames = Set.ofList [ "node_modules"; "obj"; "bin"; "__pycache__" ]

    /// Discover scripts under the configured dirs. Relative dirs resolve
    /// against the root; missing dirs are skipped. Recursive, skipping
    /// dot-directories and the denylist, capped at depth 8 (symlink-cycle
    /// guard). De-duped by Key (so overlapping scan dirs don't double-list).
    let scan (projectRoot: string) (cfg: ToolboxConfig) : ToolEntry list =
        let rootFull = Path.GetFullPath projectRoot
        let keyOf (full: string) =
            let rel = Path.GetRelativePath(rootFull, full)
            if rel.StartsWith ".." || Path.IsPathRooted rel then full.Replace('\\', '/')
            else rel.Replace('\\', '/')
        let results = Dictionary<string, ToolEntry>(StringComparer.OrdinalIgnoreCase)
        let rec walk (dir: string) (depth: int) =
            if depth > 8 then () else
            let entries =
                try Some (Directory.EnumerateFileSystemEntries dir |> Seq.toArray)
                with _ -> None
            match entries with
            | None -> ()
            | Some es ->
                for e in es do
                    if Directory.Exists e then
                        let name = Path.GetFileName e
                        if not (name.StartsWith ".") && not (skipDirNames.Contains (name.ToLowerInvariant())) then
                            walk e (depth + 1)
                    elif File.Exists e then
                        let ext = (Path.GetExtension e).ToLowerInvariant()
                        if supportedExtensions.Contains ext then
                            let full = Path.GetFullPath e
                            let key = keyOf full
                            if not (results.ContainsKey key) then
                                results[key] <-
                                    { Key = key
                                      Name = Path.GetFileName full
                                      Extension = ext
                                      FullPath = full
                                      Dir = Path.GetDirectoryName full }
        for d in cfg.ScriptDirs do
            let resolved = if Path.IsPathRooted d then d else Path.Combine(rootFull, d)
            if Directory.Exists resolved then walk (Path.GetFullPath resolved) 0
        results.Values |> List.ofSeq

    // ----- sorting -----

    /// ON tools first, OFF tools after; each group ordered by `sort`.
    /// Pure — `lastRun` supplies the most-recent time per key (None = never
    /// run, which sorts last within ByLastRun). F#'s sortWith is stable.
    let sortTools
            (sort: ToolSort)
            (lastRun: string -> DateTimeOffset option)
            (disabled: Set<string>)
            (tools: ToolEntry list)
            : ToolEntry list =
        let isOn (t: ToolEntry) = not (disabled.Contains t.Key)
        let within (a: ToolEntry) (b: ToolEntry) =
            match sort with
            | ByName ->
                match String.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) with
                | 0 -> String.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase)
                | c -> c
            | ByExtension ->
                match String.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase) with
                | 0 -> String.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
                | c -> c
            | ByLastRun ->
                match lastRun a.Key, lastRun b.Key with
                | Some da, Some db -> compare db da   // most recent first
                | Some _, None -> -1
                | None, Some _ -> 1
                | None, None -> String.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
        tools
        |> List.sortWith (fun a b ->
            match isOn a, isOn b with
            | true, false -> -1
            | false, true -> 1
            | _ -> within a b)

    // ----- running -----

    let private findOnPath (name: string) : string option =
        let exeName =
            if OperatingSystem.IsWindows() && not (name.EndsWith ".exe") then name + ".exe"
            else name
        let pathVar = Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue ""
        pathVar.Split(Path.PathSeparator)
        |> Array.tryPick (fun d ->
            if String.IsNullOrWhiteSpace d then None
            else
                let candidate = Path.Combine(d, exeName)
                if File.Exists candidate then Some candidate else None)

    /// The interpreter for a script extension: the executable plus a
    /// function that builds its argument list from the script path. An
    /// `Error` means the interpreter isn't available (surfaced, not run).
    let interpreterFor (extension: string) : Result<string * (string -> string list), string> =
        match extension.ToLowerInvariant() with
        | ".bat" | ".cmd" ->
            // cmd.exe is resolved via PATH (System32); ComSpec if set.
            let exe = Environment.GetEnvironmentVariable "ComSpec" |> Option.ofObj |> Option.defaultValue "cmd.exe"
            Ok (exe, fun p -> [ "/c"; p ])
        | ".ps1" ->
            match findOnPath "pwsh" |> Option.orElseWith (fun () -> findOnPath "powershell") with
            | Some exe -> Ok (exe, fun p -> [ "-NoProfile"; "-ExecutionPolicy"; "Bypass"; "-File"; p ])
            | None -> Error "PowerShell (pwsh / powershell) not found on PATH"
        | ".sh" ->
            match findOnPath "bash" with
            | Some exe -> Ok (exe, fun p -> [ p ])
            | None -> Error "bash not found on PATH (install Git Bash or WSL bash)"
        | other -> Error (sprintf "unsupported script type: %s" other)

    let private logFileSlug (key: string) =
        key
        |> String.map (fun c -> if c = '/' || c = '\\' || c = '.' || c = ' ' then '-' else c)
        |> sanitizeFileName

    /// Run a tool to completion, streaming stdout+stderr to a fresh log
    /// file under `<stateDir>/logs/`, and append a history record. BLOCKING
    /// — the GUI calls this on a worker thread. `Error` covers a since-
    /// deleted script, a missing interpreter, or a spawn failure; nothing
    /// is recorded in those cases.
    let runTool (stateDir: string) (tool: ToolEntry) : Result<ToolRunRecord, string> =
        if not (File.Exists tool.FullPath) then Error "script no longer exists on disk"
        else
            match interpreterFor tool.Extension with
            | Error e -> Error e
            | Ok (exe, argsOf) ->
                try
                    let logsDir = Path.Combine(stateDir, "logs")
                    Directory.CreateDirectory logsDir |> ignore
                    let started = DateTimeOffset.Now
                    let logName =
                        sprintf "%s-%s.log"
                            (started.ToString "yyyyMMdd-HHmmss")
                            (logFileSlug tool.Key)
                    let logPath = Path.Combine(logsDir, logName)

                    let psi = ProcessStartInfo(exe)
                    for a in argsOf tool.FullPath do psi.ArgumentList.Add a
                    psi.WorkingDirectory <- tool.Dir
                    psi.UseShellExecute <- false
                    psi.RedirectStandardOutput <- true
                    psi.RedirectStandardError <- true
                    psi.CreateNoWindow <- true

                    use writer = new StreamWriter(logPath, false)
                    let writeLock = obj ()
                    let writeLine (s: string) =
                        if not (isNull s) then lock writeLock (fun () -> writer.WriteLine s)
                    use proc = new Process(StartInfo = psi)
                    proc.OutputDataReceived.Add(fun e -> writeLine e.Data)
                    proc.ErrorDataReceived.Add(fun e -> writeLine e.Data)

                    let sw = Stopwatch.StartNew()
                    proc.Start() |> ignore
                    proc.BeginOutputReadLine()
                    proc.BeginErrorReadLine()
                    proc.WaitForExit()   // no-arg overload also flushes async reads
                    sw.Stop()
                    writer.Flush()

                    let record =
                        { ToolKey = tool.Key
                          StartedAt = started.ToUniversalTime()
                          DurationSec = sw.Elapsed.TotalSeconds
                          ExitCode = proc.ExitCode
                          LogPath = logPath }
                    appendHistory stateDir record
                    Ok record
                with ex -> Error ex.Message
