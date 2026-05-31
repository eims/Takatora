module Takatora.Gui.View

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Takatora.Core
open Takatora.Gui.State

// ─── Colors / small helpers ─────────────────────────────────────────────

let private brush (hex: string) : IBrush =
    SolidColorBrush(Color.Parse hex) :> _

let private mutedBrush  = brush "#888888"
let private dimBrush    = brush "#aaaaaa"
let private stripBg     = brush "#1f1f1f"
let private stripBorder = brush "#0e0e0e"
let private activeBg    = brush "#2a2a2a"
let private accent      = brush "#3d8bfd"
let private cardBg      = brush "#252525"

let private handCursor = new Cursor(StandardCursorType.Hand)

let private formatDuration (sec: float) : string =
    let total = int sec
    let h = total / 3600
    let m = (total % 3600) / 60
    let s = total % 60
    if h > 0 then sprintf "%d:%02d:%02d" h m s
    else sprintf "%d:%02d" m s

let private statusIcon = function
    | "success"   -> "✓"
    | "failure"   -> "✗"
    | "cancelled" -> "⊘"
    | _           -> "?"

let private statusBrush = function
    | "success"   -> brush "#4ec97a"
    | "failure"   -> brush "#f15a5a"
    | "cancelled" -> mutedBrush
    | _           -> dimBrush

let rec private renderTomlValue (v: TomlValue) : string =
    match v with
    | TString s -> sprintf "\"%s\"" s
    | TInt i    -> string i
    | TFloat f  -> sprintf "%g" f
    | TBool b   -> if b then "true" else "false"
    | TArray xs -> "[" + (xs |> List.map renderTomlValue |> String.concat ", ") + "]"
    | TTable _  -> "{…}"  // nested tables in params are rare; keep label short

/// Short label for RunDetail tabs. Run ids look like
/// `r-2026052314-0653-d6f6`; the last hyphen-separated chunk is the
/// unique-ish suffix that disambiguates same-second runs.
let private runShortLabel (runId: string) : string =
    let parts = runId.Split('-')
    if parts.Length > 0 then sprintf "Run %s" (Array.last parts)
    else sprintf "Run %s" runId

let private sectionHeader (text: string) : IView =
    TextBlock.create [
        TextBlock.text text
        TextBlock.fontSize 14.0
        TextBlock.fontWeight FontWeight.SemiBold
        TextBlock.margin (Thickness(0.0, 12.0, 0.0, 4.0))
    ] :> _

// ─── Root tab strip ─────────────────────────────────────────────────────

let private tabLabel = function
    | Home                  -> "Home"
    | Project pid           -> pid
    | RunDetail (pid, rid)  -> sprintf "%s · %s" (runShortLabel rid) pid

let private tabClosable = function
    | Home -> false
    | _    -> true

let private tabChip
        (tab: RootTab)
        (isActive: bool)
        (dispatch: Msg -> unit)
        : IView =
    Border.create [
        Border.borderThickness (Thickness(0.0, 0.0, 0.0, 2.0))
        Border.borderBrush (if isActive then accent else stripBg)
        Border.background (if isActive then activeBg else stripBg)
        Border.child (
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.verticalAlignment VerticalAlignment.Center
                StackPanel.children [
                    Button.create [
                        Button.content (tabLabel tab)
                        Button.background Brushes.Transparent
                        Button.borderThickness 0.0
                        Button.padding (Thickness(10.0, 4.0))
                        Button.onClick (fun _ -> dispatch (ActivateTab tab))
                    ]
                    if tabClosable tab then
                        Button.create [
                            Button.content "×"
                            Button.background Brushes.Transparent
                            Button.borderThickness 0.0
                            Button.padding (Thickness(6.0, 4.0))
                            Button.onClick (fun _ -> dispatch (CloseTab tab))
                        ]
                ]
            ]
        )
    ] :> _

let private rootTabStrip (model: Model) (dispatch: Msg -> unit) : IView =
    Border.create [
        DockPanel.dock Dock.Top
        Border.borderThickness (Thickness(0.0, 0.0, 0.0, 1.0))
        Border.borderBrush stripBorder
        Border.background stripBg
        Border.height 36.0
        Border.child (
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.children [
                    for tab in model.OpenTabs ->
                        tabChip tab (tab = model.ActiveTab) dispatch
                ]
            ]
        )
    ] :> _

