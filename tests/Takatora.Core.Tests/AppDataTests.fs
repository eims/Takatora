module Takatora.Core.Tests.AppDataTests

open System
open System.IO
open Xunit
open Takatora.Core

/// TAKATORA_DATA_DIR overrides the machine-local base dir wholesale. Set +
/// restore around the assertion so the rest of the suite is unaffected.
let private withDataDir (value: string option) (f: unit -> unit) =
    let prev = Environment.GetEnvironmentVariable AppData.EnvVar
    match value with
    | Some v -> Environment.SetEnvironmentVariable(AppData.EnvVar, v)
    | None   -> Environment.SetEnvironmentVariable(AppData.EnvVar, null)
    try f ()
    finally Environment.SetEnvironmentVariable(AppData.EnvVar, prev)

[<Fact>]
let ``baseDir defaults under %APPDATA%/Takatora when unset`` () =
    withDataDir None (fun () ->
        let expected =
            Path.Combine(
                Environment.GetFolderPath Environment.SpecialFolder.ApplicationData, "Takatora")
        Assert.Equal(expected, AppData.baseDir ()))

[<Fact>]
let ``TAKATORA_DATA_DIR overrides the base dir`` () =
    let custom = Path.Combine(Path.GetTempPath(), "takatora-demo-" + Guid.NewGuid().ToString("N"))
    withDataDir (Some custom) (fun () ->
        Assert.Equal(custom, AppData.baseDir ()))

[<Fact>]
let ``an empty TAKATORA_DATA_DIR falls back to the default`` () =
    withDataDir (Some "") (fun () ->
        Assert.EndsWith("Takatora", AppData.baseDir ()))
