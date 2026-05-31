namespace Takatora.Gui.Tests

open System
open System.IO
open Xunit
open Takatora.Core
open Takatora.Gui.State

/// State tests construct Model values directly rather than going
/// through `init`, so they don't depend on (or mutate) the real
/// %APPDATA% project registry. The one `init` test only asserts
/// structural defaults — not the registry-derived Projects field.
///
/// Tests that exercise OpenProject's file I/O paths write a small
/// .ci/ tree under a per-instance tmp dir.
type StateTests() =

    let tmpRoot =
        Path.Combine(
            Path.GetTempPath(),
            "takatora-gui-state-tests",
            Guid.NewGuid().ToString("N"))
    do Directory.CreateDirectory(tmpRoot) |> ignore

    // ─── helpers ────────────────────────────────────────────────────

    /// `update` now returns Model * Cmd<Msg>. Tests overwhelmingly care
    /// about the model only; this helper strips the Cmd. Tests that want
    /// to assert Cmd behavior (none of them today) can call `update`
    /// directly.
    let apply (msg: Msg) (model: Model) : Model =
        update msg model |> fst

    let baseModel : Model =
        { OpenTabs       = [ Home ]
          ActiveTab      = Home
          Projects       = []
          ProjectSubTabs = Map.empty
          ProjectHistory = Map.empty
          ProjectFlows   = Map.empty
          ProjectInfo    = Map.empty
          RunDetails     = Map.empty
          LiveRuns       = Map.empty }

    let modelWithTabs (active: RootTab) (tabs: RootTab list) : Model =
        { baseModel with OpenTabs = tabs; ActiveTab = active }

    let fakeEntry (runId: string) : RunHistoryEntry =
        { RunId       = runId
          FlowId      = "smoke"
          StartedAt   = DateTimeOffset.UtcNow
          FinishedAt  = Some DateTimeOffset.UtcNow
          DurationSec = 1.0
          Result      = "success"
          Trigger     = "test"
          Params      = Map.empty
          RunDir      = "/tmp/fake" }

    /// Write a minimal `.ci/` tree under tmpRoot and return a
    /// ProjectRegistration pointing at it.
    let setupProjectDir (name: string) (flowsToml: string option) : ProjectRegistration =
        let pdir = Path.Combine(tmpRoot, name)
        let ci  = Path.Combine(pdir, ".ci")
        Directory.CreateDirectory(ci) |> ignore
        File.WriteAllText(
            Path.Combine(ci, "project.toml"),
            sprintf "[project]\nname=\"%s\"\nworking_dir=\".\"\n[engine]\ntype=\"godot\"\n" name)
        match flowsToml with
        | Some text -> File.WriteAllText(Path.Combine(ci, "flows.toml"), text)
        | None -> ()
        { Name = name; Path = pdir; AddedAt = DateTimeOffset.UtcNow }

    interface IDisposable with
        member _.Dispose() =
            try Directory.Delete(tmpRoot, recursive = true) with _ -> ()

    // ─── init ───────────────────────────────────────────────────────

    [<Fact>]
    member _.``init starts with Home active and empty per-tab caches`` () =
        // Projects depends on the machine's registry; assert only the
        // structural defaults that init owns.
        let m = init () |> fst
        Assert.Equal<RootTab list>([Home], m.OpenTabs)
        Assert.Equal(Home, m.ActiveTab)
        Assert.True(Map.isEmpty m.ProjectSubTabs)
        Assert.True(Map.isEmpty m.ProjectHistory)
        Assert.True(Map.isEmpty m.ProjectFlows)
        Assert.True(Map.isEmpty m.ProjectInfo)
        Assert.True(Map.isEmpty m.RunDetails)

    // ─── OpenProject ────────────────────────────────────────────────

    [<Fact>]
    member _.``OpenProject appends a new Project tab and focuses it`` () =
        let m = apply (OpenProject "p1") baseModel
        Assert.Equal<RootTab list>([Home; Project "p1"], m.OpenTabs)
        Assert.Equal(Project "p1", m.ActiveTab)

    [<Fact>]
    member _.``OpenProject on an already-open tab refocuses without duplicating`` () =
        let m =
            baseModel
            |> apply (OpenProject "p1")
            |> apply (OpenProject "p2")
            |> apply (OpenProject "p1")
        Assert.Equal<RootTab list>([Home; Project "p1"; Project "p2"], m.OpenTabs)
        Assert.Equal(Project "p1", m.ActiveTab)

    [<Fact>]
    member _.``OpenProject populates ProjectHistory and ProjectFlows when project is registered`` () =
        let entry =
            setupProjectDir "tp" (Some "[[flow]]\nid=\"smoke\"\nname=\"Smoke\"\n\n[[flow.steps]]\ntype=\"notify.console\"\n")
        let m =
            { baseModel with Projects = [ entry ] }
            |> apply (OpenProject "tp")
        // History dir doesn't exist → empty list cached
        Assert.Equal<RunHistoryEntry list>([], Map.find "tp" m.ProjectHistory)
        // flows.toml parsed → FlowsOk with one flow
        match Map.tryFind "tp" m.ProjectFlows with
        | Some (FlowsOk flows) ->
            Assert.Equal(1, List.length flows)
            Assert.Equal("smoke", flows.[0].Id)
        | other -> Assert.Fail(sprintf "expected FlowsOk, got %A" other)

    [<Fact>]
    member _.``OpenProject yields FlowsMissing when flows.toml is absent`` () =
        let entry = setupProjectDir "tp-no-flows" None
        let m =
            { baseModel with Projects = [ entry ] }
            |> apply (OpenProject "tp-no-flows")
        Assert.Equal(FlowsMissing, Map.find "tp-no-flows" m.ProjectFlows)

    [<Fact>]
    member _.``OpenProject yields FlowsError when flows.toml is malformed`` () =
        let entry = setupProjectDir "tp-bad-flows" (Some "this is not valid TOML [[")
        let m =
            { baseModel with Projects = [ entry ] }
            |> apply (OpenProject "tp-bad-flows")
        match Map.find "tp-bad-flows" m.ProjectFlows with
        | FlowsError _ -> ()
        | other -> Assert.Fail(sprintf "expected FlowsError, got %A" other)

    [<Fact>]
    member _.``OpenProject populates ProjectInfo with parsed project.toml`` () =
        let entry = setupProjectDir "tp-info" None
        let m =
            { baseModel with Projects = [ entry ] }
            |> apply (OpenProject "tp-info")
        match Map.tryFind "tp-info" m.ProjectInfo with
        | Some (ProjectInfoOk proj) ->
            Assert.Equal("tp-info", proj.Name)
            Assert.Equal(EngineKind.Godot, proj.Engine.Kind)
        | other -> Assert.Fail(sprintf "expected ProjectInfoOk, got %A" other)

    [<Fact>]
    member _.``OpenProject yields ProjectInfoMissing when project.toml absent`` () =
        // Create a registry entry pointing at a dir with no .ci/project.toml.
        let pdir = Path.Combine(tmpRoot, "tp-no-info")
        Directory.CreateDirectory(pdir) |> ignore
        let entry =
            { Name = "tp-no-info"; Path = pdir; AddedAt = DateTimeOffset.UtcNow }
        let m =
            { baseModel with Projects = [ entry ] }
            |> apply (OpenProject "tp-no-info")
        Assert.Equal(ProjectInfoMissing, Map.find "tp-no-info" m.ProjectInfo)

    [<Fact>]
    member _.``OpenProject yields ProjectInfoError when project.toml is malformed`` () =
        let pdir = Path.Combine(tmpRoot, "tp-bad-info")
        let ci = Path.Combine(pdir, ".ci")
        Directory.CreateDirectory(ci) |> ignore
        File.WriteAllText(Path.Combine(ci, "project.toml"), "garbage [[")
        let entry =
            { Name = "tp-bad-info"; Path = pdir; AddedAt = DateTimeOffset.UtcNow }
        let m =
            { baseModel with Projects = [ entry ] }
            |> apply (OpenProject "tp-bad-info")
        match Map.find "tp-bad-info" m.ProjectInfo with
        | ProjectInfoError _ -> ()
        | other -> Assert.Fail(sprintf "expected ProjectInfoError, got %A" other)

    // ─── ActivateTab ────────────────────────────────────────────────

    [<Fact>]
    member _.``ActivateTab to an open tab sets it as active`` () =
        let m =
            modelWithTabs Home [Home; Project "p1"; Project "p2"]
            |> apply (ActivateTab (Project "p2"))
        Assert.Equal(Project "p2", m.ActiveTab)

    [<Fact>]
    member _.``ActivateTab to a tab not in OpenTabs is a no-op`` () =
        let m0 = modelWithTabs Home [Home; Project "p1"]
        let m1 = apply (ActivateTab (Project "ghost")) m0
        Assert.Equal(Home, m1.ActiveTab)
        Assert.Equal<RootTab list>(m0.OpenTabs, m1.OpenTabs)

    // ─── CloseTab — close logic ─────────────────────────────────────

    [<Fact>]
    member _.``CloseTab Home is a no-op`` () =
        let m =
            modelWithTabs Home [Home; Project "p1"]
            |> apply (CloseTab Home)
        Assert.Equal<RootTab list>([Home; Project "p1"], m.OpenTabs)
        Assert.Equal(Home, m.ActiveTab)

    [<Fact>]
    member _.``CloseTab on a non-active tab leaves ActiveTab untouched`` () =
        let m =
            modelWithTabs (Project "p1") [Home; Project "p1"; Project "p2"]
            |> apply (CloseTab (Project "p2"))
        Assert.Equal<RootTab list>([Home; Project "p1"], m.OpenTabs)
        Assert.Equal(Project "p1", m.ActiveTab)

    [<Fact>]
    member _.``CloseTab on the active tab picks its right neighbor`` () =
        let m =
            modelWithTabs (Project "p2") [Home; Project "p1"; Project "p2"; Project "p3"]
            |> apply (CloseTab (Project "p2"))
        Assert.Equal<RootTab list>([Home; Project "p1"; Project "p3"], m.OpenTabs)
        Assert.Equal(Project "p3", m.ActiveTab)

    [<Fact>]
    member _.``CloseTab on the active rightmost tab falls back to the previous`` () =
        let m =
            modelWithTabs (Project "p2") [Home; Project "p1"; Project "p2"]
            |> apply (CloseTab (Project "p2"))
        Assert.Equal<RootTab list>([Home; Project "p1"], m.OpenTabs)
        Assert.Equal(Project "p1", m.ActiveTab)

    [<Fact>]
    member _.``CloseTab on the only non-Home active tab falls back to Home`` () =
        let m =
            modelWithTabs (Project "p1") [Home; Project "p1"]
            |> apply (CloseTab (Project "p1"))
        Assert.Equal<RootTab list>([Home], m.OpenTabs)
        Assert.Equal(Home, m.ActiveTab)

    // ─── CloseTab — cache cleanup ───────────────────────────────────

    [<Fact>]
    member _.``CloseTab Project drops only its per-project caches`` () =
        let m0 =
            { baseModel with
                OpenTabs       = [Home; Project "p1"; Project "p2"]
                ActiveTab      = Project "p1"
                ProjectSubTabs = Map.ofList [ "p1", ProjectHistory; "p2", ProjectFlows ]
                ProjectHistory = Map.ofList [ "p1", []; "p2", [] ]
                ProjectFlows   = Map.ofList [ "p1", FlowsMissing; "p2", FlowsMissing ]
                ProjectInfo    = Map.ofList [ "p1", ProjectInfoMissing; "p2", ProjectInfoMissing ]
                RunDetails     = Map.ofList [ ("p1", "r1"), (fakeEntry "r1", []) ] }
        let m = apply (CloseTab (Project "p1")) m0
        // p1's per-project caches gone
        Assert.False(Map.containsKey "p1" m.ProjectSubTabs)
        Assert.False(Map.containsKey "p1" m.ProjectHistory)
        Assert.False(Map.containsKey "p1" m.ProjectFlows)
        Assert.False(Map.containsKey "p1" m.ProjectInfo)
        // p2 untouched
        Assert.True(Map.containsKey "p2" m.ProjectSubTabs)
        Assert.True(Map.containsKey "p2" m.ProjectHistory)
        Assert.True(Map.containsKey "p2" m.ProjectFlows)
        Assert.True(Map.containsKey "p2" m.ProjectInfo)
        // RunDetails cache for p1's run is NOT dropped by closing the
        // Project tab — RunDetail tabs own that cache and may still
        // be open after the Project tab is gone.
        Assert.True(Map.containsKey ("p1", "r1") m.RunDetails)

    [<Fact>]
    member _.``CloseTab RunDetail drops only its own cache entry`` () =
        let m0 =
            { baseModel with
                OpenTabs       = [Home; RunDetail ("p1", "r1"); RunDetail ("p1", "r2")]
                ActiveTab      = RunDetail ("p1", "r1")
                ProjectSubTabs = Map.ofList [ "p1", ProjectHistory ]
                ProjectHistory = Map.ofList [ "p1", [] ]
                ProjectFlows   = Map.ofList [ "p1", FlowsMissing ]
                RunDetails =
                    Map.ofList [
                        ("p1", "r1"), (fakeEntry "r1", [])
                        ("p1", "r2"), (fakeEntry "r2", [])
                    ] }
        let m = apply (CloseTab (RunDetail ("p1", "r1"))) m0
        Assert.False(Map.containsKey ("p1", "r1") m.RunDetails)
        Assert.True (Map.containsKey ("p1", "r2") m.RunDetails)
        // Project caches untouched
        Assert.True(Map.containsKey "p1" m.ProjectSubTabs)
        Assert.True(Map.containsKey "p1" m.ProjectHistory)
        Assert.True(Map.containsKey "p1" m.ProjectFlows)

    // ─── ActivateSubTab / projectSubTab ─────────────────────────────

    [<Fact>]
    member _.``projectSubTab defaults to ProjectFlows when unset`` () =
        Assert.Equal(ProjectFlows, projectSubTab "p1" baseModel)

    [<Fact>]
    member _.``ActivateSubTab stores the new sub-tab choice`` () =
        let m =
            { baseModel with OpenTabs = [Home; Project "p1"] }
            |> apply (ActivateSubTab ("p1", ProjectHistory))
        Assert.Equal(ProjectHistory, projectSubTab "p1" m)

    // ─── OpenRunDetail ──────────────────────────────────────────────

    [<Fact>]
    member _.``OpenRunDetail appends a RunDetail tab and focuses it`` () =
        let m = apply (OpenRunDetail ("p1", "r1")) baseModel
        Assert.Equal<RootTab list>([Home; RunDetail ("p1", "r1")], m.OpenTabs)
        Assert.Equal(RunDetail ("p1", "r1"), m.ActiveTab)

    [<Fact>]
    member _.``OpenRunDetail on an already-open tab does not duplicate it`` () =
        let m =
            baseModel
            |> apply (OpenRunDetail ("p1", "r1"))
            |> apply (OpenRunDetail ("p1", "r2"))
            |> apply (OpenRunDetail ("p1", "r1"))
        Assert.Equal<RootTab list>(
            [Home; RunDetail ("p1", "r1"); RunDetail ("p1", "r2")],
            m.OpenTabs)
        Assert.Equal(RunDetail ("p1", "r1"), m.ActiveTab)

    // ─── Refresh* don't perturb tabs / active ───────────────────────

    [<Fact>]
    member _.``RefreshHistory leaves OpenTabs and ActiveTab untouched`` () =
        let m0 = modelWithTabs (Project "p1") [Home; Project "p1"]
        let m  = apply (RefreshHistory "p1") m0
        Assert.Equal<RootTab list>(m0.OpenTabs, m.OpenTabs)
        Assert.Equal(m0.ActiveTab, m.ActiveTab)

    [<Fact>]
    member _.``RefreshFlows leaves OpenTabs and ActiveTab untouched`` () =
        let m0 = modelWithTabs (Project "p1") [Home; Project "p1"]
        let m  = apply (RefreshFlows "p1") m0
        Assert.Equal<RootTab list>(m0.OpenTabs, m.OpenTabs)
        Assert.Equal(m0.ActiveTab, m.ActiveTab)

    [<Fact>]
    member _.``RefreshProjectInfo leaves OpenTabs and ActiveTab untouched`` () =
        let m0 = modelWithTabs (Project "p1") [Home; Project "p1"]
        let m  = apply (RefreshProjectInfo "p1") m0
        Assert.Equal<RootTab list>(m0.OpenTabs, m.OpenTabs)
        Assert.Equal(m0.ActiveTab, m.ActiveTab)

    // ─── RunFlow ────────────────────────────────────────────────────

    [<Fact>]
    member _.``RunFlow on a non-registered project is a graceful no-op`` () =
        // No project in registry means projectRoot returns None — Run.execute
        // must not be invoked, and the model is returned unchanged.
        let m = apply (RunFlow ("ghost", "smoke")) baseModel
        Assert.Equal<RootTab list>(baseModel.OpenTabs, m.OpenTabs)
        Assert.Equal(baseModel.ActiveTab, m.ActiveTab)
        Assert.True(Map.isEmpty m.ProjectHistory)
