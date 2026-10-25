using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ClubShell.Windows.Native;

namespace ClubShell.Windows.Processes;

/// <summary>Authenticode checks of executable files.</summary>
[SupportedOSPlatform("windows")]
public static class Authenticode
{
    /// <summary>
    /// Subject of the certificate that signed <paramref name="path"/> when its embedded signature verifies
    /// (WinVerifyTrust, Authenticode policy: intact file, chain to a trusted root; revocation is not fetched so the
    /// check never waits on the network), otherwise <see langword="null"/> (unsigned, tampered, untrusted, unreadable).
    /// </summary>
    public static unsafe string? VerifiedSigner(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        nint pathChars = Marshal.StringToHGlobalUni(path);
        try
        {
            WINTRUST_FILE_INFO file = WINTRUST_FILE_INFO.Create();
            file.pcwszFilePath = pathChars;
            WINTRUST_DATA data = WINTRUST_DATA.Create();
            data.dwUIChoice = Wintrust.WTD_UI_NONE;
            data.fdwRevocationChecks = Wintrust.WTD_REVOKE_NONE;
            data.dwUnionChoice = Wintrust.WTD_CHOICE_FILE;
            data.pFile = (nint)(&file);
            data.dwStateAction = Wintrust.WTD_STATEACTION_IGNORE;
            data.dwProvFlags = Wintrust.WTD_REVOCATION_CHECK_NONE | Wintrust.WTD_CACHE_ONLY_URL_RETRIEVAL;
            Guid action = Wintrust.WINTRUST_ACTION_GENERIC_VERIFY_V2;
            if (Wintrust.WinVerifyTrust(NativeConst.INVALID_HANDLE_VALUE, &action, &data) != 0)
            {
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pathChars);
        }

        try
        {
            // SYSLIB0057: X509CertificateLoader has no Authenticode counterpart; this only reads the signer of a file
            // WinVerifyTrust has just verified.
#pragma warning disable SYSLIB0057
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return certificate.Subject;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Values of the <c>CN</c> and <c>O</c> attributes of an X.500 subject (<c>CN=Epic Games Inc., O=Epic Games Inc., ...</c>),
    /// quotes removed.
    /// </summary>
    public static IReadOnlyList<string> NamesOf(string subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var names = new List<string>(2);
        foreach (string part in SplitRdns(subject))
        {
            int eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            string key = part[..eq].Trim();
            if (!key.Equals("CN", StringComparison.OrdinalIgnoreCase) && !key.Equals("O", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = part[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
            }

            if (value.Length > 0)
            {
                names.Add(value);
            }
        }

        return names;
    }

    /// <summary>Splits a subject on the commas between RDNs, keeping commas inside quoted values (<c>O="Riot Games, Inc."</c>).</summary>
    private static IEnumerable<string> SplitRdns(string subject)
    {
        int start = 0;
        bool quoted = false;
        for (int i = 0; i < subject.Length; i++)
        {
            char c = subject[i];
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                yield return subject[start..i];
                start = i + 1;
            }
        }

        yield return subject[start..];
    }
}
