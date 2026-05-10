namespace Takatora.Core

open System
open System.Text.RegularExpressions

/// Lookup environment for placeholder substitution inside step params
/// and `when` expressions.
type ResolveContext = {
    /// Flow vars after defaults + overrides have been merged.
    Vars: Map<string, TomlValue>
    /// Outputs from already-executed prior steps, keyed by step id.
    StepOutputs: Map<string, Map<string, TomlValue>>
    /// Reference to the owning project — exposes `${project.name}` etc.
    Project: Project
    /// OS environment reader. Factored as a function so tests can inject
    /// a deterministic substitute without touching the real environment.
    Env: string -> string option
}

/// Raised when a placeholder can't be resolved (unknown var, bad path,
/// missing prior output, etc.) or a `when` expression doesn't fit the
/// MVP grammar.
exception VarResolutionError of message: string

[<RequireQualifiedAccess>]
module Vars =

    let private fail msg = raise (VarResolutionError msg)

    // `${ns.path}` — namespace + dotted path, no whitespace, no nesting.
    let private placeholderPattern =
        Regex(@"\$\{([a-zA-Z_][a-zA-Z0-9_]*(?:\.[a-zA-Z_][a-zA-Z0-9_]*)*)\}",
              RegexOptions.Compiled)

    /// Stringify a `TomlValue` for embedded interpolation. Single-placeholder
    /// strings preserve type via `lookup` directly; this is only for the
    /// "embedded inside a larger string" case.
    let private toStringValue (v: TomlValue) : string =
        match v with
        | TString s -> s
        | TInt i -> string i
        | TFloat f -> string f
        | TBool b -> if b then "true" else "false"
        | TArray _ | TTable _ ->
            fail "cannot interpolate array/table values inside a string — use a single ${...} placeholder"

    let private projectField (project: Project) (path: string) : TomlValue =
        match path with
        | "name" -> TString project.Name
        | "working_dir" -> TString project.WorkingDir
        | other -> fail $"unknown project field '${{project.{other}}}'"

    let private lookup (ctx: ResolveContext) (path: string) : TomlValue =
        match path.Split('.') |> Array.toList with
        | [ "vars"; name ] ->
            match Map.tryFind name ctx.Vars with
            | Some v -> v
            | None -> fail $"unknown var '${{vars.{name}}}'"
        | [ "steps"; stepId; "outputs"; key ] ->
            match Map.tryFind stepId ctx.StepOutputs with
            | None -> fail $"step '{stepId}' has no recorded outputs (referenced by ${{steps.{stepId}.outputs.{key}}})"
            | Some outs ->
                match Map.tryFind key outs with
                | Some v -> v
                | None -> fail $"step '{stepId}' has no output '{key}'"
        | "project" :: rest ->
            projectField ctx.Project (String.concat "." rest)
        | [ "env"; name ] ->
            match ctx.Env name with
            | Some v -> TString v
            | None -> fail $"environment variable '{name}' is not set"
        | _ -> fail $"unsupported placeholder ${{{path}}} — expected vars.X / steps.X.outputs.Y / project.X / env.X"

    /// Substitute placeholders inside a single string. If the entire
    /// string is a single placeholder, the typed value comes through;
    /// otherwise placeholders are stringified and concatenated.
    let private resolveString (ctx: ResolveContext) (s: string) : TomlValue =
        let matches = placeholderPattern.Matches(s)
        if matches.Count = 0 then
            TString s
        elif matches.Count = 1 && matches.[0].Value = s then
            // Whole-string placeholder — preserve the underlying type.
            lookup ctx matches.[0].Groups.[1].Value
        else
            // Embedded — stringify each match and stitch the result.
            let replaced =
                placeholderPattern.Replace(s, fun (m: Match) ->
                    lookup ctx m.Groups.[1].Value |> toStringValue)
            TString replaced

    /// Walk a `TomlValue` tree applying placeholder substitution to every
    /// `TString`. Arrays and tables are recursed; non-string scalars pass
    /// through unchanged.
    let rec resolve (ctx: ResolveContext) (value: TomlValue) : TomlValue =
        match value with
        | TString s -> resolveString ctx s
        | TArray xs -> TArray (List.map (resolve ctx) xs)
        | TTable m -> TTable (Map.map (fun _ v -> resolve ctx v) m)
        | TInt _ | TFloat _ | TBool _ -> value

    // ─── when expressions ──────────────────────────────────────────

    let private toBool (path: string) (value: TomlValue) : bool =
        match value with
        | TBool b -> b
        | other ->
            fail $"`when` expression '${{{path}}}' must reference a bool, got {other.GetType().Name}"

    /// Evaluate a step's `when` clause. MVP grammar accepts only:
    ///   - `${vars.X}` (bool reference)
    ///   - `!${vars.X}` (bool negation)
    /// Comparison and logical-combine forms are deferred.
    let evalWhen (ctx: ResolveContext) (expression: string) : bool =
        let trimmed = expression.Trim()
        let negated, body =
            if trimmed.StartsWith("!") then true, trimmed.Substring(1).TrimStart()
            else false, trimmed
        let m = placeholderPattern.Match(body)
        if not m.Success || m.Value <> body then
            fail $"`when` expression must be exactly `${{vars.X}}` or `!${{vars.X}}` (got: {expression})"
        let path = m.Groups.[1].Value
        let value = lookup ctx path
        let result = toBool path value
        if negated then not result else result
