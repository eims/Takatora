# Takatora documentation

Takatora is local-first build automation for game development — CI without a
CI server. You define **flows** (ordered steps) in `.takatora/flows.toml`, and run
them from the CLI (`takatora`) or the desktop GUI. TOML is the source of
truth; the GUI is an editor/runner on top of it.

## Getting started (any engine)

1. **Build the tools** (one time):

   ```sh
   dotnet build
   ```

   This produces `takatora` (CLI) and the GUI under `src/Takatora.Gui`.

2. **Scaffold a project** in your game's working directory:

   ```sh
   takatora init . --name MyGame --engine unreal   # or unity / godot
   ```

   This writes `.takatora/project.toml` and `.takatora/flows.toml` (a runnable `smoke`
   flow plus engine-specific presets) and registers the project. Existing
   files are never overwritten. The GUI's **Add Project** wizard does the
   same thing.

3. **Run the smoke flow** to confirm the pipeline works end to end:

   ```sh
   takatora run MyGame smoke
   ```

4. Open the **GUI** to browse flows, run them with parameters, watch live
   logs, and review past runs.

## Reference

- [Built-in tasks](builtin-tasks.md) — every bundled task type with its params + outputs
- [Run record schema](run-record-schema.md) — the on-disk run history format + CLI JSON shapes (frozen contract)

## Per-engine guides

- [Unreal Engine](unreal.md)
- [Unity](unity.md)
- [Godot](godot.md)

## For AI agents

- [Driving Takatora from the CLI](ai-cli.md) — inspect existing flows/tasks and run them from the command line (machine-readable output)
- [Authoring flows & custom tasks](ai-authoring.md) — write `flows.toml` and custom `.fsx` tasks (Takatora.Tasks SDK)

Both also ship as self-contained Claude Code skills under `.claude/skills/`
(`takatora-run`, `takatora-authoring`) — auto-active in this repo, or copy a
skill folder into your own project's `.claude/skills/` to use it there.

## Common commands

| Command | What it does |
|---|---|
| `takatora init <dir> --name <N> --engine <e>` | Scaffold + register a project |
| `takatora run <project> <flow> [--var k=v] [--dry-run]` | Run a flow |
| `takatora detect-engines [--format json]` | List detected engine installs |
| `takatora describe <task> [--project <p>]` | Print a task's param/output schema |
| `takatora project list \| add \| remove` | Manage the project registry |

`<project>` is either a registered name or a path to a directory containing
`.takatora/`.

## Concepts

- **Flow** — a named, ordered list of steps (`[[flow]]` in `flows.toml`).
- **Task** — what a step runs (`type = "..."`), an `.fsx` resolved from the
  project (`.takatora/tasks/`), the user dir, or the builtins.
- **Vars** — flow inputs (`[flow.vars]`), overridable at run time
  (`--var name=value`, or the GUI's run dialog). Referenced as
  `${vars.name}` in step params.
- **Step outputs** — values a task records (e.g. a package's `archive_path`),
  referenceable downstream as `${steps.<id>.outputs.<name>}`.
