module Takatora.Core.Tests.GrantsTests

open System
open System.IO
open Xunit
open Takatora.Core

// ─── FlowHash ──────────────────────────────────────────────────────

let private flowFrom (toml: string) : Flow =
    (TomlConfig.parseFlows toml).[0]

let private baseFlowToml = """
# deploy flow
[[flow]]
id = "deploy"
name = "Deploy build"

[flow.vars]
channel = { type = "enum", values = ["beta", "release"], default = "beta" }

[[flow.steps]]
id = "upload"
type = "shell"
command = "steamcmd +login ${params.steam_username}"
"""

[<Fact>]
let ``FlowHash is stable across comment, whitespace, and key-order edits`` () =
    let reformatted = """
[[flow]]
name = "Deploy build"
id = "deploy"


[flow.vars]
channel = { default = "beta", type = "enum", values = ["beta", "release"] }

# upload to steam
[[flow.steps]]
type = "shell"
command = "steamcmd +login ${params.steam_username}"
id = "upload"
"""
    Assert.Equal(FlowHash.compute (flowFrom baseFlowToml),
                 FlowHash.compute (flowFrom reformatted))

[<Fact>]
let ``FlowHash changes on semantic edits`` () =
    let baseHash = FlowHash.compute (flowFrom baseFlowToml)
    let changedParam = baseFlowToml.Replace("+login", "+login_other")
    let changedWhen = baseFlowToml.Replace("id = \"upload\"\ntype", "id = \"upload\"\nwhen = \"${vars.flag}\"\ntype")
    let changedDefault = baseFlowToml.Replace("default = \"beta\"", "default = \"release\"")
    Assert.NotEqual<string>(baseHash, FlowHash.compute (flowFrom changedParam))
    Assert.NotEqual<string>(baseHash, FlowHash.compute (flowFrom changedWhen))
    Assert.NotEqual<string>(baseHash, FlowHash.compute (flowFrom changedDefault))

[<Fact>]
let ``FlowHash has the sha256 prefix shape`` () =
    let h = FlowHash.compute (flowFrom baseFlowToml)
    Assert.StartsWith("sha256:", h)
    Assert.Equal("sha256:".Length + 64, h.Length)

// ─── Grants store ──────────────────────────────────────────────────

/// Redirect the store to a fresh tmp file for the duration of one test.
let private withTempStore (action: unit -> unit) =
    let path = Path.Combine(Path.GetTempPath(), "takatora-grants-" + string (Guid.NewGuid()) + ".toml")
    Grants.setPathForTests path
    try action ()
    finally
        Grants.clearPathOverride ()
        if File.Exists path then File.Delete path

let private grantFor (projectPath: string) (flowId: string) (param: string) (hash: string) : SecretGrant =
    { ProjectPath = projectPath
      ProjectName = "sample"
      FlowId = flowId
      Param = param
      FlowHash = hash
      GrantedAt = DateTimeOffset.UtcNow
      Source = "flows" }

[<Fact>]
let ``record then load round-trips a grant`` () =
    withTempStore (fun () ->
        Grants.record (grantFor "D:\\proj" "deploy" "steam_password" "sha256:aa")
        match Grants.load () with
        | [ g ] ->
            Assert.Equal(Grants.normalizePath "D:\\proj", g.ProjectPath)
            Assert.Equal("deploy", g.FlowId)
            Assert.Equal("steam_password", g.Param)
            Assert.Equal("sha256:aa", g.FlowHash)
            Assert.Equal("flows", g.Source)
        | other -> Assert.Fail($"expected one grant, got %A{other}"))

[<Fact>]
let ``record replaces an existing grant for the same key`` () =
    withTempStore (fun () ->
        Grants.record (grantFor "D:\\proj" "deploy" "steam_password" "sha256:old")
        Grants.record (grantFor "D:\\proj\\" "deploy" "steam_password" "sha256:new")
        match Grants.load () with
        | [ g ] -> Assert.Equal("sha256:new", g.FlowHash)
        | other -> Assert.Fail($"expected one grant, got %A{other}"))

[<Fact>]
let ``check reports notGranted, stale, and granted`` () =
    withTempStore (fun () ->
        let flow = flowFrom baseFlowToml
        let hash = FlowHash.compute flow
        Grants.record (grantFor "D:\\proj" flow.Id "granted_param" hash)
        Grants.record (grantFor "D:\\proj" flow.Id "stale_param" "sha256:outdated")
        let result = Grants.check "D:\\proj" flow (Set.ofList [ "granted_param"; "stale_param"; "new_param" ])
        Assert.Equal<string list>([ "new_param" ], result.NotGranted)
        Assert.Equal<string list>([ "stale_param" ], result.Stale)
        Assert.Contains("granted_param", result.Required))

[<Fact>]
let ``check matches project paths case-insensitively`` () =
    withTempStore (fun () ->
        let flow = flowFrom baseFlowToml
        Grants.record (grantFor "D:\\Proj" flow.Id "p" (FlowHash.compute flow))
        let result = Grants.check "d:\\proj" flow (Set.ofList [ "p" ])
        Assert.Empty(result.NotGranted)
        Assert.Empty(result.Stale))

[<Fact>]
let ``revoke removes by project, flow, and param filters`` () =
    withTempStore (fun () ->
        Grants.record (grantFor "D:\\proj" "deploy" "a" "sha256:x")
        Grants.record (grantFor "D:\\proj" "deploy" "b" "sha256:x")
        Grants.record (grantFor "D:\\proj" "publish" "a" "sha256:x")
        Grants.record (grantFor "D:\\other" "deploy" "a" "sha256:x")
        Assert.Equal(1, Grants.revoke "D:\\proj" (Some "deploy") (Some "a"))
        Assert.Equal(1, Grants.revoke "D:\\proj" (Some "publish") None)
        Assert.Equal(1, Grants.revoke "D:\\proj" None None)
        Assert.Equal(1, List.length (Grants.load ())))

[<Fact>]
let ``load tolerates a corrupt store by returning no grants`` () =
    withTempStore (fun () ->
        File.WriteAllText(Grants.grantsPath (), "not = = valid toml")
        Assert.Equal<SecretGrant list>([], Grants.load ()))
