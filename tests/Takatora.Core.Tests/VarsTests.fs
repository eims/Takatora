module Takatora.Core.Tests.VarsTests

open Xunit
open Takatora.Core

// ─── Test fixtures ─────────────────────────────────────────────────

let private project: Project =
    { Name = "sample"
      WorkingDir = "C:/work"
      Engine =
        { Kind = EngineKind.Unreal
          ProjectFile = None
          EnginePath = None
          EngineVersion = None
          Executable = None }
      Vcs = None
      History = { KeepLastNRuns = 50 } }

let private ctxWith (vars: (string * TomlValue) list)
                    (stepOutputs: (string * (string * TomlValue) list) list)
                    (env: Map<string, string>)
                    : ResolveContext =
    { Vars = Map.ofList vars
      StepOutputs =
        stepOutputs
        |> List.map (fun (id, kvs) -> id, Map.ofList kvs)
        |> Map.ofList
      Project = project
      Env = fun n -> Map.tryFind n env }

let private ctx vars = ctxWith vars [] Map.empty

let private catchVarError (action: unit -> 'a) : string =
    try
        action () |> ignore
        Assert.Fail("expected VarResolutionError")
        ""
    with VarResolutionError msg -> msg

// ─── resolve: scalars ──────────────────────────────────────────────

[<Fact>]
let ``resolve passes non-string scalars through unchanged`` () =
    let c = ctx []
    Assert.Equal(TInt 42L, Vars.resolve c (TInt 42L))
    Assert.Equal(TBool true, Vars.resolve c (TBool true))
    Assert.Equal(TFloat 3.14, Vars.resolve c (TFloat 3.14))

[<Fact>]
let ``resolve preserves type when the whole string is a single placeholder`` () =
    let c = ctx [ "configuration", TString "Shipping"; "clean_first", TBool true ]
    Assert.Equal(TString "Shipping", Vars.resolve c (TString "${vars.configuration}"))
    Assert.Equal(TBool true,         Vars.resolve c (TString "${vars.clean_first}"))

[<Fact>]
let ``resolve concatenates and stringifies on embedded placeholders`` () =
    let c = ctx [ "platform", TString "Win64"; "configuration", TString "Shipping" ]
    Assert.Equal(
        TString "Build/Win64-Shipping",
        Vars.resolve c (TString "Build/${vars.platform}-${vars.configuration}"))

[<Fact>]
let ``resolve stringifies bool and int when interpolated`` () =
    let c = ctx [ "n", TInt 7L; "flag", TBool false ]
    Assert.Equal(TString "n=7 flag=false",
                 Vars.resolve c (TString "n=${vars.n} flag=${vars.flag}"))

// ─── resolve: trees ────────────────────────────────────────────────

[<Fact>]
let ``resolve recurses into arrays and tables`` () =
    let c = ctx [ "x", TString "intermediate"; "y", TString "binaries" ]
    let input =
        TTable (Map.ofList [
            "targets", TArray [ TString "${vars.x}"; TString "${vars.y}" ]
            "platform", TString "Win64"
        ])
    let expected =
        TTable (Map.ofList [
            "targets", TArray [ TString "intermediate"; TString "binaries" ]
            "platform", TString "Win64"
        ])
    Assert.Equal<TomlValue>(expected, Vars.resolve c input)

// ─── resolve: lookups across namespaces ────────────────────────────

[<Fact>]
let ``resolve looks up steps.<id>.outputs.<key>`` () =
    let c = ctxWith [] [ "build", [ "exe_path", TString "Build/Game.exe" ] ] Map.empty
    Assert.Equal(TString "Build/Game.exe",
                 Vars.resolve c (TString "${steps.build.outputs.exe_path}"))

[<Fact>]
let ``resolve looks up project.name and project.working_dir`` () =
    let c = ctx []
    Assert.Equal(TString "sample",  Vars.resolve c (TString "${project.name}"))
    Assert.Equal(TString "C:/work", Vars.resolve c (TString "${project.working_dir}"))

[<Fact>]
let ``resolve looks up env.<NAME> via injected reader`` () =
    let c = ctxWith [] [] (Map.ofList [ "TAKATORA_TEST", "abc" ])
    Assert.Equal(TString "abc", Vars.resolve c (TString "${env.TAKATORA_TEST}"))

// ─── resolve: error cases ──────────────────────────────────────────

[<Fact>]
let ``resolve raises on unknown var`` () =
    let c = ctx []
    let msg = catchVarError (fun () ->
        Vars.resolve c (TString "${vars.missing}"))
    Assert.Contains("vars.missing", msg)

[<Fact>]
let ``resolve raises on missing prior step output`` () =
    let c = ctxWith [] [ "build", [ "exe_path", TString "..." ] ] Map.empty
    let msg = catchVarError (fun () ->
        Vars.resolve c (TString "${steps.build.outputs.archive_path}"))
    Assert.Contains("archive_path", msg)

[<Fact>]
let ``resolve raises on unset env var`` () =
    let c = ctxWith [] [] Map.empty
    let msg = catchVarError (fun () ->
        Vars.resolve c (TString "${env.NOT_SET}"))
    Assert.Contains("NOT_SET", msg)

[<Fact>]
let ``resolve raises on unknown project field`` () =
    let c = ctx []
    let msg = catchVarError (fun () ->
        Vars.resolve c (TString "${project.bogus}"))
    Assert.Contains("project.bogus", msg)

[<Fact>]
let ``resolve raises on unsupported placeholder namespace`` () =
    let c = ctx []
    let msg = catchVarError (fun () ->
        Vars.resolve c (TString "${weather.tokyo}"))
    Assert.Contains("weather.tokyo", msg)

[<Fact>]
let ``resolve raises on interpolating array into a string`` () =
    let c = ctx [ "lst", TArray [ TString "a"; TString "b" ] ]
    let msg = catchVarError (fun () ->
        Vars.resolve c (TString "before ${vars.lst} after"))
    Assert.Contains("array/table", msg)

// ─── evalWhen ──────────────────────────────────────────────────────

[<Fact>]
let ``evalWhen returns the bool var directly`` () =
    Assert.True(Vars.evalWhen (ctx [ "f", TBool true ]) "${vars.f}")
    Assert.False(Vars.evalWhen (ctx [ "f", TBool false ]) "${vars.f}")

[<Fact>]
let ``evalWhen negates when prefixed with bang`` () =
    Assert.False(Vars.evalWhen (ctx [ "f", TBool true ]) "!${vars.f}")
    Assert.True(Vars.evalWhen (ctx [ "f", TBool false ]) "!${vars.f}")

[<Fact>]
let ``evalWhen tolerates whitespace around the negation`` () =
    Assert.True(Vars.evalWhen (ctx [ "f", TBool false ]) "  ! ${vars.f}  ")

[<Fact>]
let ``evalWhen rejects non-bool var`` () =
    let msg = catchVarError (fun () ->
        Vars.evalWhen (ctx [ "x", TString "shipping" ]) "${vars.x}")
    Assert.Contains("must reference a bool", msg)

[<Fact>]
let ``evalWhen rejects comparison-style expressions`` () =
    let msg = catchVarError (fun () ->
        Vars.evalWhen (ctx [ "x", TString "Shipping" ]) "${vars.x} == 'Shipping'")
    Assert.Contains("when` expression", msg)

[<Fact>]
let ``evalWhen rejects logical combinators`` () =
    let msg = catchVarError (fun () ->
        Vars.evalWhen (ctx [ "a", TBool true; "b", TBool true ])
                      "${vars.a} && ${vars.b}")
    Assert.Contains("when` expression", msg)
