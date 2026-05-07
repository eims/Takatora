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
    let pathArg = Argument<string>("path")
    pathArg.Description <- "Project working directory containing .ci/ (default: current dir)"
    pathArg.DefaultValueFactory <- (fun _ -> ".")

    let validateCmd =
        Command("validate", "Check that .ci/project.toml and .ci/flows.toml parse cleanly")
    validateCmd.Arguments.Add(pathArg)
    validateCmd.SetAction(fun (pr: ParseResult) ->
        let path = pr.GetValue(pathArg)
        let outcome = Validate.run path
        let stdout, stderr, exitCode = Validate.format outcome
        if not (String.IsNullOrEmpty stdout) then Console.Out.Write(stdout)
        if not (String.IsNullOrEmpty stderr) then Console.Error.Write(stderr)
        exitCode)
    root.Subcommands.Add(validateCmd)

    // TODO: run / describe / detect-engines / cancel / project / history / show-run / replay-run

    root.Parse(argv).Invoke()
