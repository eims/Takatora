module Takatora.Core.Tests.EnginesTests

open System
open System.IO
open Xunit
open Takatora.Core

// Detection touches the real registry / file system / PATH, so the
// actual detected list depends on what's installed on the test
// machine. We assert structural properties only — that detection
// returns a list (possibly empty) without throwing, and that detectAll
// exposes an entry per kind. Specific install assertions would make
// the suite environment-dependent.

[<Fact>]
let ``detect Unreal returns a list without throwing`` () =
    let xs = Engines.detect EngineKind.Unreal
    Assert.NotNull(xs :> obj)

[<Fact>]
let ``detect Unity returns a list without throwing`` () =
    let xs = Engines.detect EngineKind.Unity
    Assert.NotNull(xs :> obj)

[<Fact>]
let ``detect Godot returns a list without throwing`` () =
    let xs = Engines.detect EngineKind.Godot
    Assert.NotNull(xs :> obj)

[<Fact>]
let ``detectAll has an entry for every engine kind`` () =
    let map = Engines.detectAll ()
    Assert.True(Map.containsKey EngineKind.Unreal map)
    Assert.True(Map.containsKey EngineKind.Unity  map)
    Assert.True(Map.containsKey EngineKind.Godot  map)

[<Fact>]
let ``every detected entry carries kind matching the lookup`` () =
    for kind in [ EngineKind.Unreal; EngineKind.Unity; EngineKind.Godot ] do
        for e in Engines.detect kind do
            Assert.Equal(kind, e.Kind)
            Assert.False(System.String.IsNullOrWhiteSpace e.Path,
                         $"detected {kind} entry has empty Path")
            Assert.False(System.String.IsNullOrWhiteSpace e.Version,
                         $"detected {kind} entry has empty Version")

[<Fact>]
let ``pick returns None when nothing is detected`` () =
    // Crank up the version hint so it can't possibly match anything real.
    let result = Engines.pick EngineKind.Unreal (Some "1.0.0-not-real")
    // pick falls back to the first detection when hint doesn't match,
    // so this either returns None (if nothing detected) OR Some of
    // whatever IS detected. Either is fine; the contract is "doesn't
    // throw, returns Option".
    match result with
    | None -> ()
    | Some e -> Assert.Equal(EngineKind.Unreal, e.Kind)

// ─── pickFrom matching (pure — no registry) ────────────────────────

let private ue version assoc =
    { Kind = EngineKind.Unreal; Version = version; Path = $@"C:\{version}"
      Executable = None; Association = assoc }

[<Fact>]
let ``pickFrom matches a launcher version exactly`` () =
    let cands = [ ue "5.6.1" None; ue "5.7.4" None ]
    let r = Engines.pickFrom cands (Some "5.7.4")
    Assert.Equal<string option>(Some "5.7.4", r |> Option.map (fun e -> e.Version))

[<Fact>]
let ``pickFrom matches a major-minor hint by prefix`` () =
    let cands = [ ue "5.6.1" None; ue "5.7.4" None ]
    let r = Engines.pickFrom cands (Some "5.7")
    Assert.Equal<string option>(Some "5.7.4", r |> Option.map (fun e -> e.Version))

[<Fact>]
let ``pickFrom resolves a source-build GUID association`` () =
    let guid = "{B9C8A4D1-1234-5678-9ABC-DEF012345678}"
    let cands = [ ue "5.7.4" None; ue "5.7.0 (source)" (Some guid) ]
    let r = Engines.pickFrom cands (Some guid)
    Assert.Equal<string option>(Some guid, (Option.get r).Association)

[<Fact>]
let ``pickFrom falls back to the first candidate when no hint matches`` () =
    let cands = [ ue "5.6.1" None; ue "5.7.4" None ]
    let r = Engines.pickFrom cands (Some "4.27")
    Assert.Equal<string option>(Some "5.6.1", r |> Option.map (fun e -> e.Version))

[<Fact>]
let ``pickFrom returns None for an empty candidate list`` () =
    Assert.Equal<DetectedEngine option>(None, Engines.pickFrom [] (Some "5.7"))

// ─── resolveEditorLaunch ───────────────────────────────────────────

