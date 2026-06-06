namespace Takatora.Core

open System
open System.Diagnostics

/// Trigger primitives for "watch" mode (auto-run a flow on a change). Kept in
/// Core so every frontend — the GUI host today, a future `takatora watch` CLI
/// or daemon — drives the same logic. This slice covers the git-commit poll:
/// frontends sample `gitHead` on an interval and run when it changes.
[<RequireQualifiedAccess>]
module Watch =

    /// The repo's current HEAD commit SHA (via `git -C <root> rev-parse HEAD`),
    /// or None when it's not a git repo / git is unavailable. Cheap enough to
    /// poll. The git-commit-poll trigger fires a run when this value changes.
    let gitHead (projectRoot: string) : string option =
        try
            let psi = ProcessStartInfo("git")
            [ "-C"; projectRoot; "rev-parse"; "HEAD" ] |> List.iter psi.ArgumentList.Add
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            use p = Process.Start psi
            let out = p.StandardOutput.ReadToEnd()
            p.WaitForExit()
            if p.ExitCode = 0 then
                let s = out.Trim()
                if String.IsNullOrWhiteSpace s then None else Some s
            else None
        with _ -> None
