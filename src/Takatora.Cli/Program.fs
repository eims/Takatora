module Takatora.Cli.Program

open System
open System.CommandLine
open Takatora.Core

[<EntryPoint>]
let main argv =
    let root = RootCommand($"{Version.Product} — local CI for game builds")

    // ─── version ──────────────────────────────────────────────────
    let versionCmd = Command("version", "Print Takatora version and exit")
    versionCmd.SetAction(fun _ -> printfn $"{Version.Product} {Version.Version}")
    root.Subcommands.Add(versionCmd)

    // ─── validate ─────────────────────────────────────────────────
    let validatePathArg = Argument<string>("path")
    validatePathArg.Description <- "Project working directory containing .ci/ (default: current dir)"
    validatePathArg.DefaultValueFactory <- (fun _ -> ".")

    let validateCmd =
        Command("validate", "Check that .ci/project.toml and .ci/flows.toml parse cleanly")
    validateCmd.Arguments.Add(validatePathArg)
    validateCmd.SetAction(fun (pr: ParseResult) ->
        let path = pr.GetValue(validatePathArg)
        let outcome = Validate.run path
        let stdout, stderr, exitCode = Validate.format outcome
        if not (String.IsNullOrEmpty stdout) then Console.Out.Write(stdout)
        if not (String.IsNullOrEmpty stderr) then Console.Error.Write(stderr)
        exitCode)
    root.Subcommands.Add(validateCmd)

    // ─── run ──────────────────────────────────────────────────────
    let runPathArg = Argument<string>("path")
    runPathArg.Description <- "Project working directory containing .ci/"
    runPathArg.DefaultValueFactory <- (fun _ -> ".")

    let runFlowArg = Argument<string>("flow")
    runFlowArg.Description <- "Flow id from flows.toml"

    let runVarOpt = Option<string array>("--var")
    runVarOpt.Description <- "Override a flow variable (KEY=VALUE). Repeatable."
    runVarOpt.AllowMultipleArgumentsPerToken <- true

    let runDryRunOpt = Option<bool>("--dry-run")
    runDryRunOpt.Description <- "Resolve vars + steps and print the plan without spawning anything"

    let runCmd = Command("run", "Execute a flow against a project's .ci/ configuration")
    runCmd.Arguments.Add(runPathArg)
    runCmd.Arguments.Add(runFlowArg)
    runCmd.Options.Add(runVarOpt)
    runCmd.Options.Add(runDryRunOpt)
    runCmd.SetAction(fun (pr: ParseResult) ->
        let path = pr.GetValue(runPathArg)
        let flow = pr.GetValue(runFlowArg)
        let vars =
            match pr.GetValue(runVarOpt) with
            | null -> Seq.empty
            | xs -> xs :> seq<string>
        let dryRun = pr.GetValue(runDryRunOpt)
        Run.invoke path flow vars dryRun)
    root.Subcommands.Add(runCmd)

    // ─── cancel ───────────────────────────────────────────────────
    let cancelPathArg = Argument<string>("path")
    cancelPathArg.Description <- "Project working directory containing .ci/"
    cancelPathArg.DefaultValueFactory <- (fun _ -> ".")

    let cancelRunIdArg = Argument<string>("run-id")
    cancelRunIdArg.Description <- "Run id from `runs/<id>/`"

    let cancelCmd =
        Command("cancel", "Request cancellation of a running flow (writes CANCEL flag)")
    cancelCmd.Arguments.Add(cancelPathArg)
    cancelCmd.Arguments.Add(cancelRunIdArg)
    cancelCmd.SetAction(fun (pr: ParseResult) ->
        let path  = pr.GetValue(cancelPathArg)
        let runId = pr.GetValue(cancelRunIdArg)
        Cancel.invoke path runId)
    root.Subcommands.Add(cancelCmd)

    // ─── detect-engines ───────────────────────────────────────────
    let formatOpt = Option<string>("--format")
    formatOpt.Description <- "Output format: human (default) | json"
    formatOpt.DefaultValueFactory <- (fun _ -> "human")

    let detectCmd =
        Command("detect-engines",
                "Scan for installed UE / Unity / Godot engines and report what was found")
    detectCmd.Options.Add(formatOpt)
    detectCmd.SetAction(fun (pr: ParseResult) ->
        match DetectEngines.parseFormat (pr.GetValue(formatOpt)) with
        | Error msg ->
            Console.Error.WriteLine($"detect-engines: {msg}")
            2
        | Ok fmt -> DetectEngines.invoke fmt)
    root.Subcommands.Add(detectCmd)

    // TODO: describe / project / history / show-run / replay-run

    root.Parse(argv).Invoke()
