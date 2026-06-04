module Takatora.Cli.DetectEngines

open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Takatora.Core

let private engineLabel = function
    | EngineKind.Unreal -> "Unreal Engine"
    | EngineKind.Unity  -> "Unity"
    | EngineKind.Godot  -> "Godot"

let private engineKey = function
    | EngineKind.Unreal -> "unreal"
    | EngineKind.Unity  -> "unity"
    | EngineKind.Godot  -> "godot"

let private toJson (results: Map<EngineKind, DetectedEngine list>) : string =
    let root = JsonObject()
    for kind in [ EngineKind.Unreal; EngineKind.Unity; EngineKind.Godot ] do
        let list = Map.tryFind kind results |> Option.defaultValue []
        let arr = JsonArray()
        for e in list do
            let entry = JsonObject()
            entry.["version"] <- JsonValue.Create(e.Version)
            entry.["path"]    <- JsonValue.Create(e.Path)
            match e.Executable with
            | Some exe -> entry.["executable"] <- JsonValue.Create(exe)
            | None -> ()
            match e.Association with
            | Some assoc -> entry.["association"] <- JsonValue.Create(assoc)
            | None -> ()
            arr.Add(entry)
        root.[engineKey kind] <- arr
    root.ToJsonString(JsonSerializerOptions(WriteIndented = true))

let private toHuman (results: Map<EngineKind, DetectedEngine list>) : string =
    let sb = StringBuilder()
    for kind in [ EngineKind.Unreal; EngineKind.Unity; EngineKind.Godot ] do
        sb.AppendLine(engineLabel kind) |> ignore
        let list = Map.tryFind kind results |> Option.defaultValue []
        if List.isEmpty list then
            sb.AppendLine("  (none detected)") |> ignore
        else
            for e in list do
                sb.AppendLine(sprintf "  %s — %s" e.Version e.Path) |> ignore
    sb.ToString()

/// Output formats accepted by `--format`.
type Format = Human | Json

let parseFormat (s: string) : Result<Format, string> =
    match s with
    | null | "" | "human" -> Ok Human
    | "json" -> Ok Json
    | other -> Error $"unknown format '{other}', expected human | json"

let invoke (format: Format) : int =
    let results = Engines.detectAll ()
    let text =
        match format with
        | Human -> toHuman results
        | Json  -> toJson results
    Console.Out.Write(text)
    if format = Human && not (text.EndsWith("\n")) then Console.Out.WriteLine()
    0
