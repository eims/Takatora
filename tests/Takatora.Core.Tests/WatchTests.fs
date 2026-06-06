module Takatora.Core.Tests.WatchTests

open System
open System.IO
open Xunit
open Takatora.Core

[<Fact>]
let ``gitHead is None for a non-repo directory`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    try Assert.Equal<string option>(None, Watch.gitHead dir)
    finally Directory.Delete(dir, true)

[<Fact>]
let ``gitHead is None for a missing directory`` () =
    let ghost = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Assert.Equal<string option>(None, Watch.gitHead ghost)