// ─── Home tab ───────────────────────────────────────────────────────────

let private projectRow
        (p: ProjectRegistration)
        (dispatch: Msg -> unit)
        : IView =
    Border.create [
        Border.padding (Thickness 12.0)
        Border.cornerRadius 4.0
        Border.background cardBg
        Border.child (
            DockPanel.create [
                DockPanel.children [
                    Button.create [
                        DockPanel.dock Dock.Right
                        Button.content "Open"
                        Button.onClick (fun _ -> dispatch (OpenProject p.Name))
                        Button.verticalAlignment VerticalAlignment.Center
                    ]
                    StackPanel.create [
                        StackPanel.children [
                            TextBlock.create [
                                TextBlock.text p.Name
                                TextBlock.fontSize 16.0
                                TextBlock.fontWeight FontWeight.SemiBold
                            ]
                            TextBlock.create [
                                TextBlock.text p.Path
                                TextBlock.foreground mutedBrush
                                TextBlock.fontSize 12.0
                            ]
                        ]
                    ]
                ]
            ]
        )
    ] :> _

let private homeBody (model: Model) (dispatch: Msg -> unit) : IView =
    if List.isEmpty model.Projects then
        TextBlock.create [
            TextBlock.text "No projects registered. Use `takatora project add <path>` from the CLI to register one, then click Refresh."
            TextBlock.margin (Thickness 16.0)
            TextBlock.foreground mutedBrush
            TextBlock.textWrapping TextWrapping.Wrap
        ] :> _
    else
        ScrollViewer.create [
            ScrollViewer.content (
                StackPanel.create [
                    StackPanel.margin (Thickness(16.0, 0.0, 16.0, 16.0))
                    StackPanel.spacing 6.0
                    StackPanel.children [
                        for p in model.Projects -> projectRow p dispatch
                    ]
                ]
            )
        ] :> _

let private homeView (model: Model) (dispatch: Msg -> unit) : IView =
    DockPanel.create [
        DockPanel.children [
            StackPanel.create [
                DockPanel.dock Dock.Top
                StackPanel.orientation Orientation.Horizontal
                StackPanel.margin (Thickness(16.0, 12.0))
                StackPanel.spacing 12.0
                StackPanel.children [
                    TextBlock.create [
                        TextBlock.text "Projects"
                        TextBlock.fontSize 20.0
                        TextBlock.verticalAlignment VerticalAlignment.Center
                    ]
                    Button.create [
                        Button.content "Refresh"
                        Button.onClick (fun _ -> dispatch RefreshProjects)
                    ]
                ]
            ]
            homeBody model dispatch
        ]
    ] :> _

// ─── Project sub-tabs ───────────────────────────────────────────────────

let private subTabButton
        (label: string)
        (isActive: bool)
        (onClick: unit -> unit)
        : IView =
    Button.create [
        Button.content label
        Button.background (if isActive then activeBg else (Brushes.Transparent :> IBrush))
        Button.foreground (if isActive then (Brushes.White :> IBrush) else dimBrush)
        Button.borderBrush (if isActive then accent else (Brushes.Transparent :> IBrush))
        Button.borderThickness (Thickness(0.0, 0.0, 0.0, 2.0))
        Button.padding (Thickness(20.0, 8.0))
        Button.onClick (fun _ -> onClick ())
    ] :> _

let private projectSubTabHeader
        (pid: ProjectId)
        (current: ProjectSubTab)
        (dispatch: Msg -> unit)
        : IView =
    Border.create [
        DockPanel.dock Dock.Top
        Border.borderThickness (Thickness(0.0, 0.0, 0.0, 1.0))
        Border.borderBrush stripBorder
        Border.background stripBg
        Border.child (
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.children [
                    subTabButton "Flows"    (current = ProjectFlows)    (fun () -> dispatch (ActivateSubTab (pid, ProjectFlows)))
                    subTabButton "History"  (current = ProjectHistory)  (fun () -> dispatch (ActivateSubTab (pid, ProjectHistory)))
                    subTabButton "Settings" (current = ProjectSettings) (fun () -> dispatch (ActivateSubTab (pid, ProjectSettings)))
                ]
            ]
        )
    ] :> _

