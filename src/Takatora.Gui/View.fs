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
/// Per-engine accent so the engine name reads at a glance — logos can't be
/// embedded (and UE/Unity are mono anyway). Distinct, non-clashing hues.
let private engineColor (kind: EngineKind) : IBrush =
    match kind with
    | EngineKind.Unreal -> brush "#5B9BD5"   // blue
    | EngineKind.Unity  -> brush "#E0902F"   // orange
    | EngineKind.Godot  -> brush "#6FB86F"   // green
// Vertical divider between the global Home chip and the project-scoped
// chips — a structural cue reads more reliably across monitors than a
// subtle hue tint (which looked like hover/active or vanished entirely).
let private dividerBrush = brush "#4d4d4d"
let private transparentBrush = Brushes.Transparent :> IBrush
// Semi-transparent scrim behind the run-parameters modal (AARRGGBB).
let private overlayBg   = brush "#aa101010"
let private errorBrush  = brush "#f15a5a"
// Watch status colors: green = armed & will fire, yellow = enabled but
// nothing armed (idle), red = globally paused. Reused for the strip pill,
// the per-project pill, and the project-list dot.
let private watchGreen  = brush "#4ec97a"
let private watchYellow = brush "#e0b341"
let private watchRed    = brush "#f15a5a"

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
    | "skipped"   -> "⊘"
    | "cancelled" -> "⊗"
    | _           -> "?"

let private statusBrush = function
    | "success"   -> brush "#4ec97a"
    | "failure"   -> brush "#f15a5a"
    | "skipped"   -> mutedBrush
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
        (engineKind: EngineKind option)
        (isCurrent: bool)
        (dispatch: Msg -> unit)
        : IView =
    Button.create [
        // A small engine-colored dot (matches the engine color used elsewhere)
        // + the project name — clearer than a generic folder glyph.
        Button.content (
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.spacing 6.0
                StackPanel.children [
                    TextBlock.create [
                        TextBlock.text "●"
                        TextBlock.fontSize 11.0
                        TextBlock.verticalAlignment VerticalAlignment.Center
                        TextBlock.foreground
                            (match engineKind with Some k -> engineColor k | None -> mutedBrush)
                    ]
                    TextBlock.create [
                        TextBlock.text pid
                        TextBlock.verticalAlignment VerticalAlignment.Center
                    ]
                ]
            ])
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
                for p in projects ->
                    projectPill p (Map.tryFind p model.ProjectEngines) (current = Some p) dispatch
            ]
        ] :> _

