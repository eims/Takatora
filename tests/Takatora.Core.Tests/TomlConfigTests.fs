module Takatora.Core.Tests.TomlConfigTests

open Xunit
open Takatora.Core

// ─── project.toml ──────────────────────────────────────────────────

let private sampleProjectToml = """
[project]
name = "sample-game"
working_dir = "."

[engine]
type = "unreal"
project_file = "SampleGame.uproject"

[vcs]
type = "git"
lfs = false

[history]
keep_last_n_runs = 50
"""

[<Fact>]
let ``parseProject reads sample-game project`` () =
    let p = TomlConfig.parseProject sampleProjectToml
    Assert.Equal("sample-game", p.Name)
    Assert.Equal(".", p.WorkingDir)
    Assert.Equal(EngineKind.Unreal, p.Engine.Kind)
    Assert.Equal(Some "SampleGame.uproject", p.Engine.ProjectFile)
    Assert.Equal(None, p.Engine.EnginePath)
    Assert.Equal(None, p.Engine.EngineVersion)
    Assert.Equal(50, p.History.KeepLastNRuns)
    match p.Vcs with
    | Some v ->
        Assert.Equal(VcsKind.Git, v.Kind)
        Assert.False(v.Lfs)
    | None -> Assert.Fail("expected vcs section to be present")

[<Fact>]
let ``parseProject defaults history when section omitted`` () =
    let toml = """
[project]
name = "x"
working_dir = "."
[engine]
type = "unity"
"""
    let p = TomlConfig.parseProject toml
    Assert.Equal(50, p.History.KeepLastNRuns)
    Assert.Equal(None, p.Vcs)
    Assert.Equal(EngineKind.Unity, p.Engine.Kind)

/// Run an action expected to raise `TomlConfigError` and return the message.
/// Fails the test if no exception (or a different exception) is thrown.
let private catchTomlError (action: unit -> unit) : string =
    try
        action ()
        Assert.Fail("expected TomlConfigError")
        "" // unreachable; Assert.Fail throws
    with TomlConfigError msg -> msg

[<Fact>]
let ``parseProject rejects unknown engine type`` () =
    let toml = """
[project]
name = "x"
working_dir = "."
[engine]
type = "cryengine"
"""
    let msg = catchTomlError (fun () -> TomlConfig.parseProject toml |> ignore)
    Assert.Contains("cryengine", msg)

[<Fact>]
let ``parseProject reports missing required key`` () =
    let toml = """
[project]
working_dir = "."
[engine]
type = "godot"
"""
    let msg = catchTomlError (fun () -> TomlConfig.parseProject toml |> ignore)
    Assert.Contains("name", msg)

// ─── flows.toml ────────────────────────────────────────────────────

let private sampleFlowsToml = """
[[flow]]
id = "smoke"
name = "Smoke test (notify only)"

[flow.vars]
message = { type = "string", default = "hello from takatora" }

[[flow.steps]]
type = "notify.console"
"""

[<Fact>]
let ``parseFlows reads sample-game smoke flow`` () =
    let flows = TomlConfig.parseFlows sampleFlowsToml
    Assert.Equal(1, List.length flows)
    let f = flows.[0]
    Assert.Equal("smoke", f.Id)
    Assert.Equal(Some "Smoke test (notify only)", f.Name)
    Assert.Equal(1, List.length f.Vars)
    let v = f.Vars.[0]
    Assert.Equal("message", v.Name)
    Assert.Equal(VarKind.String, v.Kind)
    Assert.Equal(Some (TString "hello from takatora"), v.Default)
    Assert.Equal(1, List.length f.Steps)
    let s = f.Steps.[0]
    Assert.Equal("notify.console", s.Type)
    Assert.Equal(None, s.Id)
    Assert.Equal(None, s.When)
    Assert.True(Map.isEmpty s.Params)

[<Fact>]
let ``parseFlows preserves step params and reserved keys`` () =
    let toml = """
[[flow]]
id = "release"

[[flow.steps]]
id = "clean"
type = "ue.clean"
when = "${vars.clean_first}"
targets = ["intermediate", "binaries"]

[[flow.steps]]
type = "ue.build_cook_run"
configuration = "Shipping"
"""
    let flows = TomlConfig.parseFlows toml
    let f = flows.[0]
    Assert.Equal(2, List.length f.Steps)

    let clean = f.Steps.[0]
    Assert.Equal(Some "clean", clean.Id)
    Assert.Equal("ue.clean", clean.Type)
    Assert.Equal(Some "${vars.clean_first}", clean.When)
    let targets = clean.Params.["targets"]
    Assert.Equal(TArray [ TString "intermediate"; TString "binaries" ], targets)
    Assert.False(Map.containsKey "type" clean.Params)
    Assert.False(Map.containsKey "id" clean.Params)
    Assert.False(Map.containsKey "when" clean.Params)

    let build = f.Steps.[1]
    Assert.Equal(None, build.Id)
    Assert.Equal(Some (TString "Shipping"), Map.tryFind "configuration" build.Params)

[<Fact>]
let ``parseFlows handles enum var with values`` () =
    let toml = """
[[flow]]
id = "release"

[flow.vars]
configuration = { type = "enum", values = ["Development","Shipping"], default = "Shipping" }
"""
    let flows = TomlConfig.parseFlows toml
    let v = flows.[0].Vars.[0]
    Assert.Equal("configuration", v.Name)
    Assert.Equal(VarKind.Enum [ "Development"; "Shipping" ], v.Kind)
    Assert.Equal(Some (TString "Shipping"), v.Default)

[<Fact>]
let ``parseFlows rejects enum without values`` () =
    let toml = """
[[flow]]
id = "x"
[flow.vars]
foo = { type = "enum", default = "a" }
"""
    let msg = catchTomlError (fun () -> TomlConfig.parseFlows toml |> ignore)
    Assert.Contains("enum", msg)
    Assert.Contains("values", msg)

[<Fact>]
let ``parseFlows on empty document yields empty list`` () =
    Assert.Equal<Flow list>([], TomlConfig.parseFlows "")

[<Fact>]
let ``parseProject surfaces TOML syntax errors`` () =
    let msg = catchTomlError (fun () ->
        TomlConfig.parseProject "this is not = = valid toml" |> ignore)
    Assert.Contains("TOML parse error", msg)