let private flowCard (f: Flow) : IView =
    Border.create [
        Border.padding (Thickness 12.0)
        Border.cornerRadius 4.0
        Border.background cardBg
        Border.child (
            StackPanel.create [
                StackPanel.spacing 2.0
                StackPanel.children [
                    StackPanel.create [
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.spacing 12.0
                        StackPanel.children [
                            TextBlock.create [
                                TextBlock.text f.Id
                                TextBlock.fontSize 16.0
                                TextBlock.fontWeight FontWeight.SemiBold
                            ]
                            (match f.Name with
                             | Some n ->
                                 TextBlock.create [
                                     TextBlock.text n
                                     TextBlock.foreground mutedBrush
                                     TextBlock.fontSize 13.0
                                     TextBlock.verticalAlignment VerticalAlignment.Center
                                 ] :> IView
                             | None ->
                                 TextBlock.create [ TextBlock.text "" ] :> IView)
                        ]
                    ]
                    TextBlock.create [
                        TextBlock.text
                            (sprintf "%d var(s)  ·  %d step(s)"
                                (List.length f.Vars) (List.length f.Steps))
                        TextBlock.foreground mutedBrush
                        TextBlock.fontSize 12.0
                    ]
                ]
            ]
        )
    ] :> _

let private flowsBody
        (pid: ProjectId)
        (load: FlowsLoad)
        (dispatch: Msg -> unit)
        : IView =
    let header =
        StackPanel.create [
            DockPanel.dock Dock.Top
            StackPanel.orientation Orientation.Horizontal
            StackPanel.margin (Thickness(16.0, 12.0))
            StackPanel.spacing 12.0
            StackPanel.children [
                TextBlock.create [
                    TextBlock.text
                        (match load with
                         | FlowsOk fs -> sprintf "%d flow(s)" (List.length fs)
                         | FlowsMissing -> "(no .ci/flows.toml)"
                         | FlowsError _ -> "(flows.toml error)")
                    TextBlock.foreground mutedBrush
                    TextBlock.verticalAlignment VerticalAlignment.Center
                ]
                Button.create [
                    Button.content "Refresh"
                    Button.onClick (fun _ -> dispatch (RefreshFlows pid))
                ]
            ]
        ]
    let body : IView =
        match load with
        | FlowsMissing ->
            TextBlock.create [
                TextBlock.text "No `.ci/flows.toml` in this project's working directory. Create one (or use the planned init/wizard) to define runnable flows."
                TextBlock.margin (Thickness 16.0)
                TextBlock.foreground mutedBrush
                TextBlock.textWrapping TextWrapping.Wrap
            ] :> _
        | FlowsError msg ->
            StackPanel.create [
                StackPanel.margin (Thickness 16.0)
                StackPanel.spacing 6.0
                StackPanel.children [
                    TextBlock.create [
                        TextBlock.text "Could not parse `.ci/flows.toml`:"
                        TextBlock.foreground (brush "#f15a5a")
                    ]
                    TextBlock.create [
                        TextBlock.text msg
                        TextBlock.foreground dimBrush
                        TextBlock.textWrapping TextWrapping.Wrap
                    ]
                ]
            ] :> _
        | FlowsOk [] ->
            TextBlock.create [
                TextBlock.text "`flows.toml` parsed but defines no `[[flow]]` entries yet."
                TextBlock.margin (Thickness 16.0)
                TextBlock.foreground mutedBrush
                TextBlock.textWrapping TextWrapping.Wrap
            ] :> _
        | FlowsOk fs ->
            ScrollViewer.create [
                ScrollViewer.content (
                    StackPanel.create [
                        StackPanel.margin (Thickness(16.0, 0.0, 16.0, 16.0))
                        StackPanel.spacing 6.0
                        StackPanel.children [
                            for f in fs -> flowCard f
                        ]
                    ]
                )
            ] :> _
    DockPanel.create [
        DockPanel.children [
            header
            body
        ]
    ] :> _

