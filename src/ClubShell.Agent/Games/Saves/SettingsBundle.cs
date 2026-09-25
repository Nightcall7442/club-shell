using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ClubShell.Agent.Games.Accounts;
using ClubShell.Contracts.Games;

namespace ClubShell.Agent.Games.Saves;

/// <summary>One resolved <see cref="Game.SettingsPaths"/> entry.</summary>
/// <param name="Key">Stable id of the template (hash of its text), so editing the list never sends a file to another path.</param>
/// <param name="Path">Absolute file or directory path.</param>
/// <param name="Anchor">
/// The trusted directory the template was expanded from (the kiosk profile folder or the install path); nothing between
/// it and <paramref name="Path"/> may be a reparse point.
/// </param>
public readonly record struct SettingsTarget(string Key, string Path, string Anchor);

/// <summary>
/// The zip format of a player's game settings, platform-neutral (unit-tested on any OS). Entries are
/// <c>&lt;key&gt;/f/&lt;name&gt;</c> for a file template and <c>&lt;key&gt;/d/&lt;relative path&gt;</c> for a directory
/// template. The Agent runs as LocalSystem while these paths sit in folders the kiosk user controls, so every read,
/// write and delete refuses reparse points (junctions, symlinks) between the trusted anchor and the file: a player
/// cannot point the Agent at a file outside their own settings.
/// </summary>
public static class SettingsBundle
{
    private static readonly EnumerationOptions SafeEnumeration = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Device,
        IgnoreInaccessible = true,
    };

    /// <summary>Stable key of a template: the first 10 hex chars of SHA-256 of its trimmed, lower-cased text.</summary>
    public static string KeyOf(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(template.Trim().ToLowerInvariant()));
        return Convert.ToHexString(hash)[..10].ToLowerInvariant();
    }

    /// <summary>Expands one template; <see langword="null"/> when it cannot be resolved to an absolute path.</summary>
    public static SettingsTarget? Expand(string template, Game game, IKioskProfilePaths profile)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(profile);
        string t = template.Trim();
        if (t.Length == 0 || t.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        (string Token, string? Value)[] anchors =
        [
            ("%LOCALAPPDATA%", profile.LocalAppData),
            ("%APPDATA%", profile.RoamingAppData),
            ("%USERPROFILE%", profile.UserProfile),
            ("{installPath}", game.InstallPath),
        ];
        string? anchor = null;
        foreach ((string token, string? value) in anchors)
        {
            if (t.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                anchor = value;
                t = value + t[token.Length..];
                break;
            }
        }

        t = t.Replace('\\', Path.DirectorySeparatorChar);
        if (!Path.IsPathRooted(t) || t.Contains('%', StringComparison.Ordinal) || t.Contains('{', StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(t));
            string root = anchor is null
                ? Path.GetPathRoot(full) ?? full
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(anchor.Replace('\\', Path.DirectorySeparatorChar)));
            // ".." must not climb out of the folder the template is anchored to.
            if (!IsUnder(full, root))
            {
                return null;
            }

            return new SettingsTarget(KeyOf(template), full, root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>The resolved targets of <paramref name="game"/>'s settings paths (unresolvable ones are left out, duplicates collapsed).</summary>
    public static IReadOnlyList<SettingsTarget> Targets(Game game, IKioskProfilePaths profile)
    {
        ArgumentNullException.ThrowIfNull(game);
        var list = new List<SettingsTarget>();
        foreach (string template in game.SettingsPaths ?? Array.Empty<string>())
        {
            if (Expand(template, game, profile) is { } target && list.TrueForAll(x => x.Key != target.Key))
            {
                list.Add(target);
            }
        }

        return list;
    }

    /// <summary>
    /// Writes the existing targets into a zip at <paramref name="zipPath"/>. Returns the number of files packed, or -1 when
    /// they add up to more than <paramref name="maxBytes"/> (nothing is written then: a misconfigured path such as a whole
    /// profile folder is refused before it is compressed). With 0 files no zip is written unless
    /// <paramref name="writeEmpty"/> (a baseline snapshot must record "nothing was there").
    /// </summary>
    public static int Pack(IReadOnlyList<SettingsTarget> targets, string zipPath, long maxBytes, bool writeEmpty = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var files = new List<(string Entry, string Source)>();
        long total = 0;
        foreach (SettingsTarget target in targets)
        {
            if (!IsSafe(target.Path, target.Anchor))
            {
                continue;
            }

            if (File.Exists(target.Path))
            {
                total += new FileInfo(target.Path).Length;
                files.Add(($"{target.Key}/f/{Path.GetFileName(target.Path)}", target.Path));
            }
            else if (Directory.Exists(target.Path))
            {
                foreach (string file in Directory.EnumerateFiles(target.Path, "*", SafeEnumeration))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    total += new FileInfo(file).Length;
                    if (total > maxBytes)
                    {
                        return -1;
                    }

                    string relative = Path.GetRelativePath(target.Path, file).Replace(Path.DirectorySeparatorChar, '/');
                    files.Add(($"{target.Key}/d/{relative}", file));
                }
            }

            if (total > maxBytes)
            {
                return -1;
            }
        }

        if (files.Count == 0 && !writeEmpty)
        {
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath))!);
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach ((string entry, string source) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            zip.CreateEntryFromFile(source, entry, CompressionLevel.Optimal);
        }

        return files.Count;
    }

    /// <summary>
    /// Restores a zip written by <see cref="Pack"/>. File entries land only on a file target of the same name, directory
    /// entries under their directory target. Entries of an unknown key, of the wrong kind, escaping their target, or
    /// crossing a reparse point are skipped. Returns the number of files written.
    /// </summary>
    public static int Unpack(string zipPath, IReadOnlyList<SettingsTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var byKey = targets.ToDictionary(t => t.Key);
        int written = 0;
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }

            string[] parts = entry.FullName.Split('/', 3);
            if (parts.Length != 3 || !byKey.TryGetValue(parts[0], out SettingsTarget target))
            {
                continue;
            }

            string destination;
            if (parts[1] == "f")
            {
                if (!string.Equals(parts[2], Path.GetFileName(target.Path), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                destination = target.Path;
            }
            else if (parts[1] == "d")
            {
                destination = Path.GetFullPath(Path.Combine(target.Path, parts[2].Replace('/', Path.DirectorySeparatorChar)));
                if (!IsUnder(destination, target.Path) || destination.Length == target.Path.Length)
                {
                    continue;
                }
            }
            else
            {
                continue;
            }

            if (!IsSafe(destination, target.Anchor))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            written++;
        }

        return written;
    }

    /// <summary>
    /// Removes what is at the targets (a file target's file, a directory target's folder), so a baseline snapshot can be put
    /// back without the previous player's extra files. Targets behind a reparse point are left alone; a reparse point
    /// inside a directory target is removed as a link, never followed.
    /// </summary>
    public static void Clear(IReadOnlyList<SettingsTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        foreach (SettingsTarget target in targets)
        {
            if (!IsSafe(target.Path, target.Anchor))
            {
                continue;
            }

            if (File.Exists(target.Path))
            {
                File.Delete(target.Path);
            }
            else if (Directory.Exists(target.Path))
            {
                Directory.Delete(target.Path, recursive: true);
            }
        }
    }

    /// <summary><see langword="true"/> when nothing from <paramref name="anchor"/> (exclusive) down to <paramref name="path"/> (inclusive) is a reparse point.</summary>
    public static bool IsSafe(string path, string anchor)
    {
        string current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(anchor));
        if (!IsUnder(current, root))
        {
            return false;
        }

        while (current.Length > root.Length)
        {
            var info = new FileInfo(current);
            if (info.Exists || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
            }

            string? parent = Path.GetDirectoryName(current);
            if (parent is null)
            {
                break;
            }

            current = parent;
        }

        return true;
    }

    private static bool IsUnder(string path, string root)
    {
        string p = Path.TrimEndingDirectorySeparator(path);
        string r = Path.TrimEndingDirectorySeparator(root);
        // A drive / file-system root keeps its separator after trimming ("C:\", "/").
        string prefix = Path.EndsInDirectorySeparator(r) ? r : r + Path.DirectorySeparatorChar;
        return p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
