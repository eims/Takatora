module Takatora.Core.Tests.ParamsTests

open Xunit
open Takatora.Core

// referencedIn is a pure static scan over a parsed Flow, so tests parse
// flows.toml snippets and assert on the collected names.

let private flowFrom (toml: string) : Flow =
    (TomlConfig.parseFlows toml).[0]

[<Fact>]
let ``referencedIn finds refs in step params including nested values`` () =
    let flow = flowFrom """
[[flow]]
id = "deploy"
[[flow.steps]]
type = "shell"
command = "steamcmd +login ${params.steam_username}"
args = ["--channel", "${params.build_channel}"]
[[flow.steps]]
type = "fs.write"
content = "plain text, ${vars.other}, ${env.PATH}"
"""
    Assert.Equal<Set<string>>(
        Set.ofList [ "steam_username"; "build_channel" ],
        Params.referencedIn flow)

[<Fact>]
let ``referencedIn finds refs in when expressions and var defaults`` () =
    let flow = flowFrom """
[[flow]]
id = "deploy"
[flow.vars]
target = { type = "string", default = "${params.default_target}" }
[[flow.steps]]
type = "notify.console"
when = "${params.notify_enabled}"
"""
    Assert.Equal<Set<string>>(
        Set.ofList [ "default_target"; "notify_enabled" ],
        Params.referencedIn flow)

[<Fact>]
let ``referencedIn is empty for a flow without params refs`` () =
    let flow = flowFrom """
[[flow]]
id = "smoke"
[[flow.steps]]
type = "notify.console"
message = "${vars.message}"
"""
    Assert.Equal<Set<string>>(Set.empty, Params.referencedIn flow)

[<Fact>]
let ``secretNames and nonSecretValues split declarations`` () =
    let ps = TomlConfig.parseParams """
[params]
studio_name    = { type = "string", value = "Foo" }
steam_password = { type = "secret" }
api_token      = { type = "secret" }
"""
    Assert.Equal<Set<string>>(
        Set.ofList [ "steam_password"; "api_token" ],
        Params.secretNames ps)
    Assert.Equal<Map<string, TomlValue>>(
        Map.ofList [ "studio_name", TString "Foo" ],
        Params.nonSecretValues ps)

[<Fact>]
let ``load returns empty for a project without params toml`` () =
    let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "takatora-params-" + string (System.Guid.NewGuid()))
    System.IO.Directory.CreateDirectory(dir) |> ignore
    try
        Assert.Equal<ProjectParam list>([], Params.load dir)
    finally
        System.IO.Directory.Delete(dir, true)