let private engineKindLabel = function
    | EngineKind.Unreal -> "Unreal Engine"
    | EngineKind.Unity  -> "Unity"
    | EngineKind.Godot  -> "Godot"

let private vcsKindLabel = function
    | VcsKind.Git -> "Git"

let private settingsField (label: string) (value: string) : IView =
    DockPanel.create [
        DockPanel.margin (Thickness(0.0, 2.0))
        DockPanel.children [
            TextBlock.create [
                DockPanel.dock Dock.Left
                TextBlock.text label
                TextBlock.width 160.0
                TextBlock.foreground dimBrush
            ]
            TextBlock.create [
                TextBlock.text value
                TextBlock.textWrapping TextWrapping.Wrap
            ]
        ]
    ] :> _

let private optStr (label: string) (v: string option) : IView =
    settingsField label (Option.defaultValue "(autodetect)" v)

let private projectInfoBlock (proj: Project) : IView =
    StackPanel.create [
        StackPanel.spacing 2.0
        StackPanel.children [
            sectionHeader "Engine"
            settingsField "type"           (engineKindLabel proj.Engine.Kind)
            optStr        "project_file"   proj.Engine.ProjectFile
            optStr        "engine_path"    proj.Engine.EnginePath
            optStr        "engine_version" proj.Engine.EngineVersion
            optStr        "executable"     proj.Engine.Executable

            sectionHeader "VCS"
            (match proj.Vcs with
             | Some vcs ->
                 StackPanel.create [
                     StackPanel.children [
                         settingsField "type" (vcsKindLabel vcs.Kind)
                         settingsField "lfs"  (if vcs.Lfs then "enabled" else "disabled")
                     ]
                 ] :> IView
             | None ->
                 TextBlock.create [
                     TextBlock.text "(not configured)"
                     TextBlock.foreground mutedBrush
                 ] :> IView)

            sectionHeader "History retention"
            settingsField "keep_last_n_runs" (string proj.History.KeepLastNRuns)

            sectionHeader "Working dir"
            settingsField "working_dir" proj.WorkingDir
        ]
    ] :> _

let private settingsBody
        (pid: ProjectId)
        (load: ProjectInfoLoad)
        (dispatch: Msg -> unit)
        : IView =
    let header =
        StackPanel.create [
            DockPanel.dock Dock.Top
            StackPanel.orientation Orientation.Horizontal
            StackPanel.margin (Thickness(16.0, 12.0))
            StackPanel.spacing 12.0
            StackPanel.children [
                TextBlock.create [
                    TextBlock.text
                        (match load with
                         | ProjectInfoOk _      -> "From .ci/project.toml — read-only in this slice"
                         | ProjectInfoMissing   -> "(no .ci/project.toml)"
                         | ProjectInfoError _   -> "(project.toml error)")
                    TextBlock.foreground mutedBrush
                    TextBlock.verticalAlignment VerticalAlignment.Center
                ]
                Button.create [
                    Button.content "Refresh"
                    Button.onClick (fun _ -> dispatch (RefreshProjectInfo pid))
                ]
            ]
        ]
    let body : IView =
        match load with
        | ProjectInfoMissing ->
            TextBlock.create [
                TextBlock.text "No `.ci/project.toml` under this project's working directory. The registry entry expects one — has it been moved or deleted?"
                TextBlock.margin (Thickness 16.0)
                TextBlock.foreground mutedBrush
                TextBlock.textWrapping TextWrapping.Wrap
            ] :> _
        | ProjectInfoError msg ->
            StackPanel.create [
                StackPanel.margin (Thickness 16.0)
                StackPanel.spacing 6.0
                StackPanel.children [
                    TextBlock.create [
                        TextBlock.text "Could not parse `.ci/project.toml`:"
                        TextBlock.foreground (brush "#f15a5a")
                    ]
                    TextBlock.create [
                        TextBlock.text msg
                        TextBlock.foreground dimBrush
                        TextBlock.textWrapping TextWrapping.Wrap
                    ]
                ]
            ] :> _
        | ProjectInfoOk proj ->
            ScrollViewer.create [
                ScrollViewer.content (
                    StackPanel.create [
                        StackPanel.margin (Thickness(16.0, 0.0, 16.0, 16.0))
                        StackPanel.children [ projectInfoBlock proj ]
                    ]
                )
            ] :> _
    DockPanel.create [
        DockPanel.children [
            header
            body
        ]
    ] :> _

