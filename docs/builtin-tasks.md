# Built-in tasks

Reference for the task types Takatora ships with. A flow step's `type = "<task>"`
runs one of these — or a project-local `.takatora/tasks/<task>.fsx` of the same
name, which takes precedence (the custom-task escape hatch). For the
machine-readable param/output schema, run `takatora describe <task> [--project <p>]`.

Tasks (19): `artifact.collect`, `fs.clean`, `fs.copy`, `fs.write`, `fs.zip`, `git.checkout`, `git.info`, `git.pull`, `godot.clean`, `godot.export`, `lfs.fetch`, `lfs.pull`, `notify.console`, `shell`, `ue.build_cook_run`, `ue.build_nonunity`, `ue.clean`, `unity.build`, `unity.clean`.

_Generated from the task headers by `tools/gen-builtin-task-reference.ps1`._

## artifact.collect

```text
Identify build artifacts and store them in a versioned, named drop —
the "where did my build actually go" task. Sources are usually a prior
step's output (e.g. ${steps.package.outputs.archive_path}); they are
copied into <dest>/<name>-<stamp>/ together with a manifest.json that
records what was collected (project, engine version, git hash,
timestamp, sizes). Optionally zips the drop instead of leaving a folder.

Building block, not a replacement for fs.copy / fs.zip: those move bytes;
this stamps + names + manifests a release drop.

Params:
  sources    string[]  — files/dirs to collect (required; ${steps...} ok)
  use_zip    bool?      — collect `zip_source` instead of `sources` (default false).
                         Lets a flow collect the .zip when a zip step ran, or
                         the expanded dir otherwise, by wiring both to a bool var.
  zip_source string?    — the .zip to collect when use_zip=true (required then).
  dest       string?    — output root dir (default "Artifacts")
  name       string?    — base name (default: the project name)
  stamp      string?    — none | timestamp | git | both (default "timestamp")
  archive    bool?      — zip the drop into <name>-<stamp>.zip (default false)

Outputs:
  artifact_path  string  — the produced folder (or the .zip when archived)
  manifest_path  string  — the manifest.json (folder mode only)
  stamp          string  — the resolved stamp ("" when stamp=none)
  size           int     — total payload bytes (the zip's size when archived)
```

## fs.clean

```text
Recursively delete a path. Reports how much was reclaimed so flow
authors can sanity-check / log cleanup magnitude.

Params:
  path     string  — absolute or relative to Project.workingDir
  keep?    bool    — if true and `path` is a directory, delete its
                     contents but keep the dir itself. Default false.

Outputs:
  bytes_freed     int  — total size of files removed
  files_deleted   int  — count of files removed
```

## fs.copy

```text
File-or-directory copy. If `from` is a file, `to` is treated as the
destination file path. If `from` is a directory, `to` is the
destination directory; existing files there are overwritten.

Params:
  from   string  — source (file or directory)
  to     string  — destination

Outputs: none
```

## fs.write

```text
Write text to a file (creating parent dirs). `content` is a normal step
param, so it can interpolate ${steps.X.outputs.Y} / ${vars.X} — e.g.
stamp a build with a git.info hash. Use append to add to an existing file.

Params:
  path     string   — destination file (relative → under the working dir)
  content  string?  — text to write (default empty)
  append   bool?    — append instead of overwrite (default false)

Outputs:
  path  string  — the absolute path written
  bytes int     — number of bytes written (the content length, UTF-8)
```

## fs.zip

```text
Archive a directory into a .zip. Existing archive at `to` is
overwritten. Use for build artifact packaging before upload.

Params:
  from   string  — source directory (must be a dir, not a file)
  to     string  — output .zip path

Outputs:
  archive_path  string  — absolute path to the created .zip
  size          int     — final archive size in bytes
```

## git.checkout

```text
Switch to a branch / tag / commit by ref. Surfaces the resolved
commit sha for downstream steps.

Params:
  ref  string  — branch name, tag, or commit hash

Outputs:
  commit_sha  string  — HEAD after checkout
```

## git.info

```text
Capture the working repo's state as step outputs, for downstream steps
(e.g. stamping a build with its commit hash via fs.write). Reads the repo
at the project's working dir. Missing values come through as empty.

Params:
  date_format  string?  — .NET format for the `date` output
                          (default "yyyy-MM-dd HH:mm:ss")

Outputs:
  hash      string  — full commit SHA (HEAD)
  short     string  — short commit SHA
  branch    string  — current branch (or "HEAD" when detached)
  modified  string  — "true" if the working tree has changes, else "false"
  message   string  — HEAD's subject line
  date      string  — now, formatted with date_format
```

## git.pull

```text
Runs `git pull` against the project working dir. After the pull,
captures the resulting HEAD commit so downstream steps can reference
`${steps.<id>.outputs.commit_sha}`.

Params:
  remote?  string  — default "origin"
  branch?  string  — default "" (whatever the current branch tracks)

Outputs:
  commit_sha  string  — `git rev-parse HEAD` after the pull
```

## godot.clean

