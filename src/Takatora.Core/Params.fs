namespace Takatora.Core

open System.IO
open System.Text.RegularExpressions

/// Project-shared params (`.takatora/params.toml`) — loading plus static
/// analysis of which `${params.X}` names a flow references. The runtime
/// resolution itself lives in `Vars` (the `params` namespace).
[<RequireQualifiedAccess>]
module Params =

    /// `<projectRoot>/.takatora/params.toml`.
    let paramsPath (projectRoot: string) : string =
        Path.Combine(projectRoot, ".takatora", "params.toml")

    /// Load a project's shared params. A project without a params.toml is
    /// the common case and simply has none. Parse/schema errors raise
    /// `TomlConfigError` like the other config loaders.
    let load (projectRoot: string) : ProjectParam list =
        let path = paramsPath projectRoot
        if File.Exists path then TomlConfig.loadParams path else []

    // Matches `${params.<name>}` only — deliberately narrower than the
    // resolver's generic placeholder pattern so unrelated namespaces
    // never register as a params reference.
    let private paramsRefPattern =
        Regex(@"\$\{params\.([a-zA-Z_][a-zA-Z0-9_]*)\}", RegexOptions.Compiled)

    let private refsInString (s: string) : seq<string> =
        paramsRefPattern.Matches(s) |> Seq.map (fun m -> m.Groups.[1].Value)

    let rec private refsInValue (v: TomlValue) : seq<string> =
        match v with
        | TString s -> refsInString s
        | TArray xs -> xs |> Seq.collect refsInValue
        | TTable m -> m |> Seq.collect (fun kv -> refsInValue kv.Value)
        | TInt _ | TFloat _ | TBool _ -> Seq.empty

    /// Every `${params.X}` name a flow's definition references — across
    /// step params, `when` expressions, and flow-var defaults. This is the
    /// static view used for grant checks and validate; the resolver may
    /// touch a subset at runtime (e.g. steps skipped by `when`).
    let referencedIn (flow: Flow) : Set<string> =
        seq {
            for v in flow.Vars do
                match v.Default with
                | Some d -> yield! refsInValue d
                | None -> ()
            for s in flow.Steps do
                match s.When with
                | Some w -> yield! refsInString w
                | None -> ()
                for kv in s.Params do
                    yield! refsInValue kv.Value
        }
        |> Set.ofSeq

    /// Names of the secret-kind params.
    let secretNames (ps: ProjectParam list) : Set<string> =
        ps
        |> List.choose (fun p -> if p.Kind = VarKind.Secret then Some p.Name else None)
        |> Set.ofList

    /// Non-secret params as a resolution map (name → declared value).
    let nonSecretValues (ps: ProjectParam list) : Map<string, TomlValue> =
        ps
        |> List.choose (fun p ->
            match p.Kind, p.Value with
            | VarKind.Secret, _ -> None
            | _, Some v -> Some (p.Name, v)
            | _, None -> None)
        |> Map.ofList