let private historyHeaderRow : IView list =
    let cell (col: int) (text: string) : IView =
        TextBlock.create [
            Grid.row 0
            Grid.column col
            TextBlock.text text
            TextBlock.fontWeight FontWeight.SemiBold
            TextBlock.foreground dimBrush
            TextBlock.margin (Thickness(8.0, 0.0, 8.0, 6.0))
            TextBlock.isHitTestVisible false
        ] :> IView
    [
        cell 0 " "
        cell 1 "flow"
        cell 2 "started"
        cell 3 "duration"
        cell 4 "run id"
    ]

let private historyDataRow
        (row: int)
        (pid: ProjectId)
        (e: RunHistoryEntry)
        (dispatch: Msg -> unit)
        : IView list =
    let startedLocal = e.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
    let cellMargin = Thickness(8.0, 3.0)
    [
        // Clickable backdrop. Transparent (not null) background so the
        // Border catches pointer events; cells above set
        // IsHitTestVisible=false so clicks fall through to the backdrop.
        Border.create [
            Grid.row row
            Grid.column 0
            Grid.columnSpan 5
            Border.background (Brushes.Transparent :> IBrush)
            Border.cursor handCursor
            Border.onPointerPressed (fun _ -> dispatch (OpenRunDetail (pid, e.RunId)))
        ] :> IView
        TextBlock.create [
            Grid.row row
            Grid.column 0
            TextBlock.text (statusIcon e.Result)
            TextBlock.foreground (statusBrush e.Result)
            TextBlock.margin cellMargin
            TextBlock.isHitTestVisible false
        ] :> IView
        TextBlock.create [
            Grid.row row
            Grid.column 1
            TextBlock.text e.FlowId
            TextBlock.margin cellMargin
            TextBlock.isHitTestVisible false
        ] :> IView
        TextBlock.create [
            Grid.row row
            Grid.column 2
            TextBlock.text startedLocal
            TextBlock.margin cellMargin
            TextBlock.isHitTestVisible false
        ] :> IView
        TextBlock.create [
            Grid.row row
            Grid.column 3
            TextBlock.text (formatDuration e.DurationSec)
            TextBlock.margin cellMargin
            TextBlock.isHitTestVisible false
        ] :> IView
        TextBlock.create [
            Grid.row row
            Grid.column 4
            TextBlock.text e.RunId
            TextBlock.foreground mutedBrush
            TextBlock.fontSize 11.0
            TextBlock.margin cellMargin
            TextBlock.isHitTestVisible false
        ] :> IView
    ]

let private historyBody
        (pid: ProjectId)
        (entries: RunHistoryEntry list)
        (dispatch: Msg -> unit)
        : IView =
    DockPanel.create [
        DockPanel.children [
            StackPanel.create [
                DockPanel.dock Dock.Top
                StackPanel.orientation Orientation.Horizontal
                StackPanel.margin (Thickness(16.0, 12.0))
                StackPanel.spacing 12.0
                StackPanel.children [
                    TextBlock.create [
                        TextBlock.text (sprintf "%d run(s)" (List.length entries))
                        TextBlock.foreground mutedBrush
                        TextBlock.verticalAlignment VerticalAlignment.Center
                    ]
                    Button.create [
                        Button.content "Refresh"
                        Button.onClick (fun _ -> dispatch (RefreshHistory pid))
                    ]
                ]
            ]
            if List.isEmpty entries then
                TextBlock.create [
                    TextBlock.text "No runs yet. Trigger one with `takatora run <project> <flow>` from the CLI; refresh to see it here."
                    TextBlock.margin (Thickness 16.0)
                    TextBlock.foreground mutedBrush
                    TextBlock.textWrapping TextWrapping.Wrap
                ] :> IView
            else
                ScrollViewer.create [
                    ScrollViewer.content (
                        Grid.create [
                            Grid.margin (Thickness(8.0, 0.0, 16.0, 16.0))
                            Grid.columnDefinitions "Auto,Auto,Auto,Auto,*"
                            Grid.rowDefinitions
                                (String.concat ","
                                    (List.replicate (List.length entries + 1) "Auto"))
                            Grid.children [
                                yield! historyHeaderRow
                                for i, e in List.indexed entries do
                                    yield! historyDataRow (i + 1) pid e dispatch
                            ]
                        ]
                    )
                ] :> IView
        ]
    ] :> _

