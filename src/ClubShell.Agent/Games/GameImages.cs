using ClubShell.Contracts.Games;

namespace ClubShell.Agent.Games;

/// <summary>How a catalogue game's executable is started with respect to its administrator manifest.</summary>
public enum GameElevationMode
{
    /// <summary>As the player; an exe asking for administrator rights fails with error 740 (<c>stage: elevation</c>).</summary>
    None,

    /// <summary>«Запускать без прав администратора»: as the player with the <c>RunAsInvoker</c> compatibility layer.</summary>
    Invoker,

    /// <summary>«Запускать с правами администратора»: with an elevated token in the player's session.</summary>
    Administrator,
}

/// <summary>
/// The pure parts of starting and following an exe game (ICCup and its kind): which image names are the game, which
/// process a launcher handed over to, and how the executable is elevated.
/// </summary>
public static class GameImages
{
    /// <summary>At most this many <see cref="Game.GameProcessNames"/> are honoured.</summary>
    public const int MaxNames = 10;

    /// <summary>Bare image name (no directory, no <c>.exe</c>) of <paramref name="name"/>, or <see langword="null"/> for a blank one.</summary>
    public static string? Bare(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string file = name.Trim().Trim('"');
        int slash = file.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            file = file[(slash + 1)..];
        }

        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            file = file[..^4];
        }

        file = file.Trim();
        return file.Length == 0 ? null : file;
    }

    /// <summary>
    /// The game's own image names as the catalogue gives them (<see cref="Game.GameProcessNames"/>, bare, distinct,
    /// at most <see cref="MaxNames"/>); empty when it names none.
    /// </summary>
    public static IReadOnlyList<string> Configured(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.GameProcessNames is not { Count: > 0 } names)
        {
            return [];
        }

        return names.Select(Bare).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxNames).ToList();
    }

    /// <summary>
    /// Every image name that is the game: the configured game images first (<c>war3</c>), then the catalogue
    /// executable's own (<c>iCCup Launcher</c>).
    /// </summary>
    public static IReadOnlyList<string> All(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var names = new List<string>(Configured(game));
        if (Bare(game.ExePath) is { } exe && !names.Contains(exe, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(exe);
        }

        return names;
    }

    /// <summary><see langword="true"/> when <paramref name="imageName"/> (with or without a path or <c>.exe</c>) is one of <paramref name="names"/>.</summary>
    public static bool Matches(string? imageName, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        string? bare = Bare(imageName);
        return bare is not null && names.Any(n => string.Equals(Bare(n), bare, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Which of the processes left in the game's job a launcher that exited handed over to: one with a configured game
    /// image name first, then one with any of the game's names, else the first; <see langword="null"/> for none.
    /// </summary>
    public static int? ChooseInJob(IReadOnlyList<(int Pid, string? Name)> inJob, Game game)
    {
        ArgumentNullException.ThrowIfNull(inJob);
        if (inJob.Count == 0)
        {
            return null;
        }

        IReadOnlyList<string> configured = Configured(game);
        IReadOnlyList<string> all = All(game);
        foreach (IReadOnlyList<string> names in new[] { configured, all })
        {
            foreach ((int pid, string? name) in inJob)
            {
                if (names.Count > 0 && Matches(name, names))
                {
                    return pid;
                }
            }
        }

        return inJob[0].Pid;
    }

    /// <summary>How long after the game's process exits its successor is looked for: longer when the catalogue names the game's images (a launcher may take a while to start <c>war3.exe</c>).</summary>
    public static TimeSpan SuccessorWait(Game game) => Configured(game).Count > 0 ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(5);

    /// <summary>The elevation the catalogue asks for: only an exe game starts its own process; administrator rights win over <c>RunAsInvoker</c>.</summary>
    public static GameElevationMode ElevationOf(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.Launcher != LauncherType.Exe)
        {
            return GameElevationMode.None;
        }

        return game.RunAsAdmin == true ? GameElevationMode.Administrator
            : game.RunAsInvoker == true ? GameElevationMode.Invoker
            : GameElevationMode.None;
    }

    /// <summary>
    /// Why <paramref name="exe"/> must not be started with administrator rights, or <see langword="null"/> when it may:
    /// an executable inside the user profiles (<paramref name="profilesRoot"/>, <c>C:\Users</c>) or a temp folder can be
    /// replaced by a player, and would then run with the rights the owner gave the game.
    /// </summary>
    public static string? AdminRefusal(string exe, string profilesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exe);
        ArgumentException.ThrowIfNullOrWhiteSpace(profilesRoot);
        string path = exe.Replace('/', '\\');
        string root = profilesRoot.Replace('/', '\\').TrimEnd('\\') + "\\";
        if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return $"{exe} is inside the user profiles ({profilesRoot}), which players can write to";
        }

        if (path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\Tmp\", StringComparison.OrdinalIgnoreCase))
        {
            return $"{exe} is in a temp folder, which players can write to";
        }

        return null;
    }
}
