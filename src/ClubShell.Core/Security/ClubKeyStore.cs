using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ClubShell.Core.Security;

/// <summary>
/// The club key (<c>X-Club-Key</c>, needed only by <c>POST /agents/register</c>) at rest in <c>secure\club.key</c>
/// (SHELL_CHANGES 41): DPAPI-wrapped like the agent tokens, in the folder only SYSTEM and Administrators can read, instead
/// of <c>agent.json</c>, which every local account can read. The installer still writes the key into <c>agent.json</c>;
/// the Agent moves it here at start (<c>ClubKeyMigration</c>) and blanks it there.
/// </summary>
public sealed class ClubKeyStore
{
    private readonly string _filePath;
    private readonly ITokenProtector _protector;
    private readonly ILogger<ClubKeyStore> _logger;

    /// <summary>Creates a store over <paramref name="filePath"/>.</summary>
    public ClubKeyStore(string filePath, ITokenProtector protector, ILogger<ClubKeyStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(logger);
        _filePath = filePath;
        _protector = protector;
        _logger = logger;
    }

    /// <summary>Path of the key file.</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// The stored key, or <see langword="null"/> when there is none or it cannot be decrypted (DPAPI machine scope: a
    /// file copied from another PC does not open; <c>prepare-image.ps1</c> puts the key back into <c>agent.json</c> for
    /// that reason).
    /// </summary>
    public string? Read()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            byte[] plaintext = _protector.Unprotect(File.ReadAllBytes(_filePath));
            try
            {
                string key = Encoding.UTF8.GetString(plaintext);
                return key.Length > 0 ? key : null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(
                ex, "Club key file {Path} is unreadable (copied from another PC?); give the key again in agent.json -> server.clubApiKey or with CLUBAPIKEY", _filePath);
            return null;
        }
    }

    /// <summary>Writes <paramref name="key"/> (atomic); <see langword="false"/> when it could not be written, which is logged.</summary>
    public bool Store(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        byte[] plaintext = Encoding.UTF8.GetBytes(key);
        try
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = _filePath + ".tmp";
            File.WriteAllBytes(temp, _protector.Protect(plaintext));
            File.Move(temp, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not store the club key in {Path}", _filePath);
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
