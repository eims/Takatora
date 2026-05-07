namespace Takatora.Core

/// Version info and product metadata.
module Version =
    [<Literal>]
    let Product = "Takatora"

    [<Literal>]
    let Version = "0.1.0-alpha"

    /// Schema version for run-dir layout, manifest.toml, events.ndjson.
    /// Bumped when the on-disk format changes in a breaking way.
    [<Literal>]
    let RunSchemaVersion = 1