/// Always-visible global watch master at the right of the strip: a green/red
/// dot + label (Watching N / Idle / Paused). Click toggles WatchEnabled — the
/// GUI-side twin of the tray's Pause/Resume item.
let private watchStatusPill (model: Model) (dispatch: Msg -> unit) : IView =
    let count = State.activeWatchCount model
    let dot, label =
        if not model.WatchEnabled then watchRed, "Paused"
        elif count > 0 then watchGreen, sprintf "Watching %d" count
        else watchYellow, "Idle"
    Button.create [
        DockPanel.dock Dock.Right
        Button.margin (Thickness(6.0, 0.0))
        Button.padding (Thickness(10.0, 4.0))
        Button.background transparentBrush
        Button.borderThickness (Thickness 0.0)
        Button.verticalAlignment VerticalAlignment.Center
        Button.cursor handCursor
        ToolTip.tip
            (if model.WatchEnabled then "Watching enabled — click to pause all"
             else "Watching paused — click to resume")
        Button.content (
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.spacing 6.0
                StackPanel.children [
                    TextBlock.create [
                        TextBlock.text "●"
                        TextBlock.fontSize 11.0
                        TextBlock.verticalAlignment VerticalAlignment.Center
                        TextBlock.foreground dot
                    ]
                    TextBlock.create [
                        TextBlock.text label
                        TextBlock.fontSize 12.0
                        TextBlock.verticalAlignment VerticalAlignment.Center
                        TextBlock.foreground dimBrush
                    ]
                ]
            ])
        Button.onClick ((fun _ -> dispatch ToggleGlobalWatch), SubPatchOptions.Always)
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
                    // Global watch master, pinned to the right edge — always
                    // visible so the user can see/toggle watching from any tab.
                    watchStatusPill model dispatch
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
        (engineKind: EngineKind option)
        (watch: WatchInfo option)
        (globalEnabled: bool)
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
                    // Per-project watch control, mirroring the Flows-tab pill —
                    // shown only when this project has a watched flow. Lets you
                    // pause/resume a project's watch straight from the list.
                    (match watch with
                     | Some w ->
                        let dot, tip =
                            if not w.Enabled then watchRed, "Watch paused — click to resume"
                            elif not globalEnabled then watchYellow, "Armed, but global watching is paused"
                            else watchGreen, "Watching — click to pause"
                        Button.create [
                            DockPanel.dock Dock.Right
                            Button.background transparentBrush
                            Button.borderThickness (Thickness 0.0)
                            Button.verticalAlignment VerticalAlignment.Center
                            Button.margin (Thickness(0.0, 0.0, 8.0, 0.0))
                            Button.cursor handCursor
                            ToolTip.tip tip
                            Button.content (
                                StackPanel.create [
                                    StackPanel.orientation Orientation.Horizontal
                                    StackPanel.spacing 5.0
                                    StackPanel.children [
                                        TextBlock.create [
                                            TextBlock.text "●"
                                            TextBlock.fontSize 11.0
                                            TextBlock.verticalAlignment VerticalAlignment.Center
                                            TextBlock.foreground dot
                                        ]
                                        TextBlock.create [
                                            TextBlock.text w.FlowId
                                            TextBlock.fontSize 12.0
                                            TextBlock.verticalAlignment VerticalAlignment.Center
                                            TextBlock.foreground dimBrush
                                        ]
                                    ]
                                ])
                            Button.onClick ((fun _ -> dispatch (ToggleProjectWatch p.Name)), SubPatchOptions.Always)
                        ] :> IView
                     | None -> TextBlock.create [ TextBlock.text ""; DockPanel.dock Dock.Right ] :> IView)
                    StackPanel.create [
                        StackPanel.children [
                            TextBlock.create [
                                TextBlock.text p.Name
                                TextBlock.fontSize 16.0
                                TextBlock.fontWeight FontWeight.SemiBold
                                // Tint the name by engine (matches the Settings
                                // engine color); plain when the kind is unknown.
                                match engineKind with
                                | Some k -> TextBlock.foreground (engineColor k)
                                | None -> ()
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

/// Folder picker for a toolbox scan directory: writes the chosen path into
/// the Settings "add directory" draft (mirrors browseForRunValue's flow).
let private browseForToolboxDir
        (pid: ProjectId)
        (dispatch: Msg -> unit)
        (e: RoutedEventArgs)
        : unit =
    match e.Source with
    | :? Visual as v ->
        match TopLevel.GetTopLevel v with
        | null -> ()
        | top ->
            async {
                let opts = FolderPickerOpenOptions(Title = "Select script directory", AllowMultiple = false)
                let! folders = top.StorageProvider.OpenFolderPickerAsync opts |> Async.AwaitTask
                match Seq.tryHead (folders |> Seq.cast<IStorageItem>) with
                | Some item ->
                    match item.TryGetLocalPath() with
                    | null -> ()
                    | path ->
                        Dispatcher.UIThread.Post(fun () -> dispatch (SetToolboxDirDraft (pid, path)))
                | None -> ()
            }
            |> Async.StartImmediate
    | _ -> ()

/// Copy text to the OS clipboard. Uses the event source's TopLevel (the
/// update loop has no window handle), same access route as the folder
/// pickers. Best-effort — a null clipboard just no-ops.
let private copyToClipboard (text: string) (e: RoutedEventArgs) : unit =
    match e.Source with
    | :? Visual as v ->
        match TopLevel.GetTopLevel v with
        | null -> ()
        | top ->
            match top.Clipboard with
            | null -> ()
            | cb   -> cb.SetTextAsync text |> ignore
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
                        TextBlock.text "Creates the folder (if needed) and a starter .takatora/project.toml, then registers it."
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
                    for p in model.Projects ->
                        projectRow p (Map.tryFind p.Name model.ProjectEngines)
                            (Map.tryFind p.Name model.Watches) model.WatchEnabled dispatch
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
    // The machine-local data dir (settings.toml + projects.toml + tasks/),
    // i.e. %APPDATA%\Takatora — surfaced as a link so it's discoverable.
    let dataDir =
        try System.IO.Path.GetDirectoryName(ProjectRegistry.registryPath ()) with _ -> ""
    DockPanel.create [
        DockPanel.children [
            DockPanel.create [
                DockPanel.dock Dock.Top
                DockPanel.margin (Thickness(16.0, 12.0))
                DockPanel.children [
                    // Right-edge: About, then the data-folder link.
                    Button.create [
                        DockPanel.dock Dock.Right
                        Button.content "About"
                        Button.background transparentBrush
                        Button.borderThickness (Thickness 0.0)
                        Button.foreground dimBrush
                        Button.verticalAlignment VerticalAlignment.Center
                        Button.cursor handCursor
                        Button.onClick ((fun _ -> dispatch ShowAbout), SubPatchOptions.Always)
                    ]
                    Button.create [
                        DockPanel.dock Dock.Right
                        Button.content "Data folder ↗"
                        Button.background transparentBrush
                        Button.borderThickness (Thickness 0.0)
                        Button.foreground dimBrush
                        Button.verticalAlignment VerticalAlignment.Center
                        Button.cursor handCursor
                        ToolTip.tip (sprintf "Open %s\n(settings.toml, projects.toml, tasks/)" dataDir)
                        Button.onClick ((fun _ -> dispatch (OpenInExplorer dataDir)), SubPatchOptions.Always)
                    ]
                    StackPanel.create [
                        StackPanel.orientation Orientation.Horizontal
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
                    subTabButton "Flows"    (current = ProjectFlows)      (fun () -> dispatch (ActivateSubTab (pid, ProjectFlows)))
                    subTabButton "Toolbox"  (current = ProjectToolboxTab) (fun () -> dispatch (ActivateSubTab (pid, ProjectToolboxTab)))
                    subTabButton "History"  (current = ProjectHistory)    (fun () -> dispatch (ActivateSubTab (pid, ProjectHistory)))
                    subTabButton "Settings" (current = ProjectSettings)   (fun () -> dispatch (ActivateSubTab (pid, ProjectSettings)))
                ]
            ]
        )
    ] :> _

/// A flow step row: a select button (opens the Inspector) plus move
/// up/down + delete controls (the Flow Editor).
let private stepRow
        (pid: ProjectId)
        (flowId: string)
        (idx: int)
        (step: Step)
        (selected: bool)
        (stepCount: int)
        (editing: bool)
        (dispatch: Msg -> unit)
        : IView =
    let ctrlBtn (glyph: string) (enabled: bool) (msg: Msg) : IView =
        Button.create [
            DockPanel.dock Dock.Right
            Button.content glyph
            Button.fontSize 11.0
            Button.padding (Thickness(7.0, 2.0))
            Button.isEnabled enabled
            Button.background transparentBrush
            Button.borderThickness (Thickness 0.0)
            Button.cursor handCursor
            Button.verticalAlignment VerticalAlignment.Center
            Button.onClick ((fun _ -> dispatch msg), SubPatchOptions.Always)
        ] :> IView
    DockPanel.create [
        DockPanel.margin (Thickness(0.0, 1.0))
        DockPanel.children [
            // Move/delete controls only in edit mode. Docked right, far-right
            // first: ✕ then ▾ then ▴ (reads ▴ ▾ ✕).
            if editing then yield ctrlBtn "✕" true (RemoveStep (pid, flowId, idx))
            if editing then yield ctrlBtn "▾" (idx < stepCount - 1) (MoveStep (pid, flowId, idx, 1))
            if editing then yield ctrlBtn "▴" (idx > 0) (MoveStep (pid, flowId, idx, -1))
            yield Button.create [
                Button.horizontalAlignment HorizontalAlignment.Stretch
                Button.horizontalContentAlignment HorizontalAlignment.Left
                Button.background (if selected then activeBg else transparentBrush)
                Button.borderThickness (Thickness 0.0)
                Button.padding (Thickness(6.0, 7.0))
                Button.minHeight 30.0
                Button.verticalContentAlignment VerticalAlignment.Center
                Button.cursor handCursor
                Button.onClick ((fun _ -> dispatch (SelectStep (pid, flowId, idx))), SubPatchOptions.Always)
                Button.content (
                    StackPanel.create [
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.spacing 8.0
                        StackPanel.children [
                            TextBlock.create [
                                TextBlock.text (sprintf "%d." (idx + 1))
                                TextBlock.foreground mutedBrush
                                TextBlock.fontSize 12.0
                            ]
                            TextBlock.create [
                                TextBlock.text step.Type
                                TextBlock.fontSize 12.0
                                TextBlock.fontFamily (FontFamily "Consolas, Menlo, monospace")
                            ]
                            (match step.When with
                             | Some w ->
                                 TextBlock.create [
                                     TextBlock.text (sprintf "when %s" w)
                                     TextBlock.foreground mutedBrush
                                     TextBlock.fontSize 11.0
                                     TextBlock.verticalAlignment VerticalAlignment.Center
                                 ] :> IView
                             | None -> TextBlock.create [ TextBlock.text "" ] :> IView)
                        ]
                    ]
                )
            ]
        ]
    ] :> _

/// The "+ Add step" row at the bottom of a flow's step list.
let private addStepRow
        (pid: ProjectId) (flowId: string) (draft: string) (dispatch: Msg -> unit) : IView =
    DockPanel.create [
        DockPanel.margin (Thickness(0.0, 4.0, 0.0, 0.0))
        DockPanel.children [
            Button.create [
                DockPanel.dock Dock.Right
                Button.content "+ Add step"
                Button.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                Button.isEnabled (draft.Trim() <> "")
                Button.onClick ((fun _ -> dispatch (AddStep (pid, flowId, draft))), SubPatchOptions.Always)
            ]
            TextBox.create [
                TextBox.text draft
                TextBox.watermark "task type, e.g. shell / fs.zip / ue.clean"
                TextBox.fontSize 12.0
                TextBox.onTextChanged
                    ((fun s -> dispatch (SetAddStepDraft (pid, flowId, s))), SubPatchOptions.Always)
            ]
        ]
    ] :> IView

let private flowCard
        (pid: ProjectId)
        (f: Flow)
        (selectedStep: (ProjectId * string * int) option)
        (expanded: bool)
        (editing: bool)
        (isWatched: bool)
        (watchEffective: bool)
        (addStepDraft: string)
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
                    // Watch toggle: auto-run this flow on new git commits.
                    // When watched but suppressed (project/global paused) the
                    // label says so and dims, so "set but won't fire" is clear.
                    Button.create [
                        DockPanel.dock Dock.Right
                        Button.content
                            (if not isWatched then "Watch"
                             elif watchEffective then "● Watching"
                             else "○ Watching (paused)")
                        Button.margin (Thickness(0.0, 0.0, 8.0, 0.0))
                        Button.verticalAlignment VerticalAlignment.Center
                        Button.foreground
                            (if not isWatched then dimBrush
                             elif watchEffective then watchGreen
                             else mutedBrush)
                        Button.onClick ((fun _ -> dispatch (ToggleWatch (pid, f.Id))), SubPatchOptions.Always)
                    ]
                    // Edit toggle only once expanded.
                    (if expanded then
                        Button.create [
                            DockPanel.dock Dock.Right
                            Button.content (if editing then "Done" else "Edit")
                            Button.margin (Thickness(0.0, 0.0, 8.0, 0.0))
                            Button.verticalAlignment VerticalAlignment.Center
                            Button.onClick ((fun _ -> dispatch (ToggleFlowEditing (pid, f.Id))), SubPatchOptions.Always)
                        ] :> IView
                     else TextBlock.create [ TextBlock.text ""; DockPanel.dock Dock.Right ] :> IView)
                    StackPanel.create [
                        StackPanel.spacing 2.0
                        StackPanel.children [
                            // Header is the expander: click to show/hide steps.
                            Button.create [
                                Button.horizontalAlignment HorizontalAlignment.Stretch
                                Button.horizontalContentAlignment HorizontalAlignment.Left
                                Button.background transparentBrush
                                Button.borderThickness (Thickness 0.0)
                                // Inset the content so the ▸ glyph isn't at the
                                // button's very edge — the Fluent press anim
                                // scales the button in slightly, and an edge
                                // click would land outside the shrunk bounds and
                                // be dropped ("空振り").
                                Button.padding (Thickness(8.0, 6.0))
                                Button.cursor handCursor
                                Button.onClick ((fun _ -> dispatch (ToggleFlowExpanded (pid, f.Id))), SubPatchOptions.Always)
                                Button.content (
                                    StackPanel.create [
                                        StackPanel.spacing 2.0
                                        StackPanel.children [
                                            StackPanel.create [
                                                StackPanel.orientation Orientation.Horizontal
                                                StackPanel.spacing 8.0
                                                StackPanel.children [
                                                    TextBlock.create [
                                                        TextBlock.text (if expanded then "▾" else "▸")
                                                        TextBlock.foreground mutedBrush
                                                        TextBlock.fontSize 13.0
                                                        TextBlock.verticalAlignment VerticalAlignment.Center
                                                    ]
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
                                                TextBlock.margin (Thickness(21.0, 0.0, 0.0, 0.0))
                                            ]
                                        ]
                                    ]
                                )
                            ]
                            // Steps appear only when expanded.
                            (if not expanded then TextBlock.create [ TextBlock.text "" ] :> IView
                             else
                                StackPanel.create [
                                    StackPanel.margin (Thickness(0.0, 6.0, 0.0, 0.0))
                                    StackPanel.children [
                                        let n = List.length f.Steps
                                        for i, s in List.indexed f.Steps do
                                            yield stepRow pid f.Id i s (selectedStep = Some (pid, f.Id, i)) n editing dispatch
                                        if editing then yield addStepRow pid f.Id addStepDraft dispatch
                                    ]
                                ] :> IView)
                        ]
                    ]
                ]
            ]
        )
    ] :> _

/// A colored chip showing where a task resolved from.
let private taskSourceBadge (src: TaskSource) : IView =
    let label, color =
        match src with
        | TaskSource.ProjectLocal -> "project", brush "#C58AF0"
        | TaskSource.UserLocal    -> "user",    brush "#E0902F"
        | TaskSource.Builtin      -> "builtin", brush "#6FB86F"
    Border.create [
        Border.background color
        Border.cornerRadius 3.0
        Border.padding (Thickness(6.0, 1.0))
        Border.verticalAlignment VerticalAlignment.Center
        Border.child (
            TextBlock.create [
                TextBlock.text label
                TextBlock.foreground (brush "#0A0A0A")
                TextBlock.fontSize 11.0
            ])
    ] :> IView

/// Display text for a step param's current value.
let private tomlDisplay (v: TomlValue) : string =
    let rec go v =
        match v with
        | TString s -> s
        | TInt i -> string i
        | TFloat f -> string f
        | TBool b -> if b then "true" else "false"
        | TArray xs -> "[" + (xs |> List.map go |> String.concat ", ") + "]"
        | TTable _ -> ""
    go v

/// Parse Inspector-entered text into a TomlValue per the describe kind.
let private parseStepValue (kind: string) (text: string) : TomlValue =
    match kind with
    | "int" ->
        match System.Int64.TryParse text with
        | true, i -> TInt i
        | _ -> TString text
    | "float" ->
        match System.Double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
        | true, f -> TFloat f
        | _ -> TString text
    | "bool" -> TBool (text = "true")
    | _ -> TString text

/// One editable param row in the Inspector. Scalar/enum/bool kinds write
/// back to flows.toml on commit; secret + list are read-only (a secret must
/// never be written to flows.toml; list editing is a later slice).
let private editableParamRow
        (pid: ProjectId) (flowId: string) (idx: int)
        (step: Step) (p: DescribeParam) (dispatch: Msg -> unit)
        : IView =
    let current = Map.tryFind p.Name step.Params
    let curText = current |> Option.map tomlDisplay |> Option.defaultValue ""
    let commit (text: string) =
        dispatch (SetStepParam (pid, flowId, idx, p.Name, parseStepValue p.Kind text))
    // A `${vars.X}` / `${steps...}` binding must NOT be silently clobbered by a
    // value picker — show it as editable text so the binding stays visible.
    let isTemplate = curText.Contains("${")
    let effectiveKind = if isTemplate then "string" else p.Kind
    let label =
        TextBlock.create [
            DockPanel.dock Dock.Left
            TextBlock.text p.Name
            TextBlock.width 130.0
            TextBlock.fontFamily (FontFamily "Consolas, Menlo, monospace")
            TextBlock.fontSize 12.0
            TextBlock.verticalAlignment VerticalAlignment.Center
        ] :> IView
    let widget : IView =
        match effectiveKind, p.Values with
        | "enum", Some values ->
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.spacing 6.0
                StackPanel.children [
                    for vv in values -> engineChoiceButton vv (curText = vv) (fun () -> commit vv)
                ]
            ] :> IView
        | "bool", _ ->
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.spacing 6.0
                StackPanel.children [
                    engineChoiceButton "true"  (curText = "true")  (fun () -> commit "true")
                    engineChoiceButton "false" (curText = "false") (fun () -> commit "false")
                ]
            ] :> IView
        | "secret", _ ->
            TextBlock.create [
                TextBlock.text (if curText = "" then "(secret — set in flows.toml)" else "•••• (set in flows.toml)")
                TextBlock.foreground mutedBrush
                TextBlock.fontSize 12.0
                TextBlock.verticalAlignment VerticalAlignment.Center
            ] :> IView
        | k, _ when k.StartsWith("list") ->
            TextBlock.create [
                TextBlock.text (if curText = "" then "(list — edit in flows.toml)" else curText)
                TextBlock.foreground dimBrush
                TextBlock.fontSize 12.0
                TextBlock.textWrapping TextWrapping.Wrap
                TextBlock.verticalAlignment VerticalAlignment.Center
            ] :> IView
        | _ ->
            TextBox.create [
                yield TextBox.text curText
                yield TextBox.fontSize 12.0
                yield TextBox.watermark (match current with Some _ -> "" | None -> defaultArg p.Default "")
                if p.Kind = "multiline" then
                    yield TextBox.acceptsReturn true
                    yield TextBox.textWrapping TextWrapping.Wrap
                    yield TextBox.minHeight 52.0
                // Commit on blur — only when the text actually changed (so we
                // don't materialize an unchanged default into flows.toml). A
                // repeat fire writes the same value, which is harmless.
                yield TextBox.onLostFocus
                        ((fun e ->
                            match e.Source with
                            | :? TextBox as tb when tb.Text <> curText -> commit tb.Text
                            | _ -> ()), SubPatchOptions.Always)
            ] :> IView
    DockPanel.create [
        yield DockPanel.margin (Thickness(0.0, 3.0))
        yield DockPanel.background transparentBrush
        match p.Description with
        | Some d -> yield ToolTip.tip d
        | None -> ()
        yield DockPanel.children [ label; widget ]
    ] :> IView

