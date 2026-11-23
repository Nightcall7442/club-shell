using ClubShell.Agent.Games;
using ClubShell.Contracts.Games;

namespace ClubShell.Agent.Tests;

/// <summary>
/// Which processes are an app's (apps.running / apps.kill / the "already running" check of apps.launch): the started one,
/// its descendants and the app's own executables, never a game's, the Shell's or Windows' own.
/// </summary>
public sealed class RunningAppsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 18, 0, 0, TimeSpan.Zero);
    private static readonly HashSet<int> NoGames = [];

    private static readonly App Discord = new(Guid.Parse("00000000-0000-0000-0006-000000000002"), "Discord",
        @"C:\Users\kiosk\AppData\Local\Discord\Update.exe", "", "voice", true);

    private static readonly App Chrome = new(Guid.Parse("00000000-0000-0000-0006-000000000001"), "Google Chrome",
        @"C:\Program Files\Google\Chrome\Application\chrome.exe", "", "browser", true);

    private static readonly App Steam = new(Guid.Parse("00000000-0000-0000-0006-000000000003"), "Steam",
        @"C:\Program Files (x86)\Steam\steam.exe", "", "tool", true);

    private static readonly App Notepad = new(Guid.Parse("00000000-0000-0000-0006-000000000004"), "Notepad",
        @"C:\Windows\System32\notepad.exe", "", "tool", true);

    private static AppProcess P(int pid, int parent, string image, int sec = 0) => new(pid, parent, image, T0.AddSeconds(sec));

    [Fact]
    public void A_launcher_that_hands_over_and_exits_still_leaves_its_app_running()
    {
        // Update.exe (gone) started app-1.0.9163\Discord.exe, which started its helpers.
        AppProcess[] session =
        [
            P(6120, 5000, @"C:\Users\kiosk\AppData\Local\Discord\app-1.0.9163\Discord.exe", 1),
            P(6121, 6120, @"C:\Users\kiosk\AppData\Local\Discord\app-1.0.9163\Discord.exe", 2),
            P(7344, 900, @"C:\Program Files\Google\Chrome\Application\chrome.exe", 3),
            P(900, 4, @"C:\Windows\explorer.exe"),
        ];

        RunningApps.Match(Discord, null, session, NoGames).Should().Equal(6120, 6121);
        RunningApps.Match(Chrome, null, session, NoGames).Should().Equal(7344);
    }

    [Fact]
    public void The_started_process_and_its_descendants_count_wherever_they_live()
    {
        var launch = new RunningApps.Launch(Steam.Id, 3000, T0);
        AppProcess[] session =
        [
            P(3000, 900, @"C:\Program Files (x86)\Steam\steam.exe"),
            P(3001, 3000, @"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe", 2),
            P(3002, 3001, @"C:\Temp\helper.exe", 3),
            // An older process with a recycled parent pid is not a child.
            P(2999, 3000, @"C:\Temp\older.exe", -60),
        ];

        RunningApps.Match(Steam, launch, session, NoGames).Should().Equal(3000, 3001, 3002);
    }

    [Fact]
    public void A_game_steam_started_is_never_steams_so_closing_steam_spares_it()
    {
        AppProcess[] session =
        [
            P(3000, 900, @"C:\Program Files (x86)\Steam\steam.exe"),
            P(4100, 3000, @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe", 5),
            P(4101, 4100, @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\crash.exe", 6),
        ];

        RunningApps.Match(Steam, null, session, new HashSet<int> { 4100 }).Should().Equal(3000);
    }

    [Fact]
    public void A_shared_folder_owns_nothing_and_the_shell_agent_and_windows_are_never_an_app()
    {
        RunningApps.OwnFolder(Notepad.ExePath).Should().BeNull();
        RunningApps.OwnFolder(@"C:\Program Files\tool.exe").Should().BeNull();
        RunningApps.OwnFolder(@"D:\Apps\tool.exe").Should().BeNull();
        RunningApps.OwnFolder(@"D:\Games\tool.exe").Should().BeNull();
        RunningApps.OwnFolder(Chrome.ExePath).Should().Be(@"c:\program files\google\chrome\application");

        AppProcess[] session =
        [
            P(500, 400, @"C:\Windows\System32\notepad.exe"),
            P(501, 400, @"C:\Windows\System32\cmd.exe"),
            P(502, 500, @"C:\Windows\System32\conhost.exe"),
            P(503, 500, @"C:\Program Files\ClubShell\clubshell-shell.exe"),
            P(504, 500, @"C:\Windows\explorer.exe"),
        ];
        RunningApps.Match(Notepad, null, session, NoGames).Should().Equal(500);
    }

    [Fact]
    public void Resolve_lists_each_running_app_once_with_its_launch_and_forgets_finished_launches()
    {
        var apps = new RunningApps();
        apps.Record(Chrome.Id, 7344, T0.AddSeconds(3));
        apps.Record(Steam.Id, 3000, T0);
        AppProcess[] session =
        [
            P(7344, 900, @"C:\Program Files\Google\Chrome\Application\chrome.exe", 3),
            P(7345, 7344, @"C:\Program Files\Google\Chrome\Application\chrome.exe", 4),
            P(6120, 5000, @"C:\Users\kiosk\AppData\Local\Discord\app-1.0.9163\Discord.exe", 1),
        ];

        var running = apps.Resolve([Chrome, Discord, Steam], session, NoGames);

        running.Select(r => r.Title).Should().Equal("Discord", "Google Chrome");
        running[1].Pid.Should().Be(7344);
        running[1].Pids.Should().Equal(7344, 7345);
        running[1].StartedAt.Should().Be(T0.AddSeconds(3));
        apps.LaunchOf(Steam.Id).Should().BeNull("Steam is gone: its launch is forgotten");
        apps.LaunchOf(Chrome.Id).Should().NotBeNull();
    }

    [Fact]
    public void A_recycled_pid_is_not_the_launch()
    {
        var launch = new RunningApps.Launch(Steam.Id, 3000, T0);
        AppProcess[] session = [P(3000, 900, @"C:\Temp\someone-else.exe", 600)];

        RunningApps.Match(Steam, launch, session, NoGames).Should().BeEmpty();
    }
}
