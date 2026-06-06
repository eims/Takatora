namespace Takatora.Core.Tests

open Xunit
open Takatora.Core

module FlowsEditTests =

    let private mkVar name kind dflt : FlowVar = { Name = name; Kind = kind; Default = dflt; Description = None }

    let private sample = """# my flows — hand authored
[[flow]]
id = "release"

[flow.vars]
clean = { type = "bool", default = false }   # gate the clean step
configuration = { type = "enum", values = ["Development","Shipping"], default = "Shipping" }

[[flow.steps]]
type = "notify.console"

[[flow]]
id = "daily"

[flow.vars]
clean = { type = "bool", default = true }
"""

    [<Fact>]
    let ``setVarDefaults rewrites only the changed var, preserving the rest`` () =
        let newText, skipped =
            FlowsEdit.setVarDefaults sample "release"
                [ mkVar "clean" VarKind.Bool (Some (TBool false)), TBool true ]
        Assert.Empty(skipped)
        // The release flow's clean default flipped to true, trailing comment kept.
        Assert.Contains("clean = { type = \"bool\", default = true }   # gate the clean step", newText)
        // The OTHER flow's `clean` (daily) is untouched.
        let dailyIdx = newText.IndexOf("id = \"daily\"")
        Assert.Contains("clean = { type = \"bool\", default = true }", newText.Substring(dailyIdx))
        // Untouched lines / comments survive.
        Assert.Contains("# my flows — hand authored", newText)
        Assert.Contains("configuration = { type = \"enum\", values = [\"Development\",\"Shipping\"], default = \"Shipping\" }", newText)

    [<Fact>]
    let ``setVarDefaults preserves enum values + uses new default, and re-parses`` () =
        let newText, skipped =
            FlowsEdit.setVarDefaults sample "release"
                [ mkVar "configuration" (VarKind.Enum [ "Development"; "Shipping" ]) (Some (TString "Shipping")),
                  TString "Development" ]
        Assert.Empty(skipped)
        // Round-trips through the parser with the schema intact.
        let flows = TomlConfig.parseFlows newText
        let rel = flows |> List.find (fun f -> f.Id = "release")
        let cfg = rel.Vars |> List.find (fun v -> v.Name = "configuration")
        Assert.Equal(VarKind.Enum [ "Development"; "Shipping" ], cfg.Kind)
        Assert.Equal(Some (TString "Development"), cfg.Default)

    [<Fact>]
    let ``setVarDefaults reports a var it can't locate as skipped`` () =
        let _, skipped =
            FlowsEdit.setVarDefaults sample "release"
                [ mkVar "missing" VarKind.String None, TString "x" ]
        Assert.Equal<string list>([ "missing" ], skipped)

    [<Fact>]
    let ``setVarDefaults on an unknown flow changes nothing and skips all`` () =
        let newText, skipped =
            FlowsEdit.setVarDefaults sample "nope"
                [ mkVar "clean" VarKind.Bool None, TBool true ]
        Assert.Equal(sample, newText)
        Assert.Equal<string list>([ "clean" ], skipped)

    // ─── setStepParam ──────────────────────────────────────────────

    let private stepsSample = """[[flow]]
id = "release"

[[flow.steps]]
type = "ue.build_cook_run"
platform = "Win64"
target = "Game"  # ← your target

[[flow.steps]]
type = "ue.clean"
preset = "safe"
"""

    [<Fact>]
    let ``setStepParam replaces an existing param, keeping its inline comment`` () =
        let newText, ok = FlowsEdit.setStepParam stepsSample "release" 0 "target" (TString "MyGame")
        Assert.True(ok)
        Assert.Contains("target = \"MyGame\"  # ← your target", newText)
        // Other step untouched + re-parses.
        let steps = (TomlConfig.parseFlows newText).[0].Steps
        Assert.Equal(Some (TString "MyGame"), Map.tryFind "target" steps.[0].Params)
        Assert.Equal(Some (TString "safe"), Map.tryFind "preset" steps.[1].Params)

    [<Fact>]
    let ``setStepParam inserts a param that wasn't present`` () =
        let newText, ok = FlowsEdit.setStepParam stepsSample "release" 1 "build_output" (TString "Build")
        Assert.True(ok)
        let steps = (TomlConfig.parseFlows newText).[0].Steps
        Assert.Equal(Some (TString "Build"), Map.tryFind "build_output" steps.[1].Params)
        // Step 0 still has its params.
        Assert.Equal(Some (TString "Win64"), Map.tryFind "platform" steps.[0].Params)

    [<Fact>]
    let ``setStepParam refuses reserved keys and bad indices`` () =
        let _, okType = FlowsEdit.setStepParam stepsSample "release" 0 "type" (TString "x")
        Assert.False(okType)
        let _, okIdx = FlowsEdit.setStepParam stepsSample "release" 9 "platform" (TString "Mac")
        Assert.False(okIdx)
        let _, okFlow = FlowsEdit.setStepParam stepsSample "nope" 0 "platform" (TString "Mac")
        Assert.False(okFlow)

    // ─── add / remove / move steps ─────────────────────────────────

    let private stepTypes (text: string) =
        (TomlConfig.parseFlows text).[0].Steps |> List.map (fun s -> s.Type)

    [<Fact>]
    let ``removeStep deletes the targeted step, leaving the rest`` () =
        let newText, ok = FlowsEdit.removeStep stepsSample "release" 0
        Assert.True(ok)
        Assert.Equal<string list>([ "ue.clean" ], stepTypes newText)

    [<Fact>]
    let ``addStep appends a new step at the end`` () =
        let newText, ok = FlowsEdit.addStep stepsSample "release" "git.pull"
        Assert.True(ok)
        Assert.Equal<string list>([ "ue.build_cook_run"; "ue.clean"; "git.pull" ], stepTypes newText)

    [<Fact>]
    let ``moveStep up swaps a step with its predecessor`` () =
        let newText, ok = FlowsEdit.moveStep stepsSample "release" 1 -1
        Assert.True(ok)
        Assert.Equal<string list>([ "ue.clean"; "ue.build_cook_run" ], stepTypes newText)
        // The moved build step keeps its params.
        let steps = (TomlConfig.parseFlows newText).[0].Steps
        Assert.Equal(Some (TString "Win64"), Map.tryFind "platform" steps.[1].Params)

    [<Fact>]
    let ``moveStep down swaps a step with its successor`` () =
        let newText, ok = FlowsEdit.moveStep stepsSample "release" 0 1
        Assert.True(ok)
        Assert.Equal<string list>([ "ue.clean"; "ue.build_cook_run" ], stepTypes newText)

    [<Fact>]
    let ``moveStep refuses out-of-range moves`` () =
        let _, okUp = FlowsEdit.moveStep stepsSample "release" 0 -1
        Assert.False(okUp)
        let _, okDown = FlowsEdit.moveStep stepsSample "release" 1 1
        Assert.False(okDown)

    [<Fact>]
    let ``addStep inserts before a comment that introduces the next flow`` () =
        let sample = "[[flow]]\nid = \"a\"\n\n[[flow.steps]]\ntype = \"s1\"\n\n# next flow\n[[flow]]\nid = \"b\"\n\n[[flow.steps]]\ntype = \"s2\"\n"
        let newText, ok = FlowsEdit.addStep sample "a" "added"
        Assert.True(ok)
        let flows = TomlConfig.parseFlows newText
        let a = flows |> List.find (fun f -> f.Id = "a")
        let b = flows |> List.find (fun f -> f.Id = "b")
        Assert.Equal<string list>([ "s1"; "added" ], a.Steps |> List.map (fun s -> s.Type))
        Assert.Equal<string list>([ "s2" ], b.Steps |> List.map (fun s -> s.Type))
        // The next-flow comment still sits right above that flow's header.
        Assert.Contains("# next flow\n[[flow]]\nid = \"b\"", newText)