// ─── Project tab ────────────────────────────────────────────────────────

let private projectView
        (pid: ProjectId)
        (model: Model)
        (dispatch: Msg -> unit)
        : IView =
    match List.tryFind (fun (p: ProjectRegistration) -> p.Name = pid) model.Projects with
    | None ->
        TextBlock.create [
            TextBlock.text $"Project '{pid}' not found in registry. It may have been removed externally — try Refresh on Home."
            TextBlock.margin (Thickness 16.0)
            TextBlock.foreground mutedBrush
            TextBlock.textWrapping TextWrapping.Wrap
        ] :> _
    | Some p ->
        let active = projectSubTab pid model
        DockPanel.create [
            DockPanel.children [
                StackPanel.create [
                    DockPanel.dock Dock.Top
                    StackPanel.margin (Thickness(16.0, 12.0, 16.0, 8.0))
                    StackPanel.children [
                        TextBlock.create [
                            TextBlock.text p.Name
                            TextBlock.fontSize 22.0
                            TextBlock.fontWeight FontWeight.SemiBold
                        ]
                        TextBlock.create [
                            TextBlock.text p.Path
                            TextBlock.foreground mutedBrush
                            TextBlock.fontSize 12.0
                        ]
                    ]
                ]
                projectSubTabHeader pid active dispatch
                (match active with
                 | ProjectFlows    ->
                     let load =
                         Map.tryFind pid model.ProjectFlows
                         |> Option.defaultValue FlowsMissing
                     flowsBody pid load dispatch
                 | ProjectHistory  ->
                     let entries =
                         Map.tryFind pid model.ProjectHistory
                         |> Option.defaultValue []
                     historyBody pid entries dispatch
                 | ProjectSettings ->
                     let load =
                         Map.tryFind pid model.ProjectInfo
                         |> Option.defaultValue ProjectInfoMissing
                     settingsBody pid load dispatch)
            ]
        ] :> _

// ─── RunDetail tab ──────────────────────────────────────────────────────

let private paramLine (key: string) (v: TomlValue) : IView =
    DockPanel.create [
        DockPanel.margin (Thickness(0.0, 2.0))
        DockPanel.children [
            TextBlock.create [
                DockPanel.dock Dock.Left
                TextBlock.text key
                TextBlock.width 200.0
                TextBlock.foreground dimBrush
            ]
            TextBlock.create [
                TextBlock.text (renderTomlValue v)
                TextBlock.textWrapping TextWrapping.Wrap
            ]
        ]
    ] :> _

let private stepLine (s: StepSummary) : IView =
    let msg =
        match s.Message, s.Reason with
        | Some m, _      -> sprintf " — %s" m
        | None, Some r   -> sprintf " — %s" r
        | None, None     -> ""
    StackPanel.create [
        StackPanel.orientation Orientation.Horizontal
        StackPanel.margin (Thickness(0.0, 2.0))
        StackPanel.children [
            TextBlock.create [
                TextBlock.text (statusIcon s.Status)
                TextBlock.foreground (statusBrush s.Status)
                TextBlock.width 24.0
            ]
            TextBlock.create [
                TextBlock.text (sprintf "%s  (%s)" s.Id s.Type)
                TextBlock.width 260.0
            ]
            TextBlock.create [
                TextBlock.text (formatDuration s.DurationSec)
                TextBlock.foreground mutedBrush
                TextBlock.width 60.0
            ]
            TextBlock.create [
                TextBlock.text msg
                TextBlock.foreground mutedBrush
                TextBlock.textWrapping TextWrapping.Wrap
            ]
        ]
    ] :> _

