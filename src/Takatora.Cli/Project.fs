module Takatora.Cli.Project

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json.Nodes
open Takatora.Core

/// Output format shared with `run` / `detect-engines`. Human and JSON.
type Format = Human | Json

let parseFormat (s: string) : Result<Format, string> =
    match s with
    | null | "" | "human" -> Ok Human
    | "json" -> Ok Json
    | other -> Error (sprintf "unknown format '%s', expected human | json" other)

// ─── add ──────────────────────────────────────────────────────────

let invokeAdd (path: string) (nameHint: string option) : int =
    match ProjectRegistry.add path nameHint with
    | ProjectRegistry.Added entry ->
        Console.Out.WriteLine(sprintf "Added '%s' → %s" entry.Name entry.Path)
        0
    | ProjectRegistry.DuplicateName existing ->
        Console.Error.WriteLine(
            sprintf "project: '%s' is already registered → %s%s  Use a different --name, or `takatora project remove %s` first."
                existing.Name existing.Path Environment.NewLine existing.Name)
        2
    | ProjectRegistry.InvalidPath reason ->
        Console.Error.WriteLine(sprintf "project: %s" reason)
        3

// ─── remove ───────────────────────────────────────────────────────

let invokeRemove (name: string) : int =
    if ProjectRegistry.remove name then
        Console.Out.WriteLine(sprintf "Removed '%s'" name)
        0
    else
        Console.Error.WriteLine(sprintf "project: '%s' is not registered" name)
        3

// ─── list ─────────────────────────────────────────────────────────

let private listToJson (entries: ProjectRegistration list) : string =
    let root = JsonObject()
    let arr = JsonArray()
    for e in entries do
        let item = JsonObject()
        item.["name"]     <- JsonValue.Create(e.Name)
        item.["path"]     <- JsonValue.Create(e.Path)
        item.["added_at"] <- JsonValue.Create(e.AddedAt.ToString("o", CultureInfo.InvariantCulture))
        arr.Add(item)
    root.["projects"] <- arr
    root.ToJsonString(System.Text.Json.JsonSerializerOptions(WriteIndented = true))

let private listToHuman (entries: ProjectRegistration list) : string =
    if List.isEmpty entries then
        "(no projects registered — use `takatora project add <path>`)\n"
    else
        let sb = StringBuilder()
        for e in entries do
            sb.AppendLine(sprintf "%-20s %s" e.Name e.Path) |> ignore
        sb.ToString()

let invokeList (format: Format) : int =
    let entries = ProjectRegistry.load ()
    let text =
        match format with
        | Human -> listToHuman entries
        | Json  -> listToJson  entries + Environment.NewLine
    Console.Out.Write(text)
    0

// ─── info ─────────────────────────────────────────────────────────

let private infoToJson (entry: ProjectRegistration) (project: Project option) (flows: Flow list) : string =
    let root = JsonObject()
    root.["name"]     <- JsonValue.Create(entry.Name)
    root.["path"]     <- JsonValue.Create(entry.Path)
    root.["added_at"] <- JsonValue.Create(entry.AddedAt.ToString("o", CultureInfo.InvariantCulture))
    match project with
    | Some p ->
        let proj = JsonObject()
        proj.["name"]        <- JsonValue.Create(p.Name)
        proj.["working_dir"] <- JsonValue.Create(p.WorkingDir)
        let engine = JsonObject()
        engine.["type"] <-
            JsonValue.Create(
                match p.Engine.Kind with
                | EngineKind.Unreal -> "unreal"
                | EngineKind.Unity  -> "unity"
                | EngineKind.Godot  -> "godot")
        p.Engine.ProjectFile  |> Option.iter (fun v -> engine.["project_file"]  <- JsonValue.Create(v))
        p.Engine.EnginePath   |> Option.iter (fun v -> engine.["path"]          <- JsonValue.Create(v))
        p.Engine.EngineVersion|> Option.iter (fun v -> engine.["version"]       <- JsonValue.Create(v))
        proj.["engine"] <- engine
        root.["project"] <- proj
    | None ->
        root.["project"] <- null
    let flowsArr = JsonArray()
    for f in flows do
        let item = JsonObject()
        item.["id"]    <- JsonValue.Create(f.Id)
        f.Name |> Option.iter (fun n -> item.["name"] <- JsonValue.Create(n))
        item.["steps"] <- JsonValue.Create(List.length f.Steps)
        item.["vars"]  <- JsonValue.Create(List.length f.Vars)
        flowsArr.Add(item)
    root.["flows"] <- flowsArr
    root.ToJsonString(System.Text.Json.JsonSerializerOptions(WriteIndented = true))

let private infoToHuman (entry: ProjectRegistration) (project: Project option) (flows: Flow list) : string =
    let sb = StringBuilder()
    sb.AppendLine(sprintf "Name:     %s" entry.Name) |> ignore
    sb.AppendLine(sprintf "Path:     %s" entry.Path) |> ignore
    sb.AppendLine(sprintf "Added:    %s" (entry.AddedAt.ToString("yyyy-MM-dd HH:mm zzz",
                                                                  CultureInfo.InvariantCulture))) |> ignore
    match project with
    | Some p ->
        let engineKind =
            match p.Engine.Kind with
            | EngineKind.Unreal -> "unreal"
            | EngineKind.Unity  -> "unity"
            | EngineKind.Godot  -> "godot"
        sb.AppendLine(sprintf "Engine:   %s%s"
                        engineKind
                        (p.Engine.EngineVersion
                         |> Option.map (fun v -> " " + v)
                         |> Option.defaultValue ""))
        |> ignore
    | None ->
        sb.AppendLine("Engine:   (could not parse .ci/project.toml)") |> ignore
    sb.AppendLine() |> ignore
    if List.isEmpty flows then
        sb.AppendLine("Flows:    (none)") |> ignore
    else
        sb.AppendLine(sprintf "Flows (%d):" (List.length flows)) |> ignore
        for f in flows do
            let display =
                f.Name
                |> Option.map (fun n -> sprintf "%s — %s" f.Id n)
                |> Option.defaultValue f.Id
            sb.AppendLine(sprintf "  - %s (%d step(s), %d var(s))"
                            display (List.length f.Steps) (List.length f.Vars))
            |> ignore
    sb.ToString()

let invokeInfo (name: string) (format: Format) : int =
    match ProjectRegistry.find name (ProjectRegistry.load ()) with
    | None ->
        Console.Error.WriteLine(sprintf "project: '%s' is not registered" name)
        3
    | Some entry ->
        // Best-effort load of project.toml + flows.toml — info still
        // works (with partial output) if those moved or got broken.
        let projectToml = Path.Combine(entry.Path, ".ci", "project.toml")
        let flowsToml   = Path.Combine(entry.Path, ".ci", "flows.toml")
        let project =
            if File.Exists projectToml then
                try Some (TomlConfig.loadProject projectToml) with _ -> None
            else None
        let flows =
            if File.Exists flowsToml then
                try TomlConfig.loadFlows flowsToml with _ -> []
            else []
        let text =
            match format with
            | Human -> infoToHuman entry project flows
            | Json  -> infoToJson  entry project flows + Environment.NewLine
        Console.Out.Write(text)
        0
