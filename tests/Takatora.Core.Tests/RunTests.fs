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
