module Takatora.Gui.TrayBridge

/// Thin, dependency-free bridge between the Elmish model (which owns watch
/// state) and the tray icon/menu (which lives outside Elmish, created in
/// App.OnFrameworkInitializationCompleted). Two directions:
///   model → tray : `publish` pushes the current watch status (icon color,
///                  menu text). Stateful so a late subscriber gets the last value.
///   tray → model : `requestToggleGlobal` is wired to a dispatch in `init`,
///                  so the tray's Pause/Resume item flips the global master.

/// Watch status the tray renders. `Watching` is the effective green/red flag
/// (global on AND something armed); `GlobalEnabled` drives the Pause/Resume
/// label; `Count` is the number of effectively-armed watches.
type WatchStatus =
    { Watching: bool
      Count: int
      GlobalEnabled: bool }

let mutable private last : WatchStatus =
    { Watching = false; Count = 0; GlobalEnabled = true }

let mutable private onStatus : WatchStatus -> unit = ignore

/// Model side: push the current status to the tray.
let publish (s: WatchStatus) =
    last <- s
    onStatus s

/// Tray side: register the status sink and seed it with the last value (the
/// tray is created after the Elmish program has already run `init`).
let subscribeStatus (f: WatchStatus -> unit) =
    onStatus <- f
    f last

/// Tray → model: ask to flip the global watch master. Wired in `State.init`
/// (which captures the real Elmish dispatch); a no-op until then.
let mutable requestToggleGlobal : unit -> unit = ignore

/// Tray → model: the Quit menu item routes through the model (RequestQuit)
/// so it can confirm first when toolbox runs are still in flight. Wired in
/// `State.init`; a no-op until then.
let mutable requestQuit : unit -> unit = ignore

/// Model → app: actually shut the process down (sets the close-to-tray
/// bypass and calls desktop.Shutdown). Set in
/// App.OnFrameworkInitializationCompleted; a no-op until then.
let mutable performQuit : unit -> unit = ignore

/// Model → app: re-show/activate the main window (it may be hidden to the
/// tray when the quit confirmation needs to appear). Set alongside
/// `performQuit`.
let mutable showMainWindow : unit -> unit = ignore
