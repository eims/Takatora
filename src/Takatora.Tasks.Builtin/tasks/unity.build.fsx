// Built-in task: unity.build
// Drives a Unity batch build by invoking a user-side static method
// (typically a custom BuildPipeline.Build or similar). The user's
// Editor script does the actual work; this task just plumbs args.
//
// Params:
//   build_target  string  — e.g. StandaloneWindows64, iOS, Android
//   build_method  string  — fully-qualified C# static method
//                           (e.g. BuildScripts.BuildAll)
//   output        string  — output exe / app path
//   project_path? string  — defaults to Project.workingDir
//
// Outputs:
//   exe_path  string  — same as `output`, absolutized
//   log_path  string  — Unity log under <output>.log
open Takatora.Tasks
open System.IO

let buildTarget = Param.required<string> "build_target"
let buildMethod = Param.required<string> "build_method"
let outputParam = Param.required<string> "output"
let projectPath = Param.optional<string> "project_path" Project.workingDir

let resolve (p: string) =
    if Path.IsPathRooted p then p
    else Path.Combine(Project.workingDir, p)

let outputAbs  = resolve outputParam
let projectAbs = resolve projectPath
let logPath    = outputAbs + ".log"

Directory.CreateDirectory(Path.GetDirectoryName outputAbs) |> ignore

let args = [
    "-projectPath";  projectAbs
    "-buildTarget";  buildTarget
    "-executeMethod"; buildMethod
    "-logFile";      logPath
    // Unity reads custom args after `--` is conventional, but most
    // build scripts use Environment.GetCommandLineArgs and parse
    // their own flags. Pass output via `-output` for the script
    // side to consume.
    "-output";       outputAbs
]

Step.run "Unity batch build" (fun () ->
    Unity.runBatch args
)

Output.set "exe_path" outputAbs
Output.set "log_path" logPath
