# Unreal Engine with Takatora

A getting-started guide for driving Unreal Engine builds, packaging, and
artifacts from Takatora. See the [docs index](README.md) for the basics.

## Prerequisites

- Unreal Engine installed — via the Epic Games Launcher, an HKLM install, or
  a **source build**. All three are auto-detected (see below).
- A `.uproject` in your project directory.
- Takatora built (`dotnet build`).

## 1. Scaffold

```sh
takatora init . --name MyGame --engine unreal
```

This writes `.ci/project.toml`:

```toml
[project]
name = "MyGame"
working_dir = "."

[engine]
type = "unreal"
```

and `.ci/flows.toml` with three Unreal presets (plus a `smoke` flow):

- **`compile`** — non-unity compile check (`ue.build_nonunity`, drives UBT
  with `-DisableUnity` to catch missing `#include`s the unity build hides).
- **`package`** — Windows package via UAT BuildCookRun (`ue.build_cook_run`).
- **`clean`** — remove `Intermediate/` + `Binaries/` (`ue.clean`, `safe`).

Point `[engine].project_file` at your `.uproject` so engine resolution and
"Open in editor" work:

```toml
[engine]
type = "unreal"
project_file = "MyGame.uproject"
```

## 2. Engine detection (no hardcoded paths)

Takatora resolves the engine from the `.uproject`'s `EngineAssociation`:

- A version like `"5.7"` → matches a launcher/HKLM install (`5.7.4-…`).
- A **GUID** (source build) → matched against
  `HKCU\SOFTWARE\Epic Games\Unreal Engine\Builds`.

So you normally need **no** `engine_path` / `engine_version`. Check what's
found:

```sh
takatora detect-engines
```

The GUI's project **Settings → Resolved engine** shows the exact install a
project will run on (version + path), or a clear "not resolved" reason.

If you must override (rare), set `engine_path` to the engine root in
`[engine]`.

## 3. Fill in the package preset

Open `.ci/flows.toml` and adjust the `# ←` marked value:

```toml
[[flow.steps]]
type = "ue.build_cook_run"
configuration = "${vars.configuration}"
platform = "Win64"
target = "MyGame"  # ← your game target (often the project name)
archive_dir = "Package/Windows"
extra_uat_args = ["-noP4", "-pak", "-prereqs", "-allmaps", "-targetplatform=Win64"]
```

`configuration` is a flow var (Development / DebugGame / Shipping / Test),
overridable at run time.

## 4. Run

```sh
# compile check
takatora run MyGame compile

# package, Shipping config
takatora run MyGame package --var configuration=Shipping
```

Or from the GUI: open the project, pick a flow, **Run** (the run dialog
collects vars). Watch the live log; review past runs under **History** with
full-log search.

> **Encoding note**: Takatora decodes engine/tool output in the OS code page
> (e.g. CP932 on Japanese Windows), so localized linker output is readable in
> the run log rather than mojibake.

## 5. Collect artifacts

`ue.build_cook_run` records `archive_path` (and `exe_path`) as step outputs.
Add an `artifact.collect` step to drop a versioned, manifested copy:

```toml
[[flow]]
id = "release"
name = "Package + collect"

[flow.vars]
configuration = { type = "enum", values = ["Development", "Shipping"], default = "Development" }

[[flow.steps]]
id = "package"
type = "ue.build_cook_run"
configuration = "${vars.configuration}"
platform = "Win64"
target = "MyGame"
archive_dir = "Package/Windows"
extra_uat_args = ["-noP4", "-pak", "-prereqs", "-allmaps", "-targetplatform=Win64"]

[[flow.steps]]
id = "collect"
type = "artifact.collect"
sources = ["${steps.package.outputs.archive_path}"]
dest = "Artifacts"
stamp = "both"      # timestamp + git short hash
archive = false     # true → zip it
```

This produces `Artifacts/MyGame-<stamp>/` with a `manifest.json` (project,
engine version, git hash, sizes).

## 6. Open the project

From the project header in the GUI:

- **Open in editor** — opens the `.uproject` via its shell association
  (UnrealVersionSelector → the engine, or Rider for Unreal if you associated
  it), exactly like a double-click.
- **Open in IDE** — runs your configured IDE command. Use **Settings →
  Open in IDE → Detect** to pick Rider / Visual Studio (the `{target}`
  placeholder resolves to the `.uproject` for Unreal).
