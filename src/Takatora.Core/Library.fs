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
