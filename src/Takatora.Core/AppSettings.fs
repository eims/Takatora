namespace Takatora.Core

open System
open System.IO
open System.Text
open Tomlyn.Model

/// Machine-local application settings, stored at
/// `%APPDATA%\Takatora\settings.toml` — never committed (per-developer /
/// per-machine, like the project registry). Currently just the "Open in
/// IDE" command template; more app-level prefs can join over time.
type AppSettings = {
    /// Command run to open a project in the user's IDE / code editor.
    /// A full command line with placeholders ({project_dir} / {uproject} /
    /// {sln}); see `Engines.resolveIdeCommand`. None = unset (button off).
    IdeCommand: string option
    /// Extra directories to scan for Godot executables (Godot has no
    /// canonical install location). This is the only machine-level (global)
    /// Godot setting — the engine *designation* is project-local, living in
    /// each project's `[engine].engine_path`. Used by
    /// `Engines.godotCandidates` / `Engines.pickGodot`.
    GodotSearchPaths: string list
}

[<RequireQualifiedAccess>]
module AppSettings =

    // Test hook — point reads/writes at a temp file instead of %APPDATA%.
    let mutable private pathOverride : string option = None
    let setPathForTests (p: string) = pathOverride <- Some p
    let resetPath () = pathOverride <- None

    let settingsPath () : string =
        match pathOverride with
        | Some p -> p
        | None ->
            Path.Combine(
                Environment.GetFolderPath Environment.SpecialFolder.ApplicationData,
                "Takatora",
                "settings.toml")

    let empty : AppSettings = { IdeCommand = None; GodotSearchPaths = [] }

    /// Load settings from disk. Missing/malformed file → empty (defaults).
    let load () : AppSettings =
        let path = settingsPath ()
        if not (File.Exists path) then empty
        else
            try
                let table =
                    Tomlyn.TomlSerializer.Deserialize<TomlTable>(
                        File.ReadAllText path, Tomlyn.TomlSerializerOptions())
                let tryStr key =
                    match table.TryGetValue key with
                    | true, (:? string as s) when not (String.IsNullOrWhiteSpace s) -> Some s
                    | _ -> None
                let tryStrList key =
                    match table.TryGetValue key with
                    | true, (:? TomlArray as arr) ->
                        [ for v in arr do match v with :? string as s -> yield s | _ -> () ]
                    | _ -> []
                { IdeCommand = tryStr "ide_command"
                  GodotSearchPaths = tryStrList "godot_search_paths" }
            with _ -> empty

    let private esc (s: string) =
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

    /// Overwrite the settings file. Parent dir created on demand.
    let save (settings: AppSettings) : unit =
        let path = settingsPath ()
        let dir = Path.GetDirectoryName path
        if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore
        let sb = StringBuilder()
        sb.AppendLine("# Takatora app settings — machine-local, never committed.") |> ignore
        match settings.IdeCommand with
        | Some c when not (String.IsNullOrWhiteSpace c) ->
            sb.AppendLine(sprintf "ide_command = %s" (esc c)) |> ignore
        | _ -> ()
        if not (List.isEmpty settings.GodotSearchPaths) then
            let arr = settings.GodotSearchPaths |> List.map esc |> String.concat ", "
            sb.AppendLine(sprintf "godot_search_paths = [%s]" arr) |> ignore
        File.WriteAllText(path, sb.ToString())
