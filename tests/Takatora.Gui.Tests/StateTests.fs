namespace Takatora.Gui.Tests

open System
open System.IO
open System.Collections.Generic
open Xunit
open Takatora.Core
open Takatora.Gui.State

/// In-memory secret store so dialog secret tests never touch the real
/// OS keychain.
type private InMemorySecretStore() =
    let d = Dictionary<string, string>()
    interface ISecretStore with
        member _.Read(key) = match d.TryGetValue key with | true, v -> Some v | _ -> None
        member _.Write(key, value) = d.[key] <- value
        member _.Delete(key) = d.Remove key
        member _.List(prefix) =
            [ for kv in d do if kv.Key.StartsWith(prefix, StringComparison.Ordinal) then yield kv.Key ]

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
    // Keep the run dialog's secret path off the real keychain.
    do Secrets.setBackendForTests (InMemorySecretStore() :> ISecretStore)

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
          ProjectSecrets = Map.empty
          RunDetails     = Map.empty
          LiveRuns       = Map.empty
          AddProject     = None
          CurrentProject = None
          RunDialog      = None }

    let modelWithTabs (active: RootTab) (tabs: RootTab list) : Model =
        { baseModel with OpenTabs = tabs; ActiveTab = active }

    /// A model scoped to one project with several of its own tabs open.
    let scopedToP1 (active: RootTab) : Model =
        { modelWithTabs active
            [Home; Project "p1"; RunDetail ("p1", "r1"); RunDetail ("p1", "r2")]
            with CurrentProject = Some "p1" }

    /// p1 with three run tabs open, scoped to p1.
    let scopedThreeRuns (active: RootTab) : Model =
        { modelWithTabs active
            [ Home; Project "p1"
              RunDetail ("p1", "r1"); RunDetail ("p1", "r2"); RunDetail ("p1", "r3") ]
            with CurrentProject = Some "p1" }

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

    let fakeEntryFlow (runId: string) (flowId: string) : RunHistoryEntry =
        { fakeEntry runId with FlowId = flowId }

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

    let mkVar (name: string) (kind: VarKind) (dflt: TomlValue option) : FlowVar =
        { Name = name; Kind = kind; Default = dflt }

    /// A model with one project flow cached, carrying the given vars.
    let modelWithFlow (pid: string) (flowId: string) (vars: FlowVar list) : Model =
        { baseModel with
            ProjectFlows =
                Map.ofList [ pid, FlowsOk [ { Id = flowId; Name = None; Vars = vars; Steps = [] } ] ] }

    interface IDisposable with
        member _.Dispose() =
            Secrets.resetBackend ()
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
            scopedToP1 (Project "p1")
            |> apply (CloseTab (RunDetail ("p1", "r2")))
        Assert.Equal<RootTab list>(
            [Home; Project "p1"; RunDetail ("p1", "r1")], m.OpenTabs)
        Assert.Equal(Project "p1", m.ActiveTab)
        Assert.Equal<ProjectId option>(Some "p1", m.CurrentProject)

    [<Fact>]
    member _.``CloseTab on the active tab picks its right neighbor in-context`` () =
        let m =
            scopedToP1 (RunDetail ("p1", "r1"))
            |> apply (CloseTab (RunDetail ("p1", "r1")))
        Assert.Equal(RunDetail ("p1", "r2"), m.ActiveTab)
        Assert.Equal<ProjectId option>(Some "p1", m.CurrentProject)

    [<Fact>]
    member _.``CloseTab on the active rightmost tab falls back to the previous`` () =
        let m =
            scopedToP1 (RunDetail ("p1", "r2"))
            |> apply (CloseTab (RunDetail ("p1", "r2")))
        Assert.Equal(RunDetail ("p1", "r1"), m.ActiveTab)
        Assert.Equal<ProjectId option>(Some "p1", m.CurrentProject)

    [<Fact>]
    member _.``CloseTab on a project's last tab returns to no-project Home`` () =
        let m =
            { modelWithTabs (Project "p1") [Home; Project "p1"]
                with CurrentProject = Some "p1" }
            |> apply (CloseTab (Project "p1"))
        Assert.Equal<RootTab list>([Home], m.OpenTabs)
        Assert.Equal(Home, m.ActiveTab)
        Assert.Equal<ProjectId option>(None, m.CurrentProject)

    [<Fact>]
    member _.``CloseTab of a context's last tab goes to Home, not a sibling project`` () =
        // p1 (active, single tab) and p2 both open. Closing p1 must NOT
        // jump focus into p2 — it returns to the neutral no-project state,
        // leaving p2 open and reachable via the selector.
        let m =
            { modelWithTabs (Project "p1") [Home; Project "p1"; Project "p2"]
                with CurrentProject = Some "p1" }
            |> apply (CloseTab (Project "p1"))
        Assert.Equal<RootTab list>([Home; Project "p2"], m.OpenTabs)
        Assert.Equal(Home, m.ActiveTab)
        Assert.Equal<ProjectId option>(None, m.CurrentProject)

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

    // ─── Per-project tab scoping ────────────────────────────────────

    [<Fact>]
    member _.``tabProject maps each tab kind to its owning project`` () =
        Assert.Equal<ProjectId option>(None,        tabProject baseModel Home)
        Assert.Equal<ProjectId option>(Some "p1",   tabProject baseModel (Project "p1"))
        Assert.Equal<ProjectId option>(Some "p1",   tabProject baseModel (RunDetail ("p1", "r1")))

    [<Fact>]
    member _.``currentProject reflects the sticky CurrentProject field`` () =
        Assert.Equal<ProjectId option>(None, currentProject baseModel)
        Assert.Equal<ProjectId option>(
            Some "p1",
            currentProject { baseModel with CurrentProject = Some "p1" })

    [<Fact>]
    member _.``OpenProject sets the sticky context``  () =
        let m = apply (OpenProject "p1") baseModel
        Assert.Equal<ProjectId option>(Some "p1", m.CurrentProject)

    [<Fact>]
    member _.``Activating Home keeps the project context sticky`` () =
        let m =
            baseModel
            |> apply (OpenProject "p1")
            |> apply (ActivateTab Home)
        Assert.Equal(Home, m.ActiveTab)
        Assert.Equal<ProjectId option>(Some "p1", m.CurrentProject)

    [<Fact>]
    member _.``Activating a project's tab switches the context`` () =
        let m =
            baseModel
            |> apply (OpenProject "p1")
            |> apply (OpenProject "p2")
            |> apply (ActivateTab (Project "p1"))
        Assert.Equal<ProjectId option>(Some "p1", m.CurrentProject)

    [<Fact>]
    member _.``openProjects lists distinct contexts in first-seen order`` () =
        let m =
            modelWithTabs Home
                [Home; Project "p1"; RunDetail ("p1", "r1"); Project "p2"]
        Assert.Equal<ProjectId list>(["p1"; "p2"], openProjects m)

    [<Fact>]
    member _.``visibleTabs shows only Home when no context is set`` () =
        let m = modelWithTabs Home [Home; Project "p1"; Project "p2"]
        Assert.Equal<RootTab list>([Home], visibleTabs m)

    [<Fact>]
    member _.``visibleTabs scopes to the current project's tabs plus Home`` () =
        let m =
            { modelWithTabs (Project "p1")
                [Home; Project "p1"; RunDetail ("p1", "r1"); Project "p2"; RunDetail ("p2", "r2")]
                with CurrentProject = Some "p1" }
        Assert.Equal<RootTab list>(
            [Home; Project "p1"; RunDetail ("p1", "r1")],
            visibleTabs m)

    [<Fact>]
    member _.``closing the last tab of a context falls back off that project`` () =
        // p1 has only its Project tab open; closing it should drop the
        // sticky context (no p1 tabs remain) rather than keep pointing at p1.
        let m =
            { modelWithTabs (Project "p1") [Home; Project "p1"]
                with CurrentProject = Some "p1" }
            |> apply (CloseTab (Project "p1"))
        Assert.Equal<ProjectId option>(None, m.CurrentProject)

    // ─── RunFlow ────────────────────────────────────────────────────

    [<Fact>]
    member _.``RunFlow on a non-registered project is a graceful no-op`` () =
        // No project in registry means projectRoot returns None — Run.execute
        // must not be invoked, and the model is returned unchanged.
        let m = apply (RunFlow ("ghost", "smoke")) baseModel
        Assert.Equal<RootTab list>(baseModel.OpenTabs, m.OpenTabs)
        Assert.Equal(baseModel.ActiveTab, m.ActiveTab)
        Assert.True(Map.isEmpty m.ProjectHistory)

    // ─── run numbering / flow lookup (tab labels) ───────────────────

    [<Fact>]
    member _.``runNumber is chronological: oldest is #1, newest is #N`` () =
        // History is stored newest-first (RunHistory.load sorts descending),
        // so [r3; r2; r1] means r1 is the oldest → #1, r3 the newest → #3.
        let m =
            { baseModel with
                ProjectHistory =
                    Map.ofList [ "p1", [ fakeEntry "r3"; fakeEntry "r2"; fakeEntry "r1" ] ] }
        Assert.Equal<int option>(Some 1, runNumber m "p1" "r1")
        Assert.Equal<int option>(Some 2, runNumber m "p1" "r2")
        Assert.Equal<int option>(Some 3, runNumber m "p1" "r3")

    [<Fact>]
    member _.``runNumber is None when the project or run is not cached`` () =
        let m =
            { baseModel with
                ProjectHistory = Map.ofList [ "p1", [ fakeEntry "r1" ] ] }
        Assert.Equal<int option>(None, runNumber m "p1" "missing")
        Assert.Equal<int option>(None, runNumber m "p2" "r1")

    [<Fact>]
    member _.``runFlowId returns the recorded flow, None when absent`` () =
        let m =
            { baseModel with
                ProjectHistory =
                    Map.ofList [ "p1", [ fakeEntryFlow "r1" "build"; fakeEntryFlow "r2" "test" ] ] }
        Assert.Equal<string option>(Some "build", runFlowId m "p1" "r1")
        Assert.Equal<string option>(Some "test", runFlowId m "p1" "r2")
        Assert.Equal<string option>(None, runFlowId m "p1" "missing")

    [<Fact>]
    member _.``runByNumberOffset steps to the next-newer (+1) and next-older (-1) run`` () =
        // newest-first [r3(#3); r2(#2); r1(#1)]. From r2: +1 → r3, -1 → r1.
        let m =
            { baseModel with
                ProjectHistory =
                    Map.ofList [ "p1", [ fakeEntry "r3"; fakeEntry "r2"; fakeEntry "r1" ] ] }
        Assert.Equal<RunId option>(Some "r3", runByNumberOffset m "p1" "r2" 1)
        Assert.Equal<RunId option>(Some "r1", runByNumberOffset m "p1" "r2" -1)

    [<Fact>]
    member _.``runByNumberOffset is None at the ends of history`` () =
        let m =
            { baseModel with
                ProjectHistory =
                    Map.ofList [ "p1", [ fakeEntry "r3"; fakeEntry "r2"; fakeEntry "r1" ] ] }
        // r3 is newest (#3) → no newer; r1 is oldest (#1) → no older.
        Assert.Equal<RunId option>(None, runByNumberOffset m "p1" "r3" 1)
        Assert.Equal<RunId option>(None, runByNumberOffset m "p1" "r1" -1)
        Assert.Equal<RunId option>(None, runByNumberOffset m "p1" "missing" 1)

    // ─── bulk tab close (others / to the right) ─────────────────────

    [<Fact>]
    member _.``CloseOtherTabs keeps Home, project root, and the target; target becomes active`` () =
        let m = apply (CloseOtherTabs (RunDetail ("p1", "r2"))) (scopedThreeRuns (RunDetail ("p1", "r1")))
        Assert.Equal<RootTab list>(
            [ Home; Project "p1"; RunDetail ("p1", "r2") ], m.OpenTabs)
        Assert.Equal(RunDetail ("p1", "r2"), m.ActiveTab)
        Assert.Equal<ProjectId option>(Some "p1", m.CurrentProject)

    [<Fact>]
    member _.``CloseTabsToRight closes only tabs after the target in strip order`` () =
        // Target r1 is first run; r2 and r3 sit to its right and close.
        let m = apply (CloseTabsToRight (RunDetail ("p1", "r1"))) (scopedThreeRuns (RunDetail ("p1", "r1")))
        Assert.Equal<RootTab list>(
            [ Home; Project "p1"; RunDetail ("p1", "r1") ], m.OpenTabs)

    [<Fact>]
    member _.``CloseTabsToRight on the rightmost run is a no-op`` () =
        let start = scopedThreeRuns (RunDetail ("p1", "r3"))
        let m = apply (CloseTabsToRight (RunDetail ("p1", "r3"))) start
        Assert.Equal<RootTab list>(start.OpenTabs, m.OpenTabs)

    [<Fact>]
    member _.``CloseTabsToRight reassigns the active tab when it was to the right`` () =
        // Active r3 is closed by "close right of r1" → active falls back into
        // the surviving context (never Home while p1 tabs remain).
        let m = apply (CloseTabsToRight (RunDetail ("p1", "r1"))) (scopedThreeRuns (RunDetail ("p1", "r3")))
        Assert.Equal<RootTab list>(
            [ Home; Project "p1"; RunDetail ("p1", "r1") ], m.OpenTabs)
        Assert.Equal(RunDetail ("p1", "r1"), m.ActiveTab)
        Assert.Equal<ProjectId option>(Some "p1", m.CurrentProject)

    // ─── run-with-parameters dialog ─────────────────────────────────

    [<Fact>]
    member _.``isDialogVarKind accepts every kind`` () =
        Assert.True(isDialogVarKind VarKind.String)
        Assert.True(isDialogVarKind VarKind.Int)
        Assert.True(isDialogVarKind VarKind.Bool)
        Assert.True(isDialogVarKind (VarKind.Enum [ "a"; "b" ]))
        Assert.True(isDialogVarKind VarKind.Path)
        Assert.True(isDialogVarKind VarKind.File)
        Assert.True(isDialogVarKind VarKind.Dir)
        Assert.True(isDialogVarKind VarKind.Multiline)
        Assert.True(isDialogVarKind VarKind.Secret)
        Assert.True(isDialogVarKind (VarKind.List VarKind.String))

    [<Fact>]
    member _.``varDefaultText renders defaults and falls back sensibly`` () =
        Assert.Equal("main",  varDefaultText (mkVar "b" VarKind.String (Some (TString "main"))))
        Assert.Equal("3",     varDefaultText (mkVar "n" VarKind.Int (Some (TInt 3L))))
        Assert.Equal("true",  varDefaultText (mkVar "c" VarKind.Bool (Some (TBool true))))
        // No default: bool → "false", enum → first value, others → "".
        Assert.Equal("false", varDefaultText (mkVar "c" VarKind.Bool None))
        Assert.Equal("Win64", varDefaultText (mkVar "p" (VarKind.Enum [ "Win64"; "Linux" ]) None))
        Assert.Equal("",      varDefaultText (mkVar "s" VarKind.String None))

    [<Fact>]
    member _.``RequestRun with no vars at all does not open the dialog`` () =
        // A flow that declares no vars → nothing to fill in → runs directly.
        let model = modelWithFlow "p1" "build" []
        let m = apply (RequestRun ("p1", "build")) model
        Assert.Equal<RunDialogState option>(None, m.RunDialog)

    [<Fact>]
    member _.``RequestRun opens the dialog for path and multiline vars`` () =
        let vars =
            [ mkVar "out"  VarKind.Path (Some (TString "Build"))
              mkVar "body" VarKind.Multiline None ]
        let m = apply (RequestRun ("p1", "deploy")) (modelWithFlow "p1" "deploy" vars)
        match m.RunDialog with
        | Some d ->
            Assert.Equal<string list>([ "out"; "body" ], d.Vars |> List.map (fun v -> v.Name))
            Assert.Equal("Build", Map.find "out" d.Values)
            Assert.Equal("", Map.find "body" d.Values)
        | None -> Assert.True(false, "dialog should be open")

    [<Fact>]
    member _.``RequestRun with scalar vars opens the dialog pre-filled with defaults`` () =
        let vars =
            [ mkVar "branch" VarKind.String (Some (TString "main"))
              mkVar "clean"  VarKind.Bool   (Some (TBool false)) ]
        let m = apply (RequestRun ("p1", "release")) (modelWithFlow "p1" "release" vars)
        match m.RunDialog with
        | None -> Assert.True(false, "dialog should be open")
        | Some d ->
            Assert.Equal("p1", d.ProjectId)
            Assert.Equal("release", d.FlowId)
            Assert.Equal<string option>(None, d.Error)
            Assert.Equal("main",  Map.find "branch" d.Values)
            Assert.Equal("false", Map.find "clean" d.Values)

    [<Fact>]
    member _.``RequestRun keeps every var kind including list`` () =
        let vars =
            [ mkVar "branch" VarKind.String None
              mkVar "maps"   (VarKind.List VarKind.String) None
              mkVar "token"  VarKind.Secret None ]
        let m = apply (RequestRun ("p1", "release")) (modelWithFlow "p1" "release" vars)
        match m.RunDialog with
        | Some d -> Assert.Equal<string list>([ "branch"; "maps"; "token" ], d.Vars |> List.map (fun v -> v.Name))
        | None   -> Assert.True(false, "dialog should be open")

    [<Fact>]
    member _.``RunDialogSetValue updates the value and clears the error`` () =
        let vars = [ mkVar "n" VarKind.Int (Some (TInt 1L)) ]
        let opened = apply (RequestRun ("p1", "f")) (modelWithFlow "p1" "f" vars)
        // Force an error first.
        let errored = apply RunDialogConfirm { opened with RunDialog = opened.RunDialog |> Option.map (fun d -> { d with Values = Map.add "n" "oops" d.Values }) }
        Assert.True((errored.RunDialog |> Option.bind (fun d -> d.Error)).IsSome)
        let fixed' = apply (RunDialogSetValue ("n", "42")) errored
        match fixed'.RunDialog with
        | Some d ->
            Assert.Equal("42", Map.find "n" d.Values)
            Assert.Equal<string option>(None, d.Error)
        | None -> Assert.True(false, "dialog should still be open")

    [<Fact>]
    member _.``RunDialogReset restores defaults and clears the error`` () =
        let vars = [ mkVar "branch" VarKind.String (Some (TString "main")) ]
        let opened = apply (RequestRun ("p1", "f")) (modelWithFlow "p1" "f" vars)
        let edited = apply (RunDialogSetValue ("branch", "dev")) opened
        let m = apply RunDialogReset edited
        match m.RunDialog with
        | Some d ->
            Assert.Equal("main", Map.find "branch" d.Values)
            Assert.Equal<string option>(None, d.Error)
        | None -> Assert.True(false, "dialog should still be open")

    [<Fact>]
    member _.``RunDialogCancel closes the dialog`` () =
        let vars = [ mkVar "branch" VarKind.String None ]
        let opened = apply (RequestRun ("p1", "f")) (modelWithFlow "p1" "f" vars)
        let m = apply RunDialogCancel opened
        Assert.Equal<RunDialogState option>(None, m.RunDialog)

    [<Fact>]
    member _.``RunDialogConfirm with an invalid number keeps the dialog open with an error`` () =
        let vars = [ mkVar "n" VarKind.Int (Some (TInt 1L)) ]
        let opened = apply (RequestRun ("p1", "f")) (modelWithFlow "p1" "f" vars)
        let bad = { opened with RunDialog = opened.RunDialog |> Option.map (fun d -> { d with Values = Map.add "n" "abc" d.Values }) }
        let m = apply RunDialogConfirm bad
        match m.RunDialog with
        | Some d -> Assert.True(d.Error.IsSome)
        | None   -> Assert.True(false, "dialog should stay open on a parse error")

    [<Fact>]
    member _.``RunDialogConfirm with valid values closes the dialog`` () =
        // No registered project → the run itself no-ops, but the dialog
        // must still close on a successful parse.
        let vars = [ mkVar "n" VarKind.Int (Some (TInt 1L)) ]
        let opened = apply (RequestRun ("p1", "f")) (modelWithFlow "p1" "f" vars)
        let m = apply RunDialogConfirm opened
        Assert.Equal<RunDialogState option>(None, m.RunDialog)

    [<Fact>]
    member _.``buildOverrides includes only values that differ from defaults`` () =
        let d =
            { ProjectId = "p1"
              FlowId    = "f"
              Vars =
                [ mkVar "branch" VarKind.String (Some (TString "main"))
                  mkVar "clean"  VarKind.Bool   (Some (TBool false)) ]
              Values = Map.ofList [ "branch", "main"; "clean", "true" ]
              Remember = Set.empty
              Lists    = Map.empty
              Toggles  = Set.empty
              SaveDefaults = false
              Stored   = Set.empty
              Error  = None }
        match buildOverrides d with
        | Ok overrides ->
            // branch unchanged (default) → omitted; clean flipped → present.
            Assert.False(Map.containsKey "branch" overrides)
            Assert.Equal<TomlValue>(TBool true, Map.find "clean" overrides)
        | Error e -> Assert.True(false, e)

    [<Fact>]
    member _.``buildOverrides reports the first parse error`` () =
        let d =
            { ProjectId = "p1"
              FlowId    = "f"
              Vars   = [ mkVar "n" VarKind.Int None ]
              Values = Map.ofList [ "n", "not-a-number" ]
              Remember = Set.empty
              Lists    = Map.empty
              Toggles  = Set.empty
              SaveDefaults = false
              Stored   = Set.empty
              Error  = None }
        match buildOverrides d with
        | Ok _    -> Assert.True(false, "expected a parse error")
        | Error _ -> Assert.True(true)

    // ─── secret vars in the dialog ──────────────────────────────────

    [<Fact>]
    member _.``buildOverrides skips secret vars`` () =
        let d =
            { ProjectId = "p1"
              FlowId    = "f"
              Vars   = [ mkVar "token" VarKind.Secret None ]
              Values = Map.ofList [ "token", "typed-secret" ]
              Remember = Set.empty
              Lists    = Map.empty
              Toggles  = Set.empty
              SaveDefaults = false
              Stored   = Set.empty
              Error  = None }
        match buildOverrides d with
        | Ok overrides -> Assert.False(Map.containsKey "token" overrides)
        | Error e      -> Assert.True(false, e)

    [<Fact>]
    member _.``applySecretOverrides adds a typed secret and persists when remembered`` () =
        let d =
            { ProjectId = "p1"
              FlowId    = "f"
              Vars   = [ mkVar "token" VarKind.Secret None ]
              Values = Map.ofList [ "token", "abc123" ]
              Remember = Set.ofList [ "token" ]
              Lists    = Map.empty
              Toggles  = Set.empty
              SaveDefaults = false
              Stored   = Set.empty
              Error  = None }
        let merged = applySecretOverrides d Map.empty
        Assert.Equal<TomlValue>(TString "abc123", Map.find "token" merged)
        // Remembered → written to the (in-memory) keychain.
        Assert.Equal<string option>(Some "abc123", Secrets.read "p1" "token")

    [<Fact>]
    member _.``applySecretOverrides omits a blank secret and does not persist when not remembered`` () =
        let d =
            { ProjectId = "p1"
              FlowId    = "f"
              Vars   = [ mkVar "a" VarKind.Secret None; mkVar "b" VarKind.Secret None ]
              Values = Map.ofList [ "a", ""; "b", "kept" ]
              Remember = Set.empty   // b is used for this run but not saved
              Lists    = Map.empty
              Toggles  = Set.empty
              SaveDefaults = false
              Stored   = Set.empty
              Error  = None }
        let merged = applySecretOverrides d Map.empty
        Assert.False(Map.containsKey "a" merged)
        Assert.Equal<TomlValue>(TString "kept", Map.find "b" merged)
        Assert.Equal<string option>(None, Secrets.read "p1" "b")

    [<Fact>]
    member _.``RequestRun prefills a stored secret and marks it remembered`` () =
        Secrets.write "p1" "token" "stored-secret"
        let m =
            apply (RequestRun ("p1", "f"))
                (modelWithFlow "p1" "f" [ mkVar "token" VarKind.Secret None ])
        match m.RunDialog with
        | Some d ->
            Assert.Equal("stored-secret", Map.find "token" d.Values)
            Assert.True(Set.contains "token" d.Stored)
            Assert.True(Set.contains "token" d.Remember)
        | None -> Assert.True(false, "dialog should be open")

    // ─── diff from defaults ─────────────────────────────────────────

    [<Fact>]
    member _.``dialogDiffs lists only changed non-secret fields`` () =
        let d =
            { ProjectId = "p1"
              FlowId    = "f"
              Vars =
                [ mkVar "branch" VarKind.String (Some (TString "main"))
                  mkVar "clean"  VarKind.Bool   (Some (TBool false))
                  mkVar "token"  VarKind.Secret None ]
              Values = Map.ofList [ "branch", "main"; "clean", "true"; "token", "sekret" ]
              Lists  = Map.empty
              Toggles  = Set.empty
              Remember = Set.empty
              SaveDefaults = false
              Stored   = Set.empty
              Error  = None }
        // branch unchanged → omitted; clean flipped → shown; secret never shown.
        Assert.Equal<(string * string * string) list>(
            [ ("clean", "false", "true") ], dialogDiffs d)

    [<Fact>]
    member _.``dialogDiffs renders a changed list as bracketed text`` () =
        let d =
            { ProjectId = "p1"
              FlowId    = "f"
              Vars   = [ mkVar "maps" (VarKind.List VarKind.String) (Some (TArray [ TString "Main" ])) ]
              Values = Map.empty
              Lists  = Map.ofList [ "maps", [ "Main"; "Boot" ] ]
              Toggles  = Set.empty
              Remember = Set.empty
              SaveDefaults = false
              Stored   = Set.empty
              Error  = None }
        Assert.Equal<(string * string * string) list>(
            [ ("maps", "[Main]", "[Main, Boot]") ], dialogDiffs d)

    [<Fact>]
    member _.``RunDialogConfirm with SaveDefaults writes the new default back to flows.toml`` () =
        let dir = Path.Combine(tmpRoot, "savedef")
        let ci = Path.Combine(dir, ".ci")
        Directory.CreateDirectory ci |> ignore
        File.WriteAllText(
            Path.Combine(ci, "flows.toml"),
            "[[flow]]\nid = \"f\"\n[flow.vars]\nbranch = { type = \"string\", default = \"main\" }\n")
        let flow = { Id = "f"; Name = None; Vars = [ mkVar "branch" VarKind.String (Some (TString "main")) ]; Steps = [] }
        let model =
            { baseModel with
                Projects = [ { Name = "p1"; Path = dir; AddedAt = DateTimeOffset.UtcNow } ]
                ProjectFlows = Map.ofList [ "p1", FlowsOk [ flow ] ] }
        let opened = apply (RequestRun ("p1", "f")) model
        // Change branch and tick "Save as new default".
        let edited =
            { opened with
                RunDialog =
                    opened.RunDialog
                    |> Option.map (fun d -> { d with Values = Map.add "branch" "dev" d.Values; SaveDefaults = true }) }
        let m = apply RunDialogConfirm edited
        // Saved cleanly → dialog closed; the file's default is now "dev".
        Assert.Equal<RunDialogState option>(None, m.RunDialog)
        Assert.Contains("default = \"dev\"", File.ReadAllText(Path.Combine(ci, "flows.toml")))

    // ─── Toggles / Parameters split ─────────────────────────────────

    [<Fact>]
    member _.``whenReferencedToggles picks only bool vars gated by a step when`` () =
        let flow =
            { Id = "f"; Name = None
              Vars =
                [ { Name = "clean";  Kind = VarKind.Bool;   Default = None }
                  { Name = "upload"; Kind = VarKind.Bool;   Default = None }   // not referenced
                  { Name = "branch"; Kind = VarKind.String; Default = None } ] // referenced but not bool
              Steps =
                [ { Id = Some "c"; Type = "ue.clean"; When = Some "${vars.clean}"; Params = Map.empty }
                  { Id = Some "b"; Type = "git.pull"; When = Some "${vars.branch}"; Params = Map.empty } ] }
        Assert.Equal<Set<string>>(Set.ofList [ "clean" ], whenReferencedToggles flow)

    [<Fact>]
    member _.``RequestRun records when-referenced toggles on the dialog`` () =
        let flow =
            { Id = "rel"; Name = None
              Vars =
                [ { Name = "clean"; Kind = VarKind.Bool; Default = Some (TBool false) }
                  { Name = "cfg";   Kind = VarKind.String; Default = None } ]
              Steps = [ { Id = Some "c"; Type = "ue.clean"; When = Some "${vars.clean}"; Params = Map.empty } ] }
        let model = { baseModel with ProjectFlows = Map.ofList [ "p1", FlowsOk [ flow ] ] }
        let m = apply (RequestRun ("p1", "rel")) model
        match m.RunDialog with
        | Some d -> Assert.Equal<Set<string>>(Set.ofList [ "clean" ], d.Toggles)
        | None   -> Assert.True(false, "dialog should be open")

    // ─── list vars in the dialog ────────────────────────────────────

    [<Fact>]
    member _.``RequestRun pre-fills a list var's editor from its default array`` () =
        let vars = [ mkVar "maps" (VarKind.List VarKind.String) (Some (TArray [ TString "Main"; TString "Boot" ])) ]
        let m = apply (RequestRun ("p1", "f")) (modelWithFlow "p1" "f" vars)
        match m.RunDialog with
        | Some d -> Assert.Equal<string list>([ "Main"; "Boot" ], Map.find "maps" d.Lists)
        | None   -> Assert.True(false, "dialog should be open")

    [<Fact>]
    member _.``RunDialogList add, set, and remove edit the item list`` () =
        let vars = [ mkVar "maps" (VarKind.List VarKind.String) None ]
        let m0 = apply (RequestRun ("p1", "f")) (modelWithFlow "p1" "f" vars)
        let m1 = apply (RunDialogListAdd "maps") m0
        let m2 = apply (RunDialogListSetItem ("maps", 0, "Main")) m1
        let m3 = apply (RunDialogListAdd "maps") m2
        let m4 = apply (RunDialogListSetItem ("maps", 1, "Boot")) m3
        let m5 = apply (RunDialogListRemove ("maps", 0)) m4
        match m5.RunDialog with
        | Some d -> Assert.Equal<string list>([ "Boot" ], Map.find "maps" d.Lists)
        | None   -> Assert.True(false, "dialog should be open")

    [<Fact>]
    member _.``buildOverrides parses a list var into a TArray and drops blank rows`` () =
        let d =
            { ProjectId = "p1"
              FlowId    = "f"
              Vars   = [ mkVar "ports" (VarKind.List VarKind.Int) None ]
              Values = Map.empty
              Lists  = Map.ofList [ "ports", [ "8080"; "  "; "9090" ] ]
              Remember = Set.empty
              Toggles  = Set.empty
              SaveDefaults = false
              Stored   = Set.empty
              Error  = None }
        match buildOverrides d with
        | Ok overrides -> Assert.Equal<TomlValue>(TArray [ TInt 8080L; TInt 9090L ], Map.find "ports" overrides)
        | Error e      -> Assert.True(false, e)

    [<Fact>]
    member _.``buildOverrides reports a bad list item`` () =
        let d =
            { ProjectId = "p1"
              FlowId    = "f"
              Vars   = [ mkVar "ports" (VarKind.List VarKind.Int) None ]
              Values = Map.empty
              Lists  = Map.ofList [ "ports", [ "8080"; "nope" ] ]
              Remember = Set.empty
              Toggles  = Set.empty
              SaveDefaults = false
              Stored   = Set.empty
              Error  = None }
        match buildOverrides d with
        | Ok _    -> Assert.True(false, "expected a parse error")
        | Error _ -> Assert.True(true)

    [<Fact>]
    member _.``buildOverrides omits an empty list with no default`` () =
        let d =
            { ProjectId = "p1"
              FlowId    = "f"
              Vars   = [ mkVar "maps" (VarKind.List VarKind.String) None ]
              Values = Map.empty
              Lists  = Map.ofList [ "maps", [ ""; "  " ] ]
              Remember = Set.empty
              Toggles  = Set.empty
              SaveDefaults = false
              Stored   = Set.empty
              Error  = None }
        match buildOverrides d with
        | Ok overrides -> Assert.False(Map.containsKey "maps" overrides)
        | Error e      -> Assert.True(false, e)

    [<Fact>]
    member _.``DeleteSecret removes the keychain entry and refreshes the cache`` () =
        Secrets.write "p1" "token" "v"
        Secrets.write "p1" "keep" "v"
        // Seed the cache as if Settings had loaded it.
        let seeded = { baseModel with ProjectSecrets = Map.ofList [ "p1", [ "keep"; "token" ] ] }
        let m = apply (DeleteSecret ("p1", "token")) seeded
        Assert.Equal<string option>(None, Secrets.read "p1" "token")
        // Cache no longer lists the deleted entry (drives the re-render).
        Assert.Equal<string list>([ "keep" ], Map.find "p1" m.ProjectSecrets)

    [<Fact>]
    member _.``RunDialogForget deletes the stored secret and clears the field`` () =
        Secrets.write "p1" "token" "stored-secret"
        let opened =
            apply (RequestRun ("p1", "f"))
                (modelWithFlow "p1" "f" [ mkVar "token" VarKind.Secret None ])
        let m = apply (RunDialogForget "token") opened
        match m.RunDialog with
        | Some d ->
            Assert.Equal("", Map.find "token" d.Values)
            Assert.False(Set.contains "token" d.Stored)
        | None -> Assert.True(false, "dialog should still be open")
        Assert.Equal<string option>(None, Secrets.read "p1" "token")
