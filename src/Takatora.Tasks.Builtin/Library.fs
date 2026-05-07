namespace Takatora.Tasks.Builtin

// Built-in tasks ship as `.fsx` files under the `tasks/` folder.
// This assembly exists primarily to package those scripts; see the
// `tasks/` folder for the actual implementations.
//
// At runtime, the runner copies these scripts to its install dir
// (%PROGRAMFILES%\Takatora\builtin-tasks\) and resolves task `type`
// references by checking project-local → user-level → built-in.

module BuiltinTasksAssembly =
    /// Marker so this assembly has at least one symbol; replace with
    /// real registry/manifest helpers as the runner matures.
    let signature = "Takatora.Tasks.Builtin"
