module Takatora.Core.Tests.ToolboxTests

open System
open System.IO
open Xunit
open Takatora.Core

/// A throwaway project root + a redirected toolbox state root, so nothing
/// touches the real repo or %APPDATA%. Both are deleted afterwards.
let private withTempProject (f: string -> string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), "tbx-" + Guid.NewGuid().ToString("N"))
    let stateRoot = Path.Combine(Path.GetTempPath(), "tbxstate-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    Toolbox.setStateRootForTests stateRoot
    try f root (Toolbox.stateDir "Sample" root)
    finally
        Toolbox.resetStateRoot ()
        try Directory.Delete(root, true) with _ -> ()
        try Directory.Delete(stateRoot, true) with _ -> ()

let private touch (path: string) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, "")

// ----- config round-trip -----

[<Fact>]
let ``loadConfig returns empty when no file exists`` () =
    withTempProject (fun root _ ->
        Assert.Equal<string list>([], (Toolbox.loadConfig root).ScriptDirs))

[<Fact>]
let ``saveConfig then loadConfig round-trips dirs incl. spaces and backslashes`` () =
    withTempProject (fun root _ ->
        let dirs = [ "tools"; @"C:\shared scripts"; "scripts/dev" ]
        Toolbox.saveConfig root { ScriptDirs = dirs }
        Assert.Equal<string list>(dirs, (Toolbox.loadConfig root).ScriptDirs))

// ----- scan -----

[<Fact>]
let ``scan finds supported scripts, skips dot/denylist dirs and unsupported extensions`` () =
    withTempProject (fun root _ ->
        touch (Path.Combine(root, "tools", "build.ps1"))
        touch (Path.Combine(root, "tools", "sub", "clean.bat"))
        touch (Path.Combine(root, "tools", ".hidden", "secret.sh"))
        touch (Path.Combine(root, "tools", "node_modules", "junk.cmd"))
        touch (Path.Combine(root, "tools", "readme.md"))
        Toolbox.saveConfig root { ScriptDirs = [ "tools" ] }
        let keys = Toolbox.scan root (Toolbox.loadConfig root) |> List.map (fun t -> t.Key) |> List.sort
        Assert.Equal<string list>([ "tools/build.ps1"; "tools/sub/clean.bat" ], keys))

[<Fact>]
let ``scan skips missing dirs and de-dupes overlapping scan dirs`` () =
    withTempProject (fun root _ ->
        touch (Path.Combine(root, "tools", "a.bat"))
        // "tools" and "tools/sub" overlap; a.bat must appear once.
        touch (Path.Combine(root, "tools", "sub", "b.bat"))
        Toolbox.saveConfig root { ScriptDirs = [ "tools"; "tools/sub"; "does-not-exist" ] }
        let tools = Toolbox.scan root (Toolbox.loadConfig root)
        Assert.Equal(2, tools.Length)
        let keys = tools |> List.map (fun t -> t.Key) |> List.sort
        Assert.Equal<string list>([ "tools/a.bat"; "tools/sub/b.bat" ], keys))

[<Fact>]
let ``scan keys scripts outside the root by absolute path`` () =
    withTempProject (fun root _ ->
        let outside = Path.Combine(Path.GetTempPath(), "tbxout-" + Guid.NewGuid().ToString("N"))
        touch (Path.Combine(outside, "external.sh"))
        try
            Toolbox.saveConfig root { ScriptDirs = [ outside ] }
            let tool = Toolbox.scan root (Toolbox.loadConfig root) |> List.exactlyOne
            Assert.True(Path.IsPathRooted tool.Key, sprintf "expected absolute key, got %s" tool.Key)
            Assert.Equal("external.sh", tool.Name)
        finally
            try Directory.Delete(outside, true) with _ -> ())

// ----- state round-trip -----

[<Fact>]
let ``loadState returns defaults when no file exists`` () =
    withTempProject (fun _ stateDir ->
        let st = Toolbox.loadState stateDir
        Assert.Equal<Set<string>>(Set.empty, st.Disabled)
        Assert.Equal(ByName, st.Sort))

[<Theory>]
[<InlineData("name")>]
[<InlineData("last_run")>]
[<InlineData("extension")>]
let ``saveState then loadState round-trips sort and disabled`` (sortStr: string) =
    withTempProject (fun _ stateDir ->
        let sort =
            match sortStr with
            | "last_run" -> ByLastRun
            | "extension" -> ByExtension
            | _ -> ByName
        let st = { Disabled = Set.ofList [ "tools/a.bat"; "tools/b.ps1" ]; Sort = sort }
        Toolbox.saveState stateDir st
        let loaded = Toolbox.loadState stateDir
        Assert.Equal(sort, loaded.Sort)
        Assert.Equal<Set<string>>(st.Disabled, loaded.Disabled))

// ----- history -----

