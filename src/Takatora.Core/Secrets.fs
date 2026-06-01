namespace Takatora.Core

open System
open System.Runtime.InteropServices
open System.Text

/// Pluggable secret-store backend. The production backend talks to the OS
/// keychain (Windows Credential Manager); tests inject an in-memory
/// substitute so they never touch the real machine store.
///
/// Keys are opaque strings here — `Secrets` owns the `Takatora:<project>:<var>`
/// scheme on top.
type ISecretStore =
    abstract member Read   : key: string -> string option
    abstract member Write  : key: string * value: string -> unit
    /// True if a credential was removed; false if there was nothing to remove.
    abstract member Delete : key: string -> bool
    /// All stored keys beginning with `prefix` (a literal prefix, not a glob).
    abstract member List   : prefix: string -> string list

/// P/Invoke surface for Windows Credential Manager (advapi32). File-private;
/// nested type defs and `extern` aren't allowed inside an F# class, so they
/// live here and WindowsCredentialStore calls in.
module private CredInterop =

    [<Literal>]
    let CRED_TYPE_GENERIC = 1u
    [<Literal>]
    let CRED_PERSIST_LOCAL_MACHINE = 2u

    [<StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)>]
    type CREDENTIAL =
        struct
            val mutable Flags: uint32
            val mutable Type: uint32
            val mutable TargetName: nativeint
            val mutable Comment: nativeint
            val mutable LastWritten: System.Runtime.InteropServices.ComTypes.FILETIME
            val mutable CredentialBlobSize: uint32
            val mutable CredentialBlob: nativeint
            val mutable Persist: uint32
            val mutable AttributeCount: uint32
            val mutable Attributes: nativeint
            val mutable TargetAlias: nativeint
            val mutable UserName: nativeint
        end

    [<DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern bool CredRead(string target, uint32 ``type``, uint32 reservedFlag, nativeint& credentialPtr)

    [<DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern bool CredWrite(CREDENTIAL& credential, uint32 flags)

    [<DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern bool CredDelete(string target, uint32 ``type``, uint32 flags)

    [<DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern bool CredEnumerate(string filter, uint32 flags, uint32& count, nativeint& credentialsPtr)

    [<DllImport("advapi32.dll", EntryPoint = "CredFree")>]
    extern void CredFree(nativeint buffer)

    let ensureWindows () =
        if not (OperatingSystem.IsWindows()) then
            raise (PlatformNotSupportedException "Credential Manager is only available on Windows")

    /// Read the UTF-16 blob out of a marshaled CREDENTIAL.
    let blobToString (cred: CREDENTIAL) : string =
        if cred.CredentialBlob = IntPtr.Zero || cred.CredentialBlobSize = 0u then ""
        else Marshal.PtrToStringUni(cred.CredentialBlob, int cred.CredentialBlobSize / 2)

/// Windows Credential Manager backend (CRED_TYPE_GENERIC). Persists to the
/// local machine user's credential set; visible/removable in the OS
/// "Credential Manager" UI as a bonus. Windows-only — every method throws
/// PlatformNotSupportedException elsewhere.
type WindowsCredentialStore() =
    interface ISecretStore with

        member _.Read(key: string) : string option =
            CredInterop.ensureWindows ()
            let mutable ptr = IntPtr.Zero
            if CredInterop.CredRead(key, CredInterop.CRED_TYPE_GENERIC, 0u, &ptr) then
                try
                    let cred = Marshal.PtrToStructure<CredInterop.CREDENTIAL>(ptr)
                    Some (CredInterop.blobToString cred)
                finally
                    CredInterop.CredFree(ptr)
            else
                None

        member _.Write(key: string, value: string) : unit =
            CredInterop.ensureWindows ()
            let blob = Encoding.Unicode.GetBytes(value)
            let blobPtr = Marshal.AllocHGlobal(max blob.Length 1)
            let targetPtr = Marshal.StringToCoTaskMemUni(key)
            try
                Marshal.Copy(blob, 0, blobPtr, blob.Length)
                let mutable cred = CredInterop.CREDENTIAL()
                cred.Type <- CredInterop.CRED_TYPE_GENERIC
                cred.TargetName <- targetPtr
                cred.CredentialBlobSize <- uint32 blob.Length
                cred.CredentialBlob <- blobPtr
                cred.Persist <- CredInterop.CRED_PERSIST_LOCAL_MACHINE
                if not (CredInterop.CredWrite(&cred, 0u)) then
                    raise (InvalidOperationException(
                        sprintf "CredWrite failed (key '%s'): Win32 error %d" key (Marshal.GetLastWin32Error())))
            finally
                Marshal.FreeHGlobal(blobPtr)
                Marshal.FreeCoTaskMem(targetPtr)

        member _.Delete(key: string) : bool =
            CredInterop.ensureWindows ()
            CredInterop.CredDelete(key, CredInterop.CRED_TYPE_GENERIC, 0u)

        member _.List(prefix: string) : string list =
            CredInterop.ensureWindows ()
            let mutable count = 0u
            let mutable ptr = IntPtr.Zero
            // Trailing-* glob; we still prefix-check below in case the
            // filter matches more broadly than intended.
            if not (CredInterop.CredEnumerate(prefix + "*", 0u, &count, &ptr)) then []
            else
                try
                    [ for i in 0 .. int count - 1 do
                        let credPtr = Marshal.ReadIntPtr(ptr, i * IntPtr.Size)
                        let cred = Marshal.PtrToStructure<CredInterop.CREDENTIAL>(credPtr)
                        if cred.TargetName <> IntPtr.Zero then
                            let name = Marshal.PtrToStringUni(cred.TargetName)
                            if not (isNull name) && name.StartsWith(prefix, StringComparison.Ordinal) then
                                yield name ]
                finally
                    CredInterop.CredFree(ptr)

[<RequireQualifiedAccess>]
module Secrets =

    /// Credential target prefix that namespaces all of Takatora's secrets.
    [<Literal>]
    let KeyPrefix = "Takatora:"

    /// Credential target for one project var: `Takatora:<project>:<var>`.
    let keyFor (project: string) (varName: string) : string =
        sprintf "%s%s:%s" KeyPrefix project varName

    /// Inverse of `keyFor`. The var is the last `:`-segment; everything
    /// between the prefix and that is the project (so project names with
    /// no `:` round-trip cleanly).
    let parseKey (key: string) : (string * string) option =
        if not (key.StartsWith(KeyPrefix, StringComparison.Ordinal)) then None
        else
            let rest = key.Substring(KeyPrefix.Length)
            match rest.LastIndexOf(':') with
            | i when i > 0 && i < rest.Length - 1 ->
                Some (rest.Substring(0, i), rest.Substring(i + 1))
            | _ -> None

    // Backend injection. Tests swap in an in-memory store; production uses
    // the OS keychain. Mirrors ProjectRegistry's test-path-override hook.
    let mutable private backend : ISecretStore = WindowsCredentialStore() :> ISecretStore
    let internal setBackendForTests (b: ISecretStore) = backend <- b
    let internal resetBackend () = backend <- WindowsCredentialStore() :> ISecretStore

    /// Read a stored secret. None if absent (or the store can't be reached).
    let read (project: string) (varName: string) : string option =
        try backend.Read(keyFor project varName) with _ -> None

    /// Store (or overwrite) a secret.
    let write (project: string) (varName: string) (value: string) : unit =
        backend.Write(keyFor project varName, value)

    /// Remove a stored secret. True if one was removed.
    let delete (project: string) (varName: string) : bool =
        try backend.Delete(keyFor project varName) with _ -> false

    /// Whether a secret is currently stored for this project var.
    let exists (project: string) (varName: string) : bool =
        (read project varName).IsSome

    /// Var names that currently have a stored secret for this project.
    let listForProject (project: string) : string list =
        let prefix = sprintf "%s%s:" KeyPrefix project
        try
            backend.List(prefix)
            |> List.choose parseKey
            |> List.filter (fun (p, _) -> p = project)
            |> List.map snd
            |> List.sort
        with _ -> []
