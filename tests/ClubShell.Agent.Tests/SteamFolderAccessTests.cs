using System.Security.AccessControl;
using System.Security.Principal;
using ClubShell.Agent.Games;

namespace ClubShell.Agent.Tests;

/// <summary>
/// "The Steam install folder is currently not writable": <see cref="SteamFolderAccess"/> grants BUILTIN\Users Modify on a
/// Steam folder that lacks it (as Steam's installer would), inherited by what is already inside, and never on a folder
/// that is not a Steam installation — the catalogue's paths come from the club's owner.
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
        SteamFolderAccess.IsWritableByUsers(_steam).Should().BeFalse();
        SteamFolderAccess.IsWritableByUsers(bin).Should().BeFalse();

        SteamFolderAccess.Grant(_steam).Should().BeTrue();

        SteamFolderAccess.IsWritableByUsers(_steam).Should().BeTrue();
        SteamFolderAccess.IsWritableByUsers(bin).Should().BeTrue("what is already inside inherits the right");
        SteamFolderAccess.Grant(_steam).Should().BeFalse("it is writable already");
    }

    [Fact]
    public void Only_a_Steam_installation_qualifies()
    {
        SteamFolderAccess.IsSteamInstallation(_steam).Should().BeFalse("no steam.exe and steamclient.dll in it");
        SteamFolderAccess.IsSteamInstallation(new DirectoryInfo(Environment.SystemDirectory)).Should().BeFalse();
        SteamFolderAccess.IsSteamInstallation(new DirectoryInfo(Path.GetPathRoot(_steam.FullName)!)).Should().BeFalse();

        File.WriteAllText(Path.Combine(_steam.FullName, "steam.exe"), string.Empty);
        SteamFolderAccess.IsSteamInstallation(_steam).Should().BeFalse("steamclient.dll is missing");
        MakeSteam(_steam);
        SteamFolderAccess.IsSteamInstallation(_steam).Should().BeTrue();
        SteamFolderAccess.IsSteam(@"G:\Steam\Steam.exe").Should().BeTrue();
        SteamFolderAccess.IsSteam(@"G:\Games\cstrike.exe").Should().BeFalse();
    }

    [Fact]
    public void A_network_folder_is_told_from_a_local_one()
    {
        SteamFolderAccess.IsOnNetwork(_steam).Should().BeFalse();
        SteamFolderAccess.IsOnNetwork(new DirectoryInfo(@"\\nas01\games\Steam")).Should().BeTrue();
    }

    public void Dispose() => _steam.Delete(recursive: true);

    private static void MakeSteam(DirectoryInfo folder)
    {
        File.WriteAllText(Path.Combine(folder.FullName, "steam.exe"), string.Empty);
        File.WriteAllText(Path.Combine(folder.FullName, "steamclient.dll"), string.Empty);
    }
}
