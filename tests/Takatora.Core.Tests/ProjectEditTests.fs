module Takatora.Core.Tests.ProjectEditTests

open Xunit
open Takatora.Core

let private sample = """[project]
name = "g"
working_dir = "."

[engine]
type = "godot"
# engine_path = ""        # uncomment to override autodetection

[vcs]
type = "git"
"""

[<Fact>]
let ``setEnginePath replaces a commented-out engine_path in place`` () =
    let out = ProjectEdit.setEnginePath sample @"C:\Godot\godot.exe"
    Assert.Contains("engine_path = \"C:\\\\Godot\\\\godot.exe\"", out)
    // The commented placeholder is gone; other lines untouched.
    Assert.DoesNotContain("# engine_path", out)
    Assert.Contains("type = \"godot\"", out)
    Assert.Contains("[vcs]", out)
    // And it parses back to the value we set.
    let proj = TomlConfig.parseProject out
    Assert.Equal<string option>(Some @"C:\Godot\godot.exe", proj.Engine.EnginePath)

[<Fact>]
let ``setEnginePath inserts under [engine] when absent`` () =
    let text = "[project]\nname = \"g\"\nworking_dir = \".\"\n\n[engine]\ntype = \"godot\"\n"
    let out = ProjectEdit.setEnginePath text @"D:\GDStudio\gdstudio.exe"
    let proj = TomlConfig.parseProject out
    Assert.Equal<string option>(Some @"D:\GDStudio\gdstudio.exe", proj.Engine.EnginePath)

[<Fact>]
let ``setEnginePath preserves CRLF line endings`` () =
    let crlf = sample.Replace("\n", "\r\n")
    let out = ProjectEdit.setEnginePath crlf @"C:\Godot\godot.exe"
    Assert.Contains("\r\n", out)
    // After stripping CRLF pairs, no lone LF should remain.
    Assert.DoesNotContain("\n", out.Replace("\r\n", ""))
