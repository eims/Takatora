// Built-in task: artifact.collect
// Identify build artifacts and store them in a versioned, named drop —
// the "where did my build actually go" task. Sources are usually a prior
// step's output (e.g. ${steps.package.outputs.archive_path}); they are
// copied into <dest>/<name>-<stamp>/ together with a manifest.json that
// records what was collected (project, engine version, git hash,
// timestamp, sizes). Optionally zips the drop instead of leaving a folder.
//
// Building block, not a replacement for fs.copy / fs.zip: those move bytes;
// this stamps + names + manifests a release drop.
//
// Params:
//   sources   string[]  — files/dirs to collect (required; ${steps...} ok)
//   dest      string?   — output root dir (default "Artifacts")
//   name      string?   — base name (default: the project name)
//   stamp     string?   — none | timestamp | git | both (default "timestamp")
//   archive   bool?     — zip the drop into <name>-<stamp>.zip (default false)
//
// Outputs:
//   artifact_path  string  — the produced folder (or the .zip when archived)
//   manifest_path  string  — the manifest.json (folder mode only)
//   stamp          string  — the resolved stamp ("" when stamp=none)
//   size           int     — total payload bytes (the zip's size when archived)
open Takatora.Tasks
open System
open System.IO
open System.IO.Compression
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Encodings.Web

let sources  = Param.required<string[]> "sources"
let destRoot = Param.optional<string>   "dest"    "Artifacts"
let nameArg  = Param.optional<string>   "name"    ""
let stampArg = Param.optional<string>   "stamp"   "timestamp"
let archive  = Param.optional<bool>     "archive" false

let resolve (p: string) =
    if Path.IsPathRooted p then p else Path.Combine(Project.workingDir, p)

let trimSep (p: string) = p.TrimEnd('\\', '/')

let rec copyDir (s: string) (d: string) =
    Directory.CreateDirectory d |> ignore
    for f in Directory.GetFiles s do
        File.Copy(f, Path.Combine(d, Path.GetFileName f), overwrite = true)
    for sd in Directory.GetDirectories s do
        copyDir sd (Path.Combine(d, Path.GetFileName sd))

let rec dirSize (d: string) : int64 =
    let here = Directory.GetFiles d |> Array.sumBy (fun f -> (FileInfo f).Length)
    let subs = Directory.GetDirectories d |> Array.sumBy dirSize
    here + subs

let gitShortHash () : string option =
    try
        let r = Cmd.execCapture "git" [ "-C"; Project.workingDir; "rev-parse"; "--short"; "HEAD" ]
        if r.exitCode = 0 && not (String.IsNullOrWhiteSpace r.stdout) then Some (r.stdout.Trim())
        else None
    with _ -> None

Step.run "artifact.collect" (fun () ->
    let allowed = [ "none"; "timestamp"; "git"; "both" ]
    if not (List.contains stampArg allowed) then
        Task.fail<unit> (sprintf "artifact.collect: stamp must be one of [%s], got '%s'"
                            (String.concat ", " allowed) stampArg)

    let timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss")
    let gitHash = if stampArg = "git" || stampArg = "both" then gitShortHash () else None
    if (stampArg = "git" || stampArg = "both") && Option.isNone gitHash then
        Log.warn "artifact.collect: git short hash unavailable (not a repo / git missing) — omitting it"
    let stampStr =
        match stampArg with
        | "none"      -> ""
        | "timestamp" -> timestamp
        | "git"       -> defaultArg gitHash ""
        | "both"      -> match gitHash with Some h -> sprintf "%s-%s" timestamp h | None -> timestamp
        | _           -> timestamp

    let baseName =
        let n = if String.IsNullOrWhiteSpace nameArg then Project.name else nameArg
        if String.IsNullOrWhiteSpace n then "artifact" else n
    let folderName = if stampStr = "" then baseName else sprintf "%s-%s" baseName stampStr

    let destAbs   = resolve destRoot
    let targetDir = Path.Combine(destAbs, folderName)
    if Directory.Exists targetDir then Directory.Delete(targetDir, true)
    Directory.CreateDirectory targetDir |> ignore

    // Copy each source into the drop; build manifest entries as we go.
    let entries = JsonArray()
    for src in sources do
        // GetFullPath canonicalises separators (no mixed `/`+`\` in the
        // manifest) and collapses any `.`/`..`.
        let s = Path.GetFullPath(resolve src)
        let entry = JsonObject()
        if File.Exists s then
            File.Copy(s, Path.Combine(targetDir, Path.GetFileName s), overwrite = true)
            entry.["source"] <- JsonValue.Create(s)
            entry.["name"]   <- JsonValue.Create(Path.GetFileName s)
            entry.["kind"]   <- JsonValue.Create("file")
            entry.["bytes"]  <- JsonValue.Create((FileInfo s).Length)
        elif Directory.Exists s then
            let leaf = Path.GetFileName(trimSep s)
            copyDir s (Path.Combine(targetDir, leaf))
            entry.["source"] <- JsonValue.Create(s)
            entry.["name"]   <- JsonValue.Create(leaf)
            entry.["kind"]   <- JsonValue.Create("dir")
            entry.["bytes"]  <- JsonValue.Create(dirSize s)
        else
            Task.fail<unit> (sprintf "artifact.collect: source '%s' does not exist" s)
        entries.Add(entry)
        Log.info (sprintf "collected %s" s)

    // Manifest — the record of what this drop contains and how it was built.
    let manifest = JsonObject()
    manifest.["project"]        <- JsonValue.Create(Project.name)
    manifest.["engine_version"] <- JsonValue.Create(Engine.version)
    manifest.["stamp"]          <- JsonValue.Create(stampStr)
    manifest.["collected_at"]   <- JsonValue.Create(DateTimeOffset.Now.ToString("o"))
    gitHash |> Option.iter (fun h -> manifest.["git_hash"] <- JsonValue.Create(h))
    manifest.["artifacts"]      <- entries
    let manifestPath = Path.Combine(targetDir, "manifest.json")
    // UnsafeRelaxedJsonEscaping: this manifest is read by humans, so don't
    // escape '+' → + (or <>&). It's a local file, not HTML-embedded.
    let manifestJson =
        manifest.ToJsonString(
            JsonSerializerOptions(WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping))
    File.WriteAllText(manifestPath, manifestJson)

    if archive then
        let zipPath = Path.Combine(destAbs, folderName + ".zip")
        if File.Exists zipPath then File.Delete zipPath
        // Heartbeat so a large drop doesn't make the log go silent.
        Progress.during (sprintf "  archiving → %s" (Path.GetFileName zipPath)) 3.0 (fun () ->
            ZipFile.CreateFromDirectory(targetDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory = false))
        Directory.Delete(targetDir, true)
        Output.set "artifact_path" zipPath
        Output.set "size"          (FileInfo zipPath).Length
        Log.info (sprintf "archived → %s" zipPath)
    else
        Output.set "artifact_path" targetDir
        Output.set "manifest_path" manifestPath
        Output.set "size"          (dirSize targetDir)
        Log.info (sprintf "collected → %s" targetDir)

    Output.set "stamp" stampStr
)
