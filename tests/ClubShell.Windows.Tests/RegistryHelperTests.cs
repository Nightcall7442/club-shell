using ClubShell.Windows.Registry;
using Microsoft.Win32;

namespace ClubShell.Windows.Tests;

/// <summary>
/// Exercises <see cref="RegistryHelper"/> under <c>HKCU\Software\ClubShellTests\&lt;guid&gt;</c> (no elevation needed);
/// the whole sub-tree is removed on dispose.
/// </summary>
public sealed class RegistryHelperTests : IDisposable
{
    private const RegistryHive Hive = RegistryHive.CurrentUser;
    private const string Parent = @"Software\ClubShellTests";

    private readonly string _root = Parent + "\\" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _ = RegistryHelper.DeleteKey(Hive, _root, recursive: true);
        using RegistryKey? parent = RegistryHelper.Open(Hive, Parent, writable: false);
        if (parent is { SubKeyCount: 0, ValueCount: 0 })
        {
            parent.Dispose();
            _ = RegistryHelper.DeleteKey(Hive, Parent, recursive: false);
        }
    }

    [WindowsFact]
    public void View_IsAlways64Bit()
    {
        RegistryHelper.View.Should().Be(RegistryView.Registry64);
    }

    [WindowsFact]
    public void EnsureKey_CreatesTheKey_AndExistsSeesIt()
    {
        string key = Sub("Created");
        RegistryHelper.Exists(Hive, key).Should().BeFalse();

        using (RegistryKey created = RegistryHelper.EnsureKey(Hive, key))
        {
            created.Should().NotBeNull();
        }

        RegistryHelper.Exists(Hive, key).Should().BeTrue();
        using (RegistryKey again = RegistryHelper.EnsureKey(Hive, key))
        {
            again.Should().NotBeNull("EnsureKey is idempotent");
        }

        using RegistryKey? opened = RegistryHelper.Open(Hive, key, writable: false);
        opened.Should().NotBeNull();
    }

    [WindowsFact]
    public void SetAndGet_RoundTripEveryValueKind()
    {
        string key = Sub("Typed");
        byte[] blob = [0x01, 0x02, 0xFE, 0xFF];
        string[] lines = ["first", "second", "третий"];

        RegistryHelper.Set(Hive, key, "String", "hello ClubShell", RegistryValueKind.String);
        RegistryHelper.Set(Hive, key, "Dword", 0x7FFF_0001, RegistryValueKind.DWord);
        RegistryHelper.Set(Hive, key, "Qword", 0x1234_5678_9ABC_DEF0L, RegistryValueKind.QWord);
        RegistryHelper.Set(Hive, key, "Multi", lines, RegistryValueKind.MultiString);
        RegistryHelper.Set(Hive, key, "Binary", blob, RegistryValueKind.Binary);
        RegistryHelper.Set(Hive, key, "Expand", "%SystemRoot%\\notepad.exe", RegistryValueKind.ExpandString);

        RegistryHelper.Get<string>(Hive, key, "String").Should().Be("hello ClubShell");
        RegistryHelper.Get<int>(Hive, key, "Dword").Should().Be(0x7FFF_0001);
        RegistryHelper.Get<long>(Hive, key, "Qword").Should().Be(0x1234_5678_9ABC_DEF0L);
        RegistryHelper.Get<string[]>(Hive, key, "Multi").Should().Equal(lines);
        RegistryHelper.Get<byte[]>(Hive, key, "Binary").Should().Equal(blob);
        RegistryHelper.Get<string>(Hive, key, "Expand").Should().Be(Environment.ExpandEnvironmentVariables("%SystemRoot%\\notepad.exe"), "Get expands environment variables");

        RegistryHelper.GetValue(Hive, key, "Expand", out RegistryValueKind expandKind, expandEnvironment: false).Should().Be("%SystemRoot%\\notepad.exe");
        expandKind.Should().Be(RegistryValueKind.ExpandString);
        RegistryHelper.GetValue(Hive, key, "Dword", out RegistryValueKind dwordKind).Should().Be(0x7FFF_0001);
        dwordKind.Should().Be(RegistryValueKind.DWord);
        RegistryHelper.GetValue(Hive, key, "Multi", out RegistryValueKind multiKind).Should().BeEquivalentTo(lines);
        multiKind.Should().Be(RegistryValueKind.MultiString);

        RegistryHelper.ValueExists(Hive, key, "Binary").Should().BeTrue();
        RegistryHelper.ValueExists(Hive, key, "Nope").Should().BeFalse();
    }

    [WindowsFact]
    public void Get_ReturnsTheDefault_WhenAbsentOrOfAnotherType()
    {
        string key = Sub("Defaults");
        RegistryHelper.Set(Hive, key, "Dword", 5, RegistryValueKind.DWord);

        RegistryHelper.Get<int>(Hive, key, "Missing", -1).Should().Be(-1);
        RegistryHelper.Get<string>(Hive, key, "Missing").Should().BeNull();
        RegistryHelper.Get<string>(Hive, key, "Dword", "fallback").Should().Be("fallback", "a DWORD is not a string");
        RegistryHelper.Get<long>(Hive, key, "Dword", 99).Should().Be(99, "a DWORD reads as int, not long");
        RegistryHelper.Get<int>(Hive, Sub("NoSuchKey"), "Dword", 7).Should().Be(7);
    }

    [WindowsFact]
    public void MissingKey_IsHandledEverywhere()
    {
        string key = Sub("Missing");

        RegistryHelper.Exists(Hive, key).Should().BeFalse();
        RegistryHelper.ValueExists(Hive, key, "x").Should().BeFalse();
        RegistryHelper.Open(Hive, key, writable: false).Should().BeNull();
        RegistryHelper.GetValue(Hive, key, "x", out RegistryValueKind kind).Should().BeNull();
        kind.Should().Be(RegistryValueKind.None);
        RegistryHelper.DeleteValue(Hive, key, "x").Should().BeFalse();
        RegistryHelper.DeleteKey(Hive, key, recursive: true).Should().BeFalse();

        RegistryValueSnapshot snapshot = RegistryHelper.SnapshotValue(Hive, key, "x");
        snapshot.KeyExisted.Should().BeFalse();
        snapshot.Value.Should().BeNull();
        snapshot.Kind.Should().Be(RegistryValueKind.None);
        RegistryHelper.SnapshotKey(Hive, key).Values.Should().BeEmpty();
    }

    [WindowsFact]
    public void DeleteValueAndDeleteKey_ReportWhetherSomethingWasRemoved()
    {
        string key = Sub("Delete");
        RegistryHelper.Set(Hive, key, "Value", "x", RegistryValueKind.String);
        RegistryHelper.Set(Hive, key + "\\Child", "Value", "y", RegistryValueKind.String);

        RegistryHelper.DeleteValue(Hive, key, "Value").Should().BeTrue();
        RegistryHelper.DeleteValue(Hive, key, "Value").Should().BeFalse("already gone");
        RegistryHelper.ValueExists(Hive, key, "Value").Should().BeFalse();

        RegistryHelper.DeleteKey(Hive, key, recursive: true).Should().BeTrue();
        RegistryHelper.Exists(Hive, key).Should().BeFalse();
        RegistryHelper.Exists(Hive, key + "\\Child").Should().BeFalse("the sub-tree went with it");
        RegistryHelper.DeleteKey(Hive, key, recursive: true).Should().BeFalse("already gone");
    }

    [WindowsFact]
    public void Snapshot_ThenModify_ThenApply_RestoresExactly()
    {
        string key = Sub("Snapshot");
        string missingKey = Sub("Snapshot") + "\\WasMissing\\Deeper";
        RegistryHelper.Set(Hive, key, "Text", "original", RegistryValueKind.String);
        RegistryHelper.Set(Hive, key, "Number", 5, RegistryValueKind.DWord);
        RegistryHelper.Set(Hive, key, "Expand", "%TEMP%", RegistryValueKind.ExpandString);

        RegistrySnapshot ofKey = RegistryHelper.SnapshotKey(Hive, key);
        RegistrySnapshot ofTargets = RegistryHelper.Snapshot(
        [
            (Hive, key, "Added"),
            (Hive, missingKey, "Nested"),
        ]);

        ofKey.Values.Should().HaveCount(3);
        ofKey.Values.Should().Contain(v => v.Name == "Expand" && (string)v.Value! == "%TEMP%" && v.Kind == RegistryValueKind.ExpandString, "snapshots keep the raw, unexpanded data");
        ofTargets.Values.Should().HaveCount(2);
        ofTargets.Values[0].Should().Be(new RegistryValueSnapshot(Hive, key, "Added", null, RegistryValueKind.None, KeyExisted: true));
        ofTargets.Values[1].Should().Be(new RegistryValueSnapshot(Hive, missingKey, "Nested", null, RegistryValueKind.None, KeyExisted: false));

        // Modify everything the snapshots cover.
        RegistryHelper.Set(Hive, key, "Text", "changed", RegistryValueKind.String);
        _ = RegistryHelper.DeleteValue(Hive, key, "Number");
        RegistryHelper.Set(Hive, key, "Expand", 42, RegistryValueKind.DWord);
        RegistryHelper.Set(Hive, key, "Added", "new", RegistryValueKind.String);
        RegistryHelper.Set(Hive, missingKey, "Nested", "deep", RegistryValueKind.String);
        RegistryHelper.Exists(Hive, missingKey).Should().BeTrue();

        RegistryHelper.Apply(ofKey);
        RegistryHelper.Apply(ofTargets);

        RegistryHelper.Get<string>(Hive, key, "Text").Should().Be("original");
        RegistryHelper.Get<int>(Hive, key, "Number").Should().Be(5);
        _ = RegistryHelper.GetValue(Hive, key, "Number", out RegistryValueKind numberKind);
        numberKind.Should().Be(RegistryValueKind.DWord);
        RegistryHelper.GetValue(Hive, key, "Expand", out RegistryValueKind expandKind, expandEnvironment: false).Should().Be("%TEMP%");
        expandKind.Should().Be(RegistryValueKind.ExpandString, "the original kind is restored, not just the data");
        RegistryHelper.ValueExists(Hive, key, "Added").Should().BeFalse("a value that did not exist is removed");
        RegistryHelper.Exists(Hive, missingKey).Should().BeFalse("a key that did not exist and is now empty is removed");
        RegistryHelper.Exists(Hive, Sub("Snapshot") + "\\WasMissing").Should().BeTrue("Apply only removes the leaf key it wrote into");
        RegistryHelper.SnapshotKey(Hive, key).Values.Should().BeEquivalentTo(ofKey.Values);
    }

    [WindowsFact]
    public void Apply_RejectsNull()
    {
        Action act = () => RegistryHelper.Apply(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [WindowsFact]
    public void Writes_LandInTheRegistry64View()
    {
        string key = Sub("View");
        RegistryHelper.Set(Hive, key, "Marker", "64", RegistryValueKind.String);

        using RegistryKey base64 = RegistryKey.OpenBaseKey(Hive, RegistryView.Registry64);
        using RegistryKey? direct = base64.OpenSubKey(key, writable: false);

        direct.Should().NotBeNull();
        direct!.GetValue("Marker").Should().Be("64");
        direct.GetValueKind("Marker").Should().Be(RegistryValueKind.String);
    }

    [WindowsFact]
    public void Open_WithEmptyKey_ReturnsTheHiveRoot()
    {
        using RegistryKey? root = RegistryHelper.Open(Hive, string.Empty, writable: false);

        root.Should().NotBeNull();
        root!.Name.Should().Be("HKEY_CURRENT_USER");
    }

    [WindowsFact]
    public void UserHiveKey_JoinsSidAndSubKey()
    {
        RegistryHelper.UserHiveKey("S-1-5-21-1-2-3-1001", @"Software\Microsoft").Should().Be(@"S-1-5-21-1-2-3-1001\Software\Microsoft");
        RegistryHelper.UserHiveKey("ClubShellMount", string.Empty).Should().Be("ClubShellMount");

        Action blank = () => RegistryHelper.UserHiveKey(" ", "x");
        blank.Should().Throw<ArgumentException>();
    }

    private string Sub(string name) => _root + "\\" + name;
}
