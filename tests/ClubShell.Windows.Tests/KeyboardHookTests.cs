using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Principal;
using ClubShell.Contracts.Pcs;
using ClubShell.Windows.Hooks;

namespace ClubShell.Windows.Tests;

// ---------------------------------------------------------------------------------------------
// Shared test attributes for ClubShell.Windows.Tests (this is the first file alphabetically).
// ---------------------------------------------------------------------------------------------

/// <summary>A <see cref="FactAttribute"/> that is skipped on non-Windows hosts.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires Windows.";
        }
    }
}

/// <summary>A <see cref="TheoryAttribute"/> that is skipped on non-Windows hosts.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires Windows.";
        }
    }
}

/// <summary>A <see cref="FactAttribute"/> that is skipped unless the test process runs elevated on Windows.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WindowsAdminFactAttribute : FactAttribute
{
    public WindowsAdminFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires Windows.";
        }
        else if (!IsElevated())
        {
            Skip = "Requires an elevated (Administrator) test process.";
        }
    }

    private static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

// ---------------------------------------------------------------------------------------------
// KeyCombo grammar
// ---------------------------------------------------------------------------------------------

public sealed class KeyComboTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Del", "Ctrl+Alt+Delete")]
    [InlineData("Alt+Tab", "Alt+Tab")]
    [InlineData("Win", "Win")]
    [InlineData("Ctrl+Shift+Esc", "Ctrl+Shift+Escape")]
    [InlineData("F12", "F12")]
    [InlineData("ctrl+alt+shift+f12", "Ctrl+Alt+Shift+F12")]
    [InlineData(" alt + f4 ", "Alt+F4")]
    [InlineData("Shift+Alt+Tab", "Alt+Shift+Tab")]
    [InlineData("Windows+L", "Win+L")]
    [InlineData("Meta+R", "Win+R")]
    [InlineData("Control+C", "Ctrl+C")]
    [InlineData("Menu+Enter", "Alt+Return")]
    [InlineData("LCtrl+RAlt+1", "Ctrl+Alt+D1")]
    [InlineData("PrintScreen", "Snapshot")]
    [InlineData("CapsLock", "Capital")]
    [InlineData("PgUp", "Prior")]
    public void Parse_ThenToString_YieldsTheCanonicalForm(string text, string canonical)
    {
        KeyCombo combo = KeyCombo.Parse(text);

        combo.ToString().Should().Be(canonical);
        KeyCombo.Parse(combo.ToString()).Should().Be(combo, "the canonical form round-trips");
        KeyCombo.TryParse(text, out KeyCombo again).Should().BeTrue();
        again.Should().Be(combo);
    }

    [Fact]
    public void Parse_ProducesTheExpectedKeyAndModifiers()
    {
        KeyCombo.Parse("Ctrl+Alt+Del").Should().Be(new KeyCombo(VirtualKey.Delete, Modifiers.Ctrl | Modifiers.Alt));
        KeyCombo.Parse("Alt+Tab").Should().Be(new KeyCombo(VirtualKey.Tab, Modifiers.Alt));
        KeyCombo.Parse("Win").Should().Be(new KeyCombo(VirtualKey.None, Modifiers.Win), "a modifier-only string is a wildcard");
        KeyCombo.Parse("WinKey").Should().Be(new KeyCombo(VirtualKey.LWin, Modifiers.None), "WinKey names the physical key");
        KeyCombo.Parse("Ctrl+Shift+Esc").Should().Be(new KeyCombo(VirtualKey.Escape, Modifiers.Ctrl | Modifiers.Shift));
        KeyCombo.Parse("F12").Should().Be(new KeyCombo(VirtualKey.F12, Modifiers.None));
        KeyCombo.Parse("7").Should().Be(new KeyCombo(VirtualKey.D7, Modifiers.None));
        KeyCombo.Parse("Numpad7").Should().Be(new KeyCombo(VirtualKey.Numpad7, Modifiers.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+")]
    [InlineData("Ctrl+Alt+Tab+Esc")]
    [InlineData("Ctrl+Bogus")]
    [InlineData("123")]
    [InlineData("0x5B")]
    [InlineData("Ctrl-Alt-Del")]
    [InlineData("None")]
    public void Parse_RejectsInvalidStrings(string text)
    {
        KeyCombo.TryParse(text, out KeyCombo combo).Should().BeFalse();
        combo.Should().Be(default(KeyCombo));

        Action act = () => KeyCombo.Parse(text);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void TryParse_RejectsNull()
    {
        KeyCombo.TryParse(null, out _).Should().BeFalse();
    }

    [Fact]
    public void ToString_OrdersModifiersCtrlAltShiftWin()
    {
        var combo = new KeyCombo(VirtualKey.A, Modifiers.Win | Modifiers.Shift | Modifiers.Alt | Modifiers.Ctrl);

        combo.ToString().Should().Be("Ctrl+Alt+Shift+Win+A");
        new KeyCombo(VirtualKey.None, Modifiers.Ctrl | Modifiers.Win).ToString().Should().Be("Ctrl+Win");
        default(KeyCombo).ToString().Should().BeEmpty();
    }

    [Theory]
    [InlineData(VirtualKey.Control, Modifiers.Ctrl)]
    [InlineData(VirtualKey.LControl, Modifiers.Ctrl)]
    [InlineData(VirtualKey.RControl, Modifiers.Ctrl)]
    [InlineData(VirtualKey.Menu, Modifiers.Alt)]
    [InlineData(VirtualKey.LMenu, Modifiers.Alt)]
    [InlineData(VirtualKey.RMenu, Modifiers.Alt)]
    [InlineData(VirtualKey.Shift, Modifiers.Shift)]
    [InlineData(VirtualKey.LShift, Modifiers.Shift)]
    [InlineData(VirtualKey.RShift, Modifiers.Shift)]
    [InlineData(VirtualKey.LWin, Modifiers.Win)]
    [InlineData(VirtualKey.RWin, Modifiers.Win)]
    [InlineData(VirtualKey.Tab, Modifiers.None)]
    [InlineData(VirtualKey.None, Modifiers.None)]
    public void ModifierOf_MapsModifierKeysToTheirBit(VirtualKey key, Modifiers expected) =>
        KeyCombo.ModifierOf(key).Should().Be(expected);

    [Fact]
    public void VirtualKey_ValuesAreWin32VirtualKeyCodes()
    {
        ((int)VirtualKey.Tab).Should().Be(0x09);
        ((int)VirtualKey.Escape).Should().Be(0x1B);
        ((int)VirtualKey.LWin).Should().Be(0x5B);
        ((int)VirtualKey.F12).Should().Be(0x7B);
        ((int)VirtualKey.Delete).Should().Be(0x2E);
    }
}

// ---------------------------------------------------------------------------------------------
// BlockedKeyCombos
// ---------------------------------------------------------------------------------------------

public sealed class BlockedKeyCombosTests
{
    [WindowsFact]
    public void Default_CoversTaskSwitchingAndTheWindowsKey()
    {
        ImmutableHashSet<KeyCombo> set = BlockedKeyCombos.Default;

        set.Should().Contain(KeyCombo.Parse("Alt+Tab"));
        set.Should().Contain(KeyCombo.Parse("Alt+Shift+Tab"));
        set.Should().Contain(KeyCombo.Parse("Alt+Esc"));
        set.Should().Contain(KeyCombo.Parse("Alt+F4"));
        set.Should().Contain(KeyCombo.Parse("Alt+Space"));
        set.Should().Contain(KeyCombo.Parse("Ctrl+Esc"));
        set.Should().Contain(KeyCombo.Parse("Ctrl+Shift+Esc"));
        set.Should().Contain(KeyCombo.Parse("Win"), "every Win+key shortcut is blocked through the wildcard");
        set.Should().Contain(new KeyCombo(VirtualKey.LWin, Modifiers.None));
        set.Should().Contain(new KeyCombo(VirtualKey.RWin, Modifiers.None));
        set.Should().Contain(KeyCombo.Parse("Apps"));
        set.Should().Contain(KeyCombo.Parse("F1"));
        set.Should().Contain(KeyCombo.Parse("PrintScreen"));
        set.Should().NotContain(KeyCombo.Parse("Ctrl+C"), "ordinary editing shortcuts stay usable");
        set.Should().NotContain(KeyCombo.Parse("Ctrl+Alt+Del"), "the SAS cannot be hooked and is not pretended to be");
    }

    [WindowsFact]
    public void WindowsKey_IsTheThreeComboSet()
    {
        BlockedKeyCombos.WindowsKey.Should().BeEquivalentTo(new[]
        {
            new KeyCombo(VirtualKey.LWin, Modifiers.None),
            new KeyCombo(VirtualKey.RWin, Modifiers.None),
            new KeyCombo(VirtualKey.None, Modifiers.Win),
        });
        BlockedKeyCombos.Default.Should().Contain(BlockedKeyCombos.WindowsKey);
    }

    [WindowsFact]
    public void Unblockable_DocumentsTheSasAndWorkstationLock_AndIsNeverInTheDefaultSet()
    {
        BlockedKeyCombos.Unblockable.Should().BeEquivalentTo(new[]
        {
            new KeyCombo(VirtualKey.Delete, Modifiers.Ctrl | Modifiers.Alt),
            new KeyCombo(VirtualKey.L, Modifiers.Win),
        });
        BlockedKeyCombos.Unblockable.Should().Contain(KeyCombo.Parse("Ctrl+Alt+Del"));
        BlockedKeyCombos.Unblockable.Should().Contain(KeyCombo.Parse("Win+L"));
        BlockedKeyCombos.Default.Should().NotContain(BlockedKeyCombos.Unblockable);
    }

    [WindowsFact]
    public void FromPolicy_TranslatesTogglesAndEntries_SkippingUnparseableOnes()
    {
        var policy = new ExplorerPolicy(
            DisableTaskManager: true,
            DisableRun: true,
            DisableSettings: true,
            HideTaskbar: false,
            DisableAltTab: true,
            DisableWinKey: true,
            BlockedKeyCombos: ["Ctrl+Alt+Shift+F12", "alt+f4", "Not+A+Combo", "", "Win+R"]);

        ImmutableHashSet<KeyCombo> set = BlockedKeyCombos.FromPolicy(policy);

        set.Should().BeEquivalentTo(new[]
        {
            KeyCombo.Parse("Alt+Tab"),
            KeyCombo.Parse("Alt+Shift+Tab"),
            KeyCombo.Parse("Alt+Esc"),
            new KeyCombo(VirtualKey.LWin, Modifiers.None),
            new KeyCombo(VirtualKey.RWin, Modifiers.None),
            new KeyCombo(VirtualKey.None, Modifiers.Win),
            KeyCombo.Parse("Ctrl+Shift+Esc"),
            KeyCombo.Parse("Ctrl+Alt+Shift+F12"),
            KeyCombo.Parse("Alt+F4"),
            KeyCombo.Parse("Win+R"),
        });
    }

    [WindowsFact]
    public void FromPolicy_WithEverythingOff_YieldsOnlyTheExplicitEntries()
    {
        var policy = new ExplorerPolicy(false, false, false, false, false, false, ["F1"]);

        ImmutableHashSet<KeyCombo> set = BlockedKeyCombos.FromPolicy(policy);

        set.Should().ContainSingle().Which.Should().Be(KeyCombo.Parse("F1"));
    }

    [WindowsFact]
    public void FromPolicy_RejectsNull()
    {
        Action act = () => BlockedKeyCombos.FromPolicy(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}

// ---------------------------------------------------------------------------------------------
// LowLevelKeyboardHook lifecycle (a real WH_KEYBOARD_LL hook on the hook's own message-loop thread;
// no key injection).
// ---------------------------------------------------------------------------------------------

public sealed class LowLevelKeyboardHookTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    [WindowsFact]
    public void StartStop_InstallsAndTearsDownWithoutHanging()
    {
        if (!Environment.UserInteractive)
        {
            return; // No interactive window station (service / session 0): a low-level hook cannot be installed.
        }

        using var hook = new LowLevelKeyboardHook();
        hook.Enabled.Should().BeTrue();
        hook.BlockAll.Should().BeFalse();
        hook.Blocked.Should().BeEquivalentTo(BlockedKeyCombos.Default);
        hook.CallbackCount.Should().Be(0);

        hook.Start();
        hook.Start(); // idempotent

        hook.Enabled = false;
        hook.Enabled.Should().BeFalse();
        hook.Enabled = true;
        hook.Enabled.Should().BeTrue();
        hook.BlockAll = true;
        hook.BlockAll.Should().BeTrue();
        hook.BlockAll = false;
        hook.Blocked = ImmutableHashSet.Create(KeyCombo.Parse("Alt+Tab"));
        hook.Blocked.Should().ContainSingle().Which.Should().Be(KeyCombo.Parse("Alt+Tab"));
        hook.Blocked = null!;
        hook.Blocked.Should().BeEmpty("a null set is normalized to empty");

        Stopwatch stopwatch = Stopwatch.StartNew();
        hook.Stop();
        stopwatch.Elapsed.Should().BeLessThan(Budget);
        hook.Stop(); // safe twice

        hook.Start(); // can be restarted after a stop
        stopwatch.Restart();
        hook.Dispose();
        stopwatch.Elapsed.Should().BeLessThan(Budget);
        hook.Dispose(); // safe twice

        Action afterDispose = hook.Start;
        afterDispose.Should().Throw<ObjectDisposedException>();
    }

    [WindowsFact]
    public void Observed_SubscribersDoNotBlockStartOrStop()
    {
        if (!Environment.UserInteractive)
        {
            return;
        }

        using var hook = new LowLevelKeyboardHook(blocked: ImmutableHashSet<KeyCombo>.Empty);
        hook.Observed += (_, _) => Thread.Sleep(50);
        hook.Blocked.Should().BeEmpty();

        hook.Start();
        Stopwatch stopwatch = Stopwatch.StartNew();
        hook.Stop();

        stopwatch.Elapsed.Should().BeLessThan(Budget);
    }

    [WindowsFact]
    public void DisposeWithoutStart_IsANoOp()
    {
        using var hook = new LowLevelKeyboardHook();

        Action act = hook.Dispose;

        act.Should().NotThrow();
        hook.CallbackCount.Should().Be(0);
    }
}
