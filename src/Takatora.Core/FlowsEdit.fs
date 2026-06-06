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
