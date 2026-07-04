# Godot with Takatora

A getting-started guide for driving Godot exports from Takatora. See the
[docs index](README.md) for the basics.

## Prerequisites

- Godot installed. Godot has no canonical install location, so either put
  `godot` on your `PATH` or set `engine_path` (see below).
- A Godot project (`project.godot`) with an **export preset** defined in
  `export_presets.cfg` (set these up in the Godot editor:
  *Project → Export*).
- Takatora built (`dotnet build`).

## 1. Scaffold

```sh
takatora init . --name MyGame --engine godot
```

`.takatora/flows.toml` gets two Godot presets (plus `smoke`):

- **`export`** — headless export via `godot.export`.
- **`clean`** — remove the cache (`.godot/` or `.import/`) + build output
  (`godot.clean`, `pre_release`).

## 2. Engine detection / setting the path

Takatora discovers Godot by scanning `PATH` for `godot*`. Confirm:

```sh
takatora detect-engines
```

If Godot isn't on `PATH`, point `[engine].engine_path` at the executable:

```toml
[engine]
type = "godot"
engine_path = "C:/Tools/Godot/godot.exe"
```

> Heads-up: `engine_path` lives in the committed `project.toml`. For a path
> that differs per machine, prefer putting `godot` on `PATH`.

The engine designation is **project-local** (`engine_path`); the only
machine-level Godot setting is the *search paths* — where to look. In the GUI
(**Settings → Godot**), add search dirs, click **Detect** (scans `PATH` + those
dirs), and pick a result — that writes `engine_path` into this project's
`project.toml`. Using a Godot fork (e.g. GDStudio)? Just point `engine_path` at
its binary by hand.

## 3. Fill in the export preset

Edit the `# ←` marked value in `.takatora/flows.toml` so `preset` matches a name
from your `export_presets.cfg`:

```toml
[[flow.steps]]
type = "godot.export"
preset = "Windows Desktop"  # ← match export_presets.cfg
output = "Build/Windows/MyGame.exe"
```

By default this does a release export (`--export-release`); set
`release = false` for a debug export.

## 4. Run

```sh
takatora run MyGame export
```

Or from the GUI. `godot.export` records `exe_path` as a step output, so you
can chain an `artifact.collect` step to produce a versioned, manifested drop:

```toml
[[flow.steps]]
id = "collect"
type = "artifact.collect"
sources = ["${steps.export.outputs.exe_path}"]
dest = "Artifacts"
stamp = "both"
```

## 5. Open the project

From the project header in the GUI:

- **Open in editor** — launches Godot with `--path <project> --editor`,
  using `engine_path` (or the one found on `PATH`).
- **Open in IDE** — your configured IDE command (e.g. VS Code on the project
  folder). Use **Settings → Open in IDE → Detect**.