/// The describe-driven, read-only Inspector for a selected flow step.
let private stepInspector
        (pid: ProjectId)
        (projectRoot: string)
        (flowId: string)
        (idx: int)
        (model: Model)
        (dispatch: Msg -> unit)
        : IView =
    let stepOpt =
        match Map.tryFind pid model.ProjectFlows with
        | Some (FlowsOk flows) ->
            flows
            |> List.tryFind (fun f -> f.Id = flowId)
            |> Option.bind (fun f -> List.tryItem idx f.Steps)
        | _ -> None
    let title =
        match stepOpt with
        | Some s -> sprintf "Step %d: %s" (idx + 1) s.Type
        | None -> "Step"
    let bodyChildren : IView list =
        match stepOpt with
        | None -> [ TextBlock.create [ TextBlock.text "(step no longer exists)"; TextBlock.foreground mutedBrush ] :> IView ]
        | Some step ->
            match State.resolveStepTask projectRoot step.Type with
            | None ->
                [ TextBlock.create [
                    TextBlock.text (sprintf "task '%s' not found (no project, user, or builtin match)" step.Type)
                    TextBlock.foreground mutedBrush
                    TextBlock.textWrapping TextWrapping.Wrap ] :> IView ]
            | Some resolved ->
                let key = State.stepSchemaKey resolved.Path
                let schemaView : IView =
                    if Set.contains key model.StepSchemasLoading then
                        TextBlock.create [ TextBlock.text "inspecting… (running describe)"; TextBlock.foreground mutedBrush ] :> IView
                    else
                        match Map.tryFind key model.StepSchemas with
                        | Some (Ok schema) ->
                            StackPanel.create [
                                StackPanel.spacing 4.0
                                StackPanel.children [
                                    sectionHeader "Parameters"
                                    (if List.isEmpty schema.Params then
                                        TextBlock.create [ TextBlock.text "(no params)"; TextBlock.foreground mutedBrush; TextBlock.fontSize 12.0 ] :> IView
                                     else
                                        StackPanel.create [ StackPanel.children [ for p in schema.Params -> editableParamRow pid flowId idx step p dispatch ] ] :> IView)
                                    sectionHeader "Outputs"
                                    (if List.isEmpty schema.Outputs then
                                        TextBlock.create [ TextBlock.text "(none declared at top level)"; TextBlock.foreground mutedBrush; TextBlock.fontSize 12.0 ] :> IView
                                     else
                                        TextBlock.create [
                                            TextBlock.text (String.concat ", " schema.Outputs)
                                            TextBlock.foreground dimBrush
                                            TextBlock.fontSize 12.0
                                            TextBlock.textWrapping TextWrapping.Wrap ] :> IView)
                                ]
                            ] :> IView
                        | Some (Error e) ->
                            StackPanel.create [
                                StackPanel.spacing 4.0
                                StackPanel.children [
                                    TextBlock.create [ TextBlock.text "describe failed:"; TextBlock.foreground (brush "#f15a5a"); TextBlock.fontSize 12.0 ]
                                    TextBlock.create [ TextBlock.text e; TextBlock.foreground dimBrush; TextBlock.fontSize 11.0; TextBlock.textWrapping TextWrapping.Wrap ]
                                ]
                            ] :> IView
                        | None ->
                            TextBlock.create [ TextBlock.text "…"; TextBlock.foreground mutedBrush ] :> IView
                [ StackPanel.create [
                    StackPanel.orientation Orientation.Horizontal
                    StackPanel.spacing 8.0
                    StackPanel.children [
                        TextBlock.create [ TextBlock.text "source:"; TextBlock.foreground mutedBrush; TextBlock.fontSize 12.0; TextBlock.verticalAlignment VerticalAlignment.Center ]
                        taskSourceBadge resolved.Source
                    ] ] :> IView
                  (match step.When with
                   | Some w ->
                       TextBlock.create [
                           TextBlock.text (sprintf "when: %s" w)
                           TextBlock.foreground mutedBrush
                           TextBlock.fontSize 12.0
                           TextBlock.margin (Thickness(0.0, 4.0, 0.0, 0.0)) ] :> IView
                   | None -> TextBlock.create [ TextBlock.text "" ] :> IView)
                  schemaView ]
    Border.create [
        DockPanel.dock Dock.Right
        Border.width 360.0
        Border.background (brush "#1b1b1b")
        Border.borderBrush stripBorder
        Border.borderThickness (Thickness(1.0, 0.0, 0.0, 0.0))
        Border.padding (Thickness 12.0)
        Border.child (
            DockPanel.create [
                DockPanel.children [
                    DockPanel.create [
                        DockPanel.dock Dock.Top
                        DockPanel.children [
                            Button.create [
                                DockPanel.dock Dock.Right
                                Button.content "✕"
                                Button.background transparentBrush
                                Button.borderThickness (Thickness 0.0)
                                Button.cursor handCursor
                                Button.onClick ((fun _ -> dispatch CloseInspector), SubPatchOptions.Always)
                            ]
                            TextBlock.create [
                                TextBlock.text title
                                TextBlock.fontWeight FontWeight.SemiBold
                                TextBlock.fontSize 14.0
                                TextBlock.verticalAlignment VerticalAlignment.Center
                            ]
                        ]
                    ]
                    ScrollViewer.create [
                        ScrollViewer.content (
                            StackPanel.create [
                                StackPanel.spacing 4.0
                                StackPanel.margin (Thickness(0.0, 10.0, 0.0, 0.0))
                                StackPanel.children bodyChildren
                            ]
                        )
                    ]
                ]
            ]
        )
    ] :> IView

let private flowsBody
        (pid: ProjectId)
        (projectRoot: string)
        (model: Model)
        (dispatch: Msg -> unit)
        : IView =
    let load = Map.tryFind pid model.ProjectFlows |> Option.defaultValue FlowsMissing
    let selectedStep = model.SelectedStep
    let header =
        StackPanel.create [
            DockPanel.dock Dock.Top
            StackPanel.orientation Orientation.Horizontal
            StackPanel.margin (Thickness(16.0, 12.0))
            StackPanel.spacing 12.0
            StackPanel.children [
                yield TextBlock.create [
                    TextBlock.text
                        (match load with
                         | FlowsOk fs -> sprintf "%d flow(s)" (List.length fs)
                         | FlowsMissing -> "(no .takatora/flows.toml)"
                         | FlowsError _ -> "(flows.toml error)")
                    TextBlock.foreground mutedBrush
                    TextBlock.verticalAlignment VerticalAlignment.Center
                ] :> IView
                yield Button.create [
                    Button.content "Refresh"
                    Button.onClick (fun _ -> dispatch (RefreshFlows pid))
                ] :> IView
                // Per-project watch on/off — shown only when a flow in this
                // project is watched. Pauses/resumes this project's watch while
                // keeping which flow it watches; dims when global is off too.
                match Map.tryFind pid model.Watches with
                | Some w ->
                    // green = firing, red = this project paused, yellow =
                    // armed but globally suppressed.
                    let dot, label =
                        if not w.Enabled then watchRed, sprintf "Paused (%s)" w.FlowId
                        elif not model.WatchEnabled then watchYellow, sprintf "Suppressed (%s)" w.FlowId
                        else watchGreen, sprintf "Watching %s" w.FlowId
                    yield Button.create [
                        Button.background transparentBrush
                        Button.borderThickness (Thickness 0.0)
                        Button.verticalAlignment VerticalAlignment.Center
                        Button.cursor handCursor
                        ToolTip.tip
                            (if not w.Enabled then "This project's watch is paused — click to resume"
                             elif not model.WatchEnabled then "Armed, but global watching is paused"
                             else "Watching this project — click to pause")
                        Button.content (
                            StackPanel.create [
                                StackPanel.orientation Orientation.Horizontal
                                StackPanel.spacing 6.0
                                StackPanel.children [
                                    TextBlock.create [
                                        TextBlock.text "●"
                                        TextBlock.fontSize 11.0
                                        TextBlock.verticalAlignment VerticalAlignment.Center
                                        TextBlock.foreground dot
                                    ]
                                    TextBlock.create [
                                        TextBlock.text label
                                        TextBlock.fontSize 12.0
                                        TextBlock.verticalAlignment VerticalAlignment.Center
                                        TextBlock.foreground dimBrush
                                    ]
                                ]
                            ])
                        Button.onClick ((fun _ -> dispatch (ToggleProjectWatch pid)), SubPatchOptions.Always)
                    ] :> IView
                | None -> ()
            ]
        ]
    let body : IView =
        match load with
        | FlowsMissing ->
            TextBlock.create [
                TextBlock.text "No `.takatora/flows.toml` in this project's working directory. Create one (or use the planned init/wizard) to define runnable flows."
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
                        TextBlock.text "Could not parse `.takatora/flows.toml`:"
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
            let list =
                ScrollViewer.create [
                    ScrollViewer.content (
                        StackPanel.create [
                            StackPanel.margin (Thickness(16.0, 0.0, 16.0, 16.0))
                            StackPanel.spacing 6.0
                            StackPanel.children [
                                for f in fs ->
                                let draft = Map.tryFind (pid, f.Id) model.AddStepDraft |> Option.defaultValue ""
                                let expanded = Set.contains (pid, f.Id) model.ExpandedFlows
                                let editing  = Set.contains (pid, f.Id) model.EditingFlows
                                let isWatched =
                                    (Map.tryFind pid model.Watches |> Option.map (fun w -> w.FlowId)) = Some f.Id
                                let watchEff = isWatched && State.watchEffective model pid
                                flowCard pid f selectedStep expanded editing isWatched watchEff draft dispatch
                            ]
                        ]
                    )
                ]
            // Dock the Inspector to the right when a step in THIS project is
            // selected and still points at a real flow.
            let inspector : IView option =
                match selectedStep with
                | Some (sp, fid, idx) when sp = pid && List.exists (fun (f: Flow) -> f.Id = fid) fs ->
                    Some (stepInspector pid projectRoot fid idx model dispatch)
                | _ -> None
            DockPanel.create [
                DockPanel.children [
                    match inspector with Some i -> yield i | None -> ()
                    yield list
                ]
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

/// A settings row whose value is tinted + emphasised — used to color the
/// engine name by engine type.
let private settingsFieldColored (label: string) (value: string) (fg: IBrush) : IView =
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
                TextBlock.foreground fg
                TextBlock.fontWeight FontWeight.SemiBold
                TextBlock.textWrapping TextWrapping.Wrap
            ]
        ]
    ] :> _

/// One read-only setting, as data so the Settings search can filter it.
type private SettingRow =
    { Section: string; Label: string; Value: string; Color: IBrush option }

