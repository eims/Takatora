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

    // ─── init ─────────────────────────────────────────────────────
    let initPathArg = Argument<string>("path")
    initPathArg.Description <- "Directory to scaffold a .ci/ in (created if missing; default: current dir)"
    initPathArg.DefaultValueFactory <- (fun _ -> ".")

    let initNameOpt = Option<string>("--name")
    initNameOpt.Description <- "Project name (default: the directory name)"

    let initEngineOpt = Option<string>("--engine")
    initEngineOpt.Description <- "Engine: unreal (default) | unity | godot"
    initEngineOpt.DefaultValueFactory <- (fun _ -> "unreal")

    let initCmd =
        Command("init", "Scaffold a new project's .ci/ (project.toml + a starter flow) and register it")
    initCmd.Arguments.Add(initPathArg)
    initCmd.Options.Add(initNameOpt)
    initCmd.Options.Add(initEngineOpt)
    initCmd.SetAction(fun (pr: ParseResult) ->
        let path = pr.GetValue(initPathArg)
        let nameRaw = pr.GetValue(initNameOpt)
        let nameHint = if String.IsNullOrWhiteSpace nameRaw then None else Some nameRaw
        match Init.parseEngine (pr.GetValue(initEngineOpt)) with
        | Error msg ->
            Console.Error.WriteLine($"init: {msg}")
            2
        | Ok engine -> Init.invoke path nameHint engine)
    root.Subcommands.Add(initCmd)

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

    let runFormatOpt = Option<string>("--output-format")
    runFormatOpt.Description <- "Output format: human (default) | json"
    runFormatOpt.DefaultValueFactory <- (fun _ -> "human")

    let runCmd = Command("run", "Execute a flow against a project's .ci/ configuration")
    runCmd.Arguments.Add(runPathArg)
    runCmd.Arguments.Add(runFlowArg)
    runCmd.Options.Add(runVarOpt)
    runCmd.Options.Add(runDryRunOpt)
    runCmd.Options.Add(runFormatOpt)
    runCmd.SetAction(fun (pr: ParseResult) ->
        let path = pr.GetValue(runPathArg)
        let flow = pr.GetValue(runFlowArg)
        let vars =
            match pr.GetValue(runVarOpt) with
            | null -> Seq.empty
            | xs -> xs :> seq<string>
        let dryRun = pr.GetValue(runDryRunOpt)
        match Run.parseFormat (pr.GetValue(runFormatOpt)) with
        | Error msg ->
            Console.Error.WriteLine($"run: {msg}")
            2
        | Ok fmt -> Run.invoke path flow vars dryRun fmt)
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

    // ─── project add/remove/list/info ─────────────────────────────
    let projectCmd =
        Command("project", "Manage the local project registry (%APPDATA%\\Takatora\\projects.toml)")

    // project add <path> [--name X]
    let projAddPath = Argument<string>("path")
    projAddPath.Description <- "Path to the project working directory containing .ci/"
    let projAddName = Option<string>("--name")
    projAddName.Description <- "Override the registered name (default: project.toml's [project] name)"
    let projAddCmd = Command("add", "Register a project for `takatora run` lookup")
    projAddCmd.Arguments.Add(projAddPath)
    projAddCmd.Options.Add(projAddName)
    projAddCmd.SetAction(fun (pr: ParseResult) ->
        let path = pr.GetValue(projAddPath)
        let nameRaw = pr.GetValue(projAddName)
        let nameHint =
            if String.IsNullOrWhiteSpace nameRaw then None else Some nameRaw
        Project.invokeAdd path nameHint)
    projectCmd.Subcommands.Add(projAddCmd)

    // project remove <name>
    let projRemoveName = Argument<string>("name")
    projRemoveName.Description <- "Registered project name (see `takatora project list`)"
    let projRemoveCmd =
        Command("remove", "Unregister a project (does NOT delete its .ci/ directory)")
    projRemoveCmd.Arguments.Add(projRemoveName)
    projRemoveCmd.SetAction(fun (pr: ParseResult) ->
        Project.invokeRemove (pr.GetValue(projRemoveName)))
    projectCmd.Subcommands.Add(projRemoveCmd)

    // project list
    let projListFormat = Option<string>("--output-format")
    projListFormat.Description <- "Output format: human (default) | json"
    projListFormat.DefaultValueFactory <- (fun _ -> "human")
    let projListCmd = Command("list", "Show registered projects")
    projListCmd.Options.Add(projListFormat)
    projListCmd.SetAction(fun (pr: ParseResult) ->
        match Project.parseFormat (pr.GetValue(projListFormat)) with
        | Error msg ->
            Console.Error.WriteLine(sprintf "project list: %s" msg)
            2
        | Ok fmt -> Project.invokeList fmt)
    projectCmd.Subcommands.Add(projListCmd)

    // project info <name>
    let projInfoName = Argument<string>("name")
    projInfoName.Description <- "Registered project name"
    let projInfoFormat = Option<string>("--output-format")
    projInfoFormat.Description <- "Output format: human (default) | json"
    projInfoFormat.DefaultValueFactory <- (fun _ -> "human")
    let projInfoCmd =
        Command("info", "Show registration details + project.toml/flows.toml summary")
    projInfoCmd.Arguments.Add(projInfoName)
    projInfoCmd.Options.Add(projInfoFormat)
    projInfoCmd.SetAction(fun (pr: ParseResult) ->
        match Project.parseFormat (pr.GetValue(projInfoFormat)) with
        | Error msg ->
            Console.Error.WriteLine(sprintf "project info: %s" msg)
            2
        | Ok fmt -> Project.invokeInfo (pr.GetValue(projInfoName)) fmt)
    projectCmd.Subcommands.Add(projInfoCmd)

    root.Subcommands.Add(projectCmd)

    // ─── history ──────────────────────────────────────────────────
    let historyProjectArg = Argument<string>("project")
    historyProjectArg.Description <- "Registered project name OR path to a working dir with .ci/"
    let historyFlowOpt = Option<string>("--flow")
    historyFlowOpt.Description <- "Limit to runs of this flow id"
    let historyLimitOpt = Option<int>("--limit")
    historyLimitOpt.Description <- "Maximum number of entries to show (default 50)"
    historyLimitOpt.DefaultValueFactory <- (fun _ -> 50)
    let historyFormatOpt = Option<string>("--output-format")
    historyFormatOpt.Description <- "Output format: human (default) | json"
    historyFormatOpt.DefaultValueFactory <- (fun _ -> "human")

    let historyCmd = Command("history", "List past runs for a project")
    historyCmd.Arguments.Add(historyProjectArg)
    historyCmd.Options.Add(historyFlowOpt)
    historyCmd.Options.Add(historyLimitOpt)
    historyCmd.Options.Add(historyFormatOpt)
    historyCmd.SetAction(fun (pr: ParseResult) ->
        let project = pr.GetValue(historyProjectArg)
        let flowRaw = pr.GetValue(historyFlowOpt)
        let flow = if String.IsNullOrWhiteSpace flowRaw then None else Some flowRaw
        let limit = pr.GetValue(historyLimitOpt)
        match History.parseFormat (pr.GetValue(historyFormatOpt)) with
        | Error msg ->
            Console.Error.WriteLine(sprintf "history: %s" msg)
            2
        | Ok fmt -> History.invokeHistory project flow limit fmt)
    root.Subcommands.Add(historyCmd)

    // ─── show-run ─────────────────────────────────────────────────
    let showProjectArg = Argument<string>("project")
    showProjectArg.Description <- "Registered project name OR path"
    let showRunIdArg = Argument<string>("run-id")
    showRunIdArg.Description <- "Run id (see `takatora history`)"
    let showFormatOpt = Option<string>("--output-format")
    showFormatOpt.Description <- "Output format: human (default) | json"
    showFormatOpt.DefaultValueFactory <- (fun _ -> "human")

    let showRunCmd = Command("show-run", "Show details of one past run (manifest + step summary)")
    showRunCmd.Arguments.Add(showProjectArg)
    showRunCmd.Arguments.Add(showRunIdArg)
    showRunCmd.Options.Add(showFormatOpt)
    showRunCmd.SetAction(fun (pr: ParseResult) ->
        let project = pr.GetValue(showProjectArg)
        let runId   = pr.GetValue(showRunIdArg)
        match History.parseFormat (pr.GetValue(showFormatOpt)) with
        | Error msg ->
            Console.Error.WriteLine(sprintf "show-run: %s" msg)
            2
        | Ok fmt -> History.invokeShowRun project runId fmt)
    root.Subcommands.Add(showRunCmd)

    // ─── replay-run ───────────────────────────────────────────────
    let replayProjectArg = Argument<string>("project")
    replayProjectArg.Description <- "Registered project name OR path"
    let replayRunIdArg = Argument<string>("run-id")
    replayRunIdArg.Description <- "Run id whose params to reuse"

    let replayCmd =
        Command("replay-run", "Re-run a flow using the exact params of a prior run")
    replayCmd.Arguments.Add(replayProjectArg)
    replayCmd.Arguments.Add(replayRunIdArg)
    replayCmd.SetAction(fun (pr: ParseResult) ->
        History.invokeReplay (pr.GetValue(replayProjectArg)) (pr.GetValue(replayRunIdArg)))
    root.Subcommands.Add(replayCmd)

    // ─── describe ─────────────────────────────────────────────────
    let describeTypeArg = Argument<string>("task-type")
    describeTypeArg.Description <- "Task type (e.g. ue.build_cook_run, shell, notify.console)"
    let describeCmd =
        Command("describe", "Print the param + output schema for a built-in task as JSON")
    describeCmd.Arguments.Add(describeTypeArg)
    describeCmd.SetAction(fun (pr: ParseResult) ->
        Describe.invoke (pr.GetValue(describeTypeArg)))
    root.Subcommands.Add(describeCmd)

    root.Parse(argv).Invoke()
