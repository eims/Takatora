module Takatora.Gui.Program

open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Hosts
open Avalonia.Themes.Fluent
open Takatora.Core

// Minimal FuncUI bootstrap.
// Real UI structure (root tabs / Home / Project tabs / LiveRun / etc.)
// gets built in dedicated view modules during the GUI implementation phase.

type MainView() =
    inherit Component(fun _ctx ->
        DockPanel.create [
            Panel.children [
                TextBlock.create [
                    TextBlock.text $"{Version.Product} {Version.Version}"
                    TextBlock.fontSize 24.0
                    TextBlock.margin 24.0
                ]
            ]
        ])

type MainWindow() as this =
    inherit HostWindow()
    do
        base.Title <- Version.Product
        base.Width <- 1024.0
        base.Height <- 700.0
        this.Content <- MainView()

type App() =
    inherit Application()

    override this.Initialize() =
        this.Styles.Add(FluentTheme())
        this.RequestedThemeVariant <- Styling.ThemeVariant.Dark

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            desktop.MainWindow <- MainWindow()
        | _ -> ()

[<EntryPoint>]
let main argv =
    AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .UseSkia()
        .StartWithClassicDesktopLifetime(argv)
