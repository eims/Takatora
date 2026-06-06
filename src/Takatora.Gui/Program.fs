module Takatora.Gui.Program

open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.FuncUI.Hosts
open Avalonia.Themes.Fluent
open Elmish
open Avalonia.FuncUI.Elmish
open Takatora.Core

type MainWindow() as this =
    inherit HostWindow()
    do
        base.Title  <- Version.Product
        base.Width  <- 1024.0
        base.Height <- 700.0
        // NOTE: custom window chrome (ExtendClientAreaToDecorationsHint + a
        // self-drawn title bar) was attempted but BeginMoveDrag doesn't track
        // reliably on the pinned Avalonia 11.2.0-beta1 (the window snaps back
        // on drag). Reverted to the OS title bar; revisit after an Avalonia bump.
        Program.mkProgram State.init State.update View.view
        |> Program.withHost this
        |> Program.run

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
