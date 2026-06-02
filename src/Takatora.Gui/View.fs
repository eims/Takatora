module Takatora.Gui.View

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Input
open Avalonia.Interactivity
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Platform.Storage
open Avalonia.VisualTree
open Avalonia.Threading
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
let private rowHoverBg  = brush "#262626"
// Vertical divider between the global Home chip and the project-scoped
// chips — a structural cue reads more reliably across monitors than a
// subtle hue tint (which looked like hover/active or vanished entirely).
let private dividerBrush = brush "#4d4d4d"
let private transparentBrush = Brushes.Transparent :> IBrush
// Semi-transparent scrim behind the run-parameters modal (AARRGGBB).
let private overlayBg   = brush "#aa101010"
let private errorBrush  = brush "#f15a5a"

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

let private liveRunIcon (phase: LiveRunPhase) : string =
    match phase with
    | LivePending -> "▶"
    | LiveCompleted (Ok outcome) ->
        match outcome.Result with
        | RunResult.Success   -> "✓"
        | RunResult.Failure   -> "✗"
        | RunResult.Cancelled -> "⊘"
    | LiveCompleted (Error _) -> "✗"

let private tabLabel (model: Model) (tab: RootTab) : string =
    // Project name is intentionally dropped — the strip is scoped to one
    // project (shown in the pinned pill), so repeating it on every chip is
    // noise. RunDetail chips read "#N flow"; the serial number is the run's
    // chronological position in the project (see State.runNumber).
    match tab with
    | Home                  -> "Home"
    | Project pid           -> pid
    | RunDetail (pid, rid)  ->
        let flow = State.runFlowId model pid rid
        match State.runNumber model pid rid, flow with
        | Some n, Some f -> sprintf "#%d %s" n f
        | Some n, None   -> sprintf "#%d" n
        | None,   _      -> runShortLabel rid
    | LiveRun key ->
        match Map.tryFind key model.LiveRuns with
        | None   -> "Run"
        | Some s -> sprintf "%s %s" (liveRunIcon s.Phase) s.FlowId

let private tabClosable = function
    | Home -> false
    | _    -> true

/// Right-click menu on a tab chip: single close plus the bulk operations
/// (others / to the right), each disabled when there's nothing to act on.
let private tabContextMenu (model: Model) (tab: RootTab) (dispatch: Msg -> unit) : IView<ContextMenu> =
    let vis = State.visibleTabs model
    let othersExist = vis |> List.exists (fun t -> State.bulkClosable t && t <> tab)
    let rightExist =
        match List.tryFindIndex ((=) tab) vis with
        | Some i -> vis |> List.indexed |> List.exists (fun (j, t) -> j > i && State.bulkClosable t)
        | None   -> false
    // SubPatchOptions.Always throughout: the menu is rebuilt per chip and
    // the captured `tab` shifts as the strip re-renders (same FuncUI
    // closure-freezing trap as the chip buttons).
    ContextMenu.create [
        ContextMenu.viewItems [
            MenuItem.create [
                MenuItem.header "Close"
                MenuItem.onClick ((fun _ -> dispatch (CloseTab tab)), SubPatchOptions.Always)
            ]
            MenuItem.create [
                MenuItem.header "Close others"
                MenuItem.isEnabled othersExist
                MenuItem.onClick ((fun _ -> dispatch (CloseOtherTabs tab)), SubPatchOptions.Always)
            ]
            MenuItem.create [
                MenuItem.header "Close to the right"
                MenuItem.isEnabled rightExist
                MenuItem.onClick ((fun _ -> dispatch (CloseTabsToRight tab)), SubPatchOptions.Always)
            ]
        ]
    ]

let private tabChip
        (model: Model)
        (tab: RootTab)
        (isActive: bool)
        (dispatch: Msg -> unit)
        : IView =
    let baseAttrs = [
        Border.borderThickness (Thickness(0.0, 0.0, 0.0, 2.0))
        Border.borderBrush (if isActive then accent else stripBg)
        Border.background (if isActive then activeBg else stripBg)
        Border.child (
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.verticalAlignment VerticalAlignment.Center
                StackPanel.children [
                    Button.create [
                        Button.content (tabLabel model tab)
                        Button.background Brushes.Transparent
                        Button.borderThickness 0.0
                        Button.padding (Thickness(10.0, 4.0))
                        // SubPatchOptions.Always: this chip is reused by
                        // position across context switches, so the captured
                        // `tab` changes; without re-subscribing, the frozen
                        // first-render closure dispatches the wrong tab.
                        Button.onClick ((fun _ -> dispatch (ActivateTab tab)), SubPatchOptions.Always)
                    ]
                    if tabClosable tab then
                        Button.create [
                            Button.content "×"
                            Button.background Brushes.Transparent
                            Button.borderThickness 0.0
                            Button.padding (Thickness(6.0, 4.0))
                            Button.onClick ((fun _ -> dispatch (CloseTab tab)), SubPatchOptions.Always)
                        ]
                ]
            ]
        )
    ]
    // Attach the right-click bulk-close menu only to closable chips
    // (Home has nothing to close).
    let attrs =
        if tabClosable tab then
            Border.contextMenu (tabContextMenu model tab dispatch) :: baseAttrs
        else baseAttrs
    Border.create attrs :> _

/// One project-switch pill. Highlighted when it's the current context.
/// Pure click → OpenProject (idempotent on an already-open tab) — no
/// two-way binding, so none of the ComboBox focus/popup churn that was
/// eating the next click on the tab chips.
let private projectPill
        (pid: ProjectId)
        (isCurrent: bool)
        (dispatch: Msg -> unit)
        : IView =
    Button.create [
        Button.content ("\U0001F4C1  " + pid)
        Button.margin (Thickness(2.0, 0.0))
        Button.padding (Thickness(10.0, 4.0))
        Button.background (if isCurrent then activeBg else (Brushes.Transparent :> IBrush))
        Button.foreground (if isCurrent then (Brushes.White :> IBrush) else dimBrush)
        Button.borderBrush (if isCurrent then accent else stripBorder)
        Button.borderThickness (Thickness(1.0, 1.0, 1.0, if isCurrent then 2.0 else 1.0))
        Button.onClick ((fun _ -> dispatch (OpenProject pid)), SubPatchOptions.Always)
    ] :> _

