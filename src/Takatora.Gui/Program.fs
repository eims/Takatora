module Takatora.Gui.Program

open System
open System.IO
open System.Threading
open Avalonia
open Avalonia.Threading
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.FuncUI.Hosts
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Themes.Fluent
open Elmish
open Avalonia.FuncUI.Elmish
open Takatora.Core

/// Set true by the tray "Quit" so the window's close handler stops canceling
/// (close-to-tray) and lets the real shutdown through.
let mutable private quitting = false

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
        //
        // Close-to-tray: closing the window hides it instead of exiting, so a
        // running watch keeps going. The tray menu re-shows or quits. (App sets
        // ShutdownMode = OnExplicitShutdown so the hidden window doesn't end the
        // process.)
        this.Closing.Add(fun e ->
            if not quitting then
                e.Cancel <- true
                this.Hide())
        Program.mkProgram State.init State.update View.view
        |> Program.withHost this
        |> Program.run

/// A small generated tray icon (no .ico asset in the repo): a rounded accent
/// square in the given color. Falls back to None if rendering isn't available.
let private trayIcon (hex: string) : WindowIcon option =
    try
        let rtb = new RenderTargetBitmap(PixelSize(32, 32), Vector(96.0, 96.0))
        use ctx = rtb.CreateDrawingContext()
        ctx.FillRectangle(SolidColorBrush(Color.Parse hex), Rect(0.0, 0.0, 32.0, 32.0), 6.0f)
        let ms = new MemoryStream()
        rtb.Save(ms)
        ms.Position <- 0L
        Some (WindowIcon(ms))
    with _ -> None

type App() =
    inherit Application()

    override this.Initialize() =
        this.Styles.Add(FluentTheme())
        this.RequestedThemeVariant <- Styling.ThemeVariant.Dark

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            // Don't exit when the window closes (it hides to tray); only an
            // explicit Quit shuts the process down.
            desktop.ShutdownMode <- ShutdownMode.OnExplicitShutdown
            let win = MainWindow()
            desktop.MainWindow <- win
            win.Show()

            let showWindow () =
                win.Show()
                win.Activate() |> ignore

            // Quit plumbing: the model triggers the real shutdown (after its
            // in-flight-tools check) and may need the hidden window back for
            // the confirmation overlay. Whatever path shutdown takes, Exit
            // kills any toolbox scripts the GUI started — they must not
            // outlive the app.
            TrayBridge.performQuit <- fun () ->
                quitting <- true
                desktop.Shutdown()
            TrayBridge.showMainWindow <- showWindow
            desktop.Exit.Add(fun _ -> State.stopAllToolRuns ())

            let tray = new TrayIcon()
            // green = armed & firing, yellow = enabled but nothing armed
            // (idle), red = globally paused. Pre-rendered so updates are cheap.
            let greenIcon  = trayIcon "#4ec97a"
            let yellowIcon = trayIcon "#e0b341"
            let redIcon    = trayIcon "#f15a5a"
            yellowIcon |> Option.iter (fun i -> tray.Icon <- i)
            tray.ToolTipText <- Version.Product
            tray.Clicked.Add(fun _ -> showWindow ())
            let menu = NativeMenu()
            let showItem = NativeMenuItem("Show")
            showItem.Click.Add(fun _ -> showWindow ())
            // Watch master toggle — flips the global WatchEnabled via the model.
            let watchItem = NativeMenuItem("Pause watching")
            watchItem.Click.Add(fun _ -> TrayBridge.requestToggleGlobal ())
            // A disabled info line above it ("Watching: N" / "Idle" / "Paused").
            let watchInfo = NativeMenuItem("Watching: 0")
            watchInfo.IsEnabled <- false
            // Routed through the model (RequestQuit): quits immediately when
            // idle, or asks for confirmation when toolbox runs are in flight.
            let quitItem = NativeMenuItem("Quit")
            quitItem.Click.Add(fun _ -> TrayBridge.requestQuit ())
            menu.Items.Add(showItem)
            menu.Items.Add(NativeMenuItemSeparator())
            menu.Items.Add(watchInfo)
            menu.Items.Add(watchItem)
            menu.Items.Add(NativeMenuItemSeparator())
            menu.Items.Add(quitItem)
            tray.Menu <- menu
            tray.IsVisible <- true

            // Reflect watch status pushed from the model: icon color, tooltip,
            // and the menu's info line + Pause/Resume label. Marshaled to the
            // UI thread (publish may come from a background WatchPoll tick).
            TrayBridge.subscribeStatus(fun s ->
                Dispatcher.UIThread.Post(fun () ->
                    let icon =
                        if not s.GlobalEnabled then redIcon
                        elif s.Count > 0 then greenIcon
                        else yellowIcon
                    icon |> Option.iter (fun i -> tray.Icon <- i)
                    let state =
                        if not s.GlobalEnabled then "Paused"
                        elif s.Count > 0 then sprintf "Watching: %d" s.Count
                        else "Idle"
                    watchInfo.Header <- state
                    watchItem.Header <- (if s.GlobalEnabled then "Pause watching" else "Resume watching")
                    tray.ToolTipText <- sprintf "%s — %s" Version.Product state))
        | _ -> ()

/// Bring the running instance's window back to the front (used when a second
/// launch is detected — it asks us to show instead of starting another tray
/// resident).
let private activateExisting () =
    Dispatcher.UIThread.Post(fun () ->
        match (Application.Current :> obj) with
        | :? Application as app ->
            match app.ApplicationLifetime with
            | :? IClassicDesktopStyleApplicationLifetime as d ->
                match d.MainWindow with
                | null -> ()
                | w -> w.Show(); w.Activate() |> ignore
            | _ -> ()
        | _ -> ())

[<EntryPoint>]
let main argv =
    // Single instance: with tray residency, a second launch would mean two
    // tray icons and two watchers (double-running watched flows). Instead,
    // detect a running instance via a named mutex and ask it to show, then
    // exit this one.
    //
    // The mutex is keyed by the resolved data dir so instances on DIFFERENT
    // data dirs (a TAKATORA_DATA_DIR demo/portable instance vs. the real
    // %APPDATA% one) coexist instead of blocking each other — they manage
    // separate registries/watches, so they're genuinely independent.
    let dataDirKey =
        let h = System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes((AppData.baseDir ()).ToLowerInvariant()))
        System.Convert.ToHexString(h).Substring(0, 12)
    let instanceName = "Takatora.Gui.SingleInstance." + dataDirKey
    let activateName = "Takatora.Gui.Activate." + dataDirKey
    let mutable createdNew = false
    let mutex = new Mutex(true, instanceName, &createdNew)
    if not createdNew then
        (try
            use evt = EventWaitHandle.OpenExisting(activateName)
            evt.Set() |> ignore
         with _ -> ())
        0
    else
        // Primary: listen for "activate" pings from later launches.
        let evt = new EventWaitHandle(false, EventResetMode.AutoReset, activateName)
        let listener =
            Thread(fun () ->
                while true do
                    evt.WaitOne() |> ignore
                    activateExisting ())
        listener.IsBackground <- true
        listener.Start()
        let result =
            AppBuilder
                .Configure<App>()
                .UsePlatformDetect()
                .UseSkia()
                .StartWithClassicDesktopLifetime(argv)
        GC.KeepAlive(mutex)
        result
