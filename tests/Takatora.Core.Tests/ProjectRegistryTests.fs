namespace Takatora.Core.Tests

open System
open System.IO
open Xunit
open Takatora.Core

/// ProjectRegistry tests redirect the registry to a temp file via the
/// internal `setPathForTests` hook so they never touch the real
/// %APPDATA%\Takatora\projects.toml. Tests share state through the
/// path override; xUnit serializes methods within a class, so this is
/// safe so long as everything lives here.
type ProjectRegistryTests() =

    let dir =
        Path.Combine(
            Path.GetTempPath(),
            "takatora-registry-tests",
            Guid.NewGuid().ToString("N"))
    do
        Directory.CreateDirectory(dir) |> ignore
        ProjectRegistry.setPathForTests (Path.Combine(dir, "projects.toml"))

    let writeProjectAt (root: string) (name: string) =
        let ci = Path.Combine(root, ".ci")
        Directory.CreateDirectory(ci) |> ignore
        File.WriteAllText(
            Path.Combine(ci, "project.toml"),
            sprintf "[project]\nname = \"%s\"\nworking_dir = \".\"\n[engine]\ntype = \"godot\"\n" name)

    interface IDisposable with
        member _.Dispose() =
            ProjectRegistry.clearPathOverride ()
            try Directory.Delete(dir, recursive = true) with _ -> ()

    [<Fact>]
    member _.``load on missing file yields empty list`` () =
        Assert.Equal<ProjectRegistration list>([], ProjectRegistry.load ())

    [<Fact>]
    member _.``add registers and load round-trips it`` () =
        let projDir = Path.Combine(dir, "sample-game")
        writeProjectAt projDir "sample-game"

        match ProjectRegistry.add projDir None with
        | ProjectRegistry.Added entry ->
            Assert.Equal("sample-game", entry.Name)
            Assert.Equal(Path.GetFullPath projDir, entry.Path)
        | other -> Assert.Fail($"expected Added, got %A{other}")

        let loaded = ProjectRegistry.load ()
        Assert.Equal(1, List.length loaded)
        Assert.Equal("sample-game", loaded.[0].Name)

    [<Fact>]
    member _.``add with --name override wins over project.toml`` () =
        let projDir = Path.Combine(dir, "game-with-default-name")
        writeProjectAt projDir "internal-name"
        match ProjectRegistry.add projDir (Some "my-shortcut") with
        | ProjectRegistry.Added entry ->
            Assert.Equal("my-shortcut", entry.Name)
        | other -> Assert.Fail($"expected Added, got %A{other}")

    [<Fact>]
    member _.``add duplicate name returns DuplicateName`` () =
        let projDir = Path.Combine(dir, "dup")
        writeProjectAt projDir "dup"
        match ProjectRegistry.add projDir None with
        | ProjectRegistry.Added _ -> ()
        | other -> Assert.Fail($"first add unexpectedly %A{other}")
        // Try again with same effective name.
        match ProjectRegistry.add projDir None with
        | ProjectRegistry.DuplicateName existing ->
            Assert.Equal("dup", existing.Name)
        | other -> Assert.Fail($"expected DuplicateName, got %A{other}")

    [<Fact>]
    member _.``add on nonexistent path returns InvalidPath`` () =
        match ProjectRegistry.add (Path.Combine(dir, "does-not-exist")) None with
        | ProjectRegistry.InvalidPath reason ->
            Assert.Contains("does not exist", reason)
        | other -> Assert.Fail($"expected InvalidPath, got %A{other}")

    [<Fact>]
    member _.``add on dir without .ci\project.toml returns InvalidPath`` () =
        let projDir = Path.Combine(dir, "bare-dir")
        Directory.CreateDirectory(projDir) |> ignore
        match ProjectRegistry.add projDir None with
        | ProjectRegistry.InvalidPath reason ->
            Assert.Contains("project.toml", reason)
        | other -> Assert.Fail($"expected InvalidPath, got %A{other}")

    [<Fact>]
    member _.``remove returns true on hit, false on miss`` () =
        let projDir = Path.Combine(dir, "removable")
        writeProjectAt projDir "removable"
        ProjectRegistry.add projDir None |> ignore
        Assert.True(ProjectRegistry.remove "removable")
        Assert.False(ProjectRegistry.remove "removable")  // already gone
        Assert.False(ProjectRegistry.remove "never-existed")
        Assert.Equal<ProjectRegistration list>([], ProjectRegistry.load ())

    [<Fact>]
    member _.``find matches by exact name`` () =
        let projDir = Path.Combine(dir, "findable")
        writeProjectAt projDir "findable"
        ProjectRegistry.add projDir None |> ignore
        let entries = ProjectRegistry.load ()
        Assert.True(ProjectRegistry.find "findable" entries |> Option.isSome)
        Assert.True(ProjectRegistry.find "FINDABLE" entries |> Option.isNone)
        Assert.True(ProjectRegistry.find "missing"  entries |> Option.isNone)
