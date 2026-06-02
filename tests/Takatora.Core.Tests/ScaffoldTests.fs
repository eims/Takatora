namespace Takatora.Core.Tests

open System
open System.IO
open Xunit
open Takatora.Core

type ScaffoldTests() =

    let tmpRoot =
        Path.Combine(Path.GetTempPath(), "takatora-scaffold-tests", Guid.NewGuid().ToString("N"))
    do Directory.CreateDirectory(tmpRoot) |> ignore

    interface IDisposable with
        member _.Dispose() =
            try Directory.Delete(tmpRoot, recursive = true) with _ -> ()

    [<Fact>]
    member _.``generated project.toml parses and carries name + engine`` () =
        let p = TomlConfig.parseProject (Scaffold.projectToml "MyGame" EngineKind.Godot)
        Assert.Equal("MyGame", p.Name)
        Assert.Equal(EngineKind.Godot, p.Engine.Kind)

    [<Fact>]
    member _.``generated flows.toml parses with a runnable smoke flow`` () =
        let flows = TomlConfig.parseFlows (Scaffold.flowsToml ())
        Assert.Equal(1, List.length flows)
        Assert.Equal("smoke", flows.[0].Id)
        Assert.Contains(flows.[0].Steps, fun s -> s.Type = "notify.console")

    [<Fact>]
    member _.``project name with a backslash stays valid TOML`` () =
        // The name is escaped, so a stray backslash doesn't break the parse.
        let p = TomlConfig.parseProject (Scaffold.projectToml @"My\Game" EngineKind.Unreal)
        Assert.Equal(@"My\Game", p.Name)

    [<Fact>]
    member _.``writeCi creates the ci tree and reports both files created`` () =
        let dir = Path.Combine(tmpRoot, "fresh")
        let outcome = Scaffold.writeCi dir "fresh" EngineKind.Unity
        Assert.True(outcome.ProjectTomlCreated)
        Assert.True(outcome.FlowsTomlCreated)
        Assert.True(File.Exists(Path.Combine(dir, ".ci", "project.toml")))
        Assert.True(File.Exists(Path.Combine(dir, ".ci", "flows.toml")))

    [<Fact>]
    member _.``writeCi never clobbers existing files`` () =
        let dir = Path.Combine(tmpRoot, "existing")
        let ci = Path.Combine(dir, ".ci")
        Directory.CreateDirectory ci |> ignore
        File.WriteAllText(Path.Combine(ci, "project.toml"), "# hand written")
        let outcome = Scaffold.writeCi dir "existing" EngineKind.Unreal
        Assert.False(outcome.ProjectTomlCreated)   // left untouched
        Assert.True(outcome.FlowsTomlCreated)      // flows was absent → created
        Assert.Equal("# hand written", File.ReadAllText(Path.Combine(ci, "project.toml")))
