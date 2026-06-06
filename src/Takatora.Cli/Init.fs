module Takatora.Cli.Init

open System
open System.IO
open Takatora.Core

/// Parse the --engine option. Defaults to Unreal (matches the GUI wizard).
let parseEngine (s: string) : Result<EngineKind, string> =
    match (if isNull s then "" else s).Trim().ToLowerInvariant() with
    | "" | "unreal" | "ue" -> Ok EngineKind.Unreal
    | "unity"              -> Ok EngineKind.Unity
    | "godot"              -> Ok EngineKind.Godot
    | other -> Error (sprintf "unknown engine '%s', expected unreal | unity | godot" other)

/// Scaffold a new project's `.takatora/` tree (creating the directory if needed)
/// and register it for `takatora run` lookup. Existing project.toml /
/// flows.toml are left untouched.
let invoke (path: string) (nameHint: string option) (engine: EngineKind) : int =
    try
        let absDir = Path.GetFullPath path
        Directory.CreateDirectory absDir |> ignore
        let name =
            match nameHint with
            | Some n when not (String.IsNullOrWhiteSpace n) -> n.Trim()
            | _ -> Path.GetFileName(absDir.TrimEnd('\\', '/'))

        let outcome = Scaffold.writeCi absDir name engine
        Console.Out.WriteLine(sprintf "Scaffolded %s" outcome.CiDir)
        let note created file =
            Console.Out.WriteLine(
                sprintf "  %s .takatora/%s" (if created then "created" else "kept   ") file)
        note outcome.ProjectTomlCreated "project.toml"
        note outcome.FlowsTomlCreated   "flows.toml"

        match ProjectRegistry.add absDir (Some name) with
        | ProjectRegistry.Added e ->
            Console.Out.WriteLine(sprintf "Registered '%s'. Try: takatora run \"%s\" smoke" e.Name e.Name)
            0
        | ProjectRegistry.DuplicateName existing ->
            Console.Out.WriteLine(
                sprintf "Already registered as '%s' → %s (scaffold left in place)."
                    existing.Name existing.Path)
            0
        | ProjectRegistry.InvalidPath reason ->
            // Scaffolding succeeded; only the registry step failed.
            Console.Error.WriteLine(sprintf "init: scaffolded, but registration failed: %s" reason)
            3
    with ex ->
        Console.Error.WriteLine(sprintf "init: %s" ex.Message)
        1