```text
Smaller catalog than UE/Unity — Godot's footprint is essentially
`.godot/` (4.x) or `.import/` (3.x) + the user's chosen build dir.

Params:
  targets?      string[]  — any of: cache, build_output
  preset?       string    — safe | pre_release | nuke
  build_output? string    — path for `build_output` target
                             (default "Build")

Outputs:
  bytes_freed     int
  files_deleted   int
```

## godot.export

```text
Headless export via Godot's CLI. Requires the user-side
`export_presets.cfg` to define a preset matching the `preset` param.

Params:
  preset    string  — preset name from export_presets.cfg
                      (e.g. "Windows Desktop")
  output    string  — output exe / app path
  release?  bool    — true → --export-release, false → --export-debug
                      (default true)

Outputs:
  exe_path  string  — same as `output`, absolutized
```

## lfs.fetch

```text
Pre-warms the LFS cache without checkout — useful as an early step
before a `git.checkout` so the actual checkout's smudge filter is
instant.

Outputs: none
```

## lfs.pull

```text
Runs `git lfs pull` in the project working dir. Separate from
`git.pull` because the smudge filter is slow and gets its own task
for explicit scheduling / parallel sequencing.

Outputs: none (lfs progress streams through to log.txt verbatim)
```

## notify.console

```text
Writes a message to the run log. Trivial; serves as the smoke-test
that the .fsx execution pipeline works end-to-end.

The runner injects the Takatora.Tasks SDK via a generated wrapper
`#r`, so this script doesn't need its own reference. Authors writing
project-local tasks under `.ci/tasks/` follow the same convention.
```

## shell

```text
Escape hatch for any command not covered by a dedicated task type.
Stdout/stderr stream to the run's log.txt verbatim.

Params:
  command            string         — passed to cmd.exe /c on Windows,
                                      /bin/sh -c elsewhere
  working_dir?       string         — defaults to Project.workingDir
  ignore_exit_codes? list<int>      — exit codes to treat as success
                                      (e.g. [1] for robocopy)

Outputs: none (use shell.capture in the future for stdout-bearing variants)
```

## ue.build_cook_run

```text
Wraps Unreal's BuildCookRun via UAT — the canonical "do everything
to ship a build" entry point. Mirrors the design's I/O contract.

Params:
  configuration   enum     — Development | DebugGame | Shipping | Test
  platform        string   — Win64 / Mac / Linux / etc.
  target          string?  — Game | Client | Server | Editor (default Game)
  maps            string[] — maps to cook (default: project's defaults)
  archive_dir     string?  — output dir, default Build/<platform>-<config>
  extra_uat_args  string[] — pass-through extras

Outputs:
  archive_path   string  — same as archive_dir but absolute
  exe_path       string  — guessed primary exe under archive_dir
```

## ue.build_nonunity

```text
Compiles a UE target with the unity (jumbo) build DISABLED so the
compiler sees each .cpp on its own — surfacing missing #includes and
stale forward-declarations that a unity build silently hides. It's a
pure correctness gate: UBT returns non-zero on the first compile error,
which fails the step.

Params:
  target?         string   — UBT target name (default "<project name>Editor")
  platform?       string   — default "Win64"
  configuration?  string   — Development (default) | DebugGame | Shipping | Test
  extra_ubt_args? string[] — pass-through (e.g. -NoPCH to also defeat PCH-
                             hidden includes)

Outputs: none — success/failure is the result.

Note: as a `ue.*` task the runner auto-acquires the UE editor mutex, so
this won't fight a build/cook running in the same workspace.
```

## ue.clean

```text
Selective UE artifact cleanup per the design's target catalog.
Use `targets` for explicit choice or `preset` for shorthand.

Params:
  targets?  string[]  — any of: intermediate, binaries, saved,
                         cooked, derived_data, project_files
  preset?   string    — safe | pre_release | nuke
                         (resolved BEFORE targets; both can be set,
                         they're unioned)

Outputs:
  bytes_freed     int
  files_deleted   int
```

## unity.build

```text
Drives a Unity batch build by invoking a user-side static method
(typically a custom BuildPipeline.Build or similar). The user's
Editor script does the actual work; this task just plumbs args.

Params:
  build_target  string  — e.g. StandaloneWindows64, iOS, Android
  build_method  string  — fully-qualified C# static method
                          (e.g. BuildScripts.BuildAll)
  output        string  — output exe / app path
  project_path? string  — defaults to Project.workingDir

Outputs:
  exe_path  string  — same as `output`, absolutized
  log_path  string  — Unity log under <output>.log
```

## unity.clean

```text
Selective Unity artifact cleanup. Same pattern as ue.clean.

Params:
  targets?      string[]  — any of: library, temp, obj, logs,
                             build_output, project_files
  preset?       string    — safe | pre_release | nuke
  build_output? string    — path for `build_output` target
                             (default "Build")

Outputs:
  bytes_freed     int
  files_deleted   int

Editor-running detection isn't done here; Unity will lock Library/
while the editor is open and the recursive delete will fail loudly.
```

