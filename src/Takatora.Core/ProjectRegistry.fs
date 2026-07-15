namespace Takatora.Core

open System
open System.Globalization
open System.IO
open System.Text
open Tomlyn.Model

/// One registered project entry as it lives on disk under
/// `%APPDATA%\Takatora\projects.toml`. The name doubles as the lookup
/// key for `takatora run <name> <flow>` and similar commands.
type ProjectRegistration = {
    Name: string
    Path: string
    AddedAt: DateTimeOffset
}

[<RequireQualifiedAccess>]
module ProjectRegistry =

    // Test hook: tests redirect the registry to a tmp file by calling
    // `setPathForTests`. Reset via `clearPathOverride`. Production code
    // never touches these.
    let mutable private pathOverride : string option = None
    let internal setPathForTests (path: string) =
        pathOverride <- Some path
    let internal clearPathOverride () =
        pathOverride <- None

    /// Standard registry location. Per design: machine-local, never
    /// committed to a repo. The dir is created on first save.
    let registryPath () : string =
        match pathOverride with
        | Some p -> p
        | None -> Path.Combine(AppData.baseDir (), "projects.toml")

    let private parseRegistration (tbl: TomlTable) : ProjectRegistration option =
        let tryStr key =
            match tbl.TryGetValue(key) with
            | true, (:? string as s) -> Some s
            | _ -> None
        match tryStr "name", tryStr "path" with
        | Some name, Some path ->
            let addedAt =
                match tryStr "added_at" with
                | Some s ->
                    match DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                                                  DateTimeStyles.RoundtripKind) with
                    | true, dt -> dt
                    | _ -> DateTimeOffset.UtcNow
                | None -> DateTimeOffset.UtcNow
            Some { Name = name; Path = path; AddedAt = addedAt }
        | _ -> None

    /// Load the registry from disk. Missing file → empty list (a fresh
    /// install hasn't registered anything yet). Malformed entries are
    /// skipped rather than failing the whole load.
    let load () : ProjectRegistration list =
        let path = registryPath ()
        if not (File.Exists path) then []
        else
            try
                let text = File.ReadAllText path
                let table =
                    Tomlyn.TomlSerializer.Deserialize<TomlTable>(
                        text, Tomlyn.TomlSerializerOptions())
                match table.TryGetValue("projects") with
                | true, (:? TomlTableArray as arr) ->
                    arr |> Seq.choose parseRegistration |> List.ofSeq
                | _ -> []
            with _ -> []

    let private writeTomlString (s: string) =
        // Minimal TOML string literal — backslash + double-quote escaping
        // is enough for the paths + names we write here.
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

    /// Overwrite the registry with the given entries. The parent
    /// directory is created on demand.
    let save (entries: ProjectRegistration list) : unit =
        let path = registryPath ()
        let dir = Path.GetDirectoryName(path)
        if not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore
        let sb = StringBuilder()
        sb.AppendLine("# Takatora project registry — machine-local, never committed.") |> ignore
        sb.AppendLine("# Edit via `takatora project add / remove` instead of by hand.") |> ignore
        sb.AppendLine() |> ignore
        for e in entries do
            sb.AppendLine("[[projects]]") |> ignore
            sb.AppendFormat("name = {0}\n", writeTomlString e.Name)     |> ignore
            sb.AppendFormat("path = {0}\n", writeTomlString e.Path)     |> ignore
            sb.AppendFormat("added_at = {0}\n",
                            writeTomlString (e.AddedAt.ToString("o", CultureInfo.InvariantCulture)))
            |> ignore
            sb.AppendLine() |> ignore
        File.WriteAllText(path, sb.ToString())

    /// Look up by name. Case-sensitive on purpose — project names are
    /// stable identifiers, not free-form labels.
    let find (name: string) (entries: ProjectRegistration list) : ProjectRegistration option =
        entries |> List.tryFind (fun e -> e.Name = name)

    /// Outcome of an add operation. Distinct from an exception type
    /// because "already registered" is a normal user mistake (not an
    /// internal failure) and the CLI wants to render it specifically.
    type AddOutcome =
        | Added of ProjectRegistration
        | DuplicateName of existing: ProjectRegistration
        | InvalidPath of reason: string

    /// Register a project. `nameHint`, if provided, overrides the
    /// project.toml's declared name. Verifies that `<path>/.takatora/project.toml`
    /// exists before recording anything.
    let add (path: string) (nameHint: string option) : AddOutcome =
        let absPath = Path.GetFullPath(path)
        if not (Directory.Exists absPath) then
            InvalidPath (sprintf "directory does not exist: %s" absPath)
        else
            let projectToml = Path.Combine(absPath, ".takatora", "project.toml")
            if not (File.Exists projectToml) then
                InvalidPath (sprintf "no .takatora/project.toml at %s" absPath)
            else
                let parsed =
                    try Some (TomlConfig.loadProject projectToml)
                    with _ -> None
                let resolvedName =
                    match nameHint with
                    | Some n when not (String.IsNullOrWhiteSpace n) -> n
                    | _ ->
                        match parsed with
                        | Some p -> p.Name
                        | None -> Path.GetFileName(absPath.TrimEnd('\\', '/'))
                let entries = load ()
                match find resolvedName entries with
                | Some existing -> DuplicateName existing
                | None ->
                    let entry =
                        { Name = resolvedName
                          Path = absPath
                          AddedAt = DateTimeOffset.UtcNow }
                    save (entries @ [ entry ])
                    Added entry

    /// Returns true if an entry was removed, false if no match.
    let remove (name: string) : bool =
        let entries = load ()
        match find name entries with
        | None -> false
        | Some _ ->
            entries
            |> List.filter (fun e -> e.Name <> name)
            |> save
            true