/// Project-context switcher at the left of the strip: a pill per open
/// project (Home is always a separate chip). Picking one scopes the strip
/// to that project. Empty state shows a muted hint.
let private projectSelector (model: Model) (dispatch: Msg -> unit) : IView =
    let projects = State.openProjects model
    if List.isEmpty projects then
        TextBlock.create [
            TextBlock.text "No project open"
            TextBlock.foreground mutedBrush
            TextBlock.fontSize 12.0
            TextBlock.verticalAlignment VerticalAlignment.Center
            TextBlock.margin (Thickness(12.0, 0.0))
        ] :> _
    else
        let current = State.currentProject model
        StackPanel.create [
            StackPanel.orientation Orientation.Horizontal
            StackPanel.verticalAlignment VerticalAlignment.Center
            StackPanel.margin (Thickness(6.0, 0.0))
            StackPanel.children [
                for p in projects -> projectPill p (current = Some p) dispatch
            ]
        ] :> _

let private rootTabStrip (model: Model) (dispatch: Msg -> unit) : IView =
    Border.create [
        DockPanel.dock Dock.Top
        Border.borderThickness (Thickness(0.0, 0.0, 0.0, 1.0))
        Border.borderBrush stripBorder
        Border.background stripBg
        Border.height 40.0
        Border.child (
            // Home and the project root chip stay pinned (they're the
            // navigation anchors); only the per-run RunDetail/LiveRun chips
            // scroll when they overflow.
            let pinned, scrolling =
                State.visibleTabs model
                |> List.partition (function Home | Project _ -> true | _ -> false)
            DockPanel.create [
                DockPanel.children [
                    // Context switcher + pinned anchor chips — never scroll away.
                    StackPanel.create [
                        DockPanel.dock Dock.Left
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.children [
                            yield projectSelector model dispatch
                            for tab in pinned do
                                yield tabChip model tab (tab = model.ActiveTab) dispatch
                                // Separate the global Home chip from the
                                // project-scoped chips with a thin rule.
                                if tab = Home then
                                    yield Border.create [
                                        Border.width 1.0
                                        Border.margin (Thickness(4.0, 0.0))
                                        Border.background dividerBrush
                                    ] :> IView
                        ]
                    ]
                    // Per-run chips scroll horizontally when they overflow.
                    // The visible bar is hidden (Fluent's overlay bar is thick
                    // and covers the chip text); horizontal scroll is driven by
                    // the mouse wheel instead (Avalonia maps the wheel to the
                    // vertical axis by default, so we translate it manually).
                    ScrollViewer.create [
                        ScrollViewer.horizontalScrollBarVisibility ScrollBarVisibility.Hidden
                        ScrollViewer.verticalScrollBarVisibility ScrollBarVisibility.Disabled
                        ScrollViewer.onPointerWheelChanged (fun e ->
                            match (e.Source :?> Visual).FindAncestorOfType<ScrollViewer>() with
                            | null -> ()
                            | sv ->
                                sv.Offset <- Vector(sv.Offset.X - e.Delta.Y * 60.0, sv.Offset.Y)
                                e.Handled <- true)
                        ScrollViewer.content (
                            StackPanel.create [
                                StackPanel.orientation Orientation.Horizontal
                                StackPanel.children [
                                    for tab in scrolling do
                                        yield tabChip model tab (tab = model.ActiveTab) dispatch
                                ]
                            ]
                        )
                    ]
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

let private engineChoiceButton
        (label: string)
        (isActive: bool)
        (onClick: unit -> unit)
        : IView =
    Button.create [
        Button.content label
        Button.background (if isActive then activeBg else (Brushes.Transparent :> IBrush))
        Button.foreground (if isActive then (Brushes.White :> IBrush) else dimBrush)
        Button.borderBrush (if isActive then accent else stripBorder)
        Button.borderThickness (Thickness 1.0)
        Button.padding (Thickness(16.0, 6.0))
        Button.onClick (fun _ -> onClick ())
    ] :> _

/// Open a native folder picker and feed the chosen path back into the
/// Add-Project form. Reaches the StorageProvider through the TopLevel of
/// whatever control raised the click — the only handle we have to the
/// window from inside a stateless FuncUI view.
let private browseForFolder (dispatch: Msg -> unit) (e: RoutedEventArgs) : unit =
    match e.Source with
    | :? Visual as v ->
        match TopLevel.GetTopLevel v with
        | null -> ()
        | top ->
            async {
                let opts =
                    FolderPickerOpenOptions(
                        Title = "Select project folder",
                        AllowMultiple = false)
                let! folders =
                    top.StorageProvider.OpenFolderPickerAsync opts |> Async.AwaitTask
                match Seq.tryHead folders with
                | Some folder ->
                    // Prefer the real filesystem path; some providers only
                    // expose a non-file URI, in which case we skip rather
                    // than feed the form a bogus path.
                    match folder.TryGetLocalPath() with
                    | null -> ()
                    | path ->
                        Dispatcher.UIThread.Post(fun () ->
                            dispatch (AddProjectSetDir path))
                | None -> ()
            }
            // onClick runs on the UI thread; StartImmediate keeps the
            // picker's continuation on the captured UI sync-context.
            |> Async.StartImmediate
    | _ -> ()

/// Open a native folder/file picker and feed the chosen path into the run
/// dialog field `name`. `pickFile=false` picks a directory (path/dir
/// kinds), `true` picks a file (file kind). Same TopLevel-reaching trick
/// as browseForFolder.
let private browseForRunValue
        (name: string)
        (pickFile: bool)
        (dispatch: Msg -> unit)
        (e: RoutedEventArgs)
        : unit =
    match e.Source with
    | :? Visual as v ->
        match TopLevel.GetTopLevel v with
        | null -> ()
        | top ->
            async {
                let! picked =
                    if pickFile then
                        async {
                            let opts = FilePickerOpenOptions(Title = "Select file", AllowMultiple = false)
                            let! files = top.StorageProvider.OpenFilePickerAsync opts |> Async.AwaitTask
                            return Seq.tryHead (files |> Seq.cast<IStorageItem>)
                        }
                    else
                        async {
                            let opts = FolderPickerOpenOptions(Title = "Select folder", AllowMultiple = false)
                            let! folders = top.StorageProvider.OpenFolderPickerAsync opts |> Async.AwaitTask
                            return Seq.tryHead (folders |> Seq.cast<IStorageItem>)
                        }
                match picked with
                | Some item ->
                    match item.TryGetLocalPath() with
                    | null -> ()
                    | path ->
                        Dispatcher.UIThread.Post(fun () ->
                            dispatch (RunDialogSetValue (name, path)))
                | None -> ()
            }
            |> Async.StartImmediate
    | _ -> ()

let private addProjectForm (form: AddProjectForm) (dispatch: Msg -> unit) : IView =
    let fieldLabel (text: string) : IView =
        TextBlock.create [
            TextBlock.text text
            TextBlock.foreground dimBrush
            TextBlock.fontSize 12.0
            TextBlock.margin (Thickness(0.0, 6.0, 0.0, 2.0))
        ] :> _
    Border.create [
        Border.padding (Thickness 16.0)
        Border.cornerRadius 4.0
        Border.background cardBg
        Border.borderBrush stripBorder
        Border.borderThickness (Thickness 1.0)
        Border.margin (Thickness(0.0, 0.0, 0.0, 12.0))
        Border.child (
            StackPanel.create [
                StackPanel.spacing 2.0
                StackPanel.children [
                    TextBlock.create [
                        TextBlock.text "Add a project"
                        TextBlock.fontSize 16.0
                        TextBlock.fontWeight FontWeight.SemiBold
                    ]
                    TextBlock.create [
                        TextBlock.text "Creates the folder (if needed) and a starter .ci/project.toml, then registers it."
                        TextBlock.foreground mutedBrush
                        TextBlock.fontSize 12.0
                        TextBlock.textWrapping TextWrapping.Wrap
                    ]

                    fieldLabel "Directory"
                    DockPanel.create [
                        DockPanel.children [
                            Button.create [
                                DockPanel.dock Dock.Right
                                Button.content "Browse…"
                                Button.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                                Button.onClick (browseForFolder dispatch)
                            ]
                            TextBox.create [
                                TextBox.text form.Dir
                                TextBox.watermark "C:\\path\\to\\your\\game"
                                TextBox.onTextChanged (fun s -> dispatch (AddProjectSetDir s))
                            ]
                        ]
                    ]

                    fieldLabel "Project name (optional — defaults to folder name)"
                    TextBox.create [
                        TextBox.text form.Name
                        TextBox.watermark "my-game"
                        TextBox.onTextChanged (fun s -> dispatch (AddProjectSetName s))
                    ]

                    fieldLabel "Engine"
                    StackPanel.create [
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.spacing 8.0
                        StackPanel.children [
                            engineChoiceButton "Unreal" (form.Engine = EngineKind.Unreal) (fun () -> dispatch (AddProjectSetEngine EngineKind.Unreal))
                            engineChoiceButton "Unity"  (form.Engine = EngineKind.Unity)  (fun () -> dispatch (AddProjectSetEngine EngineKind.Unity))
                            engineChoiceButton "Godot"  (form.Engine = EngineKind.Godot)  (fun () -> dispatch (AddProjectSetEngine EngineKind.Godot))
                        ]
                    ]

                    (match form.Error with
                     | Some msg ->
                         TextBlock.create [
                             TextBlock.text msg
                             TextBlock.foreground (brush "#f15a5a")
                             TextBlock.textWrapping TextWrapping.Wrap
                             TextBlock.margin (Thickness(0.0, 8.0, 0.0, 0.0))
                         ] :> IView
                     | None ->
                         TextBlock.create [ TextBlock.text "" ] :> IView)

                    StackPanel.create [
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.spacing 8.0
                        StackPanel.margin (Thickness(0.0, 12.0, 0.0, 0.0))
                        StackPanel.children [
                            Button.create [
                                Button.content "Create & Register"
                                Button.background accent
                                Button.foreground (Brushes.White :> IBrush)
                                Button.onClick (fun _ -> dispatch SubmitAddProject)
                            ]
                            Button.create [
                                Button.content "Cancel"
                                Button.onClick (fun _ -> dispatch HideAddProject)
                            ]
                        ]
                    ]
                ]
            ]
        )
    ] :> _

let private homeBody (model: Model) (dispatch: Msg -> unit) : IView =
    let projectList : IView =
        if List.isEmpty model.Projects then
            TextBlock.create [
                TextBlock.text "No projects registered yet. Click \"Add Project\" above, or use `takatora project add <path>` from the CLI."
                TextBlock.foreground mutedBrush
                TextBlock.textWrapping TextWrapping.Wrap
            ] :> _
        else
            StackPanel.create [
                StackPanel.spacing 6.0
                StackPanel.children [
                    for p in model.Projects -> projectRow p dispatch
                ]
            ] :> _
    ScrollViewer.create [
        ScrollViewer.content (
            StackPanel.create [
                StackPanel.margin (Thickness(16.0, 0.0, 16.0, 16.0))
                StackPanel.spacing 6.0
                StackPanel.children [
                    (match model.AddProject with
                     | Some form -> addProjectForm form dispatch
                     | None      -> StackPanel.create [] :> IView)
                    projectList
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
                        Button.content (if model.AddProject.IsSome then "Close" else "Add Project")
                        Button.onClick (fun _ ->
                            dispatch (if model.AddProject.IsSome then HideAddProject else ShowAddProject))
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

let private flowCard
        (pid: ProjectId)
        (f: Flow)
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
                        Button.content "Run"
                        Button.verticalAlignment VerticalAlignment.Center
                        Button.onClick ((fun _ -> dispatch (RequestRun (pid, f.Id))), SubPatchOptions.Always)
                    ]
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
                            for f in fs -> flowCard pid f dispatch
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

/// Keychain-backed secrets for this project, with per-entry delete. The
/// names come from the model cache (State.ProjectSecrets), refreshed when
/// Settings is opened/refreshed and updated in-place on delete — so the
/// list stays a pure function of model state (and re-renders reliably).
let private secretsBlock (pid: ProjectId) (names: string list) (dispatch: Msg -> unit) : IView =
    let rows : IView =
        if List.isEmpty names then
            TextBlock.create [
                TextBlock.text "No secrets stored for this project."
                TextBlock.foreground mutedBrush
            ] :> _
        else
            StackPanel.create [
                StackPanel.spacing 4.0
                StackPanel.children [
                    for n in names ->
                        DockPanel.create [
                            DockPanel.children [
                                Button.create [
                                    DockPanel.dock Dock.Right
                                    Button.content "Delete"
                                    Button.onClick ((fun _ -> dispatch (DeleteSecret (pid, n))), SubPatchOptions.Always)
                                ] :> IView
                                TextBlock.create [
                                    TextBlock.text n
                                    TextBlock.verticalAlignment VerticalAlignment.Center
                                ] :> IView
                            ]
                        ] :> IView
                ]
            ] :> _
    StackPanel.create [
        StackPanel.spacing 2.0
        StackPanel.children [
            sectionHeader "Secrets"
            (TextBlock.create [
                TextBlock.text "Stored in the OS keychain; the flow's secret vars read these at run time."
                TextBlock.foreground mutedBrush
                TextBlock.fontSize 12.0
                TextBlock.textWrapping TextWrapping.Wrap
                TextBlock.margin (Thickness(0.0, 0.0, 0.0, 6.0))
             ] :> IView)
            rows
        ]
    ] :> _

let private settingsBody
        (pid: ProjectId)
        (load: ProjectInfoLoad)
        (secrets: string list)
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
                        StackPanel.spacing 8.0
                        StackPanel.children [
                            projectInfoBlock proj
                            secretsBlock pid secrets dispatch
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
        cell 0 "#"
        cell 1 " "
        cell 2 "flow"
        cell 3 "started"
        cell 4 "duration"
        cell 5 "run id"
        cell 6 " "
    ]

let private historyDataRow
        (row: int)
        (num: int)
        (pid: ProjectId)
        (isOpen: bool)
        (e: RunHistoryEntry)
        (dispatch: Msg -> unit)
        : IView list =
    let startedLocal = e.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
    let cellMargin = Thickness(8.0, 3.0)
    let cells = [
        // Clickable backdrop spanning the whole row. Transparent (not null)
        // background so the Border catches pointer events; the text cells
        // set IsHitTestVisible=false so clicks fall through to it. The close
        // button (column 6) is hit-test-visible and rendered on top, so it
        // intercepts its own clicks without also opening the run.
        Border.create [
            Grid.row row
            Grid.column 0
            Grid.columnSpan 7
            Border.background transparentBrush
            Border.cursor handCursor
            // Light hover highlight. Mutated directly on the backdrop Border
            // (hover doesn't trigger an Elmish re-render, so there's no model
            // state to thread); text cells are hit-test-invisible so moving
            // across them keeps the row "entered".
            Border.onPointerEntered (fun e ->
                match e.Source with :? Border as b -> b.Background <- rowHoverBg | _ -> ())
            Border.onPointerExited (fun e ->
                match e.Source with :? Border as b -> b.Background <- transparentBrush | _ -> ())
            Border.onPointerPressed (fun _ -> dispatch (OpenRunDetail (pid, e.RunId)))
        ] :> IView
        // Serial number — matches the "#N" shown on the RunDetail tab chip.
        TextBlock.create [
            Grid.row row
            Grid.column 0
            TextBlock.text (sprintf "#%d" num)
            TextBlock.foreground mutedBrush
            TextBlock.margin cellMargin
            TextBlock.isHitTestVisible false
        ] :> IView
        TextBlock.create [
            Grid.row row
            Grid.column 1
            TextBlock.text (statusIcon e.Result)
            TextBlock.foreground (statusBrush e.Result)
            TextBlock.margin cellMargin
            TextBlock.isHitTestVisible false
        ] :> IView
        TextBlock.create [
            Grid.row row
            Grid.column 2
            TextBlock.text e.FlowId
            TextBlock.margin cellMargin
            TextBlock.isHitTestVisible false
        ] :> IView
        TextBlock.create [
            Grid.row row
            Grid.column 3
            TextBlock.text startedLocal
            TextBlock.margin cellMargin
            TextBlock.isHitTestVisible false
        ] :> IView
        TextBlock.create [
            Grid.row row
            Grid.column 4
            TextBlock.text (formatDuration e.DurationSec)
            TextBlock.margin cellMargin
            TextBlock.isHitTestVisible false
        ] :> IView
        TextBlock.create [
            Grid.row row
            Grid.column 5
            TextBlock.text e.RunId
            TextBlock.foreground mutedBrush
            TextBlock.fontSize 11.0
            TextBlock.margin cellMargin
            TextBlock.isHitTestVisible false
        ] :> IView
    ]
    // Close-tab affordance, shown only when this run's RunDetail tab is
    // open. Sits in column 6 immediately after the run id (left-aligned),
    // on top of the backdrop so it intercepts its own click.
    let closeCell =
        if isOpen then
            [ Button.create [
                Grid.row row
                Grid.column 6
                Button.content "×"
                Button.background Brushes.Transparent
                Button.borderThickness 0.0
                Button.foreground mutedBrush
                Button.padding (Thickness(8.0, 0.0))
                Button.verticalAlignment VerticalAlignment.Center
                Button.horizontalAlignment HorizontalAlignment.Left
                Button.onClick (
                    (fun _ -> dispatch (CloseTab (RunDetail (pid, e.RunId)))),
                    SubPatchOptions.Always)
              ] :> IView ]
        else []
    cells @ closeCell

let private historyBody
        (pid: ProjectId)
        (entries: RunHistoryEntry list)
        (openRunIds: Set<RunId>)
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
                            Grid.columnDefinitions "Auto,Auto,Auto,Auto,Auto,Auto,*"
                            Grid.rowDefinitions
                                (String.concat ","
                                    (List.replicate (List.length entries + 1) "Auto"))
                            Grid.children [
                                let total = List.length entries
                                yield! historyHeaderRow
                                // entries are newest-first; chronological # is
                                // total - i so it matches State.runNumber.
                                for i, e in List.indexed entries do
                                    let isOpen = Set.contains e.RunId openRunIds
                                    yield! historyDataRow (i + 1) (total - i) pid isOpen e dispatch
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
                        // The path itself is the click target to open the
                        // project folder. A Button styled to look like the
                        // plain path text — onClick is managed correctly by
                        // FuncUI (unlike onPointerPressed + Always, which
                        // re-subscribes every render → multi-fire).
                        Button.create [
                            Button.content p.Path
                            Button.foreground mutedBrush
                            Button.fontSize 12.0
                            Button.background transparentBrush
                            Button.borderThickness (Thickness 0.0)
                            Button.padding (Thickness 0.0)
                            Button.cursor handCursor
                            Button.horizontalAlignment HorizontalAlignment.Left
                            Button.onClick ((fun _ -> dispatch (OpenInExplorer p.Path)), SubPatchOptions.Always)
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
                     // Run ids that currently have an open RunDetail tab, so
                     // the History list can show a close affordance per row.
                     let openRunIds =
                         model.OpenTabs
                         |> List.choose (function
                             | RunDetail (p, r) when p = pid -> Some r
                             | _ -> None)
                         |> Set.ofList
                     historyBody pid entries openRunIds dispatch
                 | ProjectSettings ->
                     let load =
                         Map.tryFind pid model.ProjectInfo
                         |> Option.defaultValue ProjectInfoMissing
                     let secrets =
                         Map.tryFind pid model.ProjectSecrets
                         |> Option.defaultValue []
                     settingsBody pid load secrets dispatch)
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
        (dispatch: Msg -> unit)
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
                    DockPanel.create [
                        DockPanel.margin (Thickness(0.0, 2.0, 0.0, 0.0))
                        DockPanel.children [
                            Button.create [
                                DockPanel.dock Dock.Right
                                Button.content "Open folder"
                                Button.verticalAlignment VerticalAlignment.Top
                                Button.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                                Button.onClick ((fun _ -> dispatch (OpenInExplorer entry.RunDir)), SubPatchOptions.Always)
                            ]
                            TextBlock.create [
                                TextBlock.text (sprintf "Dir:      %s" entry.RunDir)
                                TextBlock.foreground dimBrush
                                TextBlock.fontSize 11.0
                                TextBlock.textWrapping TextWrapping.Wrap
                                TextBlock.verticalAlignment VerticalAlignment.Center
                            ]
                        ]
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

/// Prev/next navigation across runs by serial number, so you can step
/// through a project's history without going back to the History list.
/// "← #(N-1)" is the next-older run, "#(N+1) →" the next-newer.
let private runDetailNav
        (pid: ProjectId)
        (runId: RunId)
        (model: Model)
        (dispatch: Msg -> unit)
        : IView =
    let navButton (label: string) (target: RunId option) : IView =
        Button.create [
            Button.content label
            Button.isEnabled (Option.isSome target)
            Button.background Brushes.Transparent
            // Reused by position as the active run changes, so re-subscribe
            // the click handler (FuncUI freezes the first-render closure).
            Button.onClick (
                (fun _ -> target |> Option.iter (fun rid -> dispatch (OpenRunDetail (pid, rid)))),
                SubPatchOptions.Always)
        ] :> IView
    let older = State.runByNumberOffset model pid runId -1
    let newer = State.runByNumberOffset model pid runId 1
    let currentLabel =
        match State.runNumber model pid runId with
        | Some n -> sprintf "#%d" n
        | None   -> "—"
    Border.create [
        DockPanel.dock Dock.Top
        Border.borderThickness (Thickness(0.0, 0.0, 0.0, 1.0))
        Border.borderBrush stripBorder
        Border.padding (Thickness(12.0, 4.0))
        Border.child (
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.spacing 8.0
                StackPanel.children [
                    navButton "←  older" older
                    TextBlock.create [
                        TextBlock.text currentLabel
                        TextBlock.foreground dimBrush
                        TextBlock.verticalAlignment VerticalAlignment.Center
                    ]
                    navButton "newer  →" newer
                ]
            ]
        )
    ] :> _

let private runDetailView
        (pid: ProjectId)
        (runId: RunId)
        (model: Model)
        (dispatch: Msg -> unit)
        : IView =
    match Map.tryFind (pid, runId) model.RunDetails with
    | Some (entry, steps) ->
        DockPanel.create [
            DockPanel.children [
                runDetailNav pid runId model dispatch
                runDetailBody pid entry steps dispatch
            ]
        ] :> _
    | None ->
        TextBlock.create [
            TextBlock.text $"Run '{runId}' not found under project '{pid}'. The run dir may have been deleted, or this project's history was rotated since the tab was opened."
            TextBlock.margin (Thickness 16.0)
            TextBlock.foreground mutedBrush
            TextBlock.textWrapping TextWrapping.Wrap
        ] :> _

// ─── LiveRun tab ────────────────────────────────────────────────────────

let private failureMessage = function
    | RunFailure.FlowNotFound flowId   -> sprintf "flow '%s' not found in flows.toml" flowId
    | RunFailure.TaskNotFound stepType -> sprintf "no task .fsx for type '%s'" stepType
    | RunFailure.ConfigError (src, m)  -> sprintf "config error in %s: %s" src m
    | RunFailure.InternalError m       -> sprintf "internal error: %s" m

let private liveRunOutcomeBlock
        (outcome: RunOutcome)
        (dispatch: Msg -> unit)
        (pid: ProjectId)
        : IView =
    let resultLabel =
        match outcome.Result with
        | RunResult.Success   -> "success"
        | RunResult.Failure   -> "failure"
        | RunResult.Cancelled -> "cancelled"
    let resultBrush =
        match outcome.Result with
        | RunResult.Success   -> brush "#4ec97a"
        | RunResult.Failure   -> brush "#f15a5a"
        | RunResult.Cancelled -> mutedBrush
    StackPanel.create [
        StackPanel.spacing 4.0
        StackPanel.children [
            TextBlock.create [
                TextBlock.text (sprintf "Completed: %s" resultLabel)
                TextBlock.foreground resultBrush
                TextBlock.fontSize 16.0
            ]
            TextBlock.create [
                TextBlock.text
                    (sprintf "Duration: %.2fs · Run id: %s"
                        (outcome.FinishedAt - outcome.StartedAt).TotalSeconds
                        outcome.RunId)
                TextBlock.foreground mutedBrush
            ]
            Button.create [
                Button.content "Open run details"
                Button.margin (Thickness(0.0, 8.0, 0.0, 0.0))
                Button.horizontalAlignment HorizontalAlignment.Left
                Button.onClick (fun _ -> dispatch (OpenRunDetail (pid, outcome.RunId)))
            ]
        ]
    ] :> _

let private liveStepIcon = function
    | StepRunning   -> "▶"
    | StepOk        -> "✓"
    | StepFailed    -> "✗"
    | StepSkipped   -> "·"
    | StepCancelled -> "⊘"

let private liveStepBrush = function
    | StepRunning   -> accent
    | StepOk        -> brush "#4ec97a"
    | StepFailed    -> brush "#f15a5a"
    | StepSkipped   -> mutedBrush
    | StepCancelled -> mutedBrush

let private liveStepLine (step: LiveStep) : IView =
    StackPanel.create [
        StackPanel.orientation Orientation.Horizontal
        StackPanel.spacing 8.0
        StackPanel.margin (Thickness(0.0, 2.0))
        StackPanel.children [
            TextBlock.create [
                TextBlock.text (liveStepIcon step.Status)
                TextBlock.foreground (liveStepBrush step.Status)
                TextBlock.width 20.0
            ]
            TextBlock.create [
                TextBlock.text step.Id
                TextBlock.width 180.0
            ]
            TextBlock.create [
                TextBlock.text step.Type
                TextBlock.foreground mutedBrush
                TextBlock.width 160.0
            ]
            TextBlock.create [
                TextBlock.text
                    (match step.Status with
                     | StepRunning -> "running…"
                     | _           -> formatDuration step.DurationSec)
                TextBlock.foreground mutedBrush
            ]
        ]
    ] :> _

let private liveStepsSection (steps: LiveStep list) : IView =
    StackPanel.create [
        StackPanel.children [
            sectionHeader "Steps"
            StackPanel.create [
                StackPanel.children [ for st in steps -> liveStepLine st ]
            ]
        ]
    ] :> _

let private liveLogSection (logTail: string list) : IView =
    StackPanel.create [
        StackPanel.children [
            sectionHeader "Output (tail)"
            Border.create [
                Border.background (brush "#161616")
                Border.borderBrush stripBorder
                Border.borderThickness (Thickness 1.0)
                Border.cornerRadius 4.0
                Border.padding (Thickness 8.0)
                Border.maxHeight 280.0
                Border.child (
                    ScrollViewer.create [
                        ScrollViewer.content (
                            TextBlock.create [
                                TextBlock.text (String.concat "\n" logTail)
                                TextBlock.fontFamily (FontFamily "Consolas, Menlo, monospace")
                                TextBlock.fontSize 12.0
                                TextBlock.foreground dimBrush
                                TextBlock.textWrapping TextWrapping.NoWrap
                            ]
                        )
                    ]
                )
            ]
        ]
    ] :> _

let private liveRunBody
        (key: LiveRunKey)
        (s: LiveRunState)
        (dispatch: Msg -> unit)
        : IView =
    let started = s.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
    let phaseBlock : IView =
        match s.Phase with
        | LivePending ->
            StackPanel.create [
                StackPanel.spacing 8.0
                StackPanel.margin (Thickness(0.0, 8.0, 0.0, 0.0))
                StackPanel.children [
                    TextBlock.create [
                        TextBlock.text "Running on a background thread. Steps and output below update live as the run progresses."
                        TextBlock.foreground mutedBrush
                        TextBlock.textWrapping TextWrapping.Wrap
                    ]
                    Button.create [
                        Button.content (if s.CancelRequested then "Cancelling…" else "Cancel")
                        Button.horizontalAlignment HorizontalAlignment.Left
                        // Only enabled once the poller has matched the run
                        // dir (so we know where to drop CANCEL) and not yet
                        // requested.
                        Button.isEnabled
                            (not s.CancelRequested && Option.isSome s.Progress.RunDir)
                        Button.onClick (fun _ -> dispatch (CancelLiveRun key))
                    ]
                ]
            ] :> IView
        | LiveCompleted (Ok outcome) -> liveRunOutcomeBlock outcome dispatch s.ProjectId
        | LiveCompleted (Error failure) ->
            StackPanel.create [
                StackPanel.spacing 4.0
                StackPanel.children [
                    TextBlock.create [
                        TextBlock.text "Failed before producing an outcome:"
                        TextBlock.foreground (brush "#f15a5a")
                        TextBlock.fontSize 16.0
                    ]
                    TextBlock.create [
                        TextBlock.text (failureMessage failure)
                        TextBlock.foreground dimBrush
                        TextBlock.textWrapping TextWrapping.Wrap
                    ]
                ]
            ] :> IView
    ScrollViewer.create [
        ScrollViewer.content (
            StackPanel.create [
                StackPanel.margin (Thickness 16.0)
                StackPanel.spacing 6.0
                StackPanel.children [
                    TextBlock.create [
                        TextBlock.text s.ProjectId
                        TextBlock.foreground dimBrush
                        TextBlock.fontSize 12.0
                    ]
                    StackPanel.create [
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.spacing 12.0
                        StackPanel.children [
                            TextBlock.create [
                                TextBlock.text (liveRunIcon s.Phase)
                                TextBlock.fontSize 24.0
                                TextBlock.foreground
                                    (match s.Phase with
                                     | LivePending -> accent
                                     | LiveCompleted (Ok o) ->
                                         (match o.Result with
                                          | RunResult.Success   -> brush "#4ec97a"
                                          | RunResult.Failure   -> brush "#f15a5a"
                                          | RunResult.Cancelled -> mutedBrush)
                                     | LiveCompleted (Error _) -> brush "#f15a5a")
                            ]
                            TextBlock.create [
                                TextBlock.text s.FlowId
                                TextBlock.fontSize 22.0
                                TextBlock.fontWeight FontWeight.SemiBold
                                TextBlock.verticalAlignment VerticalAlignment.Center
                            ]
                        ]
                    ]
                    TextBlock.create [
                        TextBlock.text (sprintf "Started: %s" started)
                        TextBlock.foreground dimBrush
                    ]
                    phaseBlock
                    if not (List.isEmpty s.Progress.Steps) then
                        liveStepsSection s.Progress.Steps
                    if not (List.isEmpty s.Progress.LogTail) then
                        liveLogSection s.Progress.LogTail
                ]
            ]
        )
    ] :> _

let private liveRunView
        (key: LiveRunKey)
        (model: Model)
        (dispatch: Msg -> unit)
        : IView =
    match Map.tryFind key model.LiveRuns with
    | Some s -> liveRunBody key s dispatch
    | None ->
        TextBlock.create [
            TextBlock.text "(LiveRun state unavailable — the run completed and its tab state was cleaned up.)"
            TextBlock.margin (Thickness 16.0)
            TextBlock.foreground mutedBrush
            TextBlock.textWrapping TextWrapping.Wrap
        ] :> _

// ─── Run-with-parameters modal ──────────────────────────────────────────

/// One field row: a label + a kind-appropriate inline widget. Bool and
/// enum use pill buttons (the proven click-only pattern — FuncUI ComboBox
/// churns focus in re-rendered regions, see slice 14); everything else is
/// a text box. Values are parsed/validated at confirm, not per-keystroke.
let private runDialogField
        (v: FlowVar)
        (current: string)
        (items: string list)
        (isRemembered: bool)
        (isStored: bool)
        (dispatch: Msg -> unit)
        : IView =
    // Fixed-width label column on the left, control fills the rest, so the
    // control column starts at the same x on every row (calm single-column
    // read). Pill groups left-align within the control column.
    let label =
        TextBlock.create [
            DockPanel.dock Dock.Left
            TextBlock.text v.Name
            TextBlock.width 110.0
            TextBlock.textAlignment TextAlignment.Right
            TextBlock.foreground dimBrush
            TextBlock.fontSize 12.0
            TextBlock.margin (Thickness(0.0, 0.0, 12.0, 0.0))
            TextBlock.verticalAlignment VerticalAlignment.Center
        ] :> IView
    let widget : IView =
        match v.Kind with
        | VarKind.Bool ->
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.spacing 8.0
                StackPanel.children [
                    engineChoiceButton "true"  (current = "true")  (fun () -> dispatch (RunDialogSetValue (v.Name, "true")))
                    engineChoiceButton "false" (current = "false") (fun () -> dispatch (RunDialogSetValue (v.Name, "false")))
                ]
            ] :> _
        | VarKind.Enum values ->
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.spacing 8.0
                StackPanel.children [
                    for vv in values ->
                        engineChoiceButton vv (current = vv) (fun () -> dispatch (RunDialogSetValue (v.Name, vv)))
                ]
            ] :> _
        | VarKind.Multiline ->
            TextBox.create [
                TextBox.text current
                TextBox.acceptsReturn true
                TextBox.textWrapping TextWrapping.Wrap
                TextBox.minHeight 64.0
                TextBox.onTextChanged (fun s -> dispatch (RunDialogSetValue (v.Name, s)))
            ] :> _
        | VarKind.Path | VarKind.Dir | VarKind.File ->
            // Text field + native picker (folder for path/dir, file for file).
            let pickFile = (v.Kind = VarKind.File)
            DockPanel.create [
                DockPanel.children [
                    Button.create [
                        DockPanel.dock Dock.Right
                        Button.content "Browse…"
                        Button.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                        Button.onClick ((fun e -> browseForRunValue v.Name pickFile dispatch e), SubPatchOptions.Always)
                    ]
                    TextBox.create [
                        TextBox.text current
                        TextBox.verticalAlignment VerticalAlignment.Center
                        TextBox.onTextChanged (fun s -> dispatch (RunDialogSetValue (v.Name, s)))
                    ]
                ]
            ] :> _
        | VarKind.List _ ->
            // One TextBox per array element with a "−" remove, plus "+ Add".
            // Blank rows are dropped at confirm, so an empty trailing row is
            // harmless.
            StackPanel.create [
                StackPanel.spacing 4.0
                StackPanel.children [
                    yield! items |> List.mapi (fun i item ->
                        DockPanel.create [
                            DockPanel.children [
                                Button.create [
                                    DockPanel.dock Dock.Right
                                    Button.content "−"
                                    Button.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                                    Button.onClick ((fun _ -> dispatch (RunDialogListRemove (v.Name, i))), SubPatchOptions.Always)
                                ]
                                TextBox.create [
                                    TextBox.text item
                                    TextBox.verticalAlignment VerticalAlignment.Center
                                    TextBox.onTextChanged
                                        ((fun s -> dispatch (RunDialogListSetItem (v.Name, i, s))), SubPatchOptions.Always)
                                ]
                            ]
                        ] :> IView)
                    yield Button.create [
                        Button.content "+ Add"
                        Button.horizontalAlignment HorizontalAlignment.Left
                        Button.onClick ((fun _ -> dispatch (RunDialogListAdd v.Name)), SubPatchOptions.Always)
                    ] :> IView
                ]
            ] :> _
        | VarKind.Secret ->
            // Masked input + a Remember toggle; a Forget button + "saved ✓"
            // appear once a keychain entry exists. The real value is kept in
            // memory only — the runner redacts it from disk artifacts.
            StackPanel.create [
                StackPanel.spacing 6.0
                StackPanel.children [
                    TextBox.create [
                        TextBox.text current
                        TextBox.passwordChar '●'
                        TextBox.verticalAlignment VerticalAlignment.Center
                        TextBox.onTextChanged (fun s -> dispatch (RunDialogSetValue (v.Name, s)))
                    ]
                    StackPanel.create [
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.spacing 8.0
                        StackPanel.children [
                            yield engineChoiceButton "Remember" isRemembered
                                    (fun () -> dispatch (RunDialogToggleRemember v.Name))
                            if isStored then
                                yield TextBlock.create [
                                    TextBlock.text "saved ✓"
                                    TextBlock.foreground mutedBrush
                                    TextBlock.fontSize 12.0
                                    TextBlock.verticalAlignment VerticalAlignment.Center
                                ] :> IView
                                yield Button.create [
                                    Button.content "Forget"
                                    Button.onClick ((fun _ -> dispatch (RunDialogForget v.Name)), SubPatchOptions.Always)
                                ] :> IView
                        ]
                    ]
                ]
            ] :> _
        | _ ->
            TextBox.create [
                TextBox.text current
                TextBox.verticalAlignment VerticalAlignment.Center
                TextBox.onTextChanged (fun s -> dispatch (RunDialogSetValue (v.Name, s)))
            ] :> _
    DockPanel.create [
        DockPanel.margin (Thickness(0.0, 0.0, 0.0, 10.0))
        DockPanel.children [ label; widget ]
    ] :> _

let private runParamsDialog (d: RunDialogState) (dispatch: Msg -> unit) : IView =
    // Full-bleed scrim with a centered card on top.
    Border.create [
        Border.background overlayBg
        Border.child (
            Border.create [
                Border.horizontalAlignment HorizontalAlignment.Center
                Border.verticalAlignment VerticalAlignment.Center
                Border.width 460.0
                Border.padding (Thickness 20.0)
                Border.cornerRadius 6.0
                Border.background cardBg
                Border.borderBrush stripBorder
                Border.borderThickness (Thickness 1.0)
                Border.child (
                    StackPanel.create [
                        StackPanel.spacing 0.0
                        StackPanel.children [
                            yield DockPanel.create [
                                DockPanel.margin (Thickness(0.0, 0.0, 0.0, 16.0))
                                DockPanel.children [
                                    Button.create [
                                        DockPanel.dock Dock.Right
                                        Button.content "Reset"
                                        Button.verticalAlignment VerticalAlignment.Center
                                        Button.onClick ((fun _ -> dispatch RunDialogReset), SubPatchOptions.Always)
                                    ]
                                    StackPanel.create [
                                        StackPanel.spacing 1.0
                                        StackPanel.verticalAlignment VerticalAlignment.Center
                                        StackPanel.children [
                                            // Project context first (accent), so an
                                            // interrupted user doesn't lose track of
                                            // which project + job this run is for.
                                            TextBlock.create [
                                                TextBlock.text d.ProjectId
                                                TextBlock.fontSize 12.0
                                                TextBlock.fontWeight FontWeight.SemiBold
                                                TextBlock.foreground accent
                                            ]
                                            TextBlock.create [
                                                TextBlock.text (sprintf "Run \"%s\" with parameters" d.FlowId)
                                                TextBlock.fontSize 16.0
                                                TextBlock.fontWeight FontWeight.SemiBold
                                            ]
                                        ]
                                    ]
                                ]
                            ] :> IView
                            for v in d.Vars do
                                let current =
                                    Map.tryFind v.Name d.Values
                                    |> Option.defaultValue (varDefaultText v)
                                let items = Map.tryFind v.Name d.Lists |> Option.defaultValue []
                                yield runDialogField v current items
                                        (Set.contains v.Name d.Remember)
                                        (Set.contains v.Name d.Stored)
                                        dispatch
                            match d.Error with
                            | Some msg ->
                                yield TextBlock.create [
                                    TextBlock.text msg
                                    TextBlock.foreground errorBrush
                                    TextBlock.textWrapping TextWrapping.Wrap
                                    TextBlock.margin (Thickness(0.0, 10.0, 0.0, 0.0))
                                ] :> IView
                            | None -> ()
                            yield StackPanel.create [
                                StackPanel.orientation Orientation.Horizontal
                                StackPanel.horizontalAlignment HorizontalAlignment.Right
                                StackPanel.spacing 8.0
                                StackPanel.margin (Thickness(0.0, 16.0, 0.0, 0.0))
                                StackPanel.children [
                                    Button.create [
                                        Button.content "Cancel"
                                        Button.onClick ((fun _ -> dispatch RunDialogCancel), SubPatchOptions.Always)
                                    ]
                                    Button.create [
                                        Button.content "Run"
                                        Button.background accent
                                        Button.foreground (Brushes.White :> IBrush)
                                        Button.onClick ((fun _ -> dispatch RunDialogConfirm), SubPatchOptions.Always)
                                    ]
                                ]
                            ] :> IView
                        ]
                    ]
                )
            ]
        )
    ] :> _

// ─── Top-level ──────────────────────────────────────────────────────────

let view (model: Model) (dispatch: Msg -> unit) : IView =
    let content =
        DockPanel.create [
            DockPanel.children [
                rootTabStrip model dispatch
                (match model.ActiveTab with
                 | Home                  -> homeView model dispatch
                 | Project pid           -> projectView pid model dispatch
                 | RunDetail (pid, rid)  -> runDetailView pid rid model dispatch
                 | LiveRun key           -> liveRunView key model dispatch)
            ]
        ] :> IView
    // Stack the param dialog on top of everything when it's open.
    Panel.create [
        Panel.children [
            yield content
            match model.RunDialog with
            | Some d -> yield runParamsDialog d dispatch
            | None   -> ()
        ]
    ] :> _
