module Takatora.Gui.View

open Avalonia
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Takatora.Core
open Takatora.Gui.State

let private brush (hex: string) : IBrush =
    SolidColorBrush(Color.Parse hex) :> _

let private tabLabel = function
    | Home        -> "Home"
    | Project pid -> pid

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
        Border.borderBrush (brush (if isActive then "#3d8bfd" else "#1f1f1f"))
        Border.background (brush (if isActive then "#2a2a2a" else "#1f1f1f"))
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
        Border.borderBrush (brush "#0e0e0e")
        Border.background (brush "#1f1f1f")
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

let private projectRow
        (p: ProjectRegistration)
        (dispatch: Msg -> unit)
        : IView =
    Border.create [
        Border.padding (Thickness 12.0)
        Border.cornerRadius 4.0
        Border.background (brush "#252525")
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
                                TextBlock.foreground (brush "#888888")
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
            TextBlock.foreground (brush "#888888")
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

let private projectView
        (pid: ProjectId)
        (model: Model)
        (_dispatch: Msg -> unit)
        : IView =
    match List.tryFind (fun (p: ProjectRegistration) -> p.Name = pid) model.Projects with
    | None ->
        TextBlock.create [
            TextBlock.text $"Project '{pid}' not found in registry. It may have been removed externally — try Refresh on Home."
            TextBlock.margin (Thickness 16.0)
            TextBlock.foreground (brush "#888888")
            TextBlock.textWrapping TextWrapping.Wrap
        ] :> _
    | Some p ->
        StackPanel.create [
            StackPanel.margin (Thickness 16.0)
            StackPanel.spacing 8.0
            StackPanel.children [
                TextBlock.create [
                    TextBlock.text p.Name
                    TextBlock.fontSize 24.0
                ]
                TextBlock.create [
                    TextBlock.text p.Path
                    TextBlock.foreground (brush "#888888")
                ]
                TextBlock.create [
                    TextBlock.text "Flows / History / Settings subtabs land here in the next slice."
                    TextBlock.foreground (brush "#888888")
                    TextBlock.margin (Thickness(0.0, 16.0, 0.0, 0.0))
                    TextBlock.textWrapping TextWrapping.Wrap
                ]
            ]
        ] :> _

let view (model: Model) (dispatch: Msg -> unit) : IView =
    DockPanel.create [
        DockPanel.children [
            rootTabStrip model dispatch
            (match model.ActiveTab with
             | Home        -> homeView model dispatch
             | Project pid -> projectView pid model dispatch)
        ]
    ] :> _
