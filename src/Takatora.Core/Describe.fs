namespace Takatora.Core

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

/// One parameter in a task's describe schema.
type DescribeParam = {
    Name: string
    Kind: string
    Required: bool
    /// The default rendered as text (JSON for non-strings), or None.
    Default: string option
    /// enum values, when kind = "enum".
    Values: string list option
    /// file filters, when kind = "file".
    Filter: string list option
    /// Author-supplied description (`Param.note`), shown as a GUI tooltip.
    Description: string option
}

/// A task's describe-mode schema: its param inputs + output names.
type DescribeSchema = {
    Type: string option
    Params: DescribeParam list
    Outputs: string list
}

/// Run a task .fsx in describe mode (`TAKATORA_MODE=describe`) and read back
/// the schema JSON the SDK writes on exit. Shared by the CLI `describe`
/// command and the GUI's per-step Inspector. Spawns `dotnet fsi`, so it's
/// slow (~1-2s) — callers should run it off the UI thread and cache by the
/// task file's path + mtime.
[<RequireQualifiedAccess>]
module Describe =

    /// Spawn fsi against a wrapper that `#r`s the SDK and `#load`s the task,
    /// returning the raw schema JSON or an error.
    let spawnJson (sdkAssemblyPath: string) (taskPath: string) (taskType: string)
            : Result<string, string> =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "takatora-describe", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempDir) |> ignore
        let wrapperPath = Path.Combine(tempDir, "wrapper.fsx")
        let outputPath  = Path.Combine(tempDir, "describe.json")
        let escape (p: string) = p.Replace("\\", "\\\\").Replace("\"", "\\\"")
        File.WriteAllText(
            wrapperPath,
            sprintf "#r @\"%s\"\n#load @\"%s\"\n" (escape sdkAssemblyPath) (escape taskPath))
        try
            let psi = ProcessStartInfo("dotnet")
            psi.ArgumentList.Add("fsi")
            psi.ArgumentList.Add(wrapperPath)
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.Environment.["TAKATORA_MODE"]            <- "describe"
            psi.Environment.["TAKATORA_DESCRIBE_OUTPUT"] <- outputPath
            psi.Environment.["TAKATORA_TASK_TYPE"]       <- taskType
            use proc = Process.Start(psi)
            let stdoutTask = proc.StandardOutput.ReadToEndAsync()
            let stderrTask = proc.StandardError.ReadToEndAsync()
            proc.WaitForExit()
            if proc.ExitCode <> 0 then
                Error (
                    sprintf "fsi exited %d while inspecting %s:%s%s%s%s"
                        proc.ExitCode taskPath Environment.NewLine
                        stdoutTask.Result Environment.NewLine stderrTask.Result)
            elif not (File.Exists outputPath) then
                Error (sprintf "describe wrote no output for %s (SDK process exit hook didn't fire?)" taskPath)
            else
                Ok (File.ReadAllText outputPath)
        finally
            try Directory.Delete(tempDir, recursive = true) with _ -> ()

    /// Parse the schema JSON into the typed model. Malformed JSON → an empty
    /// schema rather than throwing (the raw text is still available to callers
    /// that want it).
    let parse (json: string) : DescribeSchema =
        try
            match JsonNode.Parse(json) with
            | :? JsonObject as root ->
                let strList (node: JsonNode) =
                    match node with
                    | :? JsonArray as arr ->
                        arr |> Seq.choose (fun n ->
                            match n with
                            | null -> None
                            | v -> Some (v.GetValue<string>())) |> List.ofSeq
                    | _ -> []
                let typeField =
                    match root.["type"] with
                    | null -> None
                    | v -> Some (v.GetValue<string>())
                let paramList =
                    match root.["params"] with
                    | :? JsonArray as arr ->
                        arr |> Seq.choose (fun n ->
                            match n with
                            | :? JsonObject as o ->
                                let str (k: string) =
                                    match o.[k] with null -> None | v -> Some (v.GetValue<string>())
                                let bool' (k: string) =
                                    match o.[k] with
                                    | null -> false
                                    | v -> (try v.GetValue<bool>() with _ -> false)
                                let listOf (k: string) =
                                    match o.[k] with null -> None | v -> Some (strList v)
                                let dflt =
                                    match o.["default"] with
                                    | null -> None
                                    | (:? JsonValue as v) ->
                                        // string defaults come through bare; others as JSON.
                                        (match v.GetValueKind() with
                                         | JsonValueKind.String -> Some (v.GetValue<string>())
                                         | _ -> Some (v.ToJsonString()))
                                    | other -> Some (other.ToJsonString())
                                Some {
                                    Name = defaultArg (str "name") ""
                                    Kind = defaultArg (str "kind") "string"
                                    Required = bool' "required"
                                    Default = dflt
                                    Values = listOf "values"
                                    Filter = listOf "filter"
                                    Description = str "description"
                                }
                            | _ -> None) |> List.ofSeq
                    | _ -> []
                let outputs =
                    match root.["outputs"] with
                    | null -> []
                    | v -> strList v
                { Type = typeField; Params = paramList; Outputs = outputs }
            | _ -> { Type = None; Params = []; Outputs = [] }
        with _ -> { Type = None; Params = []; Outputs = [] }

    /// Spawn + parse in one step.
    let schema (sdkAssemblyPath: string) (taskPath: string) (taskType: string)
            : Result<DescribeSchema, string> =
        spawnJson sdkAssemblyPath taskPath taskType |> Result.map parse
