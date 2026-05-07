namespace Takatora.Tasks

// Public SDK surface for .fsx authors.
//
// Usage from a task .fsx:
//     #r "nuget: Takatora.Tasks"
//     open Takatora.Tasks
//     let cfg = Param.required<string> "configuration"
//     Step.run "Build" (fun () -> Cmd.exec "msbuild" ["..."])
//     Output.set "exe_path" "..."
//
// All implementations are stubs at this stage. Real impls come with
// the runner contract (TAKATORA_TASK_INPUT / OUTPUT_FILE / EVENTS_FILE).

module Param =
    let required<'T> (_name: string) : 'T = failwith "Param.required: not implemented"
    let optional<'T> (_name: string) (_default: 'T) : 'T = failwith "Param.optional: not implemented"
    let requiredEnum (_name: string) (_values: string list) : string = failwith "Param.requiredEnum: not implemented"
    let optionalList<'T> (_name: string) (_default: 'T list) : 'T list = failwith "Param.optionalList: not implemented"
    let has (_name: string) : bool = failwith "Param.has: not implemented"
    let requiredPath (_name: string) : string = failwith "Param.requiredPath: not implemented"
    let optionalPath (_name: string) (_default: string) : string = failwith "Param.optionalPath: not implemented"

module Output =
    let set (_name: string) (_value: obj) : unit = failwith "Output.set: not implemented"

module Step =
    let run (_name: string) (_action: unit -> unit) : unit = failwith "Step.run: not implemented"
    let runResult (_name: string) (_action: unit -> 'T) : 'T = failwith "Step.runResult: not implemented"
    let skip (_name: string) (_reason: string) : unit = failwith "Step.skip: not implemented"

module Log =
    let info (_msg: string) : unit = failwith "Log.info: not implemented"
    let warn (_msg: string) : unit = failwith "Log.warn: not implemented"
    let error (_msg: string) : unit = failwith "Log.error: not implemented"
    let debug (_msg: string) : unit = failwith "Log.debug: not implemented"
    let section (_msg: string) : unit = failwith "Log.section: not implemented"

module Task =
    let fail<'T> (_reason: string) : 'T = failwith "Task.fail: not implemented"
