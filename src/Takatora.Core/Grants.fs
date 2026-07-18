namespace Takatora.Core

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open Tomlyn.Model

/// Canonical hashing of a flow definition. A secret-access grant is tied
/// to the flow as it looked when the user approved it; the hash is over
/// the *parsed* model rendered deterministically, so comment, whitespace,
/// and key-order edits don't invalidate grants — only semantic changes do.
[<RequireQualifiedAccess>]
module FlowHash =

    // The rendering must be injective: every distinct model renders to a
    // distinct string. Strings are length-prefixed (`s5:hello`) so no
    // escaping is needed; composites use tagged parentheses.

    let private str (s: string) = sprintf "s%d:%s" (String.length s) s

    let private opt (v: string option) =
        match v with
        | Some s -> "some(" + str s + ")"
        | None -> "none"

    let rec private value (v: TomlValue) : string =
        match v with
        | TString s -> str s
        | TInt i -> sprintf "i:%d" i
        | TFloat f -> sprintf "f:%s" (f.ToString("R", CultureInfo.InvariantCulture))
        | TBool b -> if b then "b:true" else "b:false"
        | TArray xs -> "a(" + (xs |> List.map value |> String.concat ",") + ")"
        | TTable m ->
            // F# Map enumerates in key order, so this is deterministic.
            "t(" + (m |> Seq.map (fun kv -> str kv.Key + "=" + value kv.Value) |> String.concat ",") + ")"

    let rec private kind (k: VarKind) : string =
        match k with
        | VarKind.String -> "string"
        | VarKind.Int -> "int"
        | VarKind.Float -> "float"
        | VarKind.Bool -> "bool"
        | VarKind.Path -> "path"
        | VarKind.File -> "file"
        | VarKind.Dir -> "dir"
        | VarKind.Secret -> "secret"
        | VarKind.Multiline -> "multiline"
        | VarKind.Enum values -> "enum(" + (values |> List.map str |> String.concat ",") + ")"
        | VarKind.List item -> "list(" + kind item + ")"

    let private flowVar (v: FlowVar) : string =
        "var(" + str v.Name
        + ",kind=" + kind v.Kind
        + ",default=" + (match v.Default with Some d -> "some(" + value d + ")" | None -> "none")
        + ",desc=" + opt v.Description + ")"

    let private step (s: Step) : string =
        "step(id=" + opt s.Id
        + ",type=" + str s.Type
        + ",when=" + opt s.When
        + ",params=t(" + (s.Params |> Seq.map (fun kv -> str kv.Key + "=" + value kv.Value) |> String.concat ",") + "))"

    /// Deterministic canonical form of the whole flow definition. Vars are
    /// sorted by name (declaration order of a table is not meaningful);
    /// steps keep their order (it is execution order).
    let canonicalString (flow: Flow) : string =
        "flow(id=" + str flow.Id
        + ",name=" + opt flow.Name
        + ",vars(" + (flow.Vars |> List.sortBy (fun v -> v.Name) |> List.map flowVar |> String.concat ",") + ")"
        + ",steps(" + (flow.Steps |> List.map step |> String.concat ",") + "))"

    /// `sha256:<lowercase hex>` over the UTF-8 canonical string.
    let compute (flow: Flow) : string =
        let bytes = Encoding.UTF8.GetBytes(canonicalString flow)
        let hash = SHA256.HashData(bytes)
        "sha256:" + Convert.ToHexString(hash).ToLowerInvariant()

/// One machine-local approval: "on this machine, flow <FlowId> of the
/// project at <ProjectPath> may read secret param <Param>". Tied to the
/// flow-definition hash current at grant time.
type SecretGrant = {
    ProjectPath: string
    ProjectName: string
    FlowId: string
    Param: string
    FlowHash: string
    GrantedAt: DateTimeOffset
    /// What recorded the grant. "flows" = user approved a flows.toml
    /// flow (CLI `params grant` / GUI dialog). Reserved for future
    /// sources such as trusted extension packs ("pack:<id>").
    Source: string
}

