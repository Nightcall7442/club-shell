using ClubShell.Agent.Games;
using ClubShell.Contracts.Games;

namespace ClubShell.Agent.Tests;

/// <summary>
/// ICCup and its kind: a launcher that asks for administrator rights and hands over to <c>war3.exe</c>. Which images are
/// the game, which process the launcher handed over to, how the executable is elevated and when that is refused.
/// </summary>
public sealed class GameImagesTests
{
    private static Game Exe(string exe, bool? admin = null, bool? invoker = null, params string[] images) => new(
        Guid.Parse("25fd5de4-08b2-4ebf-b92a-ae470600ae3c"), "DotA (iCCup)", LauncherType.Exe, null, exe, null, null, true, [], [], "", null, null, "", 12, 0, null,
        false, AntiCheatKind.None, null, 1.5, RunAsInvoker: invoker, RunAsAdmin: admin, GameProcessNames: images.Length == 0 ? null : images);

    private static readonly Game Iccup = Exe(@"G:\Warcraft III\iCCup Launcher.exe", admin: true, invoker: null, "war3.exe", " War3 ", "", @"C:\x\Frozen Throne.exe");

    [Fact]
    public void The_game_images_come_first_then_the_launchers_own_name()
    {
        Assert.Equal(["war3", "Frozen Throne"], GameImages.Configured(Iccup));
        Assert.Equal(["war3", "Frozen Throne", "iCCup Launcher"], GameImages.All(Iccup));
        Assert.Equal(["cstrike"], GameImages.All(Exe(@"G:\CS\cstrike.exe")));
        Assert.True(GameImages.Matches("WAR3.EXE", GameImages.All(Iccup)));
        Assert.True(GameImages.Matches(@"G:\Warcraft III\war3.exe", ["war3.exe"]));
        Assert.False(GameImages.Matches("war3helper", GameImages.All(Iccup)));
        Assert.False(GameImages.Matches(null, GameImages.All(Iccup)));
    }

    [Fact]
    public void A_launcher_that_exits_hands_over_to_war3_rather_than_to_its_helper()
    {
        // The launcher left its anti-hack helper and war3.exe in the job.
        Assert.Equal(4400, GameImages.ChooseInJob([(4300, "iccup_ah"), (4400, "war3")], Iccup));

        // Without configured images the executable's own name wins, else the first one left (as before).
        Game cs = Exe(@"G:\CS\cstrike.exe");
        Assert.Equal(20, GameImages.ChooseInJob([(10, "hl"), (20, "cstrike")], cs));
        Assert.Equal(10, GameImages.ChooseInJob([(10, "hl"), (20, null)], cs));
        Assert.Null(GameImages.ChooseInJob([], cs));
    }

    [Fact]
    public void A_launcher_with_game_images_is_waited_for_longer()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), GameImages.SuccessorWait(Iccup));
        Assert.Equal(TimeSpan.FromSeconds(5), GameImages.SuccessorWait(Exe(@"G:\CS\cstrike.exe")));
    }

    [Fact]
    public void Administrator_rights_win_over_run_as_invoker_and_only_for_an_exe_game()
    {
        Assert.Equal(GameElevationMode.Administrator, GameImages.ElevationOf(Exe(@"G:\a.exe", admin: true, invoker: true)));
        Assert.Equal(GameElevationMode.Invoker, GameImages.ElevationOf(Exe(@"G:\a.exe", admin: false, invoker: true)));
        Assert.Equal(GameElevationMode.None, GameImages.ElevationOf(Exe(@"G:\a.exe")));
        Assert.Equal(GameElevationMode.None, GameImages.ElevationOf(Exe(@"G:\a.exe", admin: true) with { Launcher = LauncherType.Steam }));
    }

    [Fact]
    public void An_executable_a_player_could_replace_is_never_run_as_administrator()
    {
        Assert.Null(GameImages.AdminRefusal(@"G:\Warcraft III\iCCup Launcher.exe", @"C:\Users"));
        Assert.Null(GameImages.AdminRefusal(@"C:\Program Files (x86)\iCCup\iCCup Launcher.exe", @"C:\Users"));
        Assert.NotNull(GameImages.AdminRefusal(@"c:\users\kiosk\Downloads\iCCup Launcher.exe", @"C:\Users\"));
        Assert.NotNull(GameImages.AdminRefusal(@"C:\Windows\Temp\x\setup.exe", @"C:\Users"));
        Assert.Null(GameImages.AdminRefusal(@"C:\UsersGames\war3.exe", @"C:\Users"));
    }
}
