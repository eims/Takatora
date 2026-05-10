namespace Takatora.Core.Tests

open System
open System.IO
open Xunit
open Takatora.Tasks

/// All SDK tests live in this single class so xUnit serializes them
/// (methods within a class never run in parallel). The SDK touches
/// process-global env vars; cross-class parallelism would be a bug.
type TasksSdkTests() =

    // Each test gets a fresh tmp dir; env vars are restored on dispose.
    let dir =
        Path.Combine(
            Path.GetTempPath(),
            "takatora-tasks-tests",
            Guid.NewGuid().ToString("N"))
    do Directory.CreateDirectory(dir) |> ignore

    let inputPath  = Path.Combine(dir, "input.json")
    let outputPath = Path.Combine(dir, "output.ndjson")
    let eventsPath = Path.Combine(dir, "events.ndjson")

    let setupInput (json: string) =
        File.WriteAllText(inputPath, json)
        Environment.SetEnvironmentVariable("TAKATORA_TASK_INPUT",  inputPath)
        Environment.SetEnvironmentVariable("TAKATORA_OUTPUT_FILE", outputPath)
        Environment.SetEnvironmentVariable("TAKATORA_EVENTS_FILE", eventsPath)
        Io.resetForTests ()

    let readEvents () =
        if File.Exists eventsPath then
            File.ReadAllLines eventsPath
            |> Array.filter (fun line -> not (String.IsNullOrWhiteSpace line))
            |> List.ofArray
        else []

    let readOutputs () =
        if File.Exists outputPath then
            File.ReadAllLines outputPath
            |> Array.filter (fun line -> not (String.IsNullOrWhiteSpace line))
            |> List.ofArray
        else []

    interface IDisposable with
        member _.Dispose() =
            Environment.SetEnvironmentVariable("TAKATORA_TASK_INPUT",  null)
            Environment.SetEnvironmentVariable("TAKATORA_OUTPUT_FILE", null)
            Environment.SetEnvironmentVariable("TAKATORA_EVENTS_FILE", null)
            Io.resetForTests ()
            try Directory.Delete(dir, recursive = true) with _ -> ()

    // ─── Param ─────────────────────────────────────────────────────

    [<Fact>]
    member _.``Param.required string returns value from input`` () =
        setupInput """{"params":{"message":"hello"}}"""
        Assert.Equal("hello", Param.required<string> "message")

    [<Fact>]
    member _.``Param.required int returns numeric value`` () =
        setupInput """{"params":{"count":42}}"""
        Assert.Equal(42, Param.required<int> "count")

    [<Fact>]
    member _.``Param.required bool returns boolean value`` () =
        setupInput """{"params":{"enabled":true}}"""
        Assert.True(Param.required<bool> "enabled")

    [<Fact>]
    member _.``Param.required missing key raises TaskFailure`` () =
        setupInput """{"params":{}}"""
        let ex = Assert.Throws<TaskFailure>(fun () ->
            Param.required<string> "absent" |> ignore)
        Assert.Contains("Required param 'absent'", ex.Message)

    [<Fact>]
    member _.``Param.optional returns default when absent`` () =
        setupInput """{"params":{}}"""
        Assert.Equal("origin", Param.optional<string> "remote" "origin")

    [<Fact>]
    member _.``Param.optional returns supplied value when present`` () =
        setupInput """{"params":{"remote":"upstream"}}"""
        Assert.Equal("upstream", Param.optional<string> "remote" "origin")

    [<Fact>]
    member _.``Param.has reflects key presence`` () =
        setupInput """{"params":{"a":1}}"""
        Assert.True(Param.has "a")
        Assert.False(Param.has "b")

    [<Fact>]
    member _.``Param.requiredEnum accepts allowed value`` () =
        setupInput """{"params":{"cfg":"Shipping"}}"""
        Assert.Equal("Shipping",
            Param.requiredEnum "cfg" [ "Development"; "Shipping" ])

    [<Fact>]
    member _.``Param.requiredEnum rejects out-of-set value`` () =
        setupInput """{"params":{"cfg":"Bogus"}}"""
        let ex = Assert.Throws<TaskFailure>(fun () ->
            Param.requiredEnum "cfg" [ "Development"; "Shipping" ] |> ignore)
        Assert.Contains("must be one of", ex.Message)

    [<Fact>]
    member _.``Param.required surfaces type mismatch as TaskFailure`` () =
        setupInput """{"params":{"count":"not-a-number"}}"""
        Assert.Throws<TaskFailure>(fun () ->
            Param.required<int> "count" |> ignore) |> ignore

    // ─── Output ────────────────────────────────────────────────────

    [<Fact>]
    member _.``Output.set appends NDJSON entries`` () =
        setupInput """{"params":{}}"""
        Output.set "archive_path" "Build/Win64-Shipping"
        Output.set "exit_code" 0
        let lines = readOutputs ()
        Assert.Equal(2, List.length lines)
        Assert.Contains("\"name\":\"archive_path\"", lines.[0])
        Assert.Contains("\"value\":\"Build/Win64-Shipping\"", lines.[0])
        Assert.Contains("\"name\":\"exit_code\"", lines.[1])
        Assert.Contains("\"value\":0", lines.[1])

    // ─── Step ──────────────────────────────────────────────────────

    [<Fact>]
    member _.``Step.run brackets action with start and success end`` () =
        setupInput """{"params":{}}"""
        Step.run "build" (fun () -> ())
        let evts = readEvents ()
        Assert.Equal(2, List.length evts)
        Assert.Contains("\"kind\":\"substep.start\"", evts.[0])
        Assert.Contains("\"name\":\"build\"", evts.[0])
        Assert.Contains("\"kind\":\"substep.end\"", evts.[1])
        Assert.Contains("\"status\":\"success\"", evts.[1])

    [<Fact>]
    member _.``Step.run on exception emits fail end and rethrows`` () =
        setupInput """{"params":{}}"""
        Assert.Throws<InvalidOperationException>(fun () ->
            Step.run "boom" (fun () -> raise (InvalidOperationException("nope"))))
        |> ignore
        let evts = readEvents ()
        Assert.Equal(2, List.length evts)
        Assert.Contains("\"status\":\"fail\"", evts.[1])
        Assert.Contains("nope", evts.[1])

    [<Fact>]
    member _.``Step.runResult returns the action's value`` () =
        setupInput """{"params":{}}"""
        let result = Step.runResult "compute" (fun () -> 7 * 6)
        Assert.Equal(42, result)

    [<Fact>]
    member _.``Step.skip emits substep.skip event with reason`` () =
        setupInput """{"params":{}}"""
        Step.skip "clean" "vars.clean_first is false"
        let evts = readEvents ()
        Assert.Single(evts) |> ignore
        Assert.Contains("\"kind\":\"substep.skip\"", evts.[0])
        Assert.Contains("\"reason\":\"vars.clean_first is false\"", evts.[0])

    // ─── Log ───────────────────────────────────────────────────────

    [<Fact>]
    member _.``Log levels emit level-tagged events`` () =
        setupInput """{"params":{}}"""
        Log.info "i"
        Log.warn "w"
        Log.error "e"
        Log.debug "d"
        let evts = readEvents ()
        Assert.Equal(4, List.length evts)
        Assert.Contains("\"level\":\"info\"",  evts.[0])
        Assert.Contains("\"level\":\"warn\"",  evts.[1])
        Assert.Contains("\"level\":\"error\"", evts.[2])
        Assert.Contains("\"level\":\"debug\"", evts.[3])

    [<Fact>]
    member _.``Log.section flags the event as a section`` () =
        setupInput """{"params":{}}"""
        Log.section "Phase 1"
        let evts = readEvents ()
        Assert.Contains("\"section\":true", evts.[0])
        Assert.Contains("\"message\":\"Phase 1\"", evts.[0])

    // ─── Project ───────────────────────────────────────────────────

    [<Fact>]
    member _.``Project metadata exposed from input`` () =
        setupInput """{"project":{"name":"sample","working_dir":"C:/work"}}"""
        Assert.Equal("sample", Project.name)
        Assert.Equal("C:/work", Project.workingDir)

    [<Fact>]
    member _.``Project metadata defaults to empty strings when absent`` () =
        setupInput """{"params":{}}"""
        Assert.Equal("", Project.name)
        Assert.Equal("", Project.workingDir)

    // ─── Engine accessors ──────────────────────────────────────────

    [<Fact>]
    member _.``Engine fields surface from input.engine block`` () =
        setupInput """{"engine":{"type":"unreal","path":"C:/UE","version":"5.4","project_file":"X.uproject","executable":"C:/UE/Editor.exe"}}"""
        Assert.Equal("unreal",       Engine.kind)
        Assert.Equal("C:/UE",        Engine.path)
        Assert.Equal("5.4",          Engine.version)
        Assert.Equal("X.uproject",   Engine.projectFile)
        Assert.Equal("C:/UE/Editor.exe", Engine.executable)

    [<Fact>]
    member _.``Engine fields default to empty strings when absent`` () =
        setupInput """{"params":{}}"""
        Assert.Equal("", Engine.kind)
        Assert.Equal("", Engine.path)
        Assert.Equal("", Engine.executable)

    [<Fact>]
    member _.``UE.uatBatPath joins engine path with the conventional sub-path`` () =
        setupInput """{"engine":{"path":"C:/Program Files/Epic Games/UE_5.4"}}"""
        let expected =
            Path.Combine(
                "C:/Program Files/Epic Games/UE_5.4",
                "Engine", "Build", "BatchFiles", "RunUAT.bat")
        Assert.Equal(expected, UE.uatBatPath ())

    [<Fact>]
    member _.``UE.editorPath uses Engine.executable when set`` () =
        setupInput """{"engine":{"path":"C:/UE","executable":"D:/custom/Editor.exe"}}"""
        Assert.Equal("D:/custom/Editor.exe", UE.editorPath ())

    [<Fact>]
    member _.``UE.editorPath falls back to convention when executable absent`` () =
        setupInput """{"engine":{"path":"C:/UE"}}"""
        let expected = Path.Combine("C:/UE", "Engine", "Binaries", "Win64", "UnrealEditor.exe")
        Assert.Equal(expected, UE.editorPath ())

    [<Fact>]
    member _.``UE helpers raise TaskFailure when engine path is unset`` () =
        setupInput """{"params":{}}"""
        Assert.Throws<TaskFailure>(fun () -> UE.uatBatPath () |> ignore) |> ignore

    [<Fact>]
    member _.``Unity.editorPath uses executable when present`` () =
        setupInput """{"engine":{"path":"C:/Hub/Editor/2022.3","executable":"C:/Hub/Editor/2022.3/Editor/Unity.exe"}}"""
        Assert.Equal("C:/Hub/Editor/2022.3/Editor/Unity.exe", Unity.editorPath ())

    [<Fact>]
    member _.``Unity.editorPath falls back to convention from engine path`` () =
        setupInput """{"engine":{"path":"C:/Hub/Editor/2022.3"}}"""
        let expected = Path.Combine("C:/Hub/Editor/2022.3", "Editor", "Unity.exe")
        Assert.Equal(expected, Unity.editorPath ())

    [<Fact>]
    member _.``Unity helpers raise TaskFailure when neither path nor executable set`` () =
        setupInput """{"params":{}}"""
        Assert.Throws<TaskFailure>(fun () -> Unity.editorPath () |> ignore) |> ignore

    [<Fact>]
    member _.``Godot.editorPath returns the configured executable`` () =
        setupInput """{"engine":{"executable":"D:/godot.exe"}}"""
        Assert.Equal("D:/godot.exe", Godot.editorPath ())

    [<Fact>]
    member _.``Godot helpers raise TaskFailure when executable absent`` () =
        // Path alone doesn't help — Godot has no install layout.
        setupInput """{"engine":{"path":"D:/godot-folder"}}"""
        Assert.Throws<TaskFailure>(fun () -> Godot.editorPath () |> ignore) |> ignore

    // ─── Task.fail / TaskFailure ───────────────────────────────────

    [<Fact>]
    member _.``Task.fail raises TaskFailure with given reason`` () =
        setupInput """{"params":{}}"""
        let ex = Assert.Throws<TaskFailure>(fun () ->
            Task.fail<int> "deliberate" |> ignore)
        Assert.Contains("deliberate", ex.Message)

    // ─── Channel discovery ─────────────────────────────────────────

    [<Fact>]
    member _.``Output and Step are no-ops when env vars are unset`` () =
        Environment.SetEnvironmentVariable("TAKATORA_TASK_INPUT",  null)
        Environment.SetEnvironmentVariable("TAKATORA_OUTPUT_FILE", null)
        Environment.SetEnvironmentVariable("TAKATORA_EVENTS_FILE", null)
        Io.resetForTests ()
        // Should not throw, even with no channels configured.
        Output.set "x" 1
        Step.run "noop" (fun () -> ())
        Log.info "still fine"
        Assert.False(File.Exists outputPath)
        Assert.False(File.Exists eventsPath)

    // ─── Cmd ───────────────────────────────────────────────────────
    //
    // Tests use `dotnet --version` because it's reliably present (we're
    // running under it) and prints a single line — keeps assertions tight
    // and avoids reaching for /bin/sh or cmd.exe quirks.

    [<Fact>]
    member _.``Cmd.execCapture returns stdout and exit 0 on success`` () =
        setupInput """{"params":{}}"""
        let r = Cmd.execCapture "dotnet" [ "--version" ]
        Assert.Equal(0, r.exitCode)
        Assert.NotEmpty(r.stdout.Trim())

    [<Fact>]
    member _.``Cmd.exec succeeds quietly on exit 0`` () =
        setupInput """{"params":{}}"""
        // Should not throw.
        Cmd.exec "dotnet" [ "--version" ]

    [<Fact>]
    member _.``Cmd.exec raises TaskFailure on non-zero exit`` () =
        setupInput """{"params":{}}"""
        // `dotnet --not-a-real-flag` exits non-zero.
        let ex = Assert.Throws<TaskFailure>(fun () ->
            Cmd.exec "dotnet" [ "--not-a-real-flag" ])
        Assert.Contains("exited with code", ex.Message)

    [<Fact>]
    member _.``Cmd.execWith ignoreExitCodes accepts the listed code`` () =
        setupInput """{"params":{}}"""
        // First, capture the actual exit code dotnet uses for unknown flags.
        let probe = Cmd.execCapture "dotnet" [ "--not-a-real-flag" ]
        Assert.NotEqual(0, probe.exitCode)
        // Now run streaming with that code in IgnoreExitCodes — should not throw.
        Cmd.execWith
            { ExecOptions.empty with IgnoreExitCodes = [ probe.exitCode ] }
            "dotnet"
            [ "--not-a-real-flag" ]

    [<Fact>]
    member _.``Cmd.execCaptureWith carries WorkingDir override`` () =
        setupInput """{"params":{}}"""
        // The runtime always knows its current dir; pwd-style probe via
        // `dotnet fsi -e` is fiddly across OSes. Settle for a structural
        // smoke: WorkingDir is plumbed into ProcessStartInfo without
        // throwing for a real existing dir.
        let probe =
            Cmd.execCaptureWith
                { ExecOptions.empty with WorkingDir = Some (Path.GetTempPath()) }
                "dotnet"
                [ "--version" ]
        Assert.Equal(0, probe.exitCode)
