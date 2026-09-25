using System.IO.Compression;
using ClubShell.Agent.Games.Accounts;
using ClubShell.Contracts.Games;

namespace ClubShell.Agent.Games.Saves;

/// <summary>
/// The zip format of a player's game settings: one folder per <see cref="Game.SettingsPaths"/> entry, named by its index
/// and the kind of target — <c>0/f/autoexec.cfg</c> for a file template, <c>1/d/Saved/Config/Input.ini</c> for a directory
/// template — so both travel in one bundle and land back exactly where they came from. Platform-neutral (no Win32), so it is unit-tested
/// on any OS.
/// </summary>
public static class SettingsBundle
{
    /// <summary>Expands one template; <see langword="null"/> when it cannot be resolved to an absolute path.</summary>
    public static string? Expand(string template, Game game, IKioskProfilePaths profile)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        if (template.Contains("{installPath}", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(game.InstallPath))
        {
            return null;
        }

        string expanded = template
            .Replace("%LOCALAPPDATA%", profile.LocalAppData, StringComparison.OrdinalIgnoreCase)
            .Replace("%APPDATA%", profile.RoamingAppData, StringComparison.OrdinalIgnoreCase)
            .Replace("%USERPROFILE%", profile.UserProfile, StringComparison.OrdinalIgnoreCase)
            .Replace("{installPath}", game.InstallPath ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace('\\', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(expanded) && !expanded.Contains('%', StringComparison.Ordinal) ? Path.GetFullPath(expanded) : null;
    }

    /// <summary>The resolved targets of <paramref name="game"/>'s settings paths, by index (unresolvable ones are left out).</summary>
    public static IReadOnlyList<(int Index, string Path)> Targets(Game game, IKioskProfilePaths profile)
    {
        ArgumentNullException.ThrowIfNull(game);
        var list = new List<(int, string)>();
        IReadOnlyList<string> templates = game.SettingsPaths ?? Array.Empty<string>();
        for (int i = 0; i < templates.Count; i++)
        {
            if (Expand(templates[i], game, profile) is { } path)
            {
                list.Add((i, path));
            }
        }

        return list;
    }

    /// <summary>
    /// Writes the existing targets into a zip at <paramref name="zipPath"/>. Returns the number of files packed; 0 means
    /// there was nothing to carry and no zip was written.
    /// </summary>
    public static int Pack(IReadOnlyList<(int Index, string Path)> targets, string zipPath)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var files = new List<(string Entry, string Source)>();
        foreach ((int index, string path) in targets)
        {
            if (File.Exists(path))
            {
                files.Add(($"{index}/f/{Path.GetFileName(path)}", path));
            }
            else if (Directory.Exists(path))
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(path, file).Replace(Path.DirectorySeparatorChar, '/');
                    files.Add(($"{index}/d/{relative}", file));
                }
            }
        }

        if (files.Count == 0)
        {
            return 0;
        }

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach ((string entry, string source) in files)
        {
            zip.CreateEntryFromFile(source, entry, CompressionLevel.Optimal);
        }

        return files.Count;
    }

    /// <summary>
    /// Restores a zip written by <see cref="Pack"/>: entries of a file target overwrite that file, entries of a directory
    /// target land under it. Entries of an index no longer in <paramref name="targets"/>, and any entry that would escape
    /// its target (<c>..</c>), are skipped. Returns the number of files written.
    /// </summary>
    public static int Unpack(string zipPath, IReadOnlyList<(int Index, string Path)> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var byIndex = targets.ToDictionary(t => t.Index, t => t.Path);
        int written = 0;
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }

            string[] parts = entry.FullName.Split('/', 3);
            if (parts.Length != 3 || !int.TryParse(parts[0], out int index) || !byIndex.TryGetValue(index, out string? target))
            {
                continue;
            }

            bool isFileTarget = parts[1] == "f";
            if (!isFileTarget && parts[1] != "d")
            {
                continue;
            }

            string relative = parts[2].Replace('/', Path.DirectorySeparatorChar);
            string root = isFileTarget ? Path.GetDirectoryName(target)! : target;
            string destination = Path.GetFullPath(isFileTarget ? target : Path.Combine(root, relative));
            string rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            written++;
        }

        return written;
    }
}
