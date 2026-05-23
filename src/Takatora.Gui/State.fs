module Takatora.Gui.State

open System.IO
open Takatora.Core

/// Project lookup key inside the GUI. The registry uses `Name` as the
/// stable identifier (per ProjectRegistry.find docs), so we mirror that
/// rather than introducing a separate id type.
type ProjectId = string

/// Run id (mirrors RunHistoryEntry.RunId). Carried alongside ProjectId
/// in RunDetail tabs because RunHistory.findRun needs the project's
/// working dir to locate the run.
type RunId = string

/// Root tab variants. Home + Project + RunDetail are wired; LiveRun and
/// Settings from gui.md will land later.
type RootTab =
    | Home
    | Project of ProjectId
    | RunDetail of ProjectId * RunId

/// Sub-tabs inside a Project tab. Per gui.md these are plain TabControl
/// territory (only the root strip is custom), but we keep the active
/// sub-tab in the model so re-focusing a Project tab restores its
/// position rather than resetting to Flows.
type ProjectSubTab =
    | ProjectFlows
    | ProjectHistory
    | ProjectSettings

/// Result of trying to read a project's `.ci/flows.toml`. The three
/// outcomes need distinct UX — missing file is a setup hint, parse
/// failure is a config bug to surface, success is the happy path.
type FlowsLoad =
    | FlowsOk of Flow list
    | FlowsMissing
    | FlowsError of string

type Model = {
    OpenTabs: RootTab list
    ActiveTab: RootTab
    Projects: ProjectRegistration list
    /// Per-Project-tab active sub-tab. Missing key means "default" (Flows).
    ProjectSubTabs: Map<ProjectId, ProjectSubTab>
    /// Cached `RunHistory.load` results, keyed by project id. Loaded
    /// lazily on OpenProject so initial app startup doesn't pay for
    /// every project's runs dir.
    ProjectHistory: Map<ProjectId, RunHistoryEntry list>
    /// Cached `TomlConfig.loadFlows` results, keyed by project id.
    /// Loaded on OpenProject alongside history.
    ProjectFlows: Map<ProjectId, FlowsLoad>
    /// Cached `RunHistory.findRun` results for open RunDetail tabs,
    /// keyed by (project, run id). Loaded on OpenRunDetail, dropped
    /// when the corresponding tab closes.
    RunDetails: Map<ProjectId * RunId, RunHistoryEntry * StepSummary list>
}

type Msg =
    | RefreshProjects
    | OpenProject of ProjectId
    | ActivateTab of RootTab
    | CloseTab of RootTab
    | ActivateSubTab of ProjectId * ProjectSubTab
    | RefreshHistory of ProjectId
    | RefreshFlows of ProjectId
    | OpenRunDetail of ProjectId * RunId

let init () : Model =
    { OpenTabs       = [ Home ]
      ActiveTab      = Home
      Projects       = ProjectRegistry.load ()
      ProjectSubTabs = Map.empty
      ProjectHistory = Map.empty
      ProjectFlows   = Map.empty
      RunDetails     = Map.empty }

let private moveOrAppend (tab: RootTab) (tabs: RootTab list) : RootTab list =
    if List.contains tab tabs then tabs else tabs @ [ tab ]

/// Compute the post-close tab list plus the next active tab. When the
/// closed tab was the active one, prefer the tab that was to its right
/// (browser convention); fall back to the previous tab, finally Home.
let private closeAndPickActive
        (target: RootTab) (active: RootTab) (tabs: RootTab list)
        : RootTab list * RootTab =
    if target = Home then tabs, active
    else
        match List.tryFindIndex ((=) target) tabs with
        | None -> tabs, active
        | Some idx ->
            let newTabs = List.filter ((<>) target) tabs
            let newActive =
                if active <> target then active
                elif idx < List.length newTabs then newTabs.[idx]
                else
                    match newTabs with
                    | [] -> Home
                    | _  -> List.last newTabs
            newTabs, newActive

/// Look up the active sub-tab for a project, defaulting to Flows. Used
/// by views; not used by `update` (which writes the map directly).
let projectSubTab (pid: ProjectId) (model: Model) : ProjectSubTab =
    Map.tryFind pid model.ProjectSubTabs
    |> Option.defaultValue ProjectFlows

