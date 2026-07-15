module Takatora.Core.Tests.RunCommandTests

open Xunit
open Takatora.Core

[<Fact>]
let ``no params yields a bare run command`` () =
    Assert.Equal(
        "takatora run my-game build",
        RunCommand.build "my-game" "build" Map.empty Set.empty)

[<Fact>]
let ``scalar params round-trip through --var typing`` () =
    let ps =
        Map.ofList
            [ "branch", TString "main"
              "count", TInt 3L
              "clean", TBool true
              "ratio", TFloat 1.5 ]
    // Emitted in sorted key order: branch, clean, count, ratio.
    Assert.Equal(
        "takatora run g f --var branch=main --var clean=true --var count=3 --var ratio=1.5",
        RunCommand.build "g" "f" ps Set.empty)

[<Fact>]
let ``secret-named vars are omitted`` () =
    let ps = Map.ofList [ "branch", TString "main"; "token", TString "s3cr3t" ]
    Assert.Equal(
        "takatora run g f --var branch=main",
        RunCommand.build "g" "f" ps (Set.ofList [ "token" ]))

[<Fact>]
let ``a masked value is omitted even without the secret name`` () =
    // Defensive: the manifest stores "***" for secrets; never emit it.
    let ps = Map.ofList [ "branch", TString "main"; "token", TString "***" ]
    Assert.Equal(
        "takatora run g f --var branch=main",
        RunCommand.build "g" "f" ps Set.empty)

[<Fact>]
let ``a value with spaces is shell-quoted as a whole token`` () =
    let ps = Map.ofList [ "out", TString "My Builds/Win64" ]
    Assert.Equal(
        "takatora run g f --var \"out=My Builds/Win64\"",
        RunCommand.build "g" "f" ps Set.empty)

[<Fact>]
let ``project and flow names with spaces are quoted`` () =
    Assert.Equal(
        "takatora run \"My Game\" \"nightly build\"",
        RunCommand.build "My Game" "nightly build" Map.empty Set.empty)

[<Fact>]
let ``an embedded double quote is escaped`` () =
    let ps = Map.ofList [ "msg", TString "say \"hi\" now" ]
    Assert.Equal(
        "takatora run g f --var \"msg=say \\\"hi\\\" now\"",
        RunCommand.build "g" "f" ps Set.empty)

[<Fact>]
let ``an array value renders best-effort in bracket form`` () =
    let ps = Map.ofList [ "maps", TArray [ TString "Main"; TString "Boot" ] ]
    Assert.Equal(
        "takatora run g f --var \"maps=[Main, Boot]\"",
        RunCommand.build "g" "f" ps Set.empty)
