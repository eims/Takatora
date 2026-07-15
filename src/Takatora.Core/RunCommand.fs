namespace Takatora.Core

open System
open System.Globalization

/// Reconstruct the `takatora run …` command line that reproduces a past
/// run, from its recorded params. Pure and unit-testable: the GUI's
/// "Copy CLI command" button (and, later, a CLI helper) call `build`.
///
/// The reconstructed command is a *normal* run invocation — not a
/// dedicated replay path. Secret vars are omitted, never emitted in
/// plaintext: the manifest only ever recorded a mask for them, and a real
/// run re-sources each secret from the keychain.
[<RequireQualifiedAccess>]
module RunCommand =

    // The manifest masks secret params with this sentinel (see Run.fs). A
    // recorded value equal to it is never something we'd want to emit — a
    // defensive skip on top of the by-name secret filter, so a command is
    // safe even when the flow definition (the name source) is unavailable.
    let [<Literal>] private SecretMask = "***"

    /// Render a value the way `takatora run --var KEY=VALUE` expects to
    /// read it back. Scalars round-trip through the CLI's heuristic typing
    /// (bool > int > float > string). Arrays/tables can't be expressed as a
    /// single `--var` (the CLI parses everything after '=' as one scalar),
    /// so they render best-effort in bracket form — readable, but re-parsed
    /// as a string. Real `--var` overrides are overwhelmingly scalar.
    let rec private renderValue (v: TomlValue) : string =
        match v with
        | TString s -> s
        | TBool b   -> if b then "true" else "false"
        | TInt i    -> string i
        | TFloat f  -> f.ToString("R", CultureInfo.InvariantCulture)
        | TArray xs -> "[" + (xs |> List.map renderValue |> String.concat ", ") + "]"
        | TTable m  ->
            "{" + (m |> Map.toList
                     |> List.map (fun (k, x) -> sprintf "%s = %s" k (renderValue x))
                     |> String.concat ", ") + "}"

    /// Quote a whole shell token when it contains whitespace or a double
    /// quote (embedded quotes are backslash-escaped), or is empty. Double
    /// quotes work in both cmd.exe and PowerShell for the values we emit.
    let private quote (token: string) : string =
        let needs =
            token = ""
            || token |> Seq.exists (fun c -> Char.IsWhiteSpace c || c = '"')
        if needs then "\"" + token.Replace("\"", "\\\"") + "\""
        else token

    /// Build `takatora run <project> <flow> [--var k=v …]` from a past
    /// run's recorded params. Secret-named vars (and any value still equal
    /// to the mask) are omitted. Params come out in key order — F# Map
    /// iteration is sorted — for a stable, testable string.
    let build
            (projectName: string)
            (flowId: string)
            (recordedParams: Map<string, TomlValue>)
            (secretNames: Set<string>)
            : string =
        let head = sprintf "takatora run %s %s" (quote projectName) (quote flowId)
        let varArgs =
            recordedParams
            |> Map.toList
            |> List.choose (fun (k, v) ->
                let isMasked = match v with TString s when s = SecretMask -> true | _ -> false
                if Set.contains k secretNames || isMasked then None
                else Some (sprintf "--var %s" (quote (sprintf "%s=%s" k (renderValue v)))))
        match varArgs with
        | [] -> head
        | args -> head + " " + String.concat " " args