let private projectRoot
        (pid: ProjectId)
        (projects: ProjectRegistration list)
        : string option =
    projects
    |> List.tryFind (fun (p: ProjectRegistration) -> p.Name = pid)
    |> Option.map (fun p -> p.Path)

let private loadHistoryFor
        (pid: ProjectId)
        (projects: ProjectRegistration list)
        : RunHistoryEntry list =
    match projectRoot pid projects with
    | Some root -> RunHistory.load root
    | None      -> []

/// Read and classify `.ci/flows.toml` for the given project. We keep
/// the three outcomes distinct because each maps to a different UX:
/// missing → "no flows yet, here's how to add one"; error → "your
/// flows.toml has a bug, here's the message"; ok → render the list.
let private loadFlowsFor
        (pid: ProjectId)
        (projects: ProjectRegistration list)
        : FlowsLoad =
    match projectRoot pid projects with
    | None -> FlowsError (sprintf "Project '%s' not in registry" pid)
    | Some root ->
        let path = Path.Combine(root, ".ci", "flows.toml")
        if not (File.Exists path) then FlowsMissing
        else
            try FlowsOk (TomlConfig.loadFlows path)
            with ex -> FlowsError ex.Message

let private loadRunDetailFor
        (pid: ProjectId)
        (runId: RunId)
        (projects: ProjectRegistration list)
        : (RunHistoryEntry * StepSummary list) option =
    match projectRoot pid projects with
    | Some root -> RunHistory.findRun root runId
    | None      -> None

let update (msg: Msg) (model: Model) : Model =
    match msg with
    | RefreshProjects ->
        { model with Projects = ProjectRegistry.load () }
    | OpenProject pid ->
        let tab = Project pid
        let history =
            if Map.containsKey pid model.ProjectHistory then model.ProjectHistory
            else Map.add pid (loadHistoryFor pid model.Projects) model.ProjectHistory
        let flows =
            if Map.containsKey pid model.ProjectFlows then model.ProjectFlows
            else Map.add pid (loadFlowsFor pid model.Projects) model.ProjectFlows
        { model with
            OpenTabs       = moveOrAppend tab model.OpenTabs
            ActiveTab      = tab
            ProjectHistory = history
            ProjectFlows   = flows }
    | ActivateTab tab ->
        if List.contains tab model.OpenTabs then
            { model with ActiveTab = tab }
        else model
    | CloseTab tab ->
        let newTabs, newActive = closeAndPickActive tab model.ActiveTab model.OpenTabs
        // Drop per-tab caches when the corresponding tab closes; reopen
        // reloads from disk anyway, and keeping stale entries would let
        // the cache grow without bound across a long session.
        let subTabs, history, flows, details =
            match tab with
            | Project pid ->
                Map.remove pid model.ProjectSubTabs,
                Map.remove pid model.ProjectHistory,
                Map.remove pid model.ProjectFlows,
                model.RunDetails
            | RunDetail (pid, runId) ->
                model.ProjectSubTabs,
                model.ProjectHistory,
                model.ProjectFlows,
                Map.remove (pid, runId) model.RunDetails
            | Home ->
                model.ProjectSubTabs,
                model.ProjectHistory,
                model.ProjectFlows,
                model.RunDetails
        { model with
            OpenTabs       = newTabs
            ActiveTab      = newActive
            ProjectSubTabs = subTabs
            ProjectHistory = history
            ProjectFlows   = flows
            RunDetails     = details }
    | ActivateSubTab (pid, sub) ->
        { model with ProjectSubTabs = Map.add pid sub model.ProjectSubTabs }
    | RefreshHistory pid ->
        { model with
            ProjectHistory = Map.add pid (loadHistoryFor pid model.Projects) model.ProjectHistory }
    | RefreshFlows pid ->
        { model with
            ProjectFlows = Map.add pid (loadFlowsFor pid model.Projects) model.ProjectFlows }
    | OpenRunDetail (pid, runId) ->
        let tab = RunDetail (pid, runId)
        let key = pid, runId
        let details =
            if Map.containsKey key model.RunDetails then model.RunDetails
            else
                match loadRunDetailFor pid runId model.Projects with
                | Some data -> Map.add key data model.RunDetails
                | None      -> model.RunDetails
        { model with
            OpenTabs   = moveOrAppend tab model.OpenTabs
            ActiveTab  = tab
            RunDetails = details }
