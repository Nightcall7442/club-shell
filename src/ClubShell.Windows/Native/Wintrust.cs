#pragma warning disable CA1051 // do not declare visible instance fields (interop structs)
#pragma warning disable CA1707 // identifiers should not contain underscores (Win32 names)
#pragma warning disable CA1815 // override equals on value types

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClubShell.Windows.Native;

/// <summary>wintrust.dll P/Invokes: Authenticode verification of a file's embedded signature.</summary>
[SupportedOSPlatform("windows")]
public static partial class Wintrust
{
    /// <summary>WINTRUST_ACTION_GENERIC_VERIFY_V2: Authenticode policy (signature valid, chain to a trusted root).</summary>
    public static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public const uint WTD_UI_NONE = 2;
    public const uint WTD_REVOKE_NONE = 0;
    public const uint WTD_CHOICE_FILE = 1;
    public const uint WTD_STATEACTION_IGNORE = 0;
    public const uint WTD_REVOCATION_CHECK_NONE = 0x00000010;
    public const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

    private const string Lib = "wintrust.dll";

    /// <summary>Returns 0 when the subject is trusted, otherwise an HRESULT such as TRUST_E_NOSIGNATURE or CERT_E_UNTRUSTEDROOT.</summary>
    [LibraryImport(Lib)]
    public static unsafe partial int WinVerifyTrust(nint hwnd, Guid* pgActionID, WINTRUST_DATA* pWVTData);
}

/// <summary>WINTRUST_FILE_INFO: the file to verify, by path.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct WINTRUST_FILE_INFO
{
    public uint cbStruct;
    public nint pcwszFilePath;
    public nint hFile;
    public nint pgKnownSubject;

    public static WINTRUST_FILE_INFO Create() => new() { cbStruct = (uint)Unsafe.SizeOf<WINTRUST_FILE_INFO>() };
}

/// <summary>WINTRUST_DATA with the union member <c>pFile</c> (<see cref="Wintrust.WTD_CHOICE_FILE"/>).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct WINTRUST_DATA
{
    public uint cbStruct;
    public nint pPolicyCallbackData;
    public nint pSIPClientData;
    public uint dwUIChoice;
    public uint fdwRevocationChecks;
    public uint dwUnionChoice;
    public nint pFile;
    public uint dwStateAction;
    public nint hWVTStateData;
    public nint pwszURLReference;
    public uint dwProvFlags;
    public uint dwUIContext;
    public nint pSignatureSettings;

    public static WINTRUST_DATA Create() => new() { cbStruct = (uint)Unsafe.SizeOf<WINTRUST_DATA>() };
}
