module Takatora.Gui.State

open Takatora.Core

/// Project lookup key inside the GUI. The registry uses `Name` as the
/// stable identifier (per ProjectRegistry.find docs), so we mirror that
/// rather than introducing a separate id type.
type ProjectId = string

/// Root tab variants. Only Home + Project are wired in this slice; the
/// other variants from gui.md (LiveRun / RunDetail / Settings) will land
/// as the corresponding views come online.
type RootTab =
    | Home
    | Project of ProjectId

type Model = {
    OpenTabs: RootTab list
    ActiveTab: RootTab
    Projects: ProjectRegistration list
}

type Msg =
    | RefreshProjects
    | OpenProject of ProjectId
    | ActivateTab of RootTab
    | CloseTab of RootTab

let init () : Model =
    { OpenTabs = [ Home ]
      ActiveTab = Home
      Projects = ProjectRegistry.load () }

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

let update (msg: Msg) (model: Model) : Model =
    match msg with
    | RefreshProjects ->
        { model with Projects = ProjectRegistry.load () }
    | OpenProject pid ->
        let tab = Project pid
        { model with
            OpenTabs  = moveOrAppend tab model.OpenTabs
            ActiveTab = tab }
    | ActivateTab tab ->
        if List.contains tab model.OpenTabs then
            { model with ActiveTab = tab }
        else model
    | CloseTab tab ->
        let newTabs, newActive = closeAndPickActive tab model.ActiveTab model.OpenTabs
        { model with OpenTabs = newTabs; ActiveTab = newActive }
