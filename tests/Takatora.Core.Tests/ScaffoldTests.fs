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
    member _.``generated flows.toml leads with a runnable smoke flow (every engine)`` () =
        for engine in [ EngineKind.Unreal; EngineKind.Unity; EngineKind.Godot ] do
            let flows = TomlConfig.parseFlows (Scaffold.flowsToml "MyGame" engine)
            Assert.Equal("smoke", flows.[0].Id)
            Assert.Contains(flows.[0].Steps, fun s -> s.Type = "notify.console")

    [<Fact>]
    member _.``generated flows.toml carries engine-specific preset flows`` () =
        let idsAndTypes engine =
            let flows = TomlConfig.parseFlows (Scaffold.flowsToml "MyGame" engine)
            let ids = flows |> List.map (fun f -> f.Id) |> Set.ofList
            let types = flows |> List.collect (fun f -> f.Steps |> List.map (fun s -> s.Type)) |> Set.ofList
            ids, types
        let ueIds, ueTypes = idsAndTypes EngineKind.Unreal
        Assert.True(Set.isSubset (Set.ofList [ "compile"; "package"; "clean" ]) ueIds)
        Assert.Contains("ue.build_cook_run", ueTypes)
        let unityIds, unityTypes = idsAndTypes EngineKind.Unity
        Assert.True(Set.isSubset (Set.ofList [ "build"; "clean" ]) unityIds)
        Assert.Contains("unity.build", unityTypes)
        let godotIds, godotTypes = idsAndTypes EngineKind.Godot
        Assert.True(Set.isSubset (Set.ofList [ "export"; "clean" ]) godotIds)
        Assert.Contains("godot.export", godotTypes)

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
        Assert.True(File.Exists(Path.Combine(dir, ".takatora", "project.toml")))
        Assert.True(File.Exists(Path.Combine(dir, ".takatora", "flows.toml")))

    [<Fact>]
    member _.``writeCi never clobbers existing files`` () =
        let dir = Path.Combine(tmpRoot, "existing")
        let ci = Path.Combine(dir, ".takatora")
        Directory.CreateDirectory ci |> ignore
        File.WriteAllText(Path.Combine(ci, "project.toml"), "# hand written")
        let outcome = Scaffold.writeCi dir "existing" EngineKind.Unreal
        Assert.False(outcome.ProjectTomlCreated)   // left untouched
        Assert.True(outcome.FlowsTomlCreated)      // flows was absent → created
        Assert.Equal("# hand written", File.ReadAllText(Path.Combine(ci, "project.toml")))
