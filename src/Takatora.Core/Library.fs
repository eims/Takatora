namespace Takatora.Core

/// Version info and product metadata.
module Version =
    [<Literal>]
    let Product = "Takatora"

    [<Literal>]
    let Version = "0.1.1-alpha"

    /// SPDX license id — surfaced in About / `takatora version`.
    [<Literal>]
    let License = "MIT"

    [<Literal>]
    let Copyright = "Copyright (c) 2026 eims"

    [<Literal>]
    let Repository = "https://github.com/eims/Takatora"

    /// Schema version for run-dir layout, manifest.toml, events.ndjson.
    /// Bumped when the on-disk format changes in a breaking way.
    [<Literal>]
    let RunSchemaVersion = 1

/// Base directory for Takatora's machine-local state — the project
/// registry, app settings, and toolbox state all live under it. Normally
/// `%APPDATA%/Takatora`, but the `TAKATORA_DATA_DIR` environment variable
/// overrides it wholesale: for a portable install, or an isolated demo /
/// test / CI instance that must not touch the developer's real config.
module AppData =
    open System
    open System.IO

    [<Literal>]
    let EnvVar = "TAKATORA_DATA_DIR"

    /// The resolved base dir. `TAKATORA_DATA_DIR` wins when set to a
    /// non-empty value; otherwise `%APPDATA%/Takatora`.
    let baseDir () : string =
        match Environment.GetEnvironmentVariable EnvVar with
        | null | "" ->
            Path.Combine(
                Environment.GetFolderPath Environment.SpecialFolder.ApplicationData,
                "Takatora")
        | dir -> dir
