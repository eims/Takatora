module Takatora.Gui.State

open Takatora.Core

/// Project lookup key inside the GUI. The registry uses `Name` as the
/// stable identifier (per ProjectRegistry.find docs), so we mirror that
/// rather than introducing a separate id type.
type ProjectId = string

/// Root tab variants. Only Home + Project are wired; the other variants
/// from gui.md (LiveRun / RunDetail / Settings) will land as the
/// corresponding views come online.
type RootTab =
    | Home
    | Project of ProjectId

/// Sub-tabs inside a Project tab. Per gui.md these are plain TabControl
/// territory (only the root strip is custom), but we keep the active
/// sub-tab in the model so re-focusing a Project tab restores its
/// position rather than resetting to Flows.
type ProjectSubTab =
    | ProjectFlows
    | ProjectHistory
    | ProjectSettings

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
}

type Msg =
    | RefreshProjects
    | OpenProject of ProjectId
    | ActivateTab of RootTab
    | CloseTab of RootTab
    | ActivateSubTab of ProjectId * ProjectSubTab
    | RefreshHistory of ProjectId

let init () : Model =
    { OpenTabs       = [ Home ]
      ActiveTab      = Home
      Projects       = ProjectRegistry.load ()
      ProjectSubTabs = Map.empty
      ProjectHistory = Map.empty }

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

let private loadHistoryFor
        (pid: ProjectId)
        (projects: ProjectRegistration list)
        : RunHistoryEntry list =
    match List.tryFind (fun (p: ProjectRegistration) -> p.Name = pid) projects with
    | Some p -> RunHistory.load p.Path
    | None   -> []

let update (msg: Msg) (model: Model) : Model =
    match msg with
    | RefreshProjects ->
        { model with Projects = ProjectRegistry.load () }
    | OpenProject pid ->
        let tab = Project pid
        let history =
            if Map.containsKey pid model.ProjectHistory then model.ProjectHistory
            else Map.add pid (loadHistoryFor pid model.Projects) model.ProjectHistory
        { model with
            OpenTabs       = moveOrAppend tab model.OpenTabs
            ActiveTab      = tab
            ProjectHistory = history }
    | ActivateTab tab ->
        if List.contains tab model.OpenTabs then
            { model with ActiveTab = tab }
        else model
    | CloseTab tab ->
        let newTabs, newActive = closeAndPickActive tab model.ActiveTab model.OpenTabs
        // Drop per-project caches when a Project tab is closed. Keeping
        // them would leak state across close+reopen, and reopen reloads
        // anyway.
        let subTabs, history =
            match tab with
            | Project pid ->
                Map.remove pid model.ProjectSubTabs,
                Map.remove pid model.ProjectHistory
            | _ -> model.ProjectSubTabs, model.ProjectHistory
        { model with
            OpenTabs       = newTabs
            ActiveTab      = newActive
            ProjectSubTabs = subTabs
            ProjectHistory = history }
    | ActivateSubTab (pid, sub) ->
        { model with ProjectSubTabs = Map.add pid sub model.ProjectSubTabs }
    | RefreshHistory pid ->
        { model with
            ProjectHistory = Map.add pid (loadHistoryFor pid model.Projects) model.ProjectHistory }