[<Fact>]
let ``appendHistory then loadHistory returns newest first and skips corrupt lines`` () =
    withTempProject (fun _ stateDir ->
        let mk key (secondsAgo: float) code =
            { ToolKey = key
              StartedAt = DateTimeOffset.UtcNow.AddSeconds(-secondsAgo)
              DurationSec = 1.0
              ExitCode = code
              LogPath = Path.Combine(stateDir, "logs", key + ".log") }
        Toolbox.appendHistory stateDir (mk "a.bat" 30.0 0)
        Toolbox.appendHistory stateDir (mk "b.ps1" 20.0 3)
        // inject a corrupt line between valid ones
        File.AppendAllText(Path.Combine(stateDir, "history.ndjson"), "{not json}\n")
        Toolbox.appendHistory stateDir (mk "c.sh" 10.0 0)
        let hist = Toolbox.loadHistory stateDir 100
        Assert.Equal<string list>([ "c.sh"; "b.ps1"; "a.bat" ], hist |> List.map (fun r -> r.ToolKey))
        Assert.Equal(3, (hist |> List.find (fun r -> r.ToolKey = "b.ps1")).ExitCode))

[<Fact>]
let ``loadHistory honors maxEntries cap`` () =
    withTempProject (fun _ stateDir ->
        for i in 1 .. 5 do
            Toolbox.appendHistory stateDir
                { ToolKey = sprintf "t%d.bat" i
                  StartedAt = DateTimeOffset.UtcNow.AddSeconds(float i)
                  DurationSec = 0.0; ExitCode = 0
                  LogPath = "x.log" }
        Assert.Equal(2, (Toolbox.loadHistory stateDir 2).Length))

[<Fact>]
let ``lastRuns picks the most recent record per key`` () =
    let recs =
        [ { ToolKey = "a"; StartedAt = DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero); DurationSec=0.0; ExitCode=0; LogPath="" }
          { ToolKey = "a"; StartedAt = DateTimeOffset(2026,3,1,0,0,0,TimeSpan.Zero); DurationSec=0.0; ExitCode=0; LogPath="" }
          { ToolKey = "b"; StartedAt = DateTimeOffset(2026,2,1,0,0,0,TimeSpan.Zero); DurationSec=0.0; ExitCode=0; LogPath="" } ]
    let m = Toolbox.lastRuns recs
    Assert.Equal(DateTimeOffset(2026,3,1,0,0,0,TimeSpan.Zero), m.["a"].StartedAt)
    Assert.Equal(DateTimeOffset(2026,2,1,0,0,0,TimeSpan.Zero), m.["b"].StartedAt)

// ----- sortTools -----

let private entry key ext =
    { Key = key; Name = Path.GetFileName key; Extension = ext
      FullPath = key; Dir = "" }

[<Fact>]
let ``sortTools places disabled tools below enabled ones`` () =
    let tools = [ entry "z.bat" ".bat"; entry "a.bat" ".bat"; entry "m.bat" ".bat" ]
    let disabled = Set.ofList [ "a.bat" ]   // a is OFF, should drop below z, m
    let ordered = Toolbox.sortTools ByName (fun _ -> None) disabled tools |> List.map (fun t -> t.Key)
    Assert.Equal<string list>([ "m.bat"; "z.bat"; "a.bat" ], ordered)

[<Fact>]
let ``sortTools ByLastRun orders recent first and never-run last`` () =
    let tools = [ entry "a.bat" ".bat"; entry "b.bat" ".bat"; entry "c.bat" ".bat" ]
    let times =
        Map.ofList
            [ "a.bat", DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero)
              "c.bat", DateTimeOffset(2026,5,1,0,0,0,TimeSpan.Zero) ]  // b never run
    let ordered =
        Toolbox.sortTools ByLastRun (fun k -> Map.tryFind k times) Set.empty tools
        |> List.map (fun t -> t.Key)
    Assert.Equal<string list>([ "c.bat"; "a.bat"; "b.bat" ], ordered)

[<Fact>]
let ``sortTools ByExtension groups by extension then name`` () =
    let tools = [ entry "b.ps1" ".ps1"; entry "a.bat" ".bat"; entry "c.bat" ".bat" ]
    let ordered =
        Toolbox.sortTools ByExtension (fun _ -> None) Set.empty tools |> List.map (fun t -> t.Key)
    Assert.Equal<string list>([ "a.bat"; "c.bat"; "b.ps1" ], ordered)

// ----- interpreterFor -----

[<Fact>]
let ``interpreterFor bat uses cmd with slash-c`` () =
    match Toolbox.interpreterFor ".bat" with
    | Ok (exe, argsOf) ->
        Assert.Contains("cmd", exe.ToLowerInvariant())
        Assert.Equal<string list>([ "/c"; @"C:\x.bat" ], argsOf @"C:\x.bat")
    | Error e -> Assert.Fail(e)