/// The project's read-only settings (engine config + resolved engine + VCS +
/// history + working dir), flattened to filterable rows.
let private buildSettingRows (proj: Project) (projectRoot: string) : SettingRow list =
    let row s l v   = { Section = s; Label = l; Value = v; Color = None }
    let rowC s l v c = { Section = s; Label = l; Value = v; Color = Some c }
    let opt = function Some x -> x | None -> "(autodetect)"
    [ yield rowC "Engine" "type" (engineKindLabel proj.Engine.Kind) (engineColor proj.Engine.Kind)
      yield row "Engine" "project_file"   (opt proj.Engine.ProjectFile)
      yield row "Engine" "engine_path"    (opt proj.Engine.EnginePath)
      yield row "Engine" "engine_version" (opt proj.Engine.EngineVersion)
      yield row "Engine" "executable"     (opt proj.Engine.Executable)
      // Resolved engine — reads engine_path (project-local) then detection.
      match Engines.resolveProjectEngine proj.Engine projectRoot with
      | Ok d ->
          yield rowC "Resolved engine" "version" (sprintf "%s %s" (engineKindLabel d.Kind) d.Version) (engineColor d.Kind)
          yield row "Resolved engine" "install path" d.Path
          match d.Executable with Some e -> yield row "Resolved engine" "executable" e | None -> ()
          match d.Association with Some a -> yield row "Resolved engine" "association" a | None -> ()
      | Error msg -> yield row "Resolved engine" "resolved" (sprintf "⚠ not resolved — %s" msg)
      // VCS.
      match proj.Vcs with
      | Some vcs ->
          yield row "VCS" "type" (vcsKindLabel vcs.Kind)
          yield row "VCS" "lfs"  (if vcs.Lfs then "enabled" else "disabled")
      | None -> yield row "VCS" "vcs" "(not configured)"
      yield row "History retention" "keep_last_n_runs" (string proj.History.KeepLastNRuns)
      yield row "Working dir" "working_dir" proj.WorkingDir ]

/// Render filtered setting rows, grouped under their section headers (a
/// header shows only when the section has a visible row).
let private renderSettingRows (query: string) (rows: SettingRow list) : IView list =
    let q = query.Trim().ToLowerInvariant()
    let matches (r: SettingRow) =
        q = ""
        || r.Section.ToLowerInvariant().Contains q
        || r.Label.ToLowerInvariant().Contains q
        || r.Value.ToLowerInvariant().Contains q
    let views = System.Collections.Generic.List<IView>()
    let mutable lastSection = ""
    for r in rows |> List.filter matches do
        if r.Section <> lastSection then
            views.Add(sectionHeader r.Section)
            lastSection <- r.Section
        match r.Color with
        | Some c -> views.Add(settingsFieldColored r.Label r.Value c)
        | None   -> views.Add(settingsField r.Label r.Value)
    List.ofSeq views

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

/// Settings editor for the toolbox scan directories. Unlike the rest of
/// Settings (read-only project.toml), these persist to `.takatora/toolbox.toml`
/// — committed and shared. Each row resolves against the project root and
/// flags a directory that doesn't exist on disk.
let private toolboxDirsBlock
        (pid: ProjectId)
        (projectRoot: string)
        (cfg: ToolboxConfig)
        (draft: string)
        (dispatch: Msg -> unit)
        : IView =
    let resolves (d: string) =
        let resolved = if System.IO.Path.IsPathRooted d then d else System.IO.Path.Combine(projectRoot, d)
        System.IO.Directory.Exists resolved
    let rows : IView =
        if List.isEmpty cfg.ScriptDirs then
            TextBlock.create [
                TextBlock.text "No directories yet. Add one below to discover scripts in the Toolbox tab."
                TextBlock.foreground mutedBrush
            ] :> _
        else
            StackPanel.create [
                StackPanel.spacing 4.0
                StackPanel.children [
                    for d in cfg.ScriptDirs ->
                        DockPanel.create [
                            DockPanel.children [
                                Button.create [
                                    DockPanel.dock Dock.Right
                                    Button.content "Remove"
                                    Button.onClick ((fun _ -> dispatch (RemoveToolboxDir (pid, d))), SubPatchOptions.Always)
                                ] :> IView
                                (if resolves d then
                                    TextBlock.create [ TextBlock.text "" ] :> IView
                                 else
                                    TextBlock.create [
                                        DockPanel.dock Dock.Right
                                        TextBlock.text "⚠ not found"
                                        TextBlock.foreground watchYellow
                                        TextBlock.fontSize 11.0
                                        TextBlock.margin (Thickness(0.0, 0.0, 10.0, 0.0))
                                        TextBlock.verticalAlignment VerticalAlignment.Center
                                    ] :> IView)
                                TextBlock.create [
                                    TextBlock.text d
                                    TextBlock.fontFamily (FontFamily "Consolas, Menlo, monospace")
                                    TextBlock.verticalAlignment VerticalAlignment.Center
                                ] :> IView
                            ]
                        ] :> IView
                ]
            ] :> _
    StackPanel.create [
        StackPanel.spacing 6.0
        StackPanel.children [
            sectionHeader "Toolbox"
            TextBlock.create [
                TextBlock.text "Directories scanned for runnable scripts (.bat / .cmd / .ps1 / .sh). Relative paths resolve against the project root. Saved to .takatora/toolbox.toml — committed and shared via VCS."
                TextBlock.foreground mutedBrush
                TextBlock.fontSize 12.0
                TextBlock.textWrapping TextWrapping.Wrap
            ]
            rows
            DockPanel.create [
                DockPanel.children [
                    Button.create [
                        DockPanel.dock Dock.Right
                        Button.content "Add"
                        Button.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                        Button.onClick ((fun _ -> dispatch (AddToolboxDir pid)), SubPatchOptions.Always)
                    ]
                    Button.create [
                        DockPanel.dock Dock.Right
                        Button.content "Browse…"
                        Button.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                        Button.onClick ((fun e -> browseForToolboxDir pid dispatch e), SubPatchOptions.Always)
                    ]
                    TextBox.create [
                        TextBox.text draft
                        TextBox.watermark "tools  (or an absolute path)"
                        TextBox.fontFamily (FontFamily "Consolas, Menlo, monospace")
                        TextBox.onTextChanged ((fun s -> dispatch (SetToolboxDirDraft (pid, s))), SubPatchOptions.Always)
                    ]
                ]
            ]
        ]
    ] :> IView

/// Editor for the global "Open in IDE" command template. Machine-local
/// (applies to every project), so it lives in app settings, not project.toml.
/// "Detect" finds installed IDEs (VS via vswhere / Rider / VS Code) so the
/// user can one-click a preset instead of hand-writing versioned exe paths.
let private ideCommandBlock
        (draft: string)
        (candidates: IdeCandidate list)
        (dispatch: Msg -> unit)
        : IView =
    StackPanel.create [
        StackPanel.spacing 6.0
        StackPanel.children [
            sectionHeader "Open in IDE (global)"
            TextBlock.create [
                TextBlock.text "Command for the project header's \"Open in IDE\" button — applies to all projects (machine-local, not committed). Use Detect for a preset, or edit by hand. Placeholders: {project_dir} · {uproject} · {sln} · {target} (per-engine)."
                TextBlock.foreground mutedBrush
                TextBlock.fontSize 12.0
                TextBlock.textWrapping TextWrapping.Wrap
            ]
            // Detect + the resulting presets.
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.spacing 8.0
                StackPanel.children [
                    Button.create [
                        Button.content "Detect IDEs"
                        Button.onClick ((fun _ -> dispatch DetectIdes), SubPatchOptions.Always)
                    ]
                    TextBlock.create [
                        TextBlock.text
                            (if List.isEmpty candidates then "(scan for installed IDEs)"
                             else sprintf "%d found — click one to use it:" (List.length candidates))
                        TextBlock.foreground mutedBrush
                        TextBlock.fontSize 11.0
                        TextBlock.verticalAlignment VerticalAlignment.Center
                    ]
                ]
            ]
            (if List.isEmpty candidates then TextBlock.create [ TextBlock.text "" ] :> IView
             else
                WrapPanel.create [
                    WrapPanel.orientation Orientation.Horizontal
                    WrapPanel.children [
                        for c in candidates ->
                            Button.create [
                                Button.content c.Name
                                Button.margin (Thickness(0.0, 0.0, 6.0, 6.0))
                                Button.onClick ((fun _ -> dispatch (PickIdeCandidate c.Command)), SubPatchOptions.Always)
                            ] :> IView
                    ]
                ] :> IView)
            DockPanel.create [
                DockPanel.children [
                    Button.create [
                        DockPanel.dock Dock.Right
                        Button.content "Save"
                        Button.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                        Button.onClick ((fun _ -> dispatch SaveIdeCommand), SubPatchOptions.Always)
                    ]
                    TextBox.create [
                        TextBox.text draft
                        TextBox.watermark "code \"{project_dir}\""
                        TextBox.fontFamily (FontFamily "Consolas, Menlo, monospace")
                        TextBox.onTextChanged ((fun s -> dispatch (SetIdeCommandDraft s)), SubPatchOptions.Always)
                    ]
                ]
            ]
        ]
    ] :> IView

/// Editor for the Godot engine. Godot has no canonical install path, so the
/// search dirs are machine-level (global) — where to *look*. The engine
/// itself is project-local: Detect lists candidates (PATH + search dirs) and
/// picking one writes it to THIS project's `[engine].engine_path`. Godot-fork
/// users (GDStudio, …) can also just hand-edit `engine_path` in project.toml.
let private godotBlock
        (pid: ProjectId)
        (searchPathsDraft: string)
        (candidates: DetectedEngine list)
        (chosen: string option)
        (dispatch: Msg -> unit)
        : IView =
    StackPanel.create [
        StackPanel.spacing 6.0
        StackPanel.children [
            sectionHeader "Godot"
            TextBlock.create [
                TextBlock.text "Search dirs (one per line) are machine-level — where to look for Godot. Detect scans PATH + these; picking a result sets this project's engine_path (a Godot fork can be hand-set there too)."
                TextBlock.foreground mutedBrush
                TextBlock.fontSize 12.0
                TextBlock.textWrapping TextWrapping.Wrap
            ]
            (match chosen with
             | Some p when not (System.String.IsNullOrWhiteSpace p) ->
                 settingsFieldColored "engine_path" p (engineColor EngineKind.Godot)
             | _ ->
                 TextBlock.create [
                     TextBlock.text "(no engine_path — using PATH / search-path detection)"
                     TextBlock.foreground mutedBrush
                     TextBlock.fontSize 11.0
                 ] :> IView)
            DockPanel.create [
                DockPanel.children [
                    Button.create [
                        DockPanel.dock Dock.Right
                        Button.content "Save paths"
                        Button.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                        Button.onClick ((fun _ -> dispatch SaveGodotSearchPaths), SubPatchOptions.Always)
                    ]
                    TextBox.create [
                        TextBox.text searchPathsDraft
                        TextBox.watermark "C:\\Tools\\Godot"
                        TextBox.acceptsReturn true
                        TextBox.minHeight 48.0
                        TextBox.fontFamily (FontFamily "Consolas, Menlo, monospace")
                        TextBox.onTextChanged ((fun s -> dispatch (SetGodotSearchPathsDraft s)), SubPatchOptions.Always)
                    ]
                ]
            ]
            StackPanel.create [
                StackPanel.orientation Orientation.Horizontal
                StackPanel.spacing 8.0
                StackPanel.children [
                    Button.create [
                        Button.content "Detect Godot"
                        Button.onClick ((fun _ -> dispatch DetectGodot), SubPatchOptions.Always)
                    ]
                    TextBlock.create [
                        TextBlock.text
                            (if List.isEmpty candidates then "(scan PATH + search paths)"
                             else sprintf "%d found — click to choose:" (List.length candidates))
                        TextBlock.foreground mutedBrush
                        TextBlock.fontSize 11.0
                        TextBlock.verticalAlignment VerticalAlignment.Center
                    ]
                ]
            ]
            (if List.isEmpty candidates then TextBlock.create [ TextBlock.text "" ] :> IView
             else
                WrapPanel.create [
                    WrapPanel.children [
                        for c in candidates ->
                            match c.Executable with
                            | Some exe ->
                                Button.create [
                                    Button.content (sprintf "%s (%s)" c.Version (System.IO.Path.GetFileName exe))
                                    Button.margin (Thickness(0.0, 0.0, 6.0, 6.0))
                                    Button.onClick ((fun _ -> dispatch (PickGodot (pid, exe))), SubPatchOptions.Always)
                                ] :> IView
                            | None -> TextBlock.create [ TextBlock.text "" ] :> IView
                    ]
                ] :> IView)
        ]
    ] :> IView

