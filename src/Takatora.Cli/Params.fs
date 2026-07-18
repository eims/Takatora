module Takatora.Cli.Params

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json.Nodes
open Takatora.Core

/// Subcommands for project-shared params (`.takatora/params.toml`) and
/// the machine-local secret-access grants. Granting is deliberately a
/// dedicated, explicit command: `run` never asks interactively, it fails
/// with a pointer here (the CLI stays fully non-interactive).

type Format = Human | Json

let parseFormat (s: string) : Result<Format, string> =
    match s with
    | null | "" | "human" -> Ok Human
    | "json" -> Ok Json
    | other -> Error (sprintf "unknown format '%s', expected human | json" other)

let private kindString (k: VarKind) : string =
    let rec go k =
        match k with
        | VarKind.String -> "string" | VarKind.Int -> "int" | VarKind.Float -> "float"
        | VarKind.Bool -> "bool" | VarKind.Path -> "path" | VarKind.File -> "file"
        | VarKind.Dir -> "dir" | VarKind.Secret -> "secret" | VarKind.Multiline -> "multiline"
        | VarKind.Enum vs -> sprintf "enum(%s)" (String.concat "|" vs)
        | VarKind.List item -> sprintf "list(%s)" (go item)
    go k

let rec private renderValue (v: TomlValue) : string =
    match v with
    | TString s -> sprintf "\"%s\"" s
    | TBool b -> if b then "true" else "false"
    | TInt i -> string i
    | TFloat f -> f.ToString("R", CultureInfo.InvariantCulture)
    | TArray xs -> "[" + (xs |> List.map renderValue |> String.concat ", ") + "]"
    | TTable _ -> "{...}"

/// Load a project's declarations + flows, mapping TOML errors onto the
/// CLI's usual (message, exit 2) shape.
let private loadProjectContext (projectRoot: string)
    : Result<Project * ProjectParam list * Flow list, string> =
    try
        let project = TomlConfig.loadProject (Path.Combine(projectRoot, ".takatora", "project.toml"))
        let ps = Takatora.Core.Params.load projectRoot
        let flows = TomlConfig.loadFlows (Path.Combine(projectRoot, ".takatora", "flows.toml"))
        Ok (project, ps, flows)
    with
    | TomlConfigError msg -> Error msg
    | :? FileNotFoundException as ex -> Error ex.Message

/// Referenced *secret* param names of one flow.
let private flowSecretRefs (ps: ProjectParam list) (flow: Flow) : Set<string> =
    Set.intersect (Takatora.Core.Params.referencedIn flow) (Takatora.Core.Params.secretNames ps)

// ─── list ─────────────────────────────────────────────────────────

type private GrantState = Granted | Stale | NotGranted

let private grantStates (projectRoot: string) (ps: ProjectParam list) (flows: Flow list)
    : (string * (string * GrantState) list) list =
    // Per flow: the secret params it references, with their grant state.
    flows
    |> List.choose (fun flow ->
        let refs = flowSecretRefs ps flow
        if Set.isEmpty refs then None
        else
            let check = Grants.check projectRoot flow refs
            let state p =
                if List.contains p check.NotGranted then NotGranted
                elif List.contains p check.Stale then Stale
                else Granted
            Some (flow.Id, refs |> Set.toList |> List.map (fun p -> p, state p)))

let private listToHuman (project: Project) (projectRoot: string)
                        (ps: ProjectParam list) (flows: Flow list) : string =
    let sb = StringBuilder()
    if List.isEmpty ps then
        sb.AppendLine("(no params declared — add .takatora/params.toml)") |> ignore
    else
        sb.AppendLine(sprintf "Params of '%s' (.takatora/params.toml):" project.Name) |> ignore
        for p in ps do
            let value =
                match p.Kind, p.Value with
                | VarKind.Secret, _ ->
                    if Secrets.exists project.Name p.Name then "(secret, stored)"
                    else "(secret, NOT stored)"
                | _, Some v -> renderValue v
                | _, None -> ""
            let desc = p.Description |> Option.map (sprintf "  — %s") |> Option.defaultValue ""
            sb.AppendLine(sprintf "  %-20s %-14s %s%s" p.Name (kindString p.Kind) value desc) |> ignore
        let states = grantStates projectRoot ps flows
        if not (List.isEmpty states) then
            sb.AppendLine() |> ignore
            sb.AppendLine("Secret access (this machine):") |> ignore
            for flowId, entries in states do
                for param, state in entries do
                    let label =
                        match state with
                        | Granted -> "granted"
                        | Stale -> "stale (flow changed — re-grant)"
                        | NotGranted -> "not granted"
                    sb.AppendLine(sprintf "  %-20s %-20s %s" flowId param label) |> ignore
    sb.ToString()

let private listToJson (project: Project) (projectRoot: string)
                       (ps: ProjectParam list) (flows: Flow list) : string =
    let root = JsonObject()
    root.["project_name"] <- JsonValue.Create(project.Name)
    let arr = JsonArray()
    for p in ps do
        let item = JsonObject()
        item.["name"] <- JsonValue.Create(p.Name)
        item.["type"] <- JsonValue.Create(kindString p.Kind)
        match p.Kind with
        | VarKind.Secret ->
            item.["secret"] <- JsonValue.Create(true)
            item.["stored"] <- JsonValue.Create(Secrets.exists project.Name p.Name)
        | _ ->
            item.["secret"] <- JsonValue.Create(false)
            match p.Value with
            | Some v -> item.["value"] <- Run.tomlValueToJson v
            | None -> ()
        p.Description |> Option.iter (fun d -> item.["description"] <- JsonValue.Create(d))
        arr.Add(item)
    root.["params"] <- arr
    let grantsArr = JsonArray()
    for flowId, entries in grantStates projectRoot ps flows do
        for param, state in entries do
            let item = JsonObject()
            item.["flow_id"] <- JsonValue.Create(flowId)
            item.["param"] <- JsonValue.Create(param)
            item.["state"] <-
                JsonValue.Create(
                    match state with
                    | Granted -> "granted"
                    | Stale -> "stale"
                    | NotGranted -> "not_granted")
            grantsArr.Add(item)
    root.["secret_access"] <- grantsArr
    root.ToJsonString(System.Text.Json.JsonSerializerOptions(WriteIndented = true))

