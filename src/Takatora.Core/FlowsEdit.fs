namespace Takatora.Core

open System.Globalization
open System.Text.RegularExpressions

/// In-place edits to a hand-authored `flows.toml`. Used by the run
/// dialog's "Save as new default". Deliberately surgical: only the changed
/// vars' own single-line inline-table definitions are rewritten (from their
/// parsed schema + the new default); every other line — comments,
/// formatting, untouched vars — is preserved byte-for-byte. A var whose
/// definition can't be confidently located as a single-line inline table is
/// left alone and reported back as "skipped".
[<RequireQualifiedAccess>]
module FlowsEdit =

    let private typeToken (kind: VarKind) : string =
        match kind with
        | VarKind.String -> "string" | VarKind.Int -> "int" | VarKind.Float -> "float"
        | VarKind.Bool -> "bool"     | VarKind.Path -> "path" | VarKind.File -> "file"
        | VarKind.Dir -> "dir"       | VarKind.Secret -> "secret"
        | VarKind.Multiline -> "multiline"
        | VarKind.Enum _ -> "enum"   | VarKind.List _ -> "list"

    let rec private tomlLit (v: TomlValue) : string =
        match v with
        | TString s -> "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        | TBool b   -> if b then "true" else "false"
        | TInt i    -> string i
        | TFloat f  -> f.ToString("R", CultureInfo.InvariantCulture)
        | TArray xs -> "[" + (xs |> List.map tomlLit |> String.concat ", ") + "]"
        | TTable _  -> "\"\""   // not a scalar/array default — shouldn't occur

    /// Canonical inline-table text (`{ type = "...", … default = … }`) for a
    /// var, rebuilt from its kind (+ enum values / list item) and new default.
    let renderVarInline (v: FlowVar) (def: TomlValue) : string =
        let parts = ResizeArray<string>()
        parts.Add(sprintf "type = \"%s\"" (typeToken v.Kind))
        match v.Kind with
        | VarKind.Enum values ->
            parts.Add(sprintf "values = [%s]" (values |> List.map (TString >> tomlLit) |> String.concat ", "))
        | VarKind.List itemKind ->
            parts.Add(sprintf "item = \"%s\"" (typeToken itemKind))
        | _ -> ()
        parts.Add(sprintf "default = %s" (tomlLit def))
        "{ " + String.concat ", " parts + " }"

    /// Rewrite the `default` of each `changed` var within the named flow's
    /// `[flow.vars]` block. Returns the new text and the names of vars that
    /// could NOT be edited (left untouched). On a missing flow/vars block,
    /// nothing changes and every var is reported skipped.
    let setVarDefaults (text: string) (flowId: string) (changed: (FlowVar * TomlValue) list) : string * string list =
        if List.isEmpty changed then text, []
        else
        let lines = text.Split('\n')   // each line keeps a trailing \r on CRLF files
        let trimmed i = lines.[i].Trim()
        let headers = [ for i in 0 .. lines.Length - 1 do if (trimmed i).StartsWith("[[flow]]") then yield i ]
        let allSkipped () = changed |> List.map (fun (v, _) -> v.Name)
        // The flow block [start, next) whose `id` matches.
        let targetBlock =
            headers |> List.tryPick (fun h ->
                let next = headers |> List.tryFind (fun x -> x > h) |> Option.defaultValue lines.Length
                let hasId =
                    seq { h + 1 .. next - 1 } |> Seq.exists (fun i ->
                        let m = Regex.Match(trimmed i, "^id\\s*=\\s*\"(.*)\"")
                        m.Success && m.Groups.[1].Value = flowId)
                if hasId then Some (h, next) else None)
        match targetBlock with
        | None -> text, allSkipped ()
        | Some (bStart, bEnd) ->
            match seq { bStart .. bEnd - 1 } |> Seq.tryFind (fun i -> trimmed i = "[flow.vars]") with
            | None -> text, allSkipped ()
            | Some vh ->
                // Vars region: after [flow.vars] until the next table header.
                let regionEnd =
                    seq { vh + 1 .. bEnd - 1 }
                    |> Seq.tryFind (fun i -> (trimmed i).StartsWith("["))
                    |> Option.defaultValue bEnd
                let lines2 = Array.copy lines
                let mutable skipped = []
                for (v, def) in changed do
                    let pat = Regex(sprintf "^(\\s*)%s\\s*=\\s*\\{.*\\}(.*)$" (Regex.Escape v.Name))
                    match seq { vh + 1 .. regionEnd - 1 } |> Seq.tryFind (fun i -> pat.IsMatch(lines2.[i])) with
                    | Some i ->
                        let m = pat.Match(lines2.[i])
                        // Groups: 1 = indent, 2 = trailing (comment / \r) after the }.
                        lines2.[i] <- sprintf "%s%s = %s%s" m.Groups.[1].Value v.Name (renderVarInline v def) m.Groups.[2].Value
                    | None -> skipped <- v.Name :: skipped
                String.concat "\n" lines2, List.rev skipped

    // ─── shared flow/step location helpers ─────────────────────────

    /// The line range [start, end) of the `[[flow]]` block whose id matches.
    let private locateFlow (lines: string[]) (flowId: string) : (int * int) option =
        let trimmed i = lines.[i].Trim()
        let headers = [ for i in 0 .. lines.Length - 1 do if (trimmed i).StartsWith("[[flow]]") then yield i ]
        headers |> List.tryPick (fun h ->
            let next = headers |> List.tryFind (fun x -> x > h) |> Option.defaultValue lines.Length
            let hasId =
                seq { h + 1 .. next - 1 } |> Seq.exists (fun i ->
                    let m = Regex.Match(trimmed i, "^id\\s*=\\s*\"(.*)\"")
                    m.Success && m.Groups.[1].Value = flowId)
            if hasId then Some (h, next) else None)

    let private stepHeadersIn (lines: string[]) (bStart: int) (bEnd: int) : int list =
        [ for i in bStart .. bEnd - 1 do if (lines.[i].Trim()).StartsWith("[[flow.steps]]") then yield i ]

    /// A step block runs from its header until the next table header (any
    /// line starting "[") or the flow block's end.
    let private stepBlockEnd (lines: string[]) (sh: int) (bEnd: int) : int =
        seq { sh + 1 .. bEnd - 1 }
        |> Seq.tryFind (fun i -> (lines.[i].Trim()).StartsWith("["))
        |> Option.defaultValue bEnd

    let private crlf (lines: string[]) =
        if lines.Length > 0 && lines.[0].EndsWith("\r") then "\r" else ""

    /// Remove the `stepIndex`-th step of a flow (its header + param lines, plus
    /// one trailing blank line). Returns the new text and whether it landed.
    let removeStep (text: string) (flowId: string) (stepIndex: int) : string * bool =
        let lines = text.Split('\n')
        match locateFlow lines flowId with
        | None -> text, false
        | Some (bStart, bEnd) ->
            let shs = stepHeadersIn lines bStart bEnd
            match List.tryItem stepIndex shs with
            | None -> text, false
            | Some sh ->
                let se = stepBlockEnd lines sh bEnd
                let kept = [ for i in 0 .. lines.Length - 1 do if i < sh || i >= se then yield lines.[i] ]
                String.concat "\n" kept, true

    /// Move the `stepIndex`-th step by `delta` (-1 = up, +1 = down), swapping
    /// it with the adjacent step. Returns the new text and whether it landed.
    let moveStep (text: string) (flowId: string) (stepIndex: int) (delta: int) : string * bool =
        if delta <> -1 && delta <> 1 then text, false
        else
        let lines = text.Split('\n')
        match locateFlow lines flowId with
        | None -> text, false
        | Some (bStart, bEnd) ->
            let shs = stepHeadersIn lines bStart bEnd
            let other = stepIndex + delta
            if stepIndex < 0 || other < 0 || stepIndex >= List.length shs || other >= List.length shs then text, false
            else
                // Normalize to swapping the lower-indexed block (lo) with the next (hi).
                let lo = min stepIndex other
                let aStart = List.item lo shs
                let bStartIdx = List.item (lo + 1) shs
                let bEndIdx = stepBlockEnd lines bStartIdx bEnd
                let take a b = [ for i in a .. b - 1 -> lines.[i] ]
                let before = take 0 aStart
                let aBlock = take aStart bStartIdx
                let bBlock = take bStartIdx bEndIdx
                let after  = take bEndIdx lines.Length
                String.concat "\n" (before @ bBlock @ aBlock @ after), true

    /// Append a new step (`[[flow.steps]]` + `type = "<taskType>"`) at the end
    /// of a flow's steps. Returns the new text and whether it landed.
    let addStep (text: string) (flowId: string) (taskType: string) : string * bool =
        let lines = text.Split('\n')
        match locateFlow lines flowId with
        | None -> text, false
        | Some (bStart, bEnd) ->
            let cr = crlf lines
            let shs = stepHeadersIn lines bStart bEnd
            // The region end to insert before: end of the last step block, or
            // the flow's end if there are no steps yet.
            let regionEnd =
                match List.tryLast shs with
                | Some lastSh -> stepBlockEnd lines lastSh bEnd
                | None -> bEnd
            // Back up over trailing blank/comment lines so the new step lands
            // right after the last real content — not after blank lines or a
            // comment that actually introduces the *next* flow.
            let mutable insertAt = regionEnd
            while insertAt > bStart + 1
                  && (let t = lines.[insertAt - 1].Trim() in t = "" || t.StartsWith("#")) do
                insertAt <- insertAt - 1
            let block =
                [ "" + cr
                  "[[flow.steps]]" + cr
                  sprintf "type = \"%s\"%s" (taskType.Replace("\\", "\\\\").Replace("\"", "\\\"")) cr ]
            let asList = List.ofArray lines
            let before = asList |> List.truncate insertAt
            let after = asList |> List.skip insertAt
            String.concat "\n" (before @ block @ after), true

    let private reservedStepKeys = Set.ofList [ "type"; "id"; "when" ]

    /// Set (or add) a single param `key = value` on the `stepIndex`-th
    /// `[[flow.steps]]` of the named flow. Surgical: only that line changes
    /// (a trailing inline comment is preserved); a missing key is inserted
    /// right after the step header. Returns the new text and whether the edit
    /// landed (false = flow/step not found, or a reserved key). Reserved keys
    /// (type/id/when) are refused — they're not task params.
    let setStepParam (text: string) (flowId: string) (stepIndex: int) (key: string) (value: TomlValue) : string * bool =
        if Set.contains key reservedStepKeys then text, false
        else
        let lines = text.Split('\n')
        let trimmed i = lines.[i].Trim()
        let headers = [ for i in 0 .. lines.Length - 1 do if (trimmed i).StartsWith("[[flow]]") then yield i ]
        let targetBlock =
            headers |> List.tryPick (fun h ->
                let next = headers |> List.tryFind (fun x -> x > h) |> Option.defaultValue lines.Length
                let hasId =
                    seq { h + 1 .. next - 1 } |> Seq.exists (fun i ->
                        let m = Regex.Match(trimmed i, "^id\\s*=\\s*\"(.*)\"")
                        m.Success && m.Groups.[1].Value = flowId)
                if hasId then Some (h, next) else None)
        match targetBlock with
        | None -> text, false
        | Some (bStart, bEnd) ->
            let stepHeaders =
                [ for i in bStart .. bEnd - 1 do if (trimmed i).StartsWith("[[flow.steps]]") then yield i ]
            match List.tryItem stepIndex stepHeaders with
            | None -> text, false
            | Some sh ->
                // The step's lines run until the next table header (any "[") or block end.
                let stepEnd =
                    seq { sh + 1 .. bEnd - 1 }
                    |> Seq.tryFind (fun i -> (trimmed i).StartsWith("["))
                    |> Option.defaultValue bEnd
                let valStr = tomlLit value
                let pat = Regex(sprintf "^(\\s*)%s\\s*=\\s*(.*)$" (Regex.Escape key))
                match seq { sh + 1 .. stepEnd - 1 } |> Seq.tryFind (fun i -> pat.IsMatch lines.[i]) with
                | Some i ->
                    let m = pat.Match lines.[i]
                    let indent = m.Groups.[1].Value
                    let rest = m.Groups.[2].Value
                    let cr = if rest.EndsWith("\r") then "\r" else ""
                    // Preserve a trailing inline comment ( # …), if any.
                    let comment =
                        let cm = Regex.Match(rest.TrimEnd('\r'), "\\s+#.*$")
                        if cm.Success then cm.Value else ""
                    let lines2 = Array.copy lines
                    lines2.[i] <- sprintf "%s%s = %s%s%s" indent key valStr comment cr
                    String.concat "\n" lines2, true
                | None ->
                    // Insert a fresh line right after the step header.
                    let cr = if lines.[sh].EndsWith("\r") then "\r" else ""
                    let newLine = sprintf "%s = %s%s" key valStr cr
                    let asList = List.ofArray lines
                    let before = asList |> List.truncate (sh + 1)
                    let after = asList |> List.skip (sh + 1)
                    String.concat "\n" (before @ [ newLine ] @ after), true