[<Fact>]
let ``interpreterFor ps1 passes -File`` () =
    match Toolbox.interpreterFor ".ps1" with
    | Ok (_, argsOf) -> Assert.Contains("-File", argsOf "x.ps1")
    | Error _ -> ()   // PowerShell may be absent on CI; not a failure of the mapping

[<Fact>]
let ``interpreterFor rejects unsupported extensions`` () =
    match Toolbox.interpreterFor ".exe" with
    | Ok _ -> Assert.Fail("expected Error for .exe")
    | Error _ -> ()

// ----- stateDir slug -----

[<Fact>]
let ``stateDir differs for same name at different roots`` () =
    withTempProject (fun _ _ ->
        let a = Toolbox.stateDir "Game" @"C:\projects\one"
        let b = Toolbox.stateDir "Game" @"C:\projects\two"
        Assert.NotEqual<string>(a, b))

[<Fact>]
let ``slug sanitizes illegal filename characters in the project name`` () =
    let s = Toolbox.slug "My:Game/Proj" @"C:\x"
    Assert.DoesNotContain(":", s)
    Assert.DoesNotContain("/", s)

// ----- runTool (Windows: real cmd) -----

[<Fact>]
let ``runTool records exit code and captures output`` () =
    if not (OperatingSystem.IsWindows()) then () else
    withTempProject (fun root stateDir ->
        let batPath = Path.Combine(root, "tools", "ok.bat")
        touch batPath
        File.WriteAllText(batPath, "@echo hello-from-toolbox\r\n@exit /b 0\r\n")
        let tool =
            { Key = "tools/ok.bat"; Name = "ok.bat"; Extension = ".bat"
              FullPath = batPath; Dir = Path.GetDirectoryName batPath }
        match Toolbox.runTool stateDir tool with
        | Error e -> Assert.Fail(e)
        | Ok record ->
            Assert.Equal(0, record.ExitCode)
            Assert.True(File.Exists record.LogPath)
            Assert.Contains("hello-from-toolbox", File.ReadAllText record.LogPath)
            // history got the line too
            Assert.Equal("tools/ok.bat", (Toolbox.loadHistory stateDir 10 |> List.head).ToolKey))

[<Fact>]
let ``runTool reports the nonzero exit code`` () =
    if not (OperatingSystem.IsWindows()) then () else
    withTempProject (fun root stateDir ->
        let batPath = Path.Combine(root, "tools", "fail.bat")
        touch batPath
        File.WriteAllText(batPath, "@exit /b 3\r\n")
        let tool =
            { Key = "tools/fail.bat"; Name = "fail.bat"; Extension = ".bat"
              FullPath = batPath; Dir = Path.GetDirectoryName batPath }
        match Toolbox.runTool stateDir tool with
        | Ok record -> Assert.Equal(3, record.ExitCode)
        | Error e -> Assert.Fail(e))

[<Fact>]
let ``runToolWith's stop handle kills a long-running script`` () =
    if not (OperatingSystem.IsWindows()) then () else
    withTempProject (fun root stateDir ->
        let batPath = Path.Combine(root, "tools", "slow.bat")
        touch batPath
        // ~30s if not killed; well past any sane test budget, so a pass
        // proves the kill (the duration assert is the belt-and-braces).
        File.WriteAllText(batPath, "@ping -n 30 127.0.0.1 > nul\r\n")
        let tool =
            { Key = "tools/slow.bat"; Name = "slow.bat"; Extension = ".bat"
              FullPath = batPath; Dir = Path.GetDirectoryName batPath }
        let mutable handle : ToolRunHandle option = None
        use started = new System.Threading.ManualResetEventSlim(false)
        let runner =
            System.Threading.Tasks.Task.Run(fun () ->
                Toolbox.runToolWith (fun h -> handle <- Some h; started.Set()) stateDir tool)
        Assert.True(started.Wait(TimeSpan.FromSeconds 10.0), "onStarted never fired")
        handle.Value.Stop ()
        Assert.True(runner.Wait(TimeSpan.FromSeconds 15.0), "run did not end after Stop")
        match runner.Result with
        | Error e -> Assert.Fail(e)
        | Ok record ->
            Assert.NotEqual(0, record.ExitCode)
            Assert.True(record.DurationSec < 25.0)
            // a stopped run still lands in history
            Assert.Equal("tools/slow.bat", (Toolbox.loadHistory stateDir 10 |> List.head).ToolKey)
        // Stop after exit is a harmless no-op
        handle.Value.Stop ())

[<Fact>]
let ``runTool errors when the script no longer exists`` () =
    withTempProject (fun root stateDir ->
        let tool =
            { Key = "tools/gone.bat"; Name = "gone.bat"; Extension = ".bat"
              FullPath = Path.Combine(root, "tools", "gone.bat")
              Dir = Path.Combine(root, "tools") }
        match Toolbox.runTool stateDir tool with
        | Error _ -> ()
        | Ok _ -> Assert.Fail("expected Error for a missing script"))