let invokeList (projectArg: string) (format: Format) : int =
    match Run.resolveProject projectArg with
    | None ->
        Console.Error.WriteLine(
            sprintf "params: '%s' is not a registered name and does not contain a .takatora/ directory" projectArg)
        3
    | Some projectRoot ->
        match loadProjectContext projectRoot with
        | Error msg ->
            Console.Error.WriteLine(sprintf "params: %s" msg)
            2
        | Ok (project, ps, flows) ->
            let text =
                match format with
                | Human -> listToHuman project projectRoot ps flows
                | Json  -> listToJson  project projectRoot ps flows + Environment.NewLine
            Console.Out.Write(text)
            0

// ─── grant ────────────────────────────────────────────────────────

let invokeGrant (projectArg: string) (flowId: string) : int =
    match Run.resolveProject projectArg with
    | None ->
        Console.Error.WriteLine(
            sprintf "params: '%s' is not a registered name and does not contain a .takatora/ directory" projectArg)
        3
    | Some projectRoot ->
        match loadProjectContext projectRoot with
        | Error msg ->
            Console.Error.WriteLine(sprintf "params: %s" msg)
            2
        | Ok (project, ps, flows) ->
            match flows |> List.tryFind (fun f -> f.Id = flowId) with
            | None ->
                Console.Error.WriteLine(sprintf "params: flow '%s' not found in flows.toml" flowId)
                3
            | Some flow ->
                let refs = flowSecretRefs ps flow
                if Set.isEmpty refs then
                    Console.Out.WriteLine(sprintf "Flow '%s' references no secret params — nothing to grant." flowId)
                    0
                else
                    // Running this command IS the consent (the CLI never
                    // prompts): record a grant per referenced secret,
                    // pinned to the flow definition as it exists now.
                    let hash = FlowHash.compute flow
                    let now = DateTimeOffset.UtcNow
                    Console.Out.WriteLine(
                        sprintf "Granting flow '%s' access to secret param(s) on this machine:" flowId)
                    for name in refs do
                        let desc =
                            ps
                            |> List.tryFind (fun p -> p.Name = name)
                            |> Option.bind (fun p -> p.Description)
                            |> Option.map (sprintf " — %s")
                            |> Option.defaultValue ""
                        Grants.record
                            { ProjectPath = projectRoot
                              ProjectName = project.Name
                              FlowId = flowId
                              Param = name
                              FlowHash = hash
                              GrantedAt = now
                              Source = "flows" }
                        Console.Out.WriteLine(sprintf "  - %s%s" name desc)
                    Console.Out.WriteLine("The grant is re-checked if the flow definition changes.")
                    0

// ─── revoke ───────────────────────────────────────────────────────

let invokeRevoke (projectArg: string) (flowId: string option) (param: string option) : int =
    match Run.resolveProject projectArg with
    | None ->
        Console.Error.WriteLine(
            sprintf "params: '%s' is not a registered name and does not contain a .takatora/ directory" projectArg)
        3
    | Some projectRoot ->
        let removed = Grants.revoke projectRoot flowId param
        Console.Out.WriteLine(sprintf "Revoked %d grant(s)." removed)
        0

// ─── set (store a secret value) ───────────────────────────────────

/// Store a secret param's value in the OS credential store. The value is
/// taken from an env var or stdin — never from argv, which would land in
/// shell history.
let invokeSet (projectArg: string) (name: string) (fromEnv: string option) (fromStdin: bool) : int =
    match Run.resolveProject projectArg with
    | None ->
        Console.Error.WriteLine(
            sprintf "params: '%s' is not a registered name and does not contain a .takatora/ directory" projectArg)
        3
    | Some projectRoot ->
        match loadProjectContext projectRoot with
        | Error msg ->
            Console.Error.WriteLine(sprintf "params: %s" msg)
            2
        | Ok (project, ps, _) ->
            match ps |> List.tryFind (fun p -> p.Name = name) with
            | None ->
                Console.Error.WriteLine(
                    sprintf "params: '%s' is not declared in .takatora/params.toml" name)
                2
            | Some p when p.Kind <> VarKind.Secret ->
                Console.Error.WriteLine(
                    sprintf "params: '%s' is not a secret — set its value directly in .takatora/params.toml" name)
                2
            | Some _ ->
                let value =
                    match fromEnv, fromStdin with
                    | Some env, false ->
                        match Environment.GetEnvironmentVariable(env) with
                        | null -> Error (sprintf "environment variable '%s' is not set" env)
                        | v -> Ok v
                    | None, true ->
                        let raw = Console.In.ReadToEnd()
                        Ok (raw.TrimEnd('\r', '\n'))
                    | _ ->
                        Error "specify exactly one of --from-env VAR or --stdin"
                match value with
                | Error msg ->
                    Console.Error.WriteLine(sprintf "params: %s" msg)
                    2
                | Ok v ->
                    Secrets.write project.Name name v
                    Console.Out.WriteLine(
                        sprintf "Stored secret '%s' for project '%s' in the credential manager." name project.Name)
                    0
