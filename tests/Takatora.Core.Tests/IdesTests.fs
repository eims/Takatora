module Takatora.Core.Tests.IdesTests

open Xunit
open Takatora.Core

// Detection touches the real filesystem / vswhere, so results depend on the
// machine. Assert structural invariants only (mirrors EnginesTests' stance).

[<Fact>]
let ``detect returns a list without throwing`` () =
    let xs = Ides.detect ()
    Assert.NotNull(xs :> obj)

[<Fact>]
let ``every detected candidate has a name, exe, and command`` () =
    for c in Ides.detect () do
        Assert.False(System.String.IsNullOrWhiteSpace c.Name, "candidate has empty Name")
        Assert.False(System.String.IsNullOrWhiteSpace c.Exe,  "candidate has empty Exe")
        Assert.False(System.String.IsNullOrWhiteSpace c.Command, "candidate has empty Command")
