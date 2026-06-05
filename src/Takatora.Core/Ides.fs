namespace Takatora.Core

open System
open System.IO
open System.Diagnostics
open System.Runtime.InteropServices
open System.Text.Json.Nodes

/// An installed IDE / code editor we can offer as a one-click "Open in IDE"
/// command, so the user doesn't have to hand-write versioned exe paths
/// (Rider's path changes per version, VS per release, etc.).
type IdeCandidate = {
    /// Friendly label, e.g. "Visual Studio Community 2026" / "JetBrains Rider 2025.2.1".
    Name: string
    /// Full path to the launcher executable.
    Exe: string
    /// Suggested full command template (quoted exe + the placeholder that
    /// suits this IDE): VS → {sln}, VS Code → {project_dir}, Rider → {target}
    /// (which resolves per engine — UE .uproject / Unity .sln / Godot dir).
    Command: string
}

[<RequireQualifiedAccess>]
module Ides =

    let private isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)

    let private folder (f: Environment.SpecialFolder) = Environment.GetFolderPath f

    // Quote a path for embedding in a command line.
    let private q (s: string) = "\"" + s + "\""

    // ─── Visual Studio (via vswhere — the canonical enumerator) ────
    let private detectVisualStudio () : IdeCandidate list =
        try
            let vswhere =
                Path.Combine(folder Environment.SpecialFolder.ProgramFilesX86,
                             "Microsoft Visual Studio", "Installer", "vswhere.exe")
            if not (File.Exists vswhere) then []
            else
                let psi = ProcessStartInfo(vswhere)
                [ "-all"; "-prerelease"; "-format"; "json" ] |> List.iter psi.ArgumentList.Add
                psi.UseShellExecute <- false
                psi.RedirectStandardOutput <- true
                psi.CreateNoWindow <- true
                use p = Process.Start psi
                let out = p.StandardOutput.ReadToEnd()
                p.WaitForExit()
                match JsonNode.Parse out with
                | :? JsonArray as arr ->
                    arr
                    |> Seq.choose (fun n ->
                        match n with
                        | :? JsonObject as o ->
                            let str (key: string) =
                                o.[key] |> Option.ofObj |> Option.map (fun v -> v.GetValue<string>())
                            match str "displayName", str "productPath" with
                            | Some name, Some path when File.Exists path ->
                                Some { Name = name; Exe = path
                                       Command = sprintf "%s %s" (q path) (q "{sln}") }
                            | _ -> None
                        | _ -> None)
                    |> List.ofSeq
                | _ -> []
        with _ -> []

    // ─── Rider (JetBrains install dirs + Toolbox) ──────────────────
    let private detectRider () : IdeCandidate list =
        let local = folder Environment.SpecialFolder.LocalApplicationData
        // JetBrains-only roots: recurse freely (small). Programs is shared, so
        // only descend *Rider* subdirs there.
        let jetBrainsRoots =
            [ Path.Combine(folder Environment.SpecialFolder.ProgramFiles, "JetBrains")
              Path.Combine(local, "JetBrains", "Toolbox", "apps") ]
        let fromJetBrains root =
            if Directory.Exists root then
                try Directory.GetFiles(root, "rider64.exe", SearchOption.AllDirectories) |> Array.toList
                with _ -> []
            else []
        let fromPrograms =
            let programs = Path.Combine(local, "Programs")
            if Directory.Exists programs then
                try
                    Directory.GetDirectories(programs, "*Rider*")
                    |> Array.collect (fun d ->
                        try Directory.GetFiles(d, "rider64.exe", SearchOption.AllDirectories) with _ -> [||])
                    |> Array.toList
                with _ -> []
            else []
        (jetBrainsRoots |> List.collect fromJetBrains) @ fromPrograms
        |> List.distinct
        |> List.map (fun exe ->
            // exe = ...\<RiderDir>\bin\rider64.exe — the dir two up names the version.
            let name =
                try
                    let binDir = Directory.GetParent exe
                    if isNull binDir || isNull binDir.Parent then "Rider"
                    else binDir.Parent.Name
                with _ -> "Rider"
            { Name = name; Exe = exe; Command = sprintf "%s %s" (q exe) (q "{target}") })

    // ─── VS Code (known install paths) ─────────────────────────────
    let private detectVSCode () : IdeCandidate list =
        [ Path.Combine(folder Environment.SpecialFolder.LocalApplicationData, "Programs", "Microsoft VS Code", "Code.exe")
          Path.Combine(folder Environment.SpecialFolder.ProgramFiles, "Microsoft VS Code", "Code.exe") ]
        |> List.filter File.Exists
        |> List.distinct
        |> List.map (fun exe ->
            { Name = "VS Code"; Exe = exe
              Command = sprintf "%s %s" (q exe) (q "{project_dir}") })

    /// Detect installed IDEs to offer as "Open in IDE" presets. Windows-only
    /// for now; empty elsewhere. Best-effort — each detector swallows its own
    /// failures so one missing tool doesn't break the rest.
    let detect () : IdeCandidate list =
        if not isWindows then []
        else
            (detectVisualStudio () @ detectRider () @ detectVSCode ())
            |> List.distinctBy (fun c -> c.Exe.ToLowerInvariant())
