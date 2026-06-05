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
    /// The engine-association id this install answers to when it differs
    /// from `Version` — i.e. the GUID of a source build, as referenced by a
    /// `.uproject`'s EngineAssociation. `None` for launcher/installed engines
    /// (whose association *is* their version, e.g. "5.7").
    Association: string option
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
                                        Association = None
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
                                        Association = None
                                    }
                                | _ -> None
                        with _ -> None)
                    |> List.ofArray
            with _ -> []

    /// Read a friendly "Major.Minor.Patch" from an engine root's
    /// Engine/Build/Build.version (a source build's only reliable version
    /// marker, since its registry key is a GUID, not a version).
    let private readUnrealBuildVersion (engineRoot: string) : string option =
        try
            let p = Path.Combine(engineRoot, "Engine", "Build", "Build.version")
            if not (File.Exists p) then None
            else
                match JsonNode.Parse(File.ReadAllText p) with
                | :? JsonObject as o ->
                    let getInt (key: string) =
                        o.[key] |> Option.ofObj |> Option.map (fun v -> v.GetValue<int>())
                    match getInt "MajorVersion", getInt "MinorVersion" with
                    | Some maj, Some min ->
                        let patch = getInt "PatchVersion" |> Option.defaultValue 0
                        Some (sprintf "%d.%d.%d" maj min patch)
                    | _ -> None
                | _ -> None
        with _ -> None

    // Source builds register themselves under HKCU, keyed by the GUID that a
    // `.uproject` puts in EngineAssociation; the value *is* the engine root.
    // (Note the space in "Epic Games" here vs. "EpicGames" for HKLM installs.)
    let private detectUnrealFromSourceBuilds () : DetectedEngine list =
        if not isWindows then []
        else
            try
                use key =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"SOFTWARE\Epic Games\Unreal Engine\Builds")
                if isNull key then []
                else
                    key.GetValueNames()
                    |> Array.choose (fun assoc ->
                        try
                            match key.GetValue(assoc) with
                            | :? string as raw ->
                                let path = raw.Trim()
                                if String.IsNullOrWhiteSpace path || not (Directory.Exists path) then None
                                else
                                    let exe =
                                        let p = Path.Combine(path, "Engine", "Binaries", "Win64", "UnrealEditor.exe")
                                        if File.Exists p then Some p else None
                                    let version =
                                        match readUnrealBuildVersion path with
                                        | Some v -> sprintf "%s (source)" v
                                        | None -> assoc
                                    Some {
                                        Kind = EngineKind.Unreal
                                        Version = version
                                        Path = path
                                        Executable = exe
                                        Association = Some assoc
                                    }
                            | _ -> None
                        with _ -> None)
                    |> List.ofArray
            with _ -> []

    let private detectUnreal () : DetectedEngine list =
        // De-dupe by install path; launcher entries come first, then HKLM
        // installs, then HKCU source builds.
        let combined =
            detectUnrealFromLauncher ()
            @ detectUnrealFromRegistry ()
            @ detectUnrealFromSourceBuilds ()
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
                        Association = None
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
                    Association = None
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
    /// May also be a GUID (a source build); `pick` resolves that against
    /// source builds registered under HKCU\…\Unreal Engine\Builds.
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
    /// version hint, match the source-build association GUID exactly, OR the
    /// version exactly, OR by `<hint>.` prefix (so a `.uproject`
    /// EngineAssociation "5.7" resolves to a detected "5.7.4-…"); otherwise
    /// pick the first detected install.
    let pickFrom (candidates: DetectedEngine list) (versionHint: string option) : DetectedEngine option =
        match versionHint with
        | Some v ->
            candidates
            |> List.tryFind (fun e ->
                e.Association = Some v
                || e.Version = v
                || e.Version.StartsWith(v + "."))
            |> Option.orElseWith (fun () -> List.tryHead candidates)
        | None -> List.tryHead candidates

    let pick (kind: EngineKind) (versionHint: string option) : DetectedEngine option =
        pickFrom (detect kind) versionHint

    // ─── Open project in its engine editor ─────────────────────────
    //
    // A resolved launch: the executable to start, its arguments, whether to
    // delegate to the OS shell (UE leans on the .uproject file association),
    // and a human label of what will open (for the button / tooltip).

    /// A resolved "open in editor" launch.
    type EditorLaunch = {
        Exe: string
        Args: string list
        UseShell: bool
        Describe: string
    }

    /// The Unity version a project targets, from
    /// `ProjectSettings/ProjectVersion.txt` (`m_EditorVersion: 2022.3.10f1`).
    let private unityProjectVersion (projectRoot: string) : string option =
        try
            let p = Path.Combine(projectRoot, "ProjectSettings", "ProjectVersion.txt")
            if not (File.Exists p) then None
            else
                File.ReadAllLines p
                |> Array.tryPick (fun line ->
                    let t = line.Trim()
                    if t.StartsWith("m_EditorVersion:") then
                        let v = t.Substring("m_EditorVersion:".Length).Trim()
                        if String.IsNullOrWhiteSpace v then None else Some v
                    else None)
        with _ -> None

    /// Resolve how to open a project in its engine's editor. Per engine:
    ///   Unreal — open the `.uproject` through the OS shell (the caller routes
    ///            this via Explorer so it is dispatched exactly like a
    ///            double-click), honouring whatever the user associated with
    ///            `.uproject` (UnrealVersionSelector → the engine, or Rider for
    ///            Unreal, etc.); zero config. `UseShell = true` flags this.
    ///   Unity  — read the project's target version and launch that detected
    ///            install with `-projectPath`; a wrong version must not be
    ///            substituted, so a missing version is an error (install via
    ///            Hub), not a fallback.
    ///   Godot  — Godot has no canonical location, so prefer the configured
    ///            `engine_path` (the godot exe); fall back to PATH detection;
    ///            launch `--path <dir> --editor`.
    let resolveEditorLaunch (engine: Engine) (projectRoot: string) : Result<EditorLaunch, string> =
        let abs (p: string) =
            if Path.IsPathRooted p then p else Path.Combine(projectRoot, p)
        match engine.Kind with
        | EngineKind.Unreal ->
            match engine.ProjectFile with
            | None -> Error "engine.project_file is not set (the .uproject path)"
            | Some pf ->
                let uproject = abs pf
                if File.Exists uproject then
                    Ok { Exe = uproject; Args = []; UseShell = true
                         Describe = sprintf "Unreal editor — %s" (Path.GetFileName uproject) }
                else Error (sprintf ".uproject not found: %s" uproject)
        | EngineKind.Unity ->
            match unityProjectVersion projectRoot with
            | None -> Error "couldn't read the Unity version (ProjectSettings/ProjectVersion.txt)"
            | Some ver ->
                match detect EngineKind.Unity |> List.tryFind (fun e -> e.Version = ver) with
                | Some u ->
                    match u.Executable with
                    | Some exe ->
                        Ok { Exe = exe; Args = [ "-projectPath"; projectRoot ]; UseShell = false
                             Describe = sprintf "Unity %s" ver }
                    | None -> Error (sprintf "Unity %s has no editor executable" ver)
                | None ->
                    Error (sprintf "Unity %s is not installed — add it via Unity Hub" ver)
        | EngineKind.Godot ->
            let fromConfig =
                engine.EnginePath
                |> Option.filter (fun p -> not (String.IsNullOrWhiteSpace p))
                |> Option.map abs
                |> Option.filter File.Exists
            let exe =
                match fromConfig with
                | Some e -> Some e
                | None -> detect EngineKind.Godot |> List.tryHead |> Option.bind (fun d -> d.Executable)
            match exe with
            | Some e ->
                Ok { Exe = e; Args = [ "--path"; projectRoot; "--editor" ]; UseShell = false
                     Describe = "Godot editor" }
            | None ->
                Error "Godot executable not found — set engine.engine_path, or put godot on PATH"

    /// Resolve the actual engine install a project will run on — the
    /// detected version + path behind the (often auto-detected, hence
    /// invisible) `[engine]` config. Unlike `pick`, never falls back to an
    /// arbitrary install: a hint that matches nothing is an Error, so the
    /// GUI can show "declared X, but no matching install" honestly.
    let resolveProjectEngine (engine: Engine) (projectRoot: string) : Result<DetectedEngine, string> =
        let abs (p: string) =
            if Path.IsPathRooted p then p else Path.Combine(projectRoot, p)
        let matchHint (candidates: DetectedEngine list) (hint: string) =
            candidates
            |> List.tryFind (fun e ->
                e.Association = Some hint || e.Version = hint || e.Version.StartsWith(hint + "."))
        match engine.Kind with
        | EngineKind.Unreal ->
            let hint =
                match engine.EngineVersion with
                | Some v when not (String.IsNullOrWhiteSpace v) -> Some v
                | _ ->
                    match engine.ProjectFile with
                    | Some pf -> engineAssociation (abs pf)
                    | None -> None
            match hint with
            | None -> Error "no engine_version and no .uproject EngineAssociation to resolve from"
            | Some h ->
                match matchHint (detect EngineKind.Unreal) h with
                | Some d -> Ok d
                | None -> Error (sprintf "no installed Unreal Engine matches '%s'" h)
        | EngineKind.Unity ->
            match unityProjectVersion projectRoot with
            | None -> Error "couldn't read the Unity version (ProjectSettings/ProjectVersion.txt)"
            | Some ver ->
                match detect EngineKind.Unity |> List.tryFind (fun e -> e.Version = ver) with
                | Some d -> Ok d
                | None -> Error (sprintf "Unity %s is not installed" ver)
        | EngineKind.Godot ->
            let fromConfig =
                engine.EnginePath
                |> Option.filter (fun p -> not (String.IsNullOrWhiteSpace p))
                |> Option.map abs
                |> Option.filter File.Exists
            match fromConfig with
            | Some exe ->
                Ok { Kind = EngineKind.Godot; Version = "(configured)"
                     Path = Path.GetDirectoryName exe; Executable = Some exe; Association = None }
            | None ->
                match detect EngineKind.Godot with
                | d :: _ -> Ok d
                | []     -> Error "no Godot executable found — set engine.engine_path, or put godot on PATH"

    /// Resolve a user's "Open in IDE" command template into a runnable
    /// command line (the caller runs it via the shell). Placeholders:
    ///   {project_dir} — the project working dir (always available)
    ///   {uproject}    — the .uproject path (Unreal projects only)
    ///   {sln}         — first *.sln directly under the project dir
    ///   {target}      — the natural target for this engine: UE→{uproject},
    ///                   Unity→{sln}, Godot→{project_dir} (so a single Rider
    ///                   preset opens any project type the way Rider expects)
    /// A template referencing a placeholder we can't fill is an Error with a
    /// helpful reason (e.g. {sln} before project files have been generated),
    /// so the GUI can disable the button and say why.
    let resolveIdeCommand (engine: Engine) (projectRoot: string) (template: string) : Result<string, string> =
        if String.IsNullOrWhiteSpace template then Error "no IDE command configured"
        else
            let abs (p: string) = if Path.IsPathRooted p then p else Path.Combine(projectRoot, p)
            let uproject =
                match engine.Kind, engine.ProjectFile with
                | EngineKind.Unreal, Some pf -> Some (abs pf)
                | _ -> None
            let sln =
                try
                    if Directory.Exists projectRoot then
                        Directory.GetFiles(projectRoot, "*.sln") |> Array.sortBy id |> Array.tryHead
                    else None
                with _ -> None
            let target, targetMiss =
                match engine.Kind with
                | EngineKind.Unreal -> uproject, "the .uproject (engine.project_file)"
                | EngineKind.Unity  -> sln,      "a .sln (generate project files first)"
                | EngineKind.Godot  -> Some projectRoot, ""
            let needs (token: string) = template.Contains(token)
            if needs "{uproject}" && Option.isNone uproject then
                Error "{uproject} unavailable — this isn't an Unreal project (or engine.project_file is unset)"
            elif needs "{sln}" && Option.isNone sln then
                Error "{sln} unavailable — no .sln under the project dir (generate project files first)"
            elif needs "{target}" && Option.isNone target then
                Error (sprintf "{target} unavailable — needs %s" targetMiss)
            else
                template
                    .Replace("{project_dir}", projectRoot)
                    .Replace("{uproject}", defaultArg uproject "")
                    .Replace("{sln}", defaultArg sln "")
                    .Replace("{target}", defaultArg target "")
                |> Ok
