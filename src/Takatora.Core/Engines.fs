namespace Takatora.Core

open System
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open System.Text.Json.Nodes

/// Resolved engine installation. Detection populates `Path` (the engine
/// root directory) and `Executable` (the editor binary) so engine-family
/// tasks can invoke UAT.bat / Unity.exe / godot.exe without each one
/// re-running the discovery logic.
type DetectedEngine = {
    Kind: EngineKind
    Version: string
    Path: string
    Executable: string option
}

[<RequireQualifiedAccess>]
module Engines =

    let private isWindows =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)

    // ─── Unreal Engine ─────────────────────────────────────────────
    //
    // Two sources, in priority order:
    //   1. C:\ProgramData\Epic\UnrealEngineLauncher\LauncherInstalled.dat
    //      (Epic Games Launcher installs — the common case)
    //   2. HKLM\SOFTWARE\EpicGames\Unreal Engine\<version>
    //      (older / source-built — rare, but cheap to include)

    let private unrealLauncherDatPath =
        @"C:\ProgramData\Epic\UnrealEngineLauncher\LauncherInstalled.dat"

    let private detectUnrealFromLauncher () : DetectedEngine list =
        if not (File.Exists unrealLauncherDatPath) then []
        else
            try
                let text = File.ReadAllText unrealLauncherDatPath
                match JsonNode.Parse text with
                | :? JsonObject as root ->
                    match root.["InstallationList"] with
                    | :? JsonArray as arr ->
                        arr
                        |> Seq.choose (fun n ->
                            match n with
                            | :? JsonObject as o ->
                                let appName = o.["AppName"] |> Option.ofObj |> Option.map (fun v -> v.GetValue<string>())
                                let installLocation = o.["InstallLocation"] |> Option.ofObj |> Option.map (fun v -> v.GetValue<string>())
                                let appVersion = o.["AppVersion"] |> Option.ofObj |> Option.map (fun v -> v.GetValue<string>())
                                match appName, installLocation with
                                | Some name, Some loc when name.StartsWith("UE_") ->
                                    let version =
                                        match appVersion with
                                        | Some v -> v
                                        | None -> name.Substring(3)
                                    let exe =
                                        let p = Path.Combine(loc, "Engine", "Binaries", "Win64", "UnrealEditor.exe")
                                        if File.Exists p then Some p else None
                                    Some {
                                        Kind = EngineKind.Unreal
                                        Version = version
                                        Path = loc
                                        Executable = exe
                                    }
                                | _ -> None
                            | _ -> None)
                        |> List.ofSeq
                    | _ -> []
                | _ -> []
            with _ -> []

    let private detectUnrealFromRegistry () : DetectedEngine list =
        if not isWindows then []
        else
            try
                use root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EpicGames\Unreal Engine")
                if isNull root then []
                else
                    root.GetSubKeyNames()
                    |> Array.choose (fun version ->
                        try
                            use key = root.OpenSubKey(version)
                            if isNull key then None
                            else
                                match key.GetValue("InstalledDirectory") with
                                | :? string as path when Directory.Exists path ->
                                    let exe =
                                        let p = Path.Combine(path, "Engine", "Binaries", "Win64", "UnrealEditor.exe")
                                        if File.Exists p then Some p else None
                                    Some {
                                        Kind = EngineKind.Unreal
                                        Version = version
                                        Path = path
                                        Executable = exe
                                    }
                                | _ -> None
                        with _ -> None)
                    |> List.ofArray
            with _ -> []

    let private detectUnreal () : DetectedEngine list =
        // De-dupe by install path; launcher entries come first.
        let combined = detectUnrealFromLauncher () @ detectUnrealFromRegistry ()
        combined
        |> List.distinctBy (fun e -> e.Path.TrimEnd('\\', '/'))

    // ─── Unity ─────────────────────────────────────────────────────
    //
    // Unity Hub layout: %PROGRAMDATA%\Unity\Hub\Editor\<version>\Editor\Unity.exe
    // Hub also writes %APPDATA%\UnityHub\secondaryInstallPath.json
    // pointing at user-chosen install roots; check both.

    let private detectUnityUnderRoot (root: string) : DetectedEngine list =
        let editorRoot = Path.Combine(root, "Editor")
        if not (Directory.Exists editorRoot) then []
        else
            Directory.GetDirectories editorRoot
            |> Array.choose (fun dir ->
                let version = Path.GetFileName dir
                let exe = Path.Combine(dir, "Editor", "Unity.exe")
                if File.Exists exe then
                    Some {
                        Kind = EngineKind.Unity
                        Version = version
                        Path = dir
                        Executable = Some exe
                    }
                else None)
            |> List.ofArray

    let private detectUnity () : DetectedEngine list =
        let standardRoot =
            let programData = Environment.GetFolderPath Environment.SpecialFolder.CommonApplicationData
            Path.Combine(programData, "Unity", "Hub")
        let secondaryFromHub () : string list =
            try
                let configPath =
                    Path.Combine(
                        Environment.GetFolderPath Environment.SpecialFolder.ApplicationData,
                        "UnityHub", "secondaryInstallPath.json")
                if not (File.Exists configPath) then []
                else
                    // Hub writes either a JSON-quoted string ("D:/Unity")
                    // or a bare path. Trim quotes defensively.
                    let raw = (File.ReadAllText configPath).Trim()
                    let path =
                        if raw.StartsWith("\"") && raw.EndsWith("\"") then
                            raw.Substring(1, raw.Length - 2)
                        else raw
                    if String.IsNullOrWhiteSpace path then [] else [ path ]
            with _ -> []
        let roots = standardRoot :: secondaryFromHub ()
        roots
        |> List.collect detectUnityUnderRoot
        |> List.distinctBy (fun e -> e.Path.TrimEnd('\\', '/'))

    // ─── Godot ─────────────────────────────────────────────────────
    //
    // Godot has no canonical install path. Walk PATH for godot*.exe;
    // also probe %LOCALAPPDATA%\Godot\ which Godot itself uses for user
    // data (but where users sometimes drop binaries too).

    let private executableSuffix = if isWindows then ".exe" else ""

    let private parseGodotVersion (path: string) : string =
        // Godot binaries are typically named `Godot_v4.2.2-stable_win64.exe`
        // — try to extract the v-prefixed token. Fall back to filename.
        let name = Path.GetFileNameWithoutExtension path
        let m = System.Text.RegularExpressions.Regex.Match(name, @"v(\d+(?:\.\d+){1,3}[a-z0-9\-]*)")
        if m.Success then m.Groups.[1].Value else name

    let private detectGodotFromPath () : DetectedEngine list =
        let pathEnv = Environment.GetEnvironmentVariable("PATH")
        if String.IsNullOrEmpty pathEnv then []
        else
            let separator = if isWindows then ';' else ':'
            pathEnv.Split(separator)
            |> Array.collect (fun dir ->
                if String.IsNullOrWhiteSpace dir || not (Directory.Exists dir) then [||]
                else
                    try
                        Directory.GetFiles(dir, $"godot*{executableSuffix}")
                        |> Array.append (Directory.GetFiles(dir, $"Godot*{executableSuffix}"))
                    with _ -> [||])
            |> Array.distinct
            |> Array.map (fun exe ->
                {
                    Kind = EngineKind.Godot
                    Version = parseGodotVersion exe
                    Path = Path.GetDirectoryName exe
                    Executable = Some exe
                })
            |> List.ofArray

    let private detectGodot () : DetectedEngine list =
        detectGodotFromPath ()
        |> List.distinctBy (fun e -> e.Executable |> Option.defaultValue e.Path)

    // ─── Public surface ────────────────────────────────────────────

    /// Detect installations for one engine. Empty list = none found.
    let detect (kind: EngineKind) : DetectedEngine list =
        match kind with
        | EngineKind.Unreal -> detectUnreal ()
        | EngineKind.Unity  -> detectUnity ()
        | EngineKind.Godot  -> detectGodot ()

    /// Detect across all engine kinds.
    let detectAll () : Map<EngineKind, DetectedEngine list> =
        [ EngineKind.Unreal; EngineKind.Unity; EngineKind.Godot ]
        |> List.map (fun k -> k, detect k)
        |> Map.ofList

    /// Read a `.uproject`'s `EngineAssociation` (e.g. "5.7"). Used as the
    /// version hint so the matching installed engine is auto-selected —
    /// no need to hardcode engine_path/engine_version in project.toml.
    /// (A source-build GUID association won't match launcher installs;
    /// such setups still need an explicit engine_path.)
    let engineAssociation (uprojectPath: string) : string option =
        try
            if not (File.Exists uprojectPath) then None
            else
                match JsonNode.Parse(File.ReadAllText uprojectPath) with
                | null -> None
                | node ->
                    match node.["EngineAssociation"] with
                    | null -> None
                    | v ->
                        let s = v.GetValue<string>()
                        if String.IsNullOrWhiteSpace s then None else Some s
        with _ -> None

    /// Pick the best match for a `[engine]` block in project.toml. With a
    /// version hint, match exactly OR by `<hint>.` prefix (so a `.uproject`
    /// EngineAssociation "5.7" resolves to a detected "5.7.4-…"); otherwise
    /// pick the first detected install.
    let pick (kind: EngineKind) (versionHint: string option) : DetectedEngine option =
        let candidates = detect kind
        match versionHint with
        | Some v ->
            candidates
            |> List.tryFind (fun e -> e.Version = v || e.Version.StartsWith(v + "."))
            |> Option.orElseWith (fun () -> List.tryHead candidates)
        | None -> List.tryHead candidates
