# Unity with Takatora

A getting-started guide for driving Unity builds from Takatora. See the
[docs index](README.md) for the basics.

## Prerequisites

- Unity installed via Unity Hub (auto-detected).
- A Unity project (with `ProjectSettings/ProjectVersion.txt`).
- An **editor build method** — Unity batch builds run a C# static method in
  your project's `Editor/` scripts. Takatora plumbs the args; your method
  does the build. A minimal example:

  ```csharp
  // Assets/Editor/BuildScripts.cs
  using UnityEditor;
  public static class BuildScripts
  {
      public static void BuildAll()
      {
          var args = System.Environment.GetCommandLineArgs();
          // parse -output / -buildTarget as needed, then:
          BuildPipeline.BuildPlayer(
              EditorBuildSettings.scenes,
              /* locationPathName */ GetArg(args, "-output"),
              BuildTarget.StandaloneWindows64,
              BuildOptions.None);
      }
      // ... GetArg helper ...
  }
  ```

- Takatora built (`dotnet build`).

## 1. Scaffold

```sh
takatora init . --name MyGame --engine unity
```

`.takatora/flows.toml` gets two Unity presets (plus `smoke`):

- **`build`** — batch build via `unity.build`.
- **`clean`** — remove `Temp/` + `obj/` (`unity.clean`, `safe`).

## 2. Engine detection

Takatora detects Unity installs from Unity Hub (standard + secondary install
paths). Confirm:

```sh
takatora detect-engines
```

The required version is read from `ProjectSettings/ProjectVersion.txt`; the
GUI's **Settings → Resolved engine** shows whether that exact version is
installed.

## 3. Fill in the build preset

Edit the `# ←` marked value in `.takatora/flows.toml`:

```toml
[[flow.steps]]
type = "unity.build"
build_target = "${vars.build_target}"
build_method = "BuildScripts.BuildAll"  # ← your editor build method
output = "Build/Windows/MyGame.exe"
```

`build_method` is the fully-qualified static method (`Namespace.Class.Method`).
`build_target` is a flow var (default `StandaloneWindows64`).

## 4. Run

```sh
takatora run MyGame build
# or a different target:
takatora run MyGame build --var build_target=Android
```

Or run from the GUI. Unity writes its build log next to the output
(`<output>.log`); `unity.build` records `exe_path` and `log_path` outputs.

> **Editor lock**: close the Unity editor before `clean` — Unity locks
> `Library/` while open and a delete would fail.

## 5. Open the project

From the project header in the GUI:

- **Open in editor** — launches the detected Unity version with
  `-projectPath` (the version is matched from `ProjectVersion.txt`; a missing
  version is reported rather than opening the wrong one).
- **Open in IDE** — your configured IDE command. Use **Settings → Open in
  IDE → Detect**; for Unity the natural target is the generated `.sln`
  (Visual Studio / Rider).
