module Takatora.Core.Tests.DescribeTests

open Xunit
open Takatora.Core

// Describe.parse is pure (no fsi spawn); test it against representative JSON.

[<Fact>]
let ``parse reads type, params, kinds, default, values, filter, description`` () =
    let json = """
{
  "type": "demo.task",
  "params": [
    { "name": "configuration", "kind": "enum", "required": true,
      "values": ["Development","Shipping"], "description": "Build config" },
    { "name": "cfg", "kind": "file", "required": false, "default": "a.ini",
      "filter": ["*.ini"] },
    { "name": "count", "kind": "int", "required": false, "default": 3 }
  ],
  "outputs": ["archive_path","exe_path"]
}
"""
    let s = Describe.parse json
    Assert.Equal<string option>(Some "demo.task", s.Type)
    Assert.Equal(3, List.length s.Params)
    let cfgEnum = s.Params.[0]
    Assert.Equal("enum", cfgEnum.Kind)
    Assert.True(cfgEnum.Required)
    Assert.Equal<string option>(Some "Build config", cfgEnum.Description)
    Assert.Equal<string list option>(Some [ "Development"; "Shipping" ], cfgEnum.Values)
    let file = s.Params.[1]
    Assert.Equal("file", file.Kind)
    Assert.Equal<string option>(Some "a.ini", file.Default)
    Assert.Equal<string list option>(Some [ "*.ini" ], file.Filter)
    let count = s.Params.[2]
    Assert.Equal<string option>(Some "3", count.Default)   // non-string default → JSON text
    Assert.Equal<string list>([ "archive_path"; "exe_path" ], s.Outputs)

[<Fact>]
let ``parse tolerates malformed json with an empty schema`` () =
    let s = Describe.parse "not json {{"
    Assert.Equal<string option>(None, s.Type)
    Assert.True(List.isEmpty s.Params)
    Assert.True(List.isEmpty s.Outputs)
