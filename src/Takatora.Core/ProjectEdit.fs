namespace Takatora.Core

open System.Text.RegularExpressions

/// Surgical, EOL-preserving edits to a hand-authored `.takatora/project.toml`.
/// Mirrors FlowsEdit's philosophy: touch only the target line, preserve every
/// comment and byte of formatting elsewhere. Used by the GUI to persist a
/// project-local engine designation (`[engine].engine_path`) without
/// rewriting the whole file through Tomlyn.
[<RequireQualifiedAccess>]
module ProjectEdit =

    /// Split into '\r'-free lines and report the file's newline (CRLF if any
    /// '\r\n' is present, else LF), so the edit rejoins with the original EOL.
    let private splitLines (text: string) : string[] * string =
        let eol = if text.Contains("\r\n") then "\r\n" else "\n"
        let lines =
            text.Split('\n')
            |> Array.map (fun l -> if l.EndsWith("\r") then l.Substring(0, l.Length - 1) else l)
        lines, eol

    let private escape (s: string) =
        s.Replace("\\", "\\\\").Replace("\"", "\\\"")

    /// The [start, end) line range of a top-level table `[name]` — `end` is
    /// the next top-level `[header]` (not `[[array]]`) or EOF. None if absent.
    let private tableRange (lines: string[]) (name: string) : (int * int) option =
        let isHeader i = Regex.IsMatch(lines.[i].Trim(), @"^\[[^\[]")
        [ 0 .. lines.Length - 1 ]
        |> List.tryFind (fun i -> lines.[i].Trim() = sprintf "[%s]" name)
        |> Option.map (fun s ->
            let next =
                [ s + 1 .. lines.Length - 1 ]
                |> List.tryFind isHeader
                |> Option.defaultValue lines.Length
            s, next)

    /// Set `engine_path = "<exe>"` in the `[engine]` table. Replaces an
    /// existing (possibly commented-out) `engine_path` line in place, else
    /// inserts one right after the `[engine]` header. With no `[engine]`
    /// table the text is returned unchanged.
    let setEnginePath (text: string) (exe: string) : string =
        let lines, eol = splitLines text
        match tableRange lines "engine" with
        | None -> text
        | Some (s, e) ->
            let newLine = sprintf "engine_path = \"%s\"" (escape exe)
            let existing =
                [ s + 1 .. e - 1 ]
                |> List.tryFind (fun i ->
                    Regex.IsMatch(lines.[i].Trim(), @"^#?\s*engine_path\s*="))
            let rebuilt =
                match existing with
                | Some i -> lines |> Array.mapi (fun idx l -> if idx = i then newLine else l)
                | None   -> Array.concat [ lines.[0 .. s]; [| newLine |]; lines.[s + 1 ..] ]
            String.concat eol rebuilt