let private settingsBody
        (pid: ProjectId)
        (projectRoot: string)
        (load: ProjectInfoLoad)
        (secrets: string list)
        (model: Model)
        (dispatch: Msg -> unit)
        : IView =
    let ideCommandDraft = model.IdeCommandDraft
    let ideCandidates = model.IdeCandidates
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
                         | ProjectInfoOk _      -> "From .takatora/project.toml — read-only in this slice"
                         | ProjectInfoMissing   -> "(no .takatora/project.toml)"
                         | ProjectInfoError _   -> "(project.toml error)")
                    TextBlock.foreground mutedBrush
                    TextBlock.verticalAlignment VerticalAlignment.Center
                ]
                Button.create [
                    Button.content "Refresh"
                    Button.onClick (fun _ -> dispatch (RefreshProjectInfo pid))
                ]
                TextBox.create [
                    TextBox.watermark "search settings…"
                    TextBox.width 220.0
                    TextBox.text model.SettingsFilter
                    TextBox.verticalAlignment VerticalAlignment.Center
                    TextBox.onTextChanged ((fun s -> dispatch (SetSettingsFilter s)), SubPatchOptions.Always)
                ]
            ]
        ]
    let body : IView =
        match load with
        | ProjectInfoMissing ->
            TextBlock.create [
                TextBlock.text "No `.takatora/project.toml` under this project's working directory. The registry entry expects one — has it been moved or deleted?"
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
                        TextBlock.text "Could not parse `.takatora/project.toml`:"
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
            // Read-only rows filter per-row; the interactive blocks (IDE /
            // Godot / Secrets) filter as whole sections by keyword match.
            let q = model.SettingsFilter.Trim().ToLowerInvariant()
            let blockMatches (keywords: string) = q = "" || keywords.ToLowerInvariant().Contains q
            let rowViews = renderSettingRows model.SettingsFilter (buildSettingRows proj projectRoot)
            let isGodot = proj.Engine.Kind = EngineKind.Godot
            let anyVisible =
                not (List.isEmpty rowViews)
                || blockMatches "open in ide command launch editor rider visual studio code"
                || (isGodot && blockMatches "godot search path engine executable")
                || blockMatches "secrets keychain credentials"
                || blockMatches "toolbox scripts tools directories scan bat cmd ps1 sh"
            ScrollViewer.create [
                ScrollViewer.content (
                    StackPanel.create [
                        StackPanel.margin (Thickness(16.0, 0.0, 16.0, 16.0))
                        StackPanel.spacing 8.0
                        StackPanel.children [
                            yield! rowViews
                            if blockMatches "open in ide command launch editor rider visual studio code" then
                                yield ideCommandBlock ideCommandDraft ideCandidates dispatch
                            if isGodot && blockMatches "godot search path engine executable" then
                                yield godotBlock pid model.GodotSearchPathsDraft model.GodotCandidates proj.Engine.EnginePath dispatch
                            if blockMatches "secrets keychain credentials" then
                                yield secretsBlock pid secrets dispatch
                            if blockMatches "toolbox scripts tools directories scan bat cmd ps1 sh" then
                                yield toolboxDirsBlock pid projectRoot (Toolbox.loadConfig projectRoot)
                                          (Map.tryFind pid model.ToolboxDirDraft |> Option.defaultValue "") dispatch
                            if not anyVisible then
                                yield TextBlock.create [
                                    TextBlock.text (sprintf "No settings match \"%s\"." (model.SettingsFilter.Trim()))
                                    TextBlock.foreground mutedBrush
                                    TextBlock.margin (Thickness(0.0, 8.0, 0.0, 0.0))
                                ] :> IView
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

// ─── Toolbox tab ────────────────────────────────────────────────────────

let private relativeTime (t: DateTimeOffset) : string =
    let span = DateTimeOffset.UtcNow - t.ToUniversalTime()
    if span.TotalSeconds < 60.0 then "just now"
    elif span.TotalMinutes < 60.0 then sprintf "%dm ago" (int span.TotalMinutes)
    elif span.TotalHours < 24.0 then sprintf "%dh ago" (int span.TotalHours)
    else sprintf "%dd ago" (int span.TotalDays)

let private extensionColor (ext: string) : IBrush =
    match ext with
    | ".ps1"          -> brush "#5391FE"
    | ".bat" | ".cmd" -> brush "#c1c1c1"
    | ".sh"           -> brush "#89e051"
    | _               -> mutedBrush

/// A small toggle-style choice button for the sort selector (reuses the
/// subTabButton look: active = filled + accent underline).
let private sortChoiceButton (label: string) (isActive: bool) (onClick: unit -> unit) : IView =
    Button.create [
        Button.content label
        Button.fontSize 12.0
        Button.padding (Thickness(12.0, 4.0))
        Button.background (if isActive then activeBg else transparentBrush)
        Button.foreground (if isActive then (Brushes.White :> IBrush) else dimBrush)
        Button.borderBrush (if isActive then accent else transparentBrush)
        Button.borderThickness (Thickness(0.0, 0.0, 0.0, 2.0))
        Button.cursor handCursor
        Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always)
    ] :> IView

let private toolRow
        (pid: ProjectId)
        (tool: ToolEntry)
        (isOn: bool)
        (inFlight: bool)
        (lastRun: ToolRunRecord option)
        (error: string option)
        (dispatch: Msg -> unit)
        : IView =
    // ON/OFF toggle: green dot when enabled, muted when off.
    let toggle : IView =
        Button.create [
            DockPanel.dock Dock.Left
            Button.background transparentBrush
            Button.borderThickness (Thickness 0.0)
            Button.padding (Thickness(4.0, 0.0, 10.0, 0.0))
            Button.cursor handCursor
            Button.verticalAlignment VerticalAlignment.Center
            ToolTip.tip (if isOn then "Enabled — click to turn off" else "Disabled — click to turn on")
            Button.content (
                TextBlock.create [
                    TextBlock.text "●"
                    TextBlock.fontSize 14.0
                    TextBlock.foreground (if isOn then watchGreen else mutedBrush)
                ])
            Button.onClick ((fun _ -> dispatch (ToggleTool (pid, tool.Key))), SubPatchOptions.Always)
        ] :> IView
    let runButton : IView =
        Button.create [
            DockPanel.dock Dock.Right
            Button.content (if inFlight then "Running…" else "Run")
            Button.margin (Thickness(8.0, 0.0, 0.0, 0.0))
            Button.verticalAlignment VerticalAlignment.Center
            Button.isEnabled (isOn && not inFlight)
            Button.onClick ((fun _ -> dispatch (RunTool (pid, tool.Key))), SubPatchOptions.Always)
        ] :> IView
    // Last-run summary (or the last error), Dock.Right of the name.
    let statusInfo : IView =
        match error with
        | Some msg ->
            TextBlock.create [
                DockPanel.dock Dock.Right
                TextBlock.text (sprintf "⚠ %s" msg)
                TextBlock.foreground errorBrush
                TextBlock.fontSize 11.0
                TextBlock.maxWidth 260.0
                TextBlock.textTrimming TextTrimming.CharacterEllipsis
                TextBlock.verticalAlignment VerticalAlignment.Center
                TextBlock.margin (Thickness(8.0, 0.0))
            ] :> IView
        | None ->
            match lastRun with
            | None ->
                TextBlock.create [
                    DockPanel.dock Dock.Right
                    TextBlock.text "never run"
                    TextBlock.foreground mutedBrush
                    TextBlock.fontSize 11.0
                    TextBlock.verticalAlignment VerticalAlignment.Center
                    TextBlock.margin (Thickness(8.0, 0.0))
                ] :> IView
            | Some r ->
                let statusKey = if r.ExitCode = 0 then "success" else "failure"
                StackPanel.create [
                    DockPanel.dock Dock.Right
                    StackPanel.orientation Orientation.Horizontal
                    StackPanel.spacing 6.0
                    StackPanel.verticalAlignment VerticalAlignment.Center
                    StackPanel.margin (Thickness(8.0, 0.0))
                    StackPanel.children [
                        TextBlock.create [
                            TextBlock.text (sprintf "%s %d" (statusIcon statusKey) r.ExitCode)
                            TextBlock.foreground (statusBrush statusKey)
                            TextBlock.fontSize 11.0
                            TextBlock.verticalAlignment VerticalAlignment.Center
                        ]
                        TextBlock.create [
                            TextBlock.text (sprintf "· %s · %s" (relativeTime r.StartedAt) (formatDuration r.DurationSec))
                            TextBlock.foreground mutedBrush
                            TextBlock.fontSize 11.0
                            TextBlock.verticalAlignment VerticalAlignment.Center
                        ]
                        Button.create [
                            Button.content "log"
                            Button.fontSize 11.0
                            Button.padding (Thickness(6.0, 0.0))
                            Button.background transparentBrush
                            Button.borderThickness (Thickness 0.0)
                            Button.foreground accent
                            Button.cursor handCursor
                            Button.onClick ((fun _ -> dispatch (OpenFile r.LogPath)), SubPatchOptions.Always)
                        ]
                    ]
                ] :> IView
    let badge : IView =
        Border.create [
            DockPanel.dock Dock.Left
            Border.background stripBg
            Border.cornerRadius (CornerRadius 3.0)
            Border.padding (Thickness(6.0, 1.0))
            Border.margin (Thickness(0.0, 0.0, 10.0, 0.0))
            Border.verticalAlignment VerticalAlignment.Center
            Border.child (
                TextBlock.create [
                    TextBlock.text (tool.Extension.TrimStart('.'))
                    TextBlock.fontSize 10.0
                    TextBlock.foreground (extensionColor tool.Extension)
                ])
        ] :> IView
    let nameStack : IView =
        StackPanel.create [
            StackPanel.verticalAlignment VerticalAlignment.Center
            StackPanel.children [
                TextBlock.create [
                    TextBlock.text tool.Name
                    TextBlock.fontWeight FontWeight.SemiBold
                    TextBlock.foreground (if isOn then (Brushes.White :> IBrush) else mutedBrush)
                ]
                TextBlock.create [
                    TextBlock.text tool.Key
                    TextBlock.foreground mutedBrush
                    TextBlock.fontSize 11.0
                ]
            ]
        ] :> IView
    Border.create [
        Border.background cardBg
        Border.cornerRadius (CornerRadius 4.0)
        Border.padding (Thickness(10.0, 6.0))
        Border.margin (Thickness(0.0, 0.0, 0.0, 4.0))
        Border.child (
            DockPanel.create [
                DockPanel.children [
                    toggle
                    runButton
                    statusInfo
                    badge
                    nameStack
                ]
            ])
    ] :> IView

let private toolboxRecentRow (e: ToolRunRecord) (dispatch: Msg -> unit) : IView =
    let statusKey = if e.ExitCode = 0 then "success" else "failure"
    DockPanel.create [
        DockPanel.margin (Thickness(0.0, 2.0))
        DockPanel.children [
            Button.create [
                DockPanel.dock Dock.Right
                Button.content "log"
                Button.fontSize 11.0
                Button.padding (Thickness(6.0, 0.0))
                Button.background transparentBrush
                Button.borderThickness (Thickness 0.0)
                Button.foreground accent
                Button.cursor handCursor
                Button.onClick ((fun _ -> dispatch (OpenFile e.LogPath)), SubPatchOptions.Always)
            ] :> IView
            TextBlock.create [
                DockPanel.dock Dock.Left
                TextBlock.width 70.0
                TextBlock.text (sprintf "%s %d" (statusIcon statusKey) e.ExitCode)
                TextBlock.foreground (statusBrush statusKey)
                TextBlock.fontSize 12.0
            ] :> IView
            TextBlock.create [
                DockPanel.dock Dock.Right
                TextBlock.width 70.0
                TextBlock.text (formatDuration e.DurationSec)
                TextBlock.foreground mutedBrush
                TextBlock.fontSize 12.0
            ] :> IView
            TextBlock.create [
                DockPanel.dock Dock.Right
                TextBlock.width 130.0
                TextBlock.text (e.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
                TextBlock.foreground mutedBrush
                TextBlock.fontSize 12.0
            ] :> IView
            TextBlock.create [
                TextBlock.text e.ToolKey
                TextBlock.textTrimming TextTrimming.CharacterEllipsis
                TextBlock.fontSize 12.0
            ] :> IView
        ]
    ] :> IView

let private toolboxBody
        (pid: ProjectId)
        (projectRoot: string)
        (model: Model)
        (dispatch: Msg -> unit)
        : IView =
    let load = Map.tryFind pid model.ProjectToolbox
    let state =
        Map.tryFind pid model.ToolboxStates
        |> Option.defaultValue { Disabled = Set.empty; Sort = ByName }
    let history = Map.tryFind pid model.ToolboxHistories |> Option.defaultValue []
    let lastRunsMap = Toolbox.lastRuns history
    let recentOpen = model.ToolboxRecentOpen.Contains pid
    let sortSelector : IView =
        StackPanel.create [
            StackPanel.orientation Orientation.Horizontal
            StackPanel.spacing 2.0
            StackPanel.verticalAlignment VerticalAlignment.Center
            StackPanel.children [
                TextBlock.create [
                    TextBlock.text "sort:"
                    TextBlock.foreground mutedBrush
                    TextBlock.fontSize 12.0
                    TextBlock.verticalAlignment VerticalAlignment.Center
                    TextBlock.margin (Thickness(0.0, 0.0, 4.0, 0.0))
                ]
                sortChoiceButton "name"     (state.Sort = ByName)      (fun () -> dispatch (SetToolboxSort (pid, ByName)))
                sortChoiceButton "last run" (state.Sort = ByLastRun)   (fun () -> dispatch (SetToolboxSort (pid, ByLastRun)))
                sortChoiceButton "type"     (state.Sort = ByExtension) (fun () -> dispatch (SetToolboxSort (pid, ByExtension)))
            ]
        ] :> IView
    let toolCount =
        match load with
        | Some (ToolboxOk (_, tools)) -> List.length tools
        | _ -> 0
    let header : IView =
        DockPanel.create [
            DockPanel.dock Dock.Top
            DockPanel.margin (Thickness(16.0, 12.0))
            DockPanel.children [
                sortSelector
                StackPanel.create [
                    StackPanel.orientation Orientation.Horizontal
                    StackPanel.spacing 12.0
                    StackPanel.children [
                        TextBlock.create [
                            TextBlock.text (sprintf "%d tool(s)" toolCount)
                            TextBlock.foreground mutedBrush
                            TextBlock.verticalAlignment VerticalAlignment.Center
                        ]
                        Button.create [
                            Button.content "Refresh"
                            Button.onClick ((fun _ -> dispatch (RefreshToolbox pid)), SubPatchOptions.Always)
                        ]
                    ]
                ]
            ]
        ] :> IView
    let recentSection : IView =
        if List.isEmpty history then TextBlock.create [ TextBlock.text "" ] :> IView
        else
            StackPanel.create [
                StackPanel.margin (Thickness(0.0, 12.0, 0.0, 0.0))
                StackPanel.children [
                    Button.create [
                        Button.content (sprintf "%s Recent runs (%d)" (if recentOpen then "▾" else "▸") (List.length history))
                        Button.background transparentBrush
                        Button.borderThickness (Thickness 0.0)
                        Button.foreground dimBrush
                        Button.padding (Thickness 0.0)
                        Button.cursor handCursor
                        Button.horizontalAlignment HorizontalAlignment.Left
                        Button.onClick ((fun _ -> dispatch (ToggleToolboxRecent pid)), SubPatchOptions.Always)
                    ]
                    (if recentOpen then
                        StackPanel.create [
                            StackPanel.margin (Thickness(0.0, 6.0, 0.0, 0.0))
                            StackPanel.children [
                                for e in history |> List.truncate 20 -> toolboxRecentRow e dispatch
                            ]
                        ] :> IView
                     else TextBlock.create [ TextBlock.text "" ] :> IView)
                ]
            ] :> IView
    let body : IView =
        match load with
        | None | Some ToolboxLoading ->
            TextBlock.create [
                TextBlock.text "Scanning…"
                TextBlock.margin (Thickness 16.0)
                TextBlock.foreground mutedBrush
            ] :> IView
        | Some (ToolboxOk (cfg, tools)) ->
            if List.isEmpty cfg.ScriptDirs then
                TextBlock.create [
                    TextBlock.text "No script directories configured. Add one in the Settings tab (Toolbox section) to discover scripts here."
                    TextBlock.margin (Thickness 16.0)
                    TextBlock.foreground mutedBrush
                    TextBlock.textWrapping TextWrapping.Wrap
                ] :> IView
            elif List.isEmpty tools then
                TextBlock.create [
                    TextBlock.text (sprintf "No scripts (.bat / .cmd / .ps1 / .sh) found under: %s" (String.concat ", " cfg.ScriptDirs))
                    TextBlock.margin (Thickness 16.0)
                    TextBlock.foreground mutedBrush
                    TextBlock.textWrapping TextWrapping.Wrap
                ] :> IView
            else
                let ordered =
                    Toolbox.sortTools
                        state.Sort
                        (fun k -> Map.tryFind k lastRunsMap |> Option.map (fun r -> r.StartedAt))
                        state.Disabled
                        tools
                ScrollViewer.create [
                    ScrollViewer.content (
                        StackPanel.create [
                            StackPanel.margin (Thickness(16.0, 0.0, 16.0, 16.0))
                            StackPanel.children [
                                for t in ordered do
                                    yield toolRow pid t
                                        (not (state.Disabled.Contains t.Key))
                                        (model.ToolRunsInFlight.Contains (pid, t.Key))
                                        (Map.tryFind t.Key lastRunsMap)
                                        (Map.tryFind (pid, t.Key) model.ToolRunErrors)
                                        dispatch
                                yield recentSection
                            ]
                        ]
                    )
                ] :> IView
    DockPanel.create [
        DockPanel.children [
            header
            body
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
        // "Open in editor": resolved by Core (UE delegates to the .uproject
        // shell association, Unity matches the project's target version,
        // Godot uses engine_path / PATH). Enabled on success; on failure the
        // button is disabled and the reason shown, so e.g. a Godot project
        // with no engine_path explains itself rather than failing silently.
        let editorLaunch =
            match Map.tryFind pid model.ProjectInfo with
            | Some (ProjectInfoOk proj)  ->
                // Godot resolves from the project-local engine_path (else PATH);
                // matches the OpenInEditor handler and the runner.
                Engines.resolveEditorLaunch proj.Engine p.Path
            | Some ProjectInfoMissing    -> Error "project.toml is missing"
            | Some (ProjectInfoError e)  -> Error e
            | None                       -> Error "project not loaded"
        // "Open in IDE" resolves the global command template against this
        // project (UE→{uproject}, any→{sln}/{project_dir}). Disabled (with a
        // reason) when no template is set or a placeholder can't be filled.
        let ideLaunch : Result<string, string> =
            match model.AppSettings.IdeCommand with
            | None -> Error "set the IDE command in Settings"
            | Some t ->
                match Map.tryFind pid model.ProjectInfo with
                | Some (ProjectInfoOk proj) -> Engines.resolveIdeCommand proj.Engine p.Path t
                | _ -> Error "project not loaded"
        let isOk = function Ok _ -> true | Error _ -> false
        let reasonBlock (msg: string) : IView =
            TextBlock.create [
                TextBlock.text msg
                TextBlock.foreground mutedBrush
                TextBlock.fontSize 11.0
                TextBlock.maxWidth 280.0
                TextBlock.textWrapping TextWrapping.Wrap
                TextBlock.verticalAlignment VerticalAlignment.Center
            ] :> IView
        let openButton (label: string) (result: Result<'a, string>) (opening: bool) (msg: Msg) : IView =
            Button.create [
                Button.content (if opening then "Opening…" else label)
                Button.verticalAlignment VerticalAlignment.Center
                Button.isEnabled (isOk result && not opening)
                Button.onClick ((fun _ -> dispatch msg), SubPatchOptions.Always)
            ] :> IView
        let openEditorControl : IView =
            StackPanel.create [
                DockPanel.dock Dock.Right
                StackPanel.orientation Orientation.Horizontal
                StackPanel.spacing 8.0
                StackPanel.verticalAlignment VerticalAlignment.Center
                StackPanel.children [
                    match editorLaunch with Error m -> reasonBlock m | Ok _ -> ()
                    openButton "Open in editor" editorLaunch (Set.contains pid model.OpeningEditors) (OpenInEditor pid)
                    match ideLaunch with Error m -> reasonBlock m | Ok _ -> ()
                    openButton "Open in IDE" ideLaunch (Set.contains pid model.OpeningIde) (OpenInIde pid)
                ]
            ] :> IView
        DockPanel.create [
            DockPanel.children [
                DockPanel.create [
                    DockPanel.dock Dock.Top
                    DockPanel.margin (Thickness(16.0, 12.0, 16.0, 8.0))
                    DockPanel.children [
                        openEditorControl
                        StackPanel.create [
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
                    ]
                ]
                projectSubTabHeader pid active dispatch
                (match active with
                 | ProjectFlows    ->
                     flowsBody pid p.Path model dispatch
                 | ProjectToolboxTab ->
                     toolboxBody pid p.Path model dispatch
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
                     settingsBody pid p.Path load secrets model dispatch)
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
                yield TextBlock.text (sprintf "%s  (%s)" s.Id s.Type)
                yield TextBlock.width 260.0
                // Skipped: dim the name so the row recedes (thin ⊘ alone is
                // easy to miss).
                if s.Status = "skipped" then yield TextBlock.foreground mutedBrush
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

/// Monospace, selectable (copyable) log text for the log panels.
let private logTextBlock (logTail: string list) : IView =
    SelectableTextBlock.create [
        SelectableTextBlock.text (String.concat "\n" logTail)
        SelectableTextBlock.fontFamily (FontFamily "Consolas, Menlo, monospace")
        SelectableTextBlock.fontSize 12.0
        SelectableTextBlock.foreground dimBrush
        SelectableTextBlock.textWrapping TextWrapping.NoWrap
        // Bottom gap so the last line isn't hidden under the Fluent overlay
        // horizontal scrollbar that floats over the bottom when lines overflow.
        SelectableTextBlock.margin (Thickness(0.0, 0.0, 0.0, 16.0))
    ] :> IView

/// Run step outputs (e.g. a UE package's archive_path). Path-valued outputs
/// get an "Open" button so the user can jump to the produced folder/file.
let private outputsSection (outputs: Map<string, Map<string, string>>) (dispatch: Msg -> unit) : IView =
    if Map.isEmpty outputs then TextBlock.create [ TextBlock.text "" ] :> IView
    else
        let row (name: string) (value: string) : IView =
            let isDir  = try System.IO.Directory.Exists value with _ -> false
            let isFile = try System.IO.File.Exists value with _ -> false
            DockPanel.create [
                DockPanel.margin (Thickness(0.0, 2.0))
                DockPanel.children [
                    if isDir || isFile then
                        yield Button.create [
                            DockPanel.dock Dock.Right
                            Button.content "Open"
                            Button.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                            Button.onClick
                                ((fun _ ->
                                    let target = if isDir then value else System.IO.Path.GetDirectoryName value
                                    dispatch (OpenInExplorer target)), SubPatchOptions.Always)
                        ] :> IView
                    yield TextBlock.create [
                        TextBlock.text (sprintf "%s = %s" name value)
                        TextBlock.foreground dimBrush
                        TextBlock.textWrapping TextWrapping.Wrap
                        TextBlock.verticalAlignment VerticalAlignment.Center
                    ] :> IView
                ]
            ] :> IView
        StackPanel.create [
            StackPanel.children [
                yield sectionHeader "Outputs"
                for KeyValue (_, outs) in outputs do
                    for KeyValue (name, value) in outs do
                        yield row name value
            ]
        ] :> IView

/// The RunDetail find box, captured on load (via its routed event source) so
/// a Ctrl+F anywhere in RunDetail can focus it. Only one RunDetail is visible
/// at a time, so a single ref is enough; it's refreshed on each (re)load.
let mutable private logFindBox : TextBox option = None

/// All character offsets in `text` where `q` occurs (case-insensitive),
/// non-overlapping. Empty when `q` is empty.
let private matchOffsets (text: string) (q: string) : int list =
    if System.String.IsNullOrEmpty q then []
    else
        let rec loop start acc =
            let i = text.IndexOf(q, start, System.StringComparison.OrdinalIgnoreCase)
            if i < 0 then List.rev acc
            else loop (i + q.Length) (i :: acc)
        loop 0 []

/// The run's full captured log, reviewable in RunDetail: a find box that
/// JUMPS to (highlights + scrolls to) each match — keeping the whole log
/// visible, not filtering it away — with ◀ ▶ to step matches and a "k / N"
/// counter, plus an "Open in editor" button (log.txt in the OS default app).
/// The log itself is a read-only TextBox (robust selection + copy, unlike a
/// SelectableTextBlock whose selection drops in inter-glyph gaps).
let private runDetailLogSection
        (pid: ProjectId) (runId: RunId) (logPath: string)
        (logLines: string list) (query: string) (matchIdx: int)
        (dispatch: Msg -> unit) : IView =
    if List.isEmpty logLines then TextBlock.create [ TextBlock.text "" ] :> IView
    else
        let fullText = String.concat "\n" logLines
        let offsets  = matchOffsets fullText query
        let count    = List.length offsets
        // Wrap the (unbounded) match cursor into range; the current hit's
        // char span drives the read-only TextBox's selection → highlight+scroll.
        let curIdx   = if count = 0 then 0 else ((matchIdx % count) + count) % count
        let curOff   = if count = 0 then None else Some (List.item curIdx offsets)
        let counterText =
            if System.String.IsNullOrEmpty query then ""
            elif count = 0 then "no matches"
            else sprintf "%d / %d matches" (curIdx + 1) count
        StackPanel.create [
            StackPanel.spacing 4.0
            StackPanel.children [
                sectionHeader "Output"
                DockPanel.create [
                    DockPanel.children [
                        Button.create [
                            DockPanel.dock Dock.Right
                            Button.content "Open in editor"
                            Button.margin (Thickness(8.0, 0.0, 0.0, 0.0))
                            Button.onClick ((fun _ -> dispatch (OpenFile logPath)), SubPatchOptions.Always)
                        ]
                        Button.create [
                            DockPanel.dock Dock.Right
                            Button.content "▶"
                            Button.isEnabled (count > 0)
                            Button.onClick ((fun _ -> dispatch (RunLogFindNext (pid, runId))), SubPatchOptions.Always)
                        ]
                        Button.create [
                            DockPanel.dock Dock.Right
                            Button.content "◀"
                            Button.isEnabled (count > 0)
                            Button.onClick ((fun _ -> dispatch (RunLogFindPrev (pid, runId))), SubPatchOptions.Always)
                        ]
                        TextBlock.create [
                            DockPanel.dock Dock.Right
                            TextBlock.text counterText
                            TextBlock.foreground mutedBrush
                            TextBlock.fontSize 11.0
                            TextBlock.verticalAlignment VerticalAlignment.Center
                            TextBlock.margin (Thickness(8.0, 0.0, 8.0, 0.0))
                        ]
                        TextBox.create [
                            TextBox.name "logFindBox"
                            TextBox.watermark "find… (Ctrl+F · Enter / ▶ for next)"
                            TextBox.text query
                            TextBox.onLoaded (fun e ->
                                match e.Source with
                                | :? TextBox as tb -> logFindBox <- Some tb
                                | _ -> ())
                            TextBox.onTextChanged
                                ((fun s -> dispatch (SetRunLogFilter (pid, runId, s))), SubPatchOptions.Always)
                            TextBox.onKeyDown
                                ((fun e ->
                                    if e.Key = Key.Enter then
                                        e.Handled <- true
                                        dispatch (RunLogFindNext (pid, runId))),
                                 SubPatchOptions.Always)
                        ]
                    ]
                ]
                TextBox.create [
                    yield TextBox.text fullText
                    yield TextBox.isReadOnly true
                    yield TextBox.acceptsReturn true
                    yield TextBox.textWrapping TextWrapping.NoWrap
                    yield TextBox.fontFamily (FontFamily "Consolas, Menlo, monospace")
                    yield TextBox.fontSize 12.0
                    yield TextBox.foreground dimBrush
                    yield TextBox.background (brush "#161616")
                    yield TextBox.maxHeight 420.0
                    // A vivid selection so a jumped-to match is obvious even
                    // when the log box isn't focused (default highlight is too
                    // dim on the dark background to read as "found it").
                    yield TextBox.selectionBrush (brush "#CC7A00")
                    yield TextBox.selectionForegroundBrush (brush "#0A0A0A")
                    // Drive selection to the current match so it highlights AND
                    // scrolls into view. Order matters (FuncUI applies attrs in
                    // list order): set caretIndex FIRST — that scrolls the match
                    // into view but collapses the selection — then re-apply
                    // selectionStart/End to restore the highlight at the now-
                    // visible position. Only when there is a match, so manual
                    // selection stays free when not searching.
                    match curOff with
                    | Some off ->
                        yield TextBox.caretIndex off
                        yield TextBox.selectionStart off
                        yield TextBox.selectionEnd (off + query.Length)
                    | None -> ()
                ]
            ]
        ] :> IView

let private runDetailBody
        (pid: ProjectId)
        (entry: RunHistoryEntry)
        (steps: StepSummary list)
        (outputs: Map<string, Map<string, string>>)
        (logLines: string list)
        (logFilter: string)
        (logMatchIdx: int)
        (runCommand: string option)
        (commandCopied: bool)
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
                    // The run dir path is the click target (opens it in the
                    // explorer) — a text-styled Button, same as the project
                    // header path.
                    Button.create [
                        Button.content (sprintf "Dir:      %s" entry.RunDir)
                        Button.foreground dimBrush
                        Button.fontSize 11.0
                        Button.background transparentBrush
                        Button.borderThickness (Thickness 0.0)
                        Button.padding (Thickness 0.0)
                        Button.cursor handCursor
                        Button.horizontalAlignment HorizontalAlignment.Left
                        Button.onClick ((fun _ -> dispatch (OpenInExplorer entry.RunDir)), SubPatchOptions.Always)
                    ]
                    // Re-run this flow — with this run's recorded params, or
                    // with the flow's current flows.toml defaults.
                    StackPanel.create [
                        StackPanel.orientation Orientation.Horizontal
                        StackPanel.spacing 8.0
                        StackPanel.margin (Thickness(0.0, 12.0, 0.0, 4.0))
                        StackPanel.children [
                            Button.create [
                                Button.content "Re-run with same params"
                                Button.onClick ((fun _ -> dispatch (RerunSameParams (pid, entry.RunId))), SubPatchOptions.Always)
                            ]
                            Button.create [
                                Button.content "Re-run with current defaults"
                                Button.onClick ((fun _ -> dispatch (RunFlow (pid, entry.FlowId))), SubPatchOptions.Always)
                            ]
                            // Copy the equivalent `takatora run …` command
                            // (secrets omitted) — reproduces this run from a
                            // shell / CI. Hidden if the run isn't cached.
                            match runCommand with
                            | Some cmd ->
                                Button.create [
                                    Button.content (if commandCopied then "Copied ✓" else "Copy CLI command")
                                    ToolTip.tip cmd
                                    Button.onClick (
                                        (fun e ->
                                            copyToClipboard cmd e
                                            dispatch (MarkRunCommandCopied (pid, entry.RunId))),
                                        SubPatchOptions.Always)
                                ] :> IView
                            | None -> ()
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
                    outputsSection outputs dispatch
                    runDetailLogSection pid entry.RunId
                        (System.IO.Path.Combine(entry.RunDir, "log.txt"))
                        logLines logFilter logMatchIdx dispatch
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
    | Some (entry, steps, outputs, logLines) ->
        let logFilter   = Map.tryFind (pid, runId) model.RunLogFilter   |> Option.defaultValue ""
        let logMatchIdx = Map.tryFind (pid, runId) model.RunLogMatchIdx |> Option.defaultValue 0
        DockPanel.create [
            // Ctrl+F focuses the log find box (selecting its text) from anywhere
            // in RunDetail, so search is one keystroke away — like a text editor.
            DockPanel.onKeyDown ((fun e ->
                if e.Key = Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control) then
                    match logFindBox with
                    | Some tb ->
                        e.Handled <- true
                        tb.Focus() |> ignore
                        tb.SelectAll()
                    | None -> ()), SubPatchOptions.Always)
            DockPanel.children [
                runDetailNav pid runId model dispatch
                runDetailBody pid entry steps outputs logLines logFilter logMatchIdx
                    (State.runCommandFor model pid runId)
                    (Set.contains (pid, runId) model.CopiedRunCmd)
                    dispatch
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
    | StepSkipped   -> "⊘"
    | StepCancelled -> "⊗"

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
                yield TextBlock.text step.Id
                yield TextBlock.width 180.0
                // Skipped: dim the name so the whole row visibly recedes —
                // the thin ⊘ glyph alone reads as a smudge.
                if step.Status = StepSkipped then yield TextBlock.foreground mutedBrush
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

// Whether the live-log view is "following" the tail. Flipped by the user
// scrolling: at the bottom → follow; scrolled up → stop (so reading back
// isn't yanked down). Module-level (one log viewed at a time); read/written
// only on the UI thread inside the ScrollChanged handler.
let mutable private logFollow = true

/// Log tail panel. A DockPanel so the dark box (last child) FILLS the
/// height it's given — when liveRunBody docks this as the fill region it
/// grows to the window bottom instead of being capped, and only scrolls
/// when the output truly overflows the available space.
let private liveLogSection (logTail: string list) : IView =
    DockPanel.create [
        DockPanel.margin (Thickness(16.0, 4.0, 16.0, 16.0))
        DockPanel.children [
            TextBlock.create [
                DockPanel.dock Dock.Top
                TextBlock.text "Output (tail)"
                TextBlock.fontSize 14.0
                TextBlock.fontWeight FontWeight.SemiBold
                TextBlock.margin (Thickness(0.0, 0.0, 0.0, 4.0))
            ]
            Border.create [
                Border.background (brush "#161616")
                Border.borderBrush stripBorder
                Border.borderThickness (Thickness 1.0)
                Border.cornerRadius 4.0
                Border.padding (Thickness 8.0)
                // Click in the empty area of the log panel (past the text — incl.
                // the ragged space to the right of a short line, or below the
                // last line — but still inside the box) → clear the selection.
                // A press that actually lands on a glyph is left alone so normal
                // selection still works. The control's Bounds is the full
                // rectangle covering every line, so test real glyph hits via the
                // text layout rather than the bounding box.
                Border.onPointerPressed ((fun e ->
                    let tbOpt =
                        match e.Source with
                        | :? SelectableTextBlock as tb -> Some tb
                        | :? Visual as v ->
                            match v.FindDescendantOfType<SelectableTextBlock>() with
                            | null -> None
                            | tb -> Some tb
                        | _ -> None
                    match tbOpt with
                    | Some tb ->
                        let onGlyph =
                            try
                                let pt = e.GetPosition(tb)
                                tb.TextLayout.HitTestPoint(&pt).IsInside
                            with _ -> false
                        if not onGlyph then tb.ClearSelection()
                    | None -> ()), SubPatchOptions.Always)
                Border.child (
                    ScrollViewer.create [
                        ScrollViewer.horizontalScrollBarVisibility ScrollBarVisibility.Auto
                        // Tail-follow: when new log grows the extent, scroll to
                        // the end only if the user was already at the bottom; a
                        // user scroll updates that intent. (OffsetDelta = user/
                        // programmatic move; ExtentDelta with no offset move =
                        // content grew.)
                        ScrollViewer.onScrollChanged (fun e ->
                            match e.Source with
                            | :? ScrollViewer as sv ->
                                if e.OffsetDelta.Y <> 0.0 then
                                    logFollow <- sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 4.0
                                elif e.ExtentDelta.Y <> 0.0 && logFollow then
                                    sv.ScrollToEnd()
                            | _ -> ())
                        ScrollViewer.content (logTextBlock logTail)
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
    // Header / phase / steps sit at the top; the log tail fills the rest of
    // the window (so it isn't capped to a short box with empty space below).
    let info =
        StackPanel.create [
            DockPanel.dock Dock.Top
            StackPanel.margin (Thickness(16.0, 16.0, 16.0, 4.0))
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
            ]
        ] :> IView
    if List.isEmpty s.Progress.LogTail then
        ScrollViewer.create [ ScrollViewer.content info ] :> IView
    else
        DockPanel.create [
            DockPanel.children [
                info
                liveLogSection s.Progress.LogTail
            ]
        ] :> IView

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
        yield DockPanel.margin (Thickness(0.0, 0.0, 0.0, 10.0))
        // A transparent background makes the WHOLE row (incl. gaps) hit-test
        // visible, so the tooltip triggers anywhere on the row — not only
        // directly over the label glyphs / controls.
        yield DockPanel.background transparentBrush
        // Embedded description (flows.toml `description = "..."`) → hover tooltip.
        match v.Description with
        | Some d -> yield ToolTip.tip d
        | None -> ()
        yield DockPanel.children [ label; widget ]
    ] :> _

let private runParamsDialog (d: RunDialogState) (dispatch: Msg -> unit) : IView =
    // Full-bleed scrim with a centered card on top.
    Border.create [
        Border.background overlayBg
        Border.child (
            Border.create [
                Border.horizontalAlignment HorizontalAlignment.Center
                Border.verticalAlignment VerticalAlignment.Center
                Border.width 600.0
                // Keep the card off the window edges; its content scrolls
                // (below) when it would otherwise grow past the window.
                Border.margin (Thickness(0.0, 24.0))
                // Small right padding so the scroll bar hugs the card edge;
                // title/fields/actions keep a symmetric 20px inset via their
                // own 16px right margins (20 - 4 card padding ≈ matches left).
                Border.padding (Thickness(20.0, 20.0, 4.0, 20.0))
                Border.cornerRadius 6.0
                Border.background cardBg
                Border.borderBrush stripBorder
                Border.borderThickness (Thickness 1.0)
                Border.child (
                    DockPanel.create [
                        DockPanel.children [
                            // Title row — fixed at the top.
                            yield DockPanel.create [
                                DockPanel.dock Dock.Top
                                DockPanel.margin (Thickness(0.0, 0.0, 16.0, 16.0))
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
                            // Actions — fixed at the bottom, always visible.
                            yield StackPanel.create [
                                DockPanel.dock Dock.Bottom
                                StackPanel.orientation Orientation.Horizontal
                                StackPanel.horizontalAlignment HorizontalAlignment.Right
                                StackPanel.spacing 8.0
                                StackPanel.margin (Thickness(0.0, 16.0, 16.0, 0.0))
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
                            // Save-as-new-default toggle — fixed above the
                            // actions (writes changed values back to flows.toml
                            // on Run).
                            yield CheckBox.create [
                                DockPanel.dock Dock.Bottom
                                CheckBox.margin (Thickness(0.0, 12.0, 16.0, 0.0))
                                // A checkbox (not a pill) so it reads as an
                                // option applied on Run — not an action that
                                // saves the instant you click it.
                                CheckBox.content "Save as new default"
                                CheckBox.isChecked d.SaveDefaults
                                CheckBox.onClick ((fun _ -> dispatch RunDialogToggleSaveDefaults), SubPatchOptions.Always)
                            ] :> IView
                            // Validation error — fixed just above the actions.
                            match d.Error with
                            | Some msg ->
                                yield TextBlock.create [
                                    DockPanel.dock Dock.Bottom
                                    TextBlock.text msg
                                    TextBlock.foreground errorBrush
                                    TextBlock.textWrapping TextWrapping.Wrap
                                    TextBlock.margin (Thickness(0.0, 10.0, 16.0, 0.0))
                                ] :> IView
                            | None -> ()
                            // Fields fill the middle and scroll when the dialog
                            // would exceed the window height. bool vars gated by
                            // a step's `when` go under "Toggles", rest under
                            // "Parameters" (flat if there are no toggles).
                            yield ScrollViewer.create [
                                ScrollViewer.horizontalScrollBarVisibility ScrollBarVisibility.Disabled
                                // Bar hidden so it doesn't compete with the
                                // right-edge Browse… / − buttons; the content
                                // still scrolls on the mouse wheel (vertical
                                // wheel maps to vertical scroll natively).
                                ScrollViewer.verticalScrollBarVisibility ScrollBarVisibility.Hidden
                                ScrollViewer.content (
                                    StackPanel.create [
                                        StackPanel.spacing 0.0
                                        // 16px right margin: fields keep the
                                        // symmetric 20px inset and clear the
                                        // overlay scroll bar (which hugs the
                                        // card's right edge via the small card
                                        // right padding).
                                        StackPanel.margin (Thickness(0.0, 0.0, 16.0, 0.0))
                                        StackPanel.children [
                                            let renderField (v: FlowVar) : IView =
                                                let current =
                                                    Map.tryFind v.Name d.Values
                                                    |> Option.defaultValue (varDefaultText v)
                                                let items = Map.tryFind v.Name d.Lists |> Option.defaultValue []
                                                runDialogField v current items
                                                    (Set.contains v.Name d.Remember)
                                                    (Set.contains v.Name d.Stored)
                                                    dispatch
                                            let toggleVars, paramVars =
                                                d.Vars |> List.partition (fun v -> Set.contains v.Name d.Toggles)
                                            if not (List.isEmpty toggleVars) then
                                                yield sectionHeader "Toggles"
                                                yield! toggleVars |> List.map renderField
                                                yield sectionHeader "Parameters"
                                            yield! paramVars |> List.map renderField
                                            match State.dialogDiffs d with
                                            | [] -> ()
                                            | diffs ->
                                                yield sectionHeader "Diff from defaults"
                                                for (name, def, cur) in diffs do
                                                    yield TextBlock.create [
                                                        TextBlock.text (sprintf "%s: %s → %s" name def cur)
                                                        TextBlock.foreground dimBrush
                                                        TextBlock.fontSize 12.0
                                                        TextBlock.textWrapping TextWrapping.Wrap
                                                    ] :> IView
                                        ]
                                    ]
                                )
                            ] :> IView
                        ]
                    ]
                )
            ]
        )
    ] :> _

// ─── Top-level ──────────────────────────────────────────────────────────

/// About overlay: product, version, license, copyright, repo. Click the
/// scrim or Close to dismiss.
let private aboutDialog (dispatch: Msg -> unit) : IView =
    let line (text: string) (fg: IBrush) (size: float) =
        TextBlock.create [
            TextBlock.text text
            TextBlock.foreground fg
            TextBlock.fontSize size
            TextBlock.horizontalAlignment HorizontalAlignment.Center
        ] :> IView
    // A link-style button (used for the repo URL and the notices link).
    let linkButton (text: string) (onClick: unit -> unit) =
        Button.create [
            Button.content text
            Button.horizontalAlignment HorizontalAlignment.Center
            Button.background transparentBrush
            Button.borderThickness (Thickness 0.0)
            Button.foreground accent
            Button.fontSize 12.0
            Button.cursor handCursor
            Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always)
        ] :> IView
    // NOTE: no click-the-scrim-to-dismiss — pressing inside the card would
    // close About on pointer-down before an inner button's onClick fired
    // (the THIRD-PARTY NOTICES / repo links did nothing). Use the Close button.
    Border.create [
        Border.background overlayBg
        Border.child (
            Border.create [
                Border.background cardBg
                Border.cornerRadius 6.0
                Border.padding (Thickness(28.0, 24.0))
                Border.horizontalAlignment HorizontalAlignment.Center
                Border.verticalAlignment VerticalAlignment.Center
                Border.maxWidth 420.0
                Border.child (
                    StackPanel.create [
                        StackPanel.spacing 10.0
                        StackPanel.children [
                            line Version.Product (Brushes.White :> IBrush) 22.0
                            line (sprintf "v%s" Version.Version) dimBrush 13.0
                            line "Local CI for game builds — CI without the CI server." mutedBrush 12.0
                            line (sprintf "%s License · %s" Version.License Version.Copyright) dimBrush 12.0
                            // Clickable repo link → opens in the default browser.
                            linkButton Version.Repository (fun () -> dispatch (OpenUrl Version.Repository))
                            // Opens the bundled THIRD-PARTY-NOTICES.txt next to the exe.
                            linkButton "THIRD-PARTY NOTICES" (fun () ->
                                dispatch (OpenFile (System.IO.Path.Combine(System.AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt"))))
                            Button.create [
                                Button.content "Close"
                                Button.horizontalAlignment HorizontalAlignment.Center
                                Button.margin (Thickness(0.0, 8.0, 0.0, 0.0))
                                Button.onClick ((fun _ -> dispatch HideAbout), SubPatchOptions.Always)
                            ]
                        ]
                    ]
                )
            ]
        )
    ] :> _

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
            if model.ShowingAbout then yield aboutDialog dispatch
        ]
    ] :> _