let private runDetailBody
        (pid: ProjectId)
        (entry: RunHistoryEntry)
        (steps: StepSummary list)
        : IView =
    let started  = entry.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
    let finished =
        entry.FinishedAt
        |> Option.map (fun t -> t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
        |> Option.defaultValue "(unfinished)"
    ScrollViewer.create [
        ScrollViewer.content (
            StackPanel.create [
                StackPanel.margin (Thickness 16.0)
                StackPanel.spacing 4.0
                StackPanel.children [
                    TextBlock.create [
                        TextBlock.text pid
                        TextBlock.foreground dimBrush
                        TextBlock.fontSize 12.0
                    ]
                    StackPanel.create [
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.spacing 12.0
                        StackPanel.children [
                            TextBlock.create [
                                TextBlock.text (statusIcon entry.Result)
                                TextBlock.foreground (statusBrush entry.Result)
                                TextBlock.fontSize 24.0
                            ]
                            TextBlock.create [
                                TextBlock.text entry.FlowId
                                TextBlock.fontSize 22.0
                                TextBlock.fontWeight FontWeight.SemiBold
                                TextBlock.verticalAlignment VerticalAlignment.Center
                            ]
                            TextBlock.create [
                                TextBlock.text (formatDuration entry.DurationSec)
                                TextBlock.foreground mutedBrush
                                TextBlock.fontSize 16.0
                                TextBlock.verticalAlignment VerticalAlignment.Center
                            ]
                        ]
                    ]
                    TextBlock.create [
                        TextBlock.text entry.RunId
                        TextBlock.foreground mutedBrush
                        TextBlock.fontSize 11.0
                    ]
                    TextBlock.create [
                        TextBlock.text (sprintf "Started:  %s" started)
                        TextBlock.foreground dimBrush
                    ]
                    TextBlock.create [
                        TextBlock.text (sprintf "Finished: %s" finished)
                        TextBlock.foreground dimBrush
                    ]
                    TextBlock.create [
                        TextBlock.text (sprintf "Trigger:  %s" entry.Trigger)
                        TextBlock.foreground dimBrush
                    ]
                    TextBlock.create [
                        TextBlock.text (sprintf "Dir:      %s" entry.RunDir)
                        TextBlock.foreground dimBrush
                        TextBlock.fontSize 11.0
                        TextBlock.textWrapping TextWrapping.Wrap
                    ]
                    sectionHeader "Parameters"
                    if Map.isEmpty entry.Params then
                        TextBlock.create [
                            TextBlock.text "(none)"
                            TextBlock.foreground mutedBrush
                        ] :> IView
                    else
                        StackPanel.create [
                            StackPanel.children [
                                for KeyValue (k, v) in entry.Params -> paramLine k v
                            ]
                        ] :> IView
                    sectionHeader "Steps"
                    if List.isEmpty steps then
                        TextBlock.create [
                            TextBlock.text "(no step summaries recorded)"
                            TextBlock.foreground mutedBrush
                        ] :> IView
                    else
                        StackPanel.create [
                            StackPanel.children [
                                for s in steps -> stepLine s
                            ]
                        ] :> IView
                ]
            ]
        )
    ] :> _

let private runDetailView
        (pid: ProjectId)
        (runId: RunId)
        (model: Model)
        (_dispatch: Msg -> unit)
        : IView =
    match Map.tryFind (pid, runId) model.RunDetails with
    | Some (entry, steps) -> runDetailBody pid entry steps
    | None ->
        TextBlock.create [
            TextBlock.text $"Run '{runId}' not found under project '{pid}'. The run dir may have been deleted, or this project's history was rotated since the tab was opened."
            TextBlock.margin (Thickness 16.0)
            TextBlock.foreground mutedBrush
            TextBlock.textWrapping TextWrapping.Wrap
        ] :> _

// ─── Top-level ──────────────────────────────────────────────────────────

let view (model: Model) (dispatch: Msg -> unit) : IView =
    DockPanel.create [
        DockPanel.children [
            rootTabStrip model dispatch
            (match model.ActiveTab with
             | Home                  -> homeView model dispatch
             | Project pid           -> projectView pid model dispatch
             | RunDetail (pid, rid)  -> runDetailView pid rid model dispatch)
        ]
    ] :> _
