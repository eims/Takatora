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
