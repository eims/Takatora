namespace Takatora.Core

/// Generic TOML value tree. Mirrors Tomlyn's runtime model just enough
/// for the runner to pass step params to .fsx subprocesses as JSON and
/// to evaluate `${vars.X}` references.
type TomlValue =
    | TString of string
    | TInt of int64
    | TFloat of float
    | TBool of bool
    | TArray of TomlValue list
    | TTable of Map<string, TomlValue>

[<RequireQualifiedAccess>]
module TomlValue =
    let tryString = function TString s -> Some s | _ -> None
    let tryInt    = function TInt i    -> Some i | _ -> None
    let tryFloat  = function TFloat f  -> Some f | _ -> None
    let tryBool   = function TBool b   -> Some b | _ -> None
    let tryArray  = function TArray xs -> Some xs | _ -> None
    let tryTable  = function TTable m  -> Some m | _ -> None

/// Game engine flavor. Detection and per-engine task families key off this.
[<RequireQualifiedAccess>]
type EngineKind =
    | Unreal
    | Unity
    | Godot

type Engine = {
    Kind: EngineKind
    ProjectFile: string option
    EnginePath: string option
    EngineVersion: string option
    /// Editor binary path (UnrealEditor.exe / Unity.exe / godot.exe).
    /// Never set from TOML — the runner fills it in from detection.
    Executable: string option
}

[<RequireQualifiedAccess>]
type VcsKind =
    | Git

type Vcs = {
    Kind: VcsKind
    Lfs: bool
}

type History = {
    KeepLastNRuns: int
}

/// Project (Workspace) — "where things run".
type Project = {
    Name: string
    WorkingDir: string
    Engine: Engine
    Vcs: Vcs option
    History: History
}

/// Flow var schema. Mirrors the GUI input widget vocabulary so describe-mode
/// schemas and flow-defined vars share one type.
[<RequireQualifiedAccess>]
type VarKind =
    | String
    | Int
    | Float
    | Bool
    | Enum of values: string list
    | List of itemKind: VarKind
    | Path
    | File
    | Dir
    | Secret
    | Multiline

type FlowVar = {
    Name: string
    Kind: VarKind
    Default: TomlValue option
    /// Optional human description (flows.toml `description = "..."`) — shown
    /// as a tooltip on the run-dialog field. Distinct from a TOML `#` comment.
    Description: string option
}

/// Step — "what runs". Reserved keys (type/id/when) are lifted to fields;
/// everything else is the task's typed param bag carried verbatim.
type Step = {
    Id: string option
    Type: string
    When: string option
    Params: Map<string, TomlValue>
}

/// Flow — "how things flow". Ordered steps + typed vars with defaults.
type Flow = {
    Id: string
    Name: string option
    Vars: FlowVar list
    Steps: Step list
}
