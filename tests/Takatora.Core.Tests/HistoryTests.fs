module Takatora.Core.Tests.HistoryTests

open System
open System.IO
open Xunit
open Takatora.Core

// Hand-roll a manifest.toml in a temp project tree so we don't have
// to actually execute flows to populate run dirs. That coupling
// belongs in RunTests; History tests stay narrowly scoped on parse +
// list semantics.

let private withProjectTree (action: string -> unit) =
    let dir =
        Path.Combine(
            Path.GetTempPath(),
            "takatora-history-tests",
            Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    try action dir
    finally
        try Directory.Delete(dir, recursive = true) with _ -> ()

let private writeManifest (runDir: string) (runId: string) (flowId: string) (startedAt: string) (result: string) =
    Directory.CreateDirectory(runDir) |> ignore
    let stepStatus = if result = "success" then "success" else "failure"
    // String interpolation rather than a multi-line sprintf — F#'s
    // format-string parser miscounts %s placeholders inside `"""..."""`
    // when the count is six or more, even when they're all valid.
    let manifest =
        $"""flow_id = "{flowId}"
run_id = "{runId}"
started_at = "{startedAt}"
finished_at = "{startedAt}"
trigger = "cli"
result = "{result}"
duration_sec = 1.5
project_name = "fixture"

[params]
message = "test"

[[step_summary]]
id = "step1"
type = "notify.console"
status = "{stepStatus}"
duration_sec = 1.5
"""
    File.WriteAllText(Path.Combine(runDir, "manifest.toml"), manifest)

[<Fact>]
let ``load on project with no runs dir yields empty list`` () =
    withProjectTree (fun root ->
        Assert.Equal<RunHistoryEntry list>([], RunHistory.load root))

[<Fact>]
let ``load parses multiple runs and sorts newest first`` () =
    withProjectTree (fun root ->
        let runs = Path.Combine(root, ".takatora", "runs")
        writeManifest (Path.Combine(runs, "r-2026051010-0100-aaaa")) "r-2026051010-0100-aaaa"
                      "smoke" "2026-05-10T10:01:00+00:00" "success"
        writeManifest (Path.Combine(runs, "r-2026051110-0200-bbbb")) "r-2026051110-0200-bbbb"
                      "smoke" "2026-05-11T10:02:00+00:00" "success"
        writeManifest (Path.Combine(runs, "r-2026051010-0300-cccc")) "r-2026051010-0300-cccc"
                      "smoke" "2026-05-10T10:03:00+00:00" "failure"
        let entries = RunHistory.load root
        Assert.Equal(3, List.length entries)
        // Newest first: 2026-05-11 > 2026-05-10 10:03 > 2026-05-10 10:01
        Assert.Equal("r-2026051110-0200-bbbb", entries.[0].RunId)
        Assert.Equal("r-2026051010-0300-cccc", entries.[1].RunId)
        Assert.Equal("r-2026051010-0100-aaaa", entries.[2].RunId))

[<Fact>]
let ``load skips broken manifests instead of failing the list`` () =
    withProjectTree (fun root ->
        let runs = Path.Combine(root, ".takatora", "runs")
        writeManifest (Path.Combine(runs, "r-2026051010-0100-aaaa")) "r-2026051010-0100-aaaa"
                      "smoke" "2026-05-10T10:01:00+00:00" "success"
        // Drop a malformed manifest in a sibling dir.
        let brokenDir = Path.Combine(runs, "r-broken")
        Directory.CreateDirectory(brokenDir) |> ignore
        File.WriteAllText(Path.Combine(brokenDir, "manifest.toml"), "not valid toml = = =")
        let entries = RunHistory.load root
        Assert.Equal(1, List.length entries)
        Assert.Equal("r-2026051010-0100-aaaa", entries.[0].RunId))

[<Fact>]
let ``findRun returns entry and step summaries for matching id`` () =
    withProjectTree (fun root ->
        let runs = Path.Combine(root, ".takatora", "runs")
        writeManifest (Path.Combine(runs, "r-only")) "r-only" "smoke" "2026-05-10T10:00:00+00:00" "success"
        match RunHistory.findRun root "r-only" with
        | Some (entry, steps) ->
            Assert.Equal("r-only", entry.RunId)
            Assert.Equal("smoke", entry.FlowId)
            Assert.Equal("success", entry.Result)
            Assert.Equal(Some (TString "test"), Map.tryFind "message" entry.Params)
            Assert.Equal(1, List.length steps)
            Assert.Equal("step1", steps.[0].Id)
        | None -> Assert.Fail("expected findRun to return Some"))

[<Fact>]
let ``findRun returns None for unknown id`` () =
    withProjectTree (fun root ->
        Assert.Equal(None, RunHistory.findRun root "r-nope"))

// ─── schema_version tolerance ──────────────────────────────────────
// The reader must treat a manifest with NO schema_version (every run
// written before the field existed) as v1 — losing those from history
// would be a worse regression than any format change.

[<Fact>]
let ``manifest without schema_version is read as v1`` () =
    withProjectTree (fun root ->
        let runs = Path.Combine(root, ".takatora", "runs")
        // writeManifest deliberately emits NO schema_version line.
        writeManifest (Path.Combine(runs, "r-legacy")) "r-legacy" "smoke"
                      "2026-05-10T10:00:00+00:00" "success"
        match RunHistory.findRun root "r-legacy" with
        | Some (entry, _) -> Assert.Equal(1, entry.SchemaVersion)
        | None -> Assert.Fail("expected findRun to return Some"))

[<Fact>]
let ``manifest with an explicit schema_version is preserved`` () =
    withProjectTree (fun root ->
        let runDir = Path.Combine(root, ".takatora", "runs", "r-v2")
        Directory.CreateDirectory(runDir) |> ignore
        let manifest =
            "schema_version = 2\n" +
            "flow_id = \"smoke\"\n" +
            "run_id = \"r-v2\"\n" +
            "started_at = \"2026-05-10T10:00:00+00:00\"\n" +
            "result = \"success\"\n"
        File.WriteAllText(Path.Combine(runDir, "manifest.toml"), manifest)
        match RunHistory.findRun root "r-v2" with
        | Some (entry, _) -> Assert.Equal(2, entry.SchemaVersion)
        | None -> Assert.Fail("expected findRun to return Some"))

// ─── runOutputs ────────────────────────────────────────────────────

[<Fact>]
let ``runOutputs reads step outputs from the run dir`` () =
    withProjectTree (fun dir ->
        let runId = "r-out"
        let outDir = Path.Combine(dir, ".takatora", "runs", runId, "outputs")
        Directory.CreateDirectory(outDir) |> ignore
        File.WriteAllText(
            Path.Combine(outDir, "ue.build_cook_run-1.ndjson"),
            "{\"name\":\"archive_path\",\"value\":\"C:/Pkg/Win\"}\n{\"name\":\"count\",\"value\":3}\n")
        let outs = RunHistory.runOutputs dir runId
        let step = Map.find "ue.build_cook_run-1" outs
        Assert.Equal("C:/Pkg/Win", Map.find "archive_path" step)
        Assert.Equal("3", Map.find "count" step))

[<Fact>]
let ``runOutputs is empty when there are no output files`` () =
    withProjectTree (fun dir ->
        Assert.True(Map.isEmpty (RunHistory.runOutputs dir "nope")))
