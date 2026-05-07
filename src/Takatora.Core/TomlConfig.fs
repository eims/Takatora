namespace Takatora.Core

open System.IO
open Tomlyn
open Tomlyn.Model

/// Raised when a TOML config file is structurally valid TOML but does not
/// satisfy Takatora's project/flows schema. Tomlyn's own syntax errors are
/// translated into this with a unified prefix.
exception TomlConfigError of message: string

[<RequireQualifiedAccess>]
module TomlConfig =

    let private fail msg = raise (TomlConfigError msg)

    let rec private convert (v: obj) : TomlValue =
        match v with
        | :? string as s -> TString s
        | :? bool as b -> TBool b
        | :? int64 as i -> TInt i
        | :? double as d -> TFloat d
        | :? TomlArray as arr ->
            arr |> Seq.map convert |> List.ofSeq |> TArray
        | :? TomlTable as tbl ->
            tbl
            |> Seq.map (fun kv -> kv.Key, convert kv.Value)
            |> Map.ofSeq
            |> TTable
        | null -> fail "unexpected null TOML value"
        | other -> fail $"unsupported TOML value of type {other.GetType().Name}"

    let private toModel (text: string) : TomlTable =
        try
            Tomlyn.TomlSerializer.Deserialize<TomlTable>(text, TomlSerializerOptions())
        with :? Tomlyn.TomlException as ex ->
            fail $"TOML parse error: {ex.Message}"

    let private getTable (tbl: TomlTable) (key: string) : TomlTable =
        match tbl.TryGetValue(key) with
        | true, (:? TomlTable as t) -> t
        | true, v -> fail $"'{key}' must be a table, got {v.GetType().Name}"
        | _ -> fail $"missing required table '{key}'"

    let private tryTable (tbl: TomlTable) (key: string) : TomlTable option =
        match tbl.TryGetValue(key) with
        | true, (:? TomlTable as t) -> Some t
        | true, v -> fail $"'{key}' must be a table, got {v.GetType().Name}"
        | _ -> None

    let private getString (tbl: TomlTable) (key: string) : string =
        match tbl.TryGetValue(key) with
        | true, (:? string as s) -> s
        | true, v -> fail $"'{key}' must be a string, got {v.GetType().Name}"
        | _ -> fail $"missing required string '{key}'"

    let private tryString (tbl: TomlTable) (key: string) : string option =
        match tbl.TryGetValue(key) with
        | true, (:? string as s) -> Some s
        | true, v -> fail $"'{key}' must be a string, got {v.GetType().Name}"
        | _ -> None

    let private tryBool (tbl: TomlTable) (key: string) : bool option =
        match tbl.TryGetValue(key) with
        | true, (:? bool as b) -> Some b
        | true, v -> fail $"'{key}' must be a bool, got {v.GetType().Name}"
        | _ -> None

    let private tryInt (tbl: TomlTable) (key: string) : int64 option =
        match tbl.TryGetValue(key) with
        | true, (:? int64 as i) -> Some i
        | true, v -> fail $"'{key}' must be an integer, got {v.GetType().Name}"
        | _ -> None

    let private tryArray (tbl: TomlTable) (key: string) : TomlArray option =
        match tbl.TryGetValue(key) with
        | true, (:? TomlArray as a) -> Some a
        | true, v -> fail $"'{key}' must be an inline array, got {v.GetType().Name}"
        | _ -> None

    /// `[[X]]` array-of-tables syntax — distinct from inline arrays.
    let private tryTableArray (tbl: TomlTable) (key: string) : TomlTableArray option =
        match tbl.TryGetValue(key) with
        | true, (:? TomlTableArray as a) -> Some a
        | true, v -> fail $"'{key}' must be a [[{key}]] array of tables, got {v.GetType().Name}"
        | _ -> None

    let private parseEngine (tbl: TomlTable) : Engine =
        let kind =
            match getString tbl "type" with
            | "unreal" -> EngineKind.Unreal
            | "unity" -> EngineKind.Unity
            | "godot" -> EngineKind.Godot
            | other -> fail $"unknown engine.type '{other}', expected unreal | unity | godot"
        { Kind = kind
          ProjectFile = tryString tbl "project_file"
          EnginePath = tryString tbl "engine_path"
          EngineVersion = tryString tbl "engine_version" }

    let private parseVcs (tbl: TomlTable) : Vcs =
        let kind =
            match getString tbl "type" with
            | "git" -> VcsKind.Git
            | other -> fail $"unknown vcs.type '{other}', expected git"
        { Kind = kind
          Lfs = tryBool tbl "lfs" |> Option.defaultValue false }

    let private parseHistory (tbl: TomlTable) : History =
        { KeepLastNRuns =
            tryInt tbl "keep_last_n_runs"
            |> Option.map int
            |> Option.defaultValue 50 }

    /// Parse a `project.toml` payload into a `Project`.
    let parseProject (text: string) : Project =
        let model = toModel text
        let projectTbl = getTable model "project"
        let engineTbl = getTable model "engine"
        { Name = getString projectTbl "name"
          WorkingDir = getString projectTbl "working_dir"
          Engine = parseEngine engineTbl
          Vcs = tryTable model "vcs" |> Option.map parseVcs
          History =
            tryTable model "history"
            |> Option.map parseHistory
            |> Option.defaultValue { KeepLastNRuns = 50 } }

    let private parseVarKind (varName: string) (tbl: TomlTable) : VarKind =
        match getString tbl "type" with
        | "string" -> VarKind.String
        | "int" -> VarKind.Int
        | "float" -> VarKind.Float
        | "bool" -> VarKind.Bool
        | "path" -> VarKind.Path
        | "file" -> VarKind.File
        | "dir" -> VarKind.Dir
        | "secret" -> VarKind.Secret
        | "multiline" -> VarKind.Multiline
        | "enum" ->
            match tryArray tbl "values" with
            | Some arr ->
                let values =
                    arr
                    |> Seq.map (fun v ->
                        match v with
                        | :? string as s -> s
                        | other -> fail $"var '{varName}': enum 'values' must be strings, got {other.GetType().Name}")
                    |> List.ofSeq
                if List.isEmpty values then
                    fail $"var '{varName}': enum 'values' must not be empty"
                VarKind.Enum values
            | None -> fail $"var '{varName}': enum requires a 'values' array"
        | "list" ->
            // list<T> support deferred — no Phase 1 flow needs it yet.
            fail $"var '{varName}': 'list' kind is not yet supported"
        | other -> fail $"var '{varName}': unknown type '{other}'"

    let private parseFlowVar (name: string) (tbl: TomlTable) : FlowVar =
        let kind = parseVarKind name tbl
        let defaultVal =
            match tbl.TryGetValue("default") with
            | true, v -> Some (convert v)
            | _ -> None
        { Name = name; Kind = kind; Default = defaultVal }

    let private reservedStepKeys = Set.ofList [ "type"; "id"; "when" ]

    let private parseStep (tbl: TomlTable) : Step =
        let stepType = getString tbl "type"
        let id = tryString tbl "id"
        let whenExpr = tryString tbl "when"
        let parameters =
            tbl
            |> Seq.filter (fun kv -> not (Set.contains kv.Key reservedStepKeys))
            |> Seq.map (fun kv -> kv.Key, convert kv.Value)
            |> Map.ofSeq
        { Id = id; Type = stepType; When = whenExpr; Params = parameters }

    let private parseFlow (tbl: TomlTable) : Flow =
        let id = getString tbl "id"
        let name = tryString tbl "name"
        let vars =
            match tryTable tbl "vars" with
            | None -> []
            | Some vt ->
                vt
                |> Seq.map (fun kv ->
                    match kv.Value with
                    | :? TomlTable as v -> parseFlowVar kv.Key v
                    | other -> fail $"var '{kv.Key}' must be an inline table, got {other.GetType().Name}")
                |> List.ofSeq
        let steps =
            match tryTableArray tbl "steps" with
            | None -> []
            | Some arr -> arr |> Seq.map parseStep |> List.ofSeq
        { Id = id; Name = name; Vars = vars; Steps = steps }

    /// Parse a `flows.toml` payload into a list of flows. Flow order in the
    /// file is preserved.
    let parseFlows (text: string) : Flow list =
        let model = toModel text
        match tryTableArray model "flow" with
        | Some arr -> arr |> Seq.map parseFlow |> List.ofSeq
        | None -> []

    /// Read and parse a `project.toml` from disk.
    let loadProject (path: string) : Project =
        File.ReadAllText path |> parseProject

    /// Read and parse a `flows.toml` from disk.
    let loadFlows (path: string) : Flow list =
        File.ReadAllText path |> parseFlows
