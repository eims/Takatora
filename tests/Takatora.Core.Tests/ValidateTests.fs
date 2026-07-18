module Takatora.Core.Tests.ValidateTests

open System
open System.IO
open Xunit
open Takatora.Core
open Takatora.Cli

// ─── Test fixture helpers ──────────────────────────────────────────

/// Create a fresh temp directory for one test, run the action, then
/// best-effort delete it. Returns the action's result.
let private withTempDir (action: string -> 'a) : 'a =
    let dir =
        Path.Combine(
            Path.GetTempPath(),
            "takatora-validate-tests",
            Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    try action dir
    finally
        try Directory.Delete(dir, recursive = true) with _ -> ()

let private writeCi (workingDir: string) (projectToml: string) (flowsToml: string) =
    let ci = Path.Combine(workingDir, ".takatora")
    Directory.CreateDirectory(ci) |> ignore
    File.WriteAllText(Path.Combine(ci, "project.toml"), projectToml)
    File.WriteAllText(Path.Combine(ci, "flows.toml"), flowsToml)

let private validProjectToml = """
[project]
name = "fixture"
working_dir = "."
[engine]
type = "godot"
"""

let private validFlowsToml = """
[[flow]]
id = "smoke"
[[flow.steps]]
type = "notify.console"
"""

// ─── run ───────────────────────────────────────────────────────────

[<Fact>]
let ``run returns Valid when both files parse`` () =
    withTempDir (fun dir ->
        writeCi dir validProjectToml validFlowsToml
        match Validate.run dir with
        | Validate.Valid (project, flows, ps, warnings) ->
            Assert.Equal("fixture", project.Name)
            Assert.Equal(1, List.length flows)
            Assert.Empty(ps)
            Assert.Empty(warnings)
        | other -> Assert.Fail($"expected Valid, got %A{other}"))

[<Fact>]
let ``run returns MissingFile when project toml absent`` () =
    withTempDir (fun dir ->
        // Don't create .takatora/ at all.
        match Validate.run dir with
        | Validate.MissingFile path ->
            Assert.EndsWith("project.toml", path)
        | other -> Assert.Fail($"expected MissingFile, got %A{other}"))

[<Fact>]
let ``run returns MissingFile when flows toml absent but project present`` () =
    withTempDir (fun dir ->
        let ci = Path.Combine(dir, ".takatora")
        Directory.CreateDirectory(ci) |> ignore
        File.WriteAllText(Path.Combine(ci, "project.toml"), validProjectToml)
        match Validate.run dir with
        | Validate.MissingFile path ->
            Assert.EndsWith("flows.toml", path)
        | other -> Assert.Fail($"expected MissingFile, got %A{other}"))

[<Fact>]
let ``run returns ConfigError attributing the failing file`` () =
    withTempDir (fun dir ->
        let badProject = """
[project]
working_dir = "."
[engine]
type = "godot"
"""
        writeCi dir badProject validFlowsToml
        match Validate.run dir with
        | Validate.ConfigError (source, msg) ->
            Assert.EndsWith("project.toml", source)
            Assert.Contains("name", msg)
        | other -> Assert.Fail($"expected ConfigError, got %A{other}"))

[<Fact>]
let ``run attributes flows errors to flows.toml`` () =
    withTempDir (fun dir ->
        let badFlows = """
[[flow]]
id = "x"
[flow.vars]
foo = { type = "enum", default = "a" }
"""
        writeCi dir validProjectToml badFlows
        match Validate.run dir with
        | Validate.ConfigError (source, _) ->
            Assert.EndsWith("flows.toml", source)
        | other -> Assert.Fail($"expected ConfigError, got %A{other}"))

// ─── params.toml integration ───────────────────────────────────────

let private writeParams (workingDir: string) (paramsToml: string) =
    File.WriteAllText(Path.Combine(workingDir, ".takatora", "params.toml"), paramsToml)

[<Fact>]
let ``run reports declared params and no warnings when all referenced`` () =
    withTempDir (fun dir ->
        writeCi dir validProjectToml """
[[flow]]
id = "smoke"
[[flow.steps]]
type = "notify.console"
message = "${params.studio_name}"
"""
        writeParams dir """
[params]
studio_name = { type = "string", value = "Foo" }
"""
        match Validate.run dir with
        | Validate.Valid (_, _, ps, warnings) ->
            Assert.Equal(1, List.length ps)
            Assert.Empty(warnings)
        | other -> Assert.Fail($"expected Valid, got %A{other}"))

[<Fact>]
let ``run rejects an undeclared params reference`` () =
    withTempDir (fun dir ->
        writeCi dir validProjectToml """
[[flow]]
id = "smoke"
[[flow.steps]]
type = "notify.console"
message = "${params.nope}"
"""
        match Validate.run dir with
        | Validate.ConfigError (source, msg) ->
            Assert.EndsWith("params.toml", source)
            Assert.Contains("nope", msg)
            Assert.Contains("smoke", msg)
        | other -> Assert.Fail($"expected ConfigError, got %A{other}"))

[<Fact>]
let ``run attributes params parse errors to params.toml`` () =
    withTempDir (fun dir ->
        writeCi dir validProjectToml validFlowsToml
        writeParams dir """
[params]
pw = { type = "secret", value = "oops" }
"""
        match Validate.run dir with
        | Validate.ConfigError (source, msg) ->
            Assert.EndsWith("params.toml", source)
            Assert.Contains("pw", msg)
        | other -> Assert.Fail($"expected ConfigError, got %A{other}"))

[<Fact>]
let ``run warns on flow vars shadowing params and unused params`` () =
    withTempDir (fun dir ->
        writeCi dir validProjectToml """
[[flow]]
id = "smoke"
[flow.vars]
studio_name = { type = "string", default = "local" }
[[flow.steps]]
type = "notify.console"
message = "${params.studio_name}"
"""
        writeParams dir """
[params]
studio_name = { type = "string", value = "Foo" }
orphan      = { type = "string", value = "unused" }
"""
        match Validate.run dir with
        | Validate.Valid (_, _, _, warnings) ->
            Assert.Contains(warnings, fun w -> w.Contains "shadows")
            Assert.Contains(warnings, fun w -> w.Contains "orphan")
        | other -> Assert.Fail($"expected Valid, got %A{other}"))

// ─── format ────────────────────────────────────────────────────────

[<Fact>]
let ``format Valid yields exit 0 and prints to stdout`` () =
    let project =
        { Name = "x"
          WorkingDir = "."
          Engine =
            { Kind = EngineKind.Unreal
              ProjectFile = None
              EnginePath = None
              EngineVersion = None
              Executable = None }
          Vcs = None
          History = { KeepLastNRuns = 50 } }
    let flow = { Id = "smoke"; Name = None; Vars = []; Steps = [] }
    let stdout, stderr, code = Validate.format (Validate.Valid (project, [ flow ], [], []))
    Assert.Equal(0, code)
    Assert.Contains("valid", stdout)
    Assert.Contains("smoke", stdout)
    Assert.Equal("", stderr)

[<Fact>]
let ``format MissingFile yields exit 3 to stderr`` () =
    let _, stderr, code = Validate.format (Validate.MissingFile "X/.takatora/project.toml")
    Assert.Equal(3, code)
    Assert.Contains("not found", stderr)
    Assert.Contains("project.toml", stderr)

[<Fact>]
let ``format ConfigError yields exit 2 to stderr`` () =
    let _, stderr, code = Validate.format (Validate.ConfigError ("X/.takatora/flows.toml", "bad enum"))
    Assert.Equal(2, code)
    Assert.Contains("flows.toml", stderr)
    Assert.Contains("bad enum", stderr)
