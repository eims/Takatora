# Takatora

A lightweight local CI for Windows, specialized for game development.

Named after Tōdō Takatora (藤堂高虎, 1556–1630), a Sengoku-period master castle builder who designed roughly twenty castles and codified the methods of Japanese castle construction. Takatora is positioned along a different axis from heavyweight hosted CIs (Jenkins, GitHub Actions, etc.): **local, single-machine, small-team, game-engine focused**.

## Positioning

| Aspect | Hosted CI | Takatora |
|---|---|---|
| Environment | Remote, many runners | Local, single machine |
| Scale | Cross-team, large | Solo to small team, indie |
| Focus | General-purpose | Game builds |
| Config | YAML, code-driven | TOML + F# scripts |
| GUI | Web | Desktop (Avalonia) |

Target audience: developers who want to drive UE / Unity / Godot builds, packaging, and distribution from a local machine via explicit flow definitions.

## Status

Pre-release and under active development. The CLI and GUI run flows end to end — engine detection, builds/packaging, artifacts, and run history all work (verified against a real Unreal project) — but APIs and config may still change before v0.1.

## Documentation

- [Documentation index](docs/README.md) — getting started, commands, concepts
- Per-engine guides: [Unreal Engine](docs/unreal.md) · [Unity](docs/unity.md) · [Godot](docs/godot.md)

## Scope

- Windows local (macOS / Linux are future work)
- Engines: Unreal Engine, Unity, Godot
- VCS: git + git-lfs
- Single-machine operation; distributed runners are out of scope
- Windows service mode is planned but not in the MVP

## Stack

- .NET 8 LTS, F# throughout
- Configuration: TOML (Tomlyn)
- Task definitions: F# scripts (`.fsx`)
- GUI: Avalonia 11 + Avalonia.FuncUI (Elmish / MVU)
- CLI: System.CommandLine

## Build

```sh
dotnet restore
dotnet build
dotnet test
```

## Solution layout

```
src/
  Takatora.Core/           # Core library (TOML, flow execution, engine detection)
  Takatora.Cli/            # CLI frontend → takatora.exe
  Takatora.Tasks/          # SDK for .fsx task authors (Param / Output / Step / Cmd / ...)
  Takatora.Tasks.Builtin/  # Bundled .fsx tasks
  Takatora.Gui/            # Avalonia.FuncUI desktop app
tests/
  Takatora.Core.Tests/
samples/
  sample-game/.ci/         # Sample project used for runner integration tests
```

## License

Not yet selected. All rights reserved by the author until a license is chosen.