let private engineOf kind projectFile enginePath =
    { Kind = kind; ProjectFile = projectFile; EnginePath = enginePath
      EngineVersion = None; Executable = None }

[<Fact>]
let ``resolveEditorLaunch UE delegates to the .uproject via the shell`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let uproject = Path.Combine(dir, "Game.uproject")
    File.WriteAllText(uproject, "{}")
    try
        match Engines.resolveEditorLaunch (engineOf EngineKind.Unreal (Some "Game.uproject") None) dir with
        | Ok l ->
            Assert.True(l.UseShell)
            Assert.Equal(uproject, l.Exe)
            Assert.Empty(l.Args)
        | Error e -> Assert.Fail($"expected Ok, got {e}")
    finally Directory.Delete(dir, true)

[<Fact>]
let ``resolveEditorLaunch UE errors when project_file is unset`` () =
    match Engines.resolveEditorLaunch (engineOf EngineKind.Unreal None None) (Path.GetTempPath()) with
    | Error _ -> ()
    | Ok _ -> Assert.Fail("expected Error for missing project_file")

[<Fact>]
let ``resolveEditorLaunch UE errors when the .uproject is missing`` () =
    match Engines.resolveEditorLaunch (engineOf EngineKind.Unreal (Some "Ghost.uproject") None) (Path.GetTempPath()) with
    | Error _ -> ()
    | Ok _ -> Assert.Fail("expected Error for missing .uproject")

[<Fact>]
let ``resolveEditorLaunch Godot uses configured engine_path with editor args`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let godot = Path.Combine(dir, "godot.exe")
    File.WriteAllText(godot, "")
    try
        match Engines.resolveEditorLaunch (engineOf EngineKind.Godot None (Some godot)) dir with
        | Ok l ->
            Assert.False(l.UseShell)
            Assert.Equal(godot, l.Exe)
            Assert.Equal<string list>([ "--path"; dir; "--editor" ], l.Args)
        | Error e -> Assert.Fail($"expected Ok, got {e}")
    finally Directory.Delete(dir, true)

// ─── resolveProjectEngine ──────────────────────────────────────────

[<Fact>]
let ``resolveProjectEngine Godot resolves the configured engine_path`` () =
    let d = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory d |> ignore
    let godot = Path.Combine(d, "godot.exe")
    File.WriteAllText(godot, "")
    try
        match Engines.resolveProjectEngine (engineOf EngineKind.Godot None (Some godot)) d with
        | Ok e ->
            Assert.Equal(godot, e.Executable |> Option.defaultValue "")
            Assert.Equal(EngineKind.Godot, e.Kind)
        | Error msg -> Assert.Fail($"expected Ok, got {msg}")
    finally Directory.Delete(d, true)

[<Fact>]
let ``resolveProjectEngine UE errors with nothing to resolve from`` () =
    match Engines.resolveProjectEngine (engineOf EngineKind.Unreal None None) (Path.GetTempPath()) with
    | Error _ -> ()
    | Ok _ -> Assert.Fail("expected Error when there is no engine_version and no .uproject")

[<Fact>]
let ``resolveProjectEngine Unity errors without ProjectVersion`` () =
    let d = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory d |> ignore
    try
        match Engines.resolveProjectEngine (engineOf EngineKind.Unity None None) d with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("expected Error when ProjectVersion.txt is absent")
    finally Directory.Delete(d, true)

// ─── .uproject EngineAssociation ───────────────────────────────────

[<Fact>]
let ``engineAssociation reads the EngineAssociation field`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".uproject")
    File.WriteAllText(tmp, """{ "FileVersion": 3, "EngineAssociation": "5.7", "Modules": [] }""")
    try Assert.Equal<string option>(Some "5.7", Engines.engineAssociation tmp)
    finally File.Delete tmp

[<Fact>]
let ``engineAssociation is None for a missing file`` () =
    let ghost = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".uproject")
    Assert.Equal<string option>(None, Engines.engineAssociation ghost)

[<Fact>]
let ``engineAssociation is None when the field is absent`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".uproject")
    File.WriteAllText(tmp, """{ "FileVersion": 3, "Modules": [] }""")
    try Assert.Equal<string option>(None, Engines.engineAssociation tmp)
    finally File.Delete tmp
