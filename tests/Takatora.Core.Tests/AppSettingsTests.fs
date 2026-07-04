module Takatora.Core.Tests.AppSettingsTests

open System
open System.IO
open Xunit
open Takatora.Core

/// AppSettings reads/writes a machine-local TOML; point it at a temp file
/// via the test hook so we never touch the real %APPDATA% file.
let private withTempSettings (f: unit -> unit) =
    let path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".toml")
    AppSettings.setPathForTests path
    try f ()
    finally
        AppSettings.resetPath ()
        try File.Delete path with _ -> ()

[<Fact>]
let ``load returns empty when no file exists`` () =
    withTempSettings (fun () ->
        Assert.Equal<string option>(None, (AppSettings.load ()).IdeCommand))

[<Fact>]
let ``save then load round-trips the ide command`` () =
    withTempSettings (fun () ->
        AppSettings.save { AppSettings.empty with IdeCommand = Some "code \"{project_dir}\"" }
        Assert.Equal<string option>(Some "code \"{project_dir}\"", (AppSettings.load ()).IdeCommand))

[<Fact>]
let ``save None clears the ide command`` () =
    withTempSettings (fun () ->
        AppSettings.save { AppSettings.empty with IdeCommand = Some "rider64" }
        AppSettings.save { AppSettings.empty with IdeCommand = None }
        Assert.Equal<string option>(None, (AppSettings.load ()).IdeCommand))

[<Fact>]
let ``save then load round-trips Godot search paths`` () =
    withTempSettings (fun () ->
        AppSettings.save
            { AppSettings.empty with
                GodotSearchPaths = [ @"C:\Tools\Godot"; @"D:\Godot" ] }
        let s = AppSettings.load ()
        Assert.Equal<string list>([ @"C:\Tools\Godot"; @"D:\Godot" ], s.GodotSearchPaths))
