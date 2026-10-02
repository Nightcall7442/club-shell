using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using ClubShell.Agent.Games;

namespace ClubShell.Agent.Tests;

/// <summary>
/// "The Steam install folder is currently not writable": <see cref="SteamFolderAccess"/> grants BUILTIN\Users Modify on a
/// Steam folder that lacks it (as Steam's installer would), inherited by what is already inside — and only where the
/// grant, made as LocalSystem by name, cannot be steered by a player.
/// </summary>
public sealed class SteamFolderAccessTests : IDisposable
{
    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    private readonly DirectoryInfo _steam = Directory.CreateTempSubdirectory("clubshell-steam-");

    [Fact]
    public void A_Steam_folder_the_players_cannot_write_gets_the_right_and_its_contents_inherit_it()
    {
        // Like Steam copied in by an administrator: nothing inherited, the players may only read.
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));
        _steam.SetAccessControl(acl);
        DirectoryInfo bin = _steam.CreateSubdirectory("bin");
        MakeSteam(_steam);

        SteamFolderAccess.IsSteamInstallation(_steam).Should().BeTrue();
        SteamFolderAccess.IsWritableByPlayers(_steam).Should().BeFalse();
        SteamFolderAccess.IsWritableByPlayers(bin).Should().BeFalse();

        SteamFolderAccess.Grant(_steam).Should().BeTrue();

        SteamFolderAccess.IsWritableByPlayers(_steam).Should().BeTrue();
        SteamFolderAccess.IsWritableByPlayers(bin).Should().BeTrue("what is already inside inherits the right");
        SteamFolderAccess.Grant(_steam).Should().BeFalse("it is writable already");
    }

    [Fact]
    public void A_Steam_installation_has_steam_exe_and_steamclient_dll()
    {
        SteamFolderAccess.IsSteamInstallation(_steam).Should().BeFalse();
        File.WriteAllText(Path.Combine(_steam.FullName, "steam.exe"), string.Empty);
        SteamFolderAccess.IsSteamInstallation(_steam).Should().BeFalse("steamclient.dll is missing");
        MakeSteam(_steam);
        SteamFolderAccess.IsSteamInstallation(_steam).Should().BeTrue();
        SteamFolderAccess.IsSteam(@"C:\Program Files (x86)\Steam\Steam.exe").Should().BeTrue();
        SteamFolderAccess.IsSteam(@"G:\Games\cstrike.exe").Should().BeFalse();
    }

    [Fact]
    public void Only_a_folder_no_player_can_steer_is_safe_to_grant()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        SteamFolderAccess.IsSafeToGrant(new DirectoryInfo(programFiles), out string reason).Should().BeTrue(reason);

        SteamFolderAccess.IsSafeToGrant(new DirectoryInfo(Environment.SystemDirectory), out reason).Should().BeFalse();
        reason.Should().Be("inside Windows");
        SteamFolderAccess.IsSafeToGrant(new DirectoryInfo(Path.GetPathRoot(programFiles)!), out reason).Should().BeFalse();
        reason.Should().Be("a drive root");

        // A folder in a player's own profile: they own it, they could swap it for a junction.
        SteamFolderAccess.IsSafeToGrant(_steam, out reason).Should().BeFalse();
        reason.Should().Contain("not owned by");

        // A junction on the way is refused whatever it points at.
        string junction = Path.Combine(_steam.FullName, "link");
        string target = _steam.CreateSubdirectory("target").FullName;
        using (Process mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!)
        {
            mklink.WaitForExit(10_000).Should().BeTrue();
        }

        SteamFolderAccess.IsSafeToGrant(new DirectoryInfo(junction), out reason).Should().BeFalse();
        reason.Should().Contain("junction or symlink");
    }

    [Fact]
    public void A_network_folder_is_told_from_a_local_one()
    {
        SteamFolderAccess.IsOnNetwork(_steam).Should().BeFalse();
        SteamFolderAccess.IsOnNetwork(new DirectoryInfo(@"\\nas01\games\Steam")).Should().BeTrue();
    }

    public void Dispose()
    {
        // The junction goes without its target: Directory.Delete removes a junction, never what it points at.
        foreach (DirectoryInfo link in _steam.EnumerateDirectories().Where(d => (d.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            link.Delete();
        }

        _steam.Delete(recursive: true);
    }

    private static void MakeSteam(DirectoryInfo folder)
    {
        File.WriteAllText(Path.Combine(folder.FullName, "steam.exe"), string.Empty);
        File.WriteAllText(Path.Combine(folder.FullName, "steamclient.dll"), string.Empty);
    }
}