[<RequireQualifiedAccess>]
module Grants =

    /// Result of checking one flow's referenced secret params against the
    /// recorded grants. `Stale` params had a grant, but the flow
    /// definition changed since it was recorded.
    type CheckResult = {
        Required: string list
        NotGranted: string list
        Stale: string list
    }

    // Test hook, mirroring ProjectRegistry: tests redirect the store to a
    // tmp file so they never touch the real %APPDATA%\Takatora.
    let mutable private pathOverride : string option = None
    let internal setPathForTests (path: string) = pathOverride <- Some path
    let internal clearPathOverride () = pathOverride <- None

    /// Machine-local store — never committed. Lives beside projects.toml
    /// so `TAKATORA_DATA_DIR` isolation covers it too.
    let grantsPath () : string =
        match pathOverride with
        | Some p -> p
        | None -> Path.Combine(AppData.baseDir (), "grants.toml")

    /// Normalize a project root for use as the grant key: absolute, no
    /// trailing separator. Comparison is case-insensitive (Windows paths).
    let normalizePath (path: string) : string =
        Path.GetFullPath(path).TrimEnd('\\', '/')

    let private samePath (a: string) (b: string) =
        String.Equals(normalizePath a, normalizePath b, StringComparison.OrdinalIgnoreCase)

    let private parseGrant (tbl: TomlTable) : SecretGrant option =
        let tryStr key =
            match tbl.TryGetValue(key: string) with
            | true, (:? string as s) -> Some s
            | _ -> None
        match tryStr "project_path", tryStr "flow_id", tryStr "param", tryStr "flow_hash" with
        | Some path, Some flowId, Some param, Some hash ->
            let grantedAt =
                match tryStr "granted_at" with
                | Some s ->
                    match DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                                                  DateTimeStyles.RoundtripKind) with
                    | true, dt -> dt
                    | _ -> DateTimeOffset.UtcNow
                | None -> DateTimeOffset.UtcNow
            Some { ProjectPath = path
                   ProjectName = tryStr "project_name" |> Option.defaultValue ""
                   FlowId = flowId
                   Param = param
                   FlowHash = hash
                   GrantedAt = grantedAt
                   Source = tryStr "source" |> Option.defaultValue "flows" }
        | _ -> None

    /// Load all grants. Missing or unreadable file → no grants (the safe
    /// direction: access gets re-asked, never silently widened).
    let load () : SecretGrant list =
        let path = grantsPath ()
        if not (File.Exists path) then []
        else
            try
                let table =
                    Tomlyn.TomlSerializer.Deserialize<TomlTable>(
                        File.ReadAllText path, Tomlyn.TomlSerializerOptions())
                match table.TryGetValue("grants") with
                | true, (:? TomlTableArray as arr) ->
                    arr |> Seq.choose parseGrant |> List.ofSeq
                | _ -> []
            with _ -> []

    let private writeTomlString (s: string) =
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

    /// Overwrite the store with the given grants.
    let save (grants: SecretGrant list) : unit =
        let path = grantsPath ()
        let dir = Path.GetDirectoryName(path)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore
        let sb = StringBuilder()
        sb.AppendLine("# Takatora secret-access grants — machine-local, never committed.") |> ignore
        sb.AppendLine("# Edit via `takatora params grant / revoke` instead of by hand.") |> ignore
        sb.AppendLine("schema_version = 1") |> ignore
        sb.AppendLine() |> ignore
        for g in grants do
            sb.AppendLine("[[grants]]") |> ignore
            sb.AppendFormat("project_path = {0}\n", writeTomlString g.ProjectPath) |> ignore
            sb.AppendFormat("project_name = {0}\n", writeTomlString g.ProjectName) |> ignore
            sb.AppendFormat("flow_id = {0}\n",      writeTomlString g.FlowId)      |> ignore
            sb.AppendFormat("param = {0}\n",        writeTomlString g.Param)       |> ignore
            sb.AppendFormat("flow_hash = {0}\n",    writeTomlString g.FlowHash)    |> ignore
            sb.AppendFormat("granted_at = {0}\n",
                            writeTomlString (g.GrantedAt.ToString("o", CultureInfo.InvariantCulture))) |> ignore
            sb.AppendFormat("source = {0}\n",       writeTomlString g.Source)      |> ignore
            sb.AppendLine() |> ignore
        File.WriteAllText(path, sb.ToString())

    /// Record (or refresh) one grant. An existing grant for the same
    /// (project, flow, param) is replaced — this is how a stale grant
    /// gets re-approved after a flow edit.
    let record (grant: SecretGrant) : unit =
        let grant = { grant with ProjectPath = normalizePath grant.ProjectPath }
        let others =
            load ()
            |> List.filter (fun g ->
                not (samePath g.ProjectPath grant.ProjectPath
                     && g.FlowId = grant.FlowId
                     && g.Param = grant.Param))
        save (others @ [ grant ])

    /// Remove grants matching the filters; None = match all. Returns how
    /// many were removed.
    let revoke (projectPath: string) (flowId: string option) (param: string option) : int =
        let all = load ()
        let matches (g: SecretGrant) =
            samePath g.ProjectPath projectPath
            && (match flowId with Some f -> g.FlowId = f | None -> true)
            && (match param with Some p -> g.Param = p | None -> true)
        let keep = all |> List.filter (matches >> not)
        let removed = List.length all - List.length keep
        if removed > 0 then save keep
        removed

    /// All grants recorded for one project.
    let listFor (projectPath: string) : SecretGrant list =
        load () |> List.filter (fun g -> samePath g.ProjectPath projectPath)

    /// Check a flow's referenced secret params against the store. The
    /// one-stop verdict used by the runner, the CLI, and the GUI dialog.
    let check (projectPath: string) (flow: Flow) (secretParams: Set<string>) : CheckResult =
        let required = Set.toList secretParams
        let currentHash = FlowHash.compute flow
        let forProject = listFor projectPath
        let notGranted, stale =
            required
            |> List.fold (fun (ng, st) param ->
                match forProject |> List.tryFind (fun g -> g.FlowId = flow.Id && g.Param = param) with
                | None -> (param :: ng, st)
                | Some g when g.FlowHash <> currentHash -> (ng, param :: st)
                | Some _ -> (ng, st)) ([], [])
        { Required = required
          NotGranted = List.rev notGranted
          Stale = List.rev stale }
