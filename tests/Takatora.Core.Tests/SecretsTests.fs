namespace Takatora.Core.Tests

open System
open System.Collections.Generic
open Xunit
open Takatora.Core

/// In-memory ISecretStore so tests never touch the real OS keychain.
type private InMemoryStore() =
    let d = Dictionary<string, string>()
    interface ISecretStore with
        member _.Read(key) =
            match d.TryGetValue key with
            | true, v -> Some v
            | _ -> None
        member _.Write(key, value) = d.[key] <- value
        member _.Delete(key) = d.Remove key
        member _.List(prefix) =
            [ for kv in d do
                if kv.Key.StartsWith(prefix, StringComparison.Ordinal) then yield kv.Key ]

/// Secrets tests swap in the in-memory backend via the internal
/// `setBackendForTests` hook. xUnit serializes methods within a class, so
/// the shared static backend is safe as long as everything lives here.
type SecretsTests() =

    do Secrets.setBackendForTests (InMemoryStore() :> ISecretStore)

    interface IDisposable with
        member _.Dispose() = Secrets.resetBackend ()

    // ─── key scheme ─────────────────────────────────────────────────

    [<Fact>]
    member _.``keyFor builds the namespaced target`` () =
        Assert.Equal("Takatora:MyGame:steam_password", Secrets.keyFor "MyGame" "steam_password")

    [<Fact>]
    member _.``parseKey is the inverse of keyFor`` () =
        let key = Secrets.keyFor "MyGame" "steam_password"
        Assert.Equal<(string * string) option>(Some ("MyGame", "steam_password"), Secrets.parseKey key)

    [<Fact>]
    member _.``parseKey rejects keys without the prefix`` () =
        Assert.Equal<(string * string) option>(None, Secrets.parseKey "Other:MyGame:x")
        Assert.Equal<(string * string) option>(None, Secrets.parseKey "Takatora:noseparator")

    // ─── store round-trips ──────────────────────────────────────────

    [<Fact>]
    member _.``write then read returns the stored value`` () =
        Secrets.write "p1" "token" "s3cr3t"
        Assert.Equal<string option>(Some "s3cr3t", Secrets.read "p1" "token")

    [<Fact>]
    member _.``read is None for an absent secret`` () =
        Assert.Equal<string option>(None, Secrets.read "p1" "missing")

    [<Fact>]
    member _.``exists reflects presence`` () =
        Assert.False(Secrets.exists "p1" "token")
        Secrets.write "p1" "token" "v"
        Assert.True(Secrets.exists "p1" "token")

    [<Fact>]
    member _.``write overwrites an existing secret`` () =
        Secrets.write "p1" "token" "old"
        Secrets.write "p1" "token" "new"
        Assert.Equal<string option>(Some "new", Secrets.read "p1" "token")

    [<Fact>]
    member _.``delete removes the secret and reports whether one existed`` () =
        Secrets.write "p1" "token" "v"
        Assert.True(Secrets.delete "p1" "token")
        Assert.Equal<string option>(None, Secrets.read "p1" "token")
        // Second delete: nothing left to remove.
        Assert.False(Secrets.delete "p1" "token")

    // ─── listing ────────────────────────────────────────────────────

    [<Fact>]
    member _.``listForProject returns this project's var names, sorted and scoped`` () =
        Secrets.write "p1" "zeta" "v"
        Secrets.write "p1" "alpha" "v"
        Secrets.write "p2" "other" "v"   // different project — must not leak in
        Assert.Equal<string list>([ "alpha"; "zeta" ], Secrets.listForProject "p1")
        Assert.Equal<string list>([ "other" ], Secrets.listForProject "p2")

    [<Fact>]
    member _.``listForProject is empty when nothing is stored`` () =
        Assert.Equal<string list>([], Secrets.listForProject "p1")
