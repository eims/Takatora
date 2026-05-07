module Takatora.Cli.Program

open System.CommandLine
open Takatora.Core

[<EntryPoint>]
let main argv =
    let root = RootCommand($"{Version.Product} — local CI for game builds")

    let versionCmd = Command("version", "Print Takatora version and exit")
    versionCmd.SetAction(fun _ -> printfn $"{Version.Product} {Version.Version}")
    root.Subcommands.Add(versionCmd)

    // TODO: run / describe / detect-engines / cancel / validate / project / history / show-run / replay-run

    root.Parse(argv).Invoke()
