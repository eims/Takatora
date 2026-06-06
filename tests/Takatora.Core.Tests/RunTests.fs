namespace Takatora.Core.Tests

open System
open System.IO
open Xunit
open Takatora.Core

/// End-to-end runner tests. Each test sets up a small project under a
/// temp dir, drops a task .fsx into `.ci/tasks/`, and calls
/// `Run.execute` in-process. The runner spawns real `dotnet fsi`, so
/// these tests are slower than the rest of the suite (≈1s per case)
/// but they exercise the actual contract end to end.
/// Shares the `env-sensitive` xUnit collection with TasksSdkTests so
/// describe-mode env var changes can't leak into our fsi subprocesses.
[<Xunit.Collection("env-sensitive")>]
type RunTests() =

    let dir =
        Path.Combine(
            Path.GetTempPath(),
            "takatora-run-tests",
            Guid.NewGuid().ToString("N"))
    do Directory.CreateDirectory(dir) |> ignore

    /// The runner needs an absolute path to Takatora.Tasks.dll for the
    /// wrapper `#r`. Pull it off the loaded test assembly to stay
    /// independent of build configuration.
    let sdkAssemblyPath =
        typeof<Takatora.Tasks.TaskFailure>.Assembly.Location

    let writeFile (relative: string) (content: string) =
        let full = Path.Combine(dir, relative)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, content)

    let buildOptions (flowId: string) (overrides: Map<string, TomlValue>) : Run.Options = {
        WorkingDir = dir
        FlowId = flowId
        VarOverrides = overrides
        SdkAssemblyPath = sdkAssemblyPath
        BuiltinTasksDir = Path.Combine(dir, "_no-builtins")  // intentionally absent
        UserTasksDir = None
    }

    let projectToml = """
[project]
name = "rt-fixture"
working_dir = "."

[engine]
type = "godot"
"""

    interface IDisposable with
        member _.Dispose() =
            try Directory.Delete(dir, recursive = true) with _ -> ()

    // ─── happy path ────────────────────────────────────────────────

    [<Fact>]
    member _.``execute runs a single task and writes manifest + events`` () =
        writeFile ".ci/project.toml" projectToml
        writeFile ".ci/flows.toml" """
[[flow]]
id = "smoke"
[flow.vars]
message = { type = "string", default = "hello" }

[[flow.steps]]
id = "notify"
type = "test-notify"
"""
        writeFile ".ci/tasks/test-notify.fsx" """
open Takatora.Tasks
let msg = Param.required<string> "message"
Step.run "do-it" (fun () -> Log.info msg)
Output.set "echoed" msg
"""

        let result = Run.execute (buildOptions "smoke" Map.empty)
        let outcome =
            match result with
            | Ok o -> o
            | Error e -> Assert.Fail($"expected Ok, got %A{e}"); Unchecked.defaultof<_>

        Assert.Equal(RunResult.Success, outcome.Result)
        Assert.Equal(1, List.length outcome.Steps)
        let step = outcome.Steps.[0]
        Assert.Equal(StepStatus.Success, step.Status)
        Assert.Equal("notify", step.Id)
        Assert.Equal(Some (TString "hello"), Map.tryFind "echoed" step.Outputs)

        // manifest.toml exists and parses back through TomlConfig isn't
        // appropriate (different schema), but we can check it's non-empty.
        let manifestPath = Path.Combine(outcome.RunDir, "manifest.toml")
        Assert.True(File.Exists manifestPath)
        let manifestText = File.ReadAllText manifestPath
        Assert.Contains("result = \"success\"", manifestText)
        Assert.Contains("[[step_summary]]", manifestText)

        // events.ndjson contains both runner events and SDK substep events.
        let events = File.ReadAllLines (Path.Combine(outcome.RunDir, "events.ndjson"))
        Assert.Contains(events, fun l -> l.Contains("\"kind\":\"run.start\""))
        Assert.Contains(events, fun l -> l.Contains("\"kind\":\"step.start\""))
        Assert.Contains(events, fun l -> l.Contains("\"kind\":\"substep.start\""))
        Assert.Contains(events, fun l -> l.Contains("\"kind\":\"log\""))
        Assert.Contains(events, fun l -> l.Contains("\"kind\":\"step.end\""))
        Assert.Contains(events, fun l -> l.Contains("\"kind\":\"run.end\""))

    // ─── var override ─────────────────────────────────────────────

    [<Fact>]
    member _.``CLI --var override flows into the task`` () =
        writeFile ".ci/project.toml" projectToml
        writeFile ".ci/flows.toml" """
[[flow]]
id = "smoke"
[flow.vars]
message = { type = "string", default = "default-message" }

[[flow.steps]]
type = "echo"
"""
        writeFile ".ci/tasks/echo.fsx" """
open Takatora.Tasks
let msg = Param.required<string> "message"
Output.set "echoed" msg
"""

        let opts = buildOptions "smoke" (Map.ofList [ "message", TString "overridden" ])
        let outcome = Run.execute opts |> Result.toOption |> Option.get
        Assert.Equal(RunResult.Success, outcome.Result)
        Assert.Equal(Some (TString "overridden"), Map.tryFind "echoed" outcome.Steps.[0].Outputs)

    // ─── when skips ────────────────────────────────────────────────

    [<Fact>]
    member _.``when=false marks step as skipped without spawning fsi`` () =
        writeFile ".ci/project.toml" projectToml
        writeFile ".ci/flows.toml" """
[[flow]]
id = "smoke"
[flow.vars]
do_step = { type = "bool", default = false }

[[flow.steps]]
id = "guarded"
type = "should-not-run"
when = "${vars.do_step}"
"""
        // Intentionally no .fsx for `should-not-run`. If the runner tries
        // to spawn it, TaskResolver fails. With when=false it should be
        // short-circuited before resolution.

        let outcome = Run.execute (buildOptions "smoke" Map.empty) |> Result.toOption |> Option.get
        Assert.Equal(RunResult.Success, outcome.Result)
        match outcome.Steps.[0].Status with
        | StepStatus.Skipped reason ->
            Assert.Contains("when expression", reason)
        | other -> Assert.Fail($"expected Skipped, got %A{other}")

    // ─── failure short-circuits subsequent steps ──────────────────

    [<Fact>]
    member _.``failing step fails run and skips later steps`` () =
        writeFile ".ci/project.toml" projectToml
        writeFile ".ci/flows.toml" """
[[flow]]
id = "smoke"

[[flow.steps]]
id = "boom"
type = "explode"

[[flow.steps]]
id = "after"
type = "explode"
"""
        writeFile ".ci/tasks/explode.fsx" """
open Takatora.Tasks
Task.fail<unit> "deliberate failure"
"""

        let outcome = Run.execute (buildOptions "smoke" Map.empty) |> Result.toOption |> Option.get
        Assert.Equal(RunResult.Failure, outcome.Result)
        Assert.Equal(2, List.length outcome.Steps)
        match outcome.Steps.[0].Status with
        | StepStatus.Failure _ -> ()
        | other -> Assert.Fail($"expected first step Failure, got %A{other}")
        match outcome.Steps.[1].Status with
        | StepStatus.Skipped reason ->
            Assert.Contains("earlier step failed", reason)
        | other -> Assert.Fail($"expected second step Skipped, got %A{other}")

    // ─── dry run ──────────────────────────────────────────────────

    [<Fact>]
    member _.``plan returns runnable steps and reflects var overrides`` () =
        writeFile ".ci/project.toml" projectToml
        writeFile ".ci/flows.toml" """
[[flow]]
id = "preview"
[flow.vars]
configuration = { type = "string", default = "Development" }

[[flow.steps]]
id = "notify"
type = "test-notify"
"""
        writeFile ".ci/tasks/test-notify.fsx" "open Takatora.Tasks\nLog.info \"x\"\n"

        let opts = buildOptions "preview" (Map.ofList [ "configuration", TString "Shipping" ])
        match Run.plan opts with
        | Error e -> Assert.Fail($"expected Ok, got %A{e}")
        | Ok p ->
            Assert.Equal("preview", p.FlowId)
            Assert.Equal(Some (TString "Shipping"), Map.tryFind "configuration" p.Vars)
            Assert.True(Set.contains "configuration" p.OverriddenKeys)
            Assert.Equal(1, List.length p.Steps)
            let step = p.Steps.[0]
            Assert.Equal("notify", step.Id)
            Assert.Equal(None, step.SkipReason)
            Assert.True(Option.isSome step.TaskPath)

    [<Fact>]
    member _.``plan flags when=false steps with skip reason`` () =
        writeFile ".ci/project.toml" projectToml
        writeFile ".ci/flows.toml" """
[[flow]]
id = "guarded"
[flow.vars]
do_it = { type = "bool", default = false }

[[flow.steps]]
id = "skipped"
type = "doesnt-exist-but-doesnt-matter"
when = "${vars.do_it}"
"""
        let opts = buildOptions "guarded" Map.empty
        match Run.plan opts with
        | Error e -> Assert.Fail($"expected Ok, got %A{e}")
        | Ok p ->
            Assert.Equal(1, List.length p.Steps)
            Assert.True(Option.isSome p.Steps.[0].SkipReason)

    [<Fact>]
    member _.``plan flags missing task .fsx as skip reason`` () =
        writeFile ".ci/project.toml" projectToml
        writeFile ".ci/flows.toml" """
[[flow]]
id = "broken"
[[flow.steps]]
type = "no-such-task"
"""
        let opts = buildOptions "broken" Map.empty
        match Run.plan opts with
        | Error e -> Assert.Fail($"expected Ok, got %A{e}")
        | Ok p ->
            let reason = p.Steps.[0].SkipReason
            Assert.True(Option.isSome reason)
            Assert.Contains("no-such-task", Option.defaultValue "" reason)

    // ─── flow not found ───────────────────────────────────────────

    [<Fact>]
    member _.``unknown flow id returns FlowNotFound`` () =
        writeFile ".ci/project.toml" projectToml
        writeFile ".ci/flows.toml" """
[[flow]]
id = "smoke"
[[flow.steps]]
type = "noop"
"""
        match Run.execute (buildOptions "missing-flow" Map.empty) with
        | Error (RunFailure.FlowNotFound id) -> Assert.Equal("missing-flow", id)
        | other -> Assert.Fail($"expected FlowNotFound, got %A{other}")

    // ─── builtin: fs.clean ────────────────────────────────────────

    [<Fact>]
    member _.``builtin fs.clean removes a directory and reports counts`` () =
        writeFile ".ci/project.toml" projectToml
        // Set up a junk dir with two files of known size.
        let target = Path.Combine(dir, "junk")
        Directory.CreateDirectory(Path.Combine(target, "sub")) |> ignore
        File.WriteAllText(Path.Combine(target, "a.txt"), String.replicate 100 "x")
        File.WriteAllText(Path.Combine(target, "sub", "b.txt"), String.replicate 50 "y")

        writeFile ".ci/flows.toml" """
[[flow]]
id = "clean-it"
[flow.vars]
path = { type = "string", default = "junk" }

[[flow.steps]]
id = "wipe"
type = "fs.clean"
"""
        let opts : Run.Options = {
            WorkingDir = dir
            FlowId = "clean-it"
            VarOverrides = Map.empty
            SdkAssemblyPath = sdkAssemblyPath
            BuiltinTasksDir = Path.Combine(AppContext.BaseDirectory, "builtin-tasks")
            UserTasksDir = None
        }
        let outcome =
            match Run.execute opts with
            | Ok o -> o
            | Error e -> Assert.Fail($"expected Ok, got %A{e}"); Unchecked.defaultof<_>
        Assert.Equal(RunResult.Success, outcome.Result)
        Assert.False(Directory.Exists target, "junk dir should be gone")
        let outputs = outcome.Steps.[0].Outputs
        Assert.Equal(Some (TInt 150L), Map.tryFind "bytes_freed"   outputs)
        Assert.Equal(Some (TInt 2L),   Map.tryFind "files_deleted" outputs)

    // ─── builtin: ue.clean ────────────────────────────────────────

    [<Fact>]
    member _.``builtin ue.clean preset safe removes intermediate + binaries`` () =
        writeFile ".ci/project.toml" projectToml
        // Set up the canonical UE artifact dirs the `safe` preset
        // touches, plus an unrelated dir we expect to survive.
        Directory.CreateDirectory(Path.Combine(dir, "Intermediate", "Build")) |> ignore
        File.WriteAllText(Path.Combine(dir, "Intermediate", "x.obj"), "stuff")
        Directory.CreateDirectory(Path.Combine(dir, "Binaries", "Win64")) |> ignore
        File.WriteAllText(Path.Combine(dir, "Binaries", "Win64", "Game.exe"), "binary")
        Directory.CreateDirectory(Path.Combine(dir, "Saved")) |> ignore
        File.WriteAllText(Path.Combine(dir, "Saved", "log.txt"), "log")

        writeFile ".ci/flows.toml" """
[[flow]]
id = "clean"

[[flow.steps]]
type = "ue.clean"
preset = "safe"
"""
        let opts : Run.Options = {
            WorkingDir = dir
            FlowId = "clean"
            VarOverrides = Map.empty
            SdkAssemblyPath = sdkAssemblyPath
            BuiltinTasksDir = Path.Combine(AppContext.BaseDirectory, "builtin-tasks")
            UserTasksDir = None
        }
        let outcome =
            match Run.execute opts with
            | Ok o -> o
            | Error e -> Assert.Fail($"expected Ok, got %A{e}"); Unchecked.defaultof<_>
        Assert.Equal(RunResult.Success, outcome.Result)
        Assert.False(Directory.Exists (Path.Combine(dir, "Intermediate")), "Intermediate should be gone")
        Assert.False(Directory.Exists (Path.Combine(dir, "Binaries")),     "Binaries should be gone")
        // `safe` preset doesn't touch Saved.
        Assert.True(Directory.Exists (Path.Combine(dir, "Saved")),         "Saved should survive `safe`")

    // ─── builtin: artifact.collect ────────────────────────────────

    [<Fact>]
    member _.``builtin artifact.collect copies sources into a named drop with a manifest`` () =
        writeFile ".ci/project.toml" projectToml
        // A build output dir to collect.
        Directory.CreateDirectory(Path.Combine(dir, "build", "sub")) |> ignore
        File.WriteAllText(Path.Combine(dir, "build", "Game.exe"), "exe")
        File.WriteAllText(Path.Combine(dir, "build", "sub", "data.bin"), "data")

        // stamp=none keeps the drop name deterministic (= project name).
        writeFile ".ci/flows.toml" """
[[flow]]
id = "collect"

[[flow.steps]]
id = "collect"
type = "artifact.collect"
sources = ["build"]
dest = "Artifacts"
stamp = "none"
archive = false
"""
        let opts : Run.Options = {
            WorkingDir = dir
            FlowId = "collect"
            VarOverrides = Map.empty
            SdkAssemblyPath = sdkAssemblyPath
            BuiltinTasksDir = Path.Combine(AppContext.BaseDirectory, "builtin-tasks")
            UserTasksDir = None
        }
        let outcome =
            match Run.execute opts with
            | Ok o -> o
            | Error e -> Assert.Fail($"expected Ok, got %A{e}"); Unchecked.defaultof<_>
        Assert.Equal(RunResult.Success, outcome.Result)
        let drop = Path.Combine(dir, "Artifacts", "rt-fixture")
        Assert.True(File.Exists(Path.Combine(drop, "build", "Game.exe")),     "collected file should be present")
        Assert.True(File.Exists(Path.Combine(drop, "build", "sub", "data.bin")), "nested file should be present")
        Assert.True(File.Exists(Path.Combine(drop, "manifest.json")),         "manifest should be written")
        let outputs = outcome.Steps.[0].Outputs
        Assert.Equal(Some (TString ""),   Map.tryFind "stamp" outputs)
        Assert.Equal(Some (TString drop), Map.tryFind "artifact_path" outputs)

    [<Fact>]
    member _.``builtin artifact.collect zips the drop when archive is set`` () =
        writeFile ".ci/project.toml" projectToml
        Directory.CreateDirectory(Path.Combine(dir, "build")) |> ignore
        File.WriteAllText(Path.Combine(dir, "build", "Game.exe"), "exe")

        writeFile ".ci/flows.toml" """
[[flow]]
id = "zip"

[[flow.steps]]
id = "collect"
type = "artifact.collect"
sources = ["build"]
dest = "Artifacts"
name = "drop"
stamp = "none"
archive = true
"""
        let opts : Run.Options = {
            WorkingDir = dir
            FlowId = "zip"
            VarOverrides = Map.empty
            SdkAssemblyPath = sdkAssemblyPath
            BuiltinTasksDir = Path.Combine(AppContext.BaseDirectory, "builtin-tasks")
            UserTasksDir = None
        }
        let outcome =
            match Run.execute opts with
            | Ok o -> o
            | Error e -> Assert.Fail($"expected Ok, got %A{e}"); Unchecked.defaultof<_>
        Assert.Equal(RunResult.Success, outcome.Result)
        let zip = Path.Combine(dir, "Artifacts", "drop.zip")
        Assert.True(File.Exists zip, "archive should be produced")
        Assert.False(Directory.Exists(Path.Combine(dir, "Artifacts", "drop")), "folder should be removed after archiving")
        let outputs = outcome.Steps.[0].Outputs
        Assert.Equal(Some (TString zip), Map.tryFind "artifact_path" outputs)

    // ─── builtin: fs.write ─────────────────────────────────────────

    [<Fact>]
    member _.``builtin fs.write writes content, interpolating a prior step output`` () =
        writeFile ".ci/project.toml" projectToml
        // First step records an output; fs.write interpolates it into content.
        writeFile ".ci/tasks/emit.fsx" """
open Takatora.Tasks
Step.run "emit" (fun () -> Output.set "tag" "v1")
"""
        writeFile ".ci/flows.toml" """
[[flow]]
id = "w"

[[flow.steps]]
id = "e"
type = "emit"

[[flow.steps]]
id = "write"
type = "fs.write"
path = "out/stamp.txt"
content = "tag=${steps.e.outputs.tag}"
"""
        let opts : Run.Options = {
            WorkingDir = dir
            FlowId = "w"
            VarOverrides = Map.empty
            SdkAssemblyPath = sdkAssemblyPath
            BuiltinTasksDir = Path.Combine(AppContext.BaseDirectory, "builtin-tasks")
            UserTasksDir = None
        }
        let outcome =
            match Run.execute opts with
            | Ok o -> o
            | Error e -> Assert.Fail($"expected Ok, got %A{e}"); Unchecked.defaultof<_>
        Assert.Equal(RunResult.Success, outcome.Result)
        let written = Path.Combine(dir, "out", "stamp.txt")
        Assert.True(File.Exists written, "fs.write should create the file")
        Assert.Equal("tag=v1", File.ReadAllText written)

    // ─── builtin: shell ───────────────────────────────────────────

    [<Fact>]
    member _.``builtin shell task echoes through to log.txt`` () =
        writeFile ".ci/project.toml" projectToml
        // Use a marker that survives both /bin/sh and cmd.exe quoting.
        let marker = "TAKATORA_SHELL_OK"
        writeFile ".ci/flows.toml" (sprintf """
[[flow]]
id = "shell-smoke"
[flow.vars]
command = { type = "string", default = "echo %s" }

[[flow.steps]]
id = "say"
type = "shell"
""" marker)
        // Point at the real builtin dir copied alongside the test bin
        // by the Tasks.Builtin ProjectReference chain.
        let builtinDir =
            Path.Combine(AppContext.BaseDirectory, "builtin-tasks")
        let opts : Run.Options = {
            WorkingDir = dir
            FlowId = "shell-smoke"
            VarOverrides = Map.empty
            SdkAssemblyPath = sdkAssemblyPath
            BuiltinTasksDir = builtinDir
            UserTasksDir = None
        }
        let outcome =
            match Run.execute opts with
            | Ok o -> o
            | Error e -> Assert.Fail($"expected Ok, got %A{e}"); Unchecked.defaultof<_>
        Assert.Equal(RunResult.Success, outcome.Result)
        let log = File.ReadAllText(Path.Combine(outcome.RunDir, "log.txt"))
        Assert.Contains(marker, log)

    // ─── secret redaction ─────────────────────────────────────────

    [<Fact>]
    member _.``secret var values are masked in manifest and input json`` () =
        writeFile ".ci/project.toml" projectToml
        writeFile ".ci/flows.toml" """
[[flow]]
id = "sec"
[flow.vars]
password = { type = "secret" }
[[flow.steps]]
id = "say"
type = "notify.console"
message = "leak ${vars.password}"
"""
        let builtinDir = Path.Combine(AppContext.BaseDirectory, "builtin-tasks")
        let opts =
            { buildOptions "sec" (Map.ofList [ "password", TString "topsecret123" ])
                with BuiltinTasksDir = builtinDir }
        let outcome =
            match Run.execute opts with
            | Ok o -> o
            | Error e -> Assert.Fail($"expected Ok, got %A{e}"); Unchecked.defaultof<_>
        Assert.Equal(RunResult.Success, outcome.Result)
        // Manifest masks the secret var, never records the plaintext.
        let manifest = File.ReadAllText(Path.Combine(outcome.RunDir, "manifest.toml"))
        Assert.Contains("password = \"***\"", manifest)
        Assert.DoesNotContain("topsecret123", manifest)
        // Resolved value scrubbed from the on-disk input json too.
        for f in Directory.GetFiles(Path.Combine(outcome.RunDir, "inputs")) do
            Assert.DoesNotContain("topsecret123", File.ReadAllText f)
        // The task read the scrubbed param, so even the log lacks it.
        let log = File.ReadAllText(Path.Combine(outcome.RunDir, "log.txt"))
        Assert.DoesNotContain("topsecret123", log)

    [<Fact>]
    member _.``secret real value reaches the task via TAKATORA_SECRET env var`` () =
        writeFile ".ci/project.toml" projectToml
        // Project-local task that echoes the secret env var into the log,
        // proving the real value is delivered out-of-band (not via disk).
        writeFile ".ci/tasks/echo.secret.fsx" """
open Takatora.Tasks
let v = System.Environment.GetEnvironmentVariable "TAKATORA_SECRET_password"
Step.run "echo" (fun () -> printfn "ENV=%s" v)
"""
        writeFile ".ci/flows.toml" """
[[flow]]
id = "sec2"
[flow.vars]
password = { type = "secret" }
[[flow.steps]]
id = "e"
type = "echo.secret"
"""
        let opts = buildOptions "sec2" (Map.ofList [ "password", TString "envsecret999" ])
        let outcome =
            match Run.execute opts with
            | Ok o -> o
            | Error e -> Assert.Fail($"expected Ok, got %A{e}"); Unchecked.defaultof<_>
        Assert.Equal(RunResult.Success, outcome.Result)
        let log = File.ReadAllText(Path.Combine(outcome.RunDir, "log.txt"))
        Assert.Contains("ENV=envsecret999", log)

    [<Fact>]
    member _.``manifest with a Windows-path param round-trips through findRun`` () =
        // Regression: backslashes in a param value must be escaped, or the
        // manifest is invalid TOML — the run vanishes from history and
        // RunDetail / show-run report "not found".
        writeFile ".ci/project.toml" projectToml
        writeFile ".ci/flows.toml" """
[[flow]]
id = "p"
[flow.vars]
out_dir = { type = "dir" }
[[flow.steps]]
id = "say"
type = "notify.console"
message = "done"
"""
        let builtinDir = Path.Combine(AppContext.BaseDirectory, "builtin-tasks")
        let opts =
            { buildOptions "p" (Map.ofList [ "out_dir", TString @"C:\Build\out" ])
                with BuiltinTasksDir = builtinDir }
        let outcome =
            match Run.execute opts with
            | Ok o -> o
            | Error e -> Assert.Fail($"expected Ok, got %A{e}"); Unchecked.defaultof<_>
        Assert.Equal(RunResult.Success, outcome.Result)
        // findRun re-parses the manifest — None means it was invalid TOML.
        match RunHistory.findRun dir outcome.RunId with
        | Some (e, _) -> Assert.Equal<TomlValue>(TString @"C:\Build\out", Map.find "out_dir" e.Params)
        | None        -> Assert.Fail("findRun returned None — manifest failed to parse")

    // ─── builtin task shipping ────────────────────────────────────

    [<Fact>]
    member _.``ue.build_nonunity builtin ships and resolves in a plan`` () =
        // Regression guard: the new non-unity-build task file is packaged
        // into builtin-tasks/ and the resolver finds it. (A real build needs
        // an engine, so we stop at plan — no UBT invocation.)
        writeFile ".ci/project.toml" """
[project]
name = "g"
working_dir = "."
[engine]
type = "unreal"
project_file = "g.uproject"
"""
        writeFile ".ci/flows.toml" """
[[flow]]
id = "ci"
[[flow.steps]]
type = "ue.build_nonunity"
"""
        let builtinDir = Path.Combine(AppContext.BaseDirectory, "builtin-tasks")
        let opts = { buildOptions "ci" Map.empty with BuiltinTasksDir = builtinDir }
        match Run.plan opts with
        | Ok plan ->
            let step = plan.Steps |> List.find (fun s -> s.Type = "ue.build_nonunity")
            Assert.True(step.TaskPath.IsSome, "ue.build_nonunity should resolve to a builtin .fsx")
        | Error e -> Assert.Fail(sprintf "expected a plan, got %A" e)

    // ─── engine mutex ─────────────────────────────────────────────

    [<Fact>]
    member _.``ue.* step waits on engine mutex and emits mutex events`` () =
        writeFile ".ci/project.toml" projectToml
        writeFile ".ci/flows.toml" """
[[flow]]
id = "ue-stub"

[[flow.steps]]
id = "stub"
type = "ue.stub"
"""
        writeFile ".ci/tasks/ue.stub.fsx" """
open Takatora.Tasks
Log.info "got past the mutex"
"""
        // Hold the engine mutex from the test thread so the runner is
        // forced to wait. WaitOne / ReleaseMutex must run on the same
        // thread, hence the explicit acquire here instead of `use`.
        let mutexName = @"Global\Takatora-ue-editor"
        use heldMutex = new System.Threading.Mutex(false, mutexName)
        Assert.True(heldMutex.WaitOne(1000), "test setup: could not acquire engine mutex")

        let runTask =
            System.Threading.Tasks.Task.Run(fun () ->
                Run.execute (buildOptions "ue-stub" Map.empty))

        // Give the runner enough time to enter the mutex wait loop and
        // emit mutex.wait, then hand the mutex over.
        System.Threading.Thread.Sleep(1200)
        heldMutex.ReleaseMutex()

        let outcome =
            match runTask.Result with
            | Ok o -> o
            | Error e -> Assert.Fail($"expected Ok, got %A{e}"); Unchecked.defaultof<_>
        Assert.Equal(RunResult.Success, outcome.Result)
        let events = File.ReadAllLines(Path.Combine(outcome.RunDir, "events.ndjson"))
        Assert.Contains(events, fun l -> l.Contains("\"kind\":\"mutex.wait\""))
        Assert.Contains(events, fun l -> l.Contains("\"kind\":\"mutex.acquired\""))
        Assert.Contains(events, fun l -> l.Contains("Global\\\\Takatora-ue-editor"))

    [<Fact>]
    member _.``non-engine step types skip the mutex (no mutex events emitted)`` () =
        writeFile ".ci/project.toml" projectToml
        writeFile ".ci/flows.toml" """
[[flow]]
id = "plain"
[[flow.steps]]
type = "plain-task"
"""
        writeFile ".ci/tasks/plain-task.fsx" """
open Takatora.Tasks
Log.info "no mutex required"
"""
        let outcome =
            match Run.execute (buildOptions "plain" Map.empty) with
            | Ok o -> o
            | Error e -> Assert.Fail($"expected Ok, got %A{e}"); Unchecked.defaultof<_>
        Assert.Equal(RunResult.Success, outcome.Result)
        let events = File.ReadAllLines(Path.Combine(outcome.RunDir, "events.ndjson"))
        Assert.DoesNotContain(events, fun l -> l.Contains("\"kind\":\"mutex.wait\""))
        Assert.DoesNotContain(events, fun l -> l.Contains("\"kind\":\"mutex.acquired\""))

    // ─── cancel ───────────────────────────────────────────────────

    [<Fact>]
    member _.``CANCEL flag mid-flight cancels current step + skips rest`` () =
        writeFile ".ci/project.toml" projectToml
        writeFile ".ci/flows.toml" """
[[flow]]
id = "long"

[[flow.steps]]
id = "sleeper"
type = "sleep"

[[flow.steps]]
id = "after"
type = "sleep"
"""
        writeFile ".ci/tasks/sleep.fsx" """
open Takatora.Tasks
Step.run "sleeping" (fun () ->
    System.Threading.Thread.Sleep(System.TimeSpan.FromSeconds(10.0)))
"""
        // Predict the run dir without racing: capture run id by watching
        // the runs/ directory for the new entry, then drop CANCEL inside.
        let runsRoot = Path.Combine(dir, ".ci", "runs")
        Directory.CreateDirectory(runsRoot) |> ignore

        let runTask =
            System.Threading.Tasks.Task.Run(fun () ->
                Run.execute (buildOptions "long" Map.empty))

        // Wait for the run dir to appear (runner creates it before
        // launching the first step), then drop CANCEL.
        let deadline = DateTimeOffset.UtcNow.AddSeconds(8.0)
        let mutable runDir : string option = None
        while runDir.IsNone && DateTimeOffset.UtcNow < deadline do
            match Directory.GetDirectories(runsRoot) |> Array.tryHead with
            | Some d -> runDir <- Some d
            | None -> System.Threading.Thread.Sleep(50)

        match runDir with
        | None -> Assert.Fail("runner never created a run dir")
        | Some d ->
            // Give the step a beat to actually start fsi before cancelling.
            System.Threading.Thread.Sleep(500)
            File.WriteAllText(Path.Combine(d, "CANCEL"), "")

        let outcome =
            match runTask.Result with
            | Ok o -> o
            | Error e -> Assert.Fail($"expected Ok, got %A{e}"); Unchecked.defaultof<_>

        Assert.Equal(RunResult.Cancelled, outcome.Result)
        Assert.Equal(2, List.length outcome.Steps)
        Assert.Equal(StepStatus.Cancelled, outcome.Steps.[0].Status)
        // Should be far short of 10s — Kill(entireProcessTree) is prompt.
        Assert.True(outcome.Steps.[0].DurationSec < 5.0,
                    $"expected cancel under 5s, got {outcome.Steps.[0].DurationSec}s")
        match outcome.Steps.[1].Status with
        | StepStatus.Skipped reason -> Assert.Equal("cancelled", reason)
        | other -> Assert.Fail($"expected later step Skipped(cancelled), got %A{other}")

        let manifest = File.ReadAllText(Path.Combine(outcome.RunDir, "manifest.toml"))
        Assert.Contains("result = \"cancelled\"", manifest)
        let events = File.ReadAllLines(Path.Combine(outcome.RunDir, "events.ndjson"))
        Assert.Contains(events, fun l -> l.Contains("\"kind\":\"step.cancel\""))
