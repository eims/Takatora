# Takatora

**CI without the CI server** — local-first build automation for game projects on Windows.

Takatora is for developers who want repeatable, automated builds (Unreal / Unity / Godot) but for whom a hosted or self-hosted CI — Jenkins, TeamCity, GitHub Actions runners — is more weight than the job needs: a server to stand up, agents to wire, YAML to maintain. Takatora is a single app you run on your own machine. Define flows in TOML, run them from the CLI or a desktop GUI, and have it watch a repo to auto-run on new commits. No server, no agents, no infrastructure.

Named after Tōdō Takatora (藤堂高虎, 1556–1630), a Sengoku-period master castle builder who designed roughly twenty castles and codified the methods of Japanese castle construction.

## Positioning

Takatora sits on a different axis from hosted CIs:

| Aspect | Hosted CI (Jenkins, Actions, …) | Takatora |
|---|---|---|
| Environment | Remote server + runners | Local, single machine |
| Setup | Server, agents, plugins | One exe, no services |
| Scale | Cross-team, large | Solo to small team, indie |
| Focus | General-purpose | Game builds (UE / Unity / Godot) |
| Config | YAML, code-driven | TOML + F# (`.fsx`) scripts |
| Interface | Web | CLI + desktop GUI (Avalonia) |

Because it runs **where you work** rather than on a remote server, Takatora can do things a hosted CI structurally can't: detect your installed engines, open a project in your editor / IDE, and show run history inline. That local integration is the point — it's a build runner and a project workspace in one, not a server you have to visit.

Target audience: solo to small game teams who want to drive UE / Unity / Godot builds, packaging, and distribution from their own machine via explicit flow definitions — without the overhead of a CI server.

## Status

Pre-release and under active development. The CLI and GUI run flows end to end — engine detection, builds/packaging, artifacts, run history, open-in-editor/IDE, and watch-and-auto-run-on-commit all work (verified against a real Unreal project) — but APIs and config may still change before v0.1.

## Documentation

- [Documentation index](docs/README.md) — getting started, commands, concepts
- Per-engine guides: [Unreal Engine](docs/unreal.md) · [Unity](docs/unity.md) · [Godot](docs/godot.md)

## Scope

- Windows local (macOS / Linux are future work)
- Engines: Unreal Engine, Unity, Godot
- VCS: git + git-lfs
- Single-machine operation; distributed runners are out of scope
- On-demand automation: you start/stop watching a repo from the GUI (tray-resident), rather than running an always-on service

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

[MIT](LICENSE).
