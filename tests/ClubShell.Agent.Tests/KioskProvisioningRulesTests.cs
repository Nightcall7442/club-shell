using ClubShell.Agent.Users;

namespace ClubShell.Agent.Tests;

/// <summary>
/// The decisions of <c>TempUserProvisioner</c> that need no SAM: whether a privileged account is taken over (SHELL_CHANGES
/// 24), which password is kept and whether a rotation waits for auto-logon (29), and where the folders go (26).
/// </summary>
public sealed class KioskProvisioningRulesTests
{
    [Theory]
    [InlineData(0, false, PrivilegedAccountAction.Accept)]
    [InlineData(0, true, PrivilegedAccountAction.Accept)]
    [InlineData(1, false, PrivilegedAccountAction.Refuse)]
    [InlineData(2, true, PrivilegedAccountAction.RemoveFromGroups)]
    public void A_privileged_account_is_refused_unless_the_config_says_to_remove_it_from_the_groups(int groups, bool remove, PrivilegedAccountAction expected) =>
        KioskProvisioningRules.OnPrivilegedGroups(groups, remove).Should().Be(expected);

    [Fact]
    public void A_fresh_stored_password_that_logs_on_is_kept_without_a_rotation()
    {
        var plan = KioskProvisioningRules.PlanPassword(() => true, storedFresh: true, rotateOnStart: false, autoLogonPending: false, Never);
        plan.Should().Be((KioskPasswordSource.Stored, false));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void A_due_rotation_happens_now_when_auto_logon_is_not_waiting(bool rotateOnStart, bool storedFresh)
    {
        var plan = KioskProvisioningRules.PlanPassword(() => true, storedFresh, rotateOnStart, autoLogonPending: false, Never);
        plan.Should().Be((KioskPasswordSource.New, false));
    }

    [Fact]
    public void While_auto_logon_waits_a_due_rotation_keeps_the_stored_password_and_is_owed()
    {
        var plan = KioskProvisioningRules.PlanPassword(() => true, storedFresh: true, rotateOnStart: true, autoLogonPending: true, Never);
        plan.Should().Be((KioskPasswordSource.Stored, true));
    }

    [Fact]
    public void Without_a_stored_password_auto_logon_lends_its_own_when_it_logs_on()
    {
        // A diskless seat: C: is clean, so there is no kiosk.cred, but Winlogon still holds the image's password.
        var plan = KioskProvisioningRules.PlanPassword(() => false, storedFresh: false, rotateOnStart: true, autoLogonPending: true, () => true);
        plan.Should().Be((KioskPasswordSource.AutoLogon, true));
    }

    [Fact]
    public void When_neither_password_logs_on_a_new_one_is_set_now_even_while_auto_logon_waits()
    {
        var plan = KioskProvisioningRules.PlanPassword(() => false, storedFresh: false, rotateOnStart: true, autoLogonPending: true, () => false);
        plan.Should().Be((KioskPasswordSource.New, false));
    }

    [Fact]
    public void Each_logon_probe_runs_at_most_once_and_only_when_needed()
    {
        var stored = 0;
        var autoLogon = 0;
        _ = KioskProvisioningRules.PlanPassword(() => ++stored > 99, storedFresh: true, rotateOnStart: false, autoLogonPending: true, () => ++autoLogon > 0);
        stored.Should().Be(1);
        autoLogon.Should().Be(1);

        stored = 0;
        autoLogon = 0;
        _ = KioskProvisioningRules.PlanPassword(() => ++stored > 0, storedFresh: true, rotateOnStart: true, autoLogonPending: false, () => ++autoLogon > 0);
        stored.Should().Be(0, "a rotation due at start without auto-logon needs no probe");
        autoLogon.Should().Be(0);
    }

    [Fact]
    public void Folders_go_under_a_configured_root_or_the_detected_drive()
    {
        KioskProvisioningRules.RedirectTarget(@"P:\Players", @"G:\", "club").Should().Be(Path.Combine(@"P:\Players", "club"));
        KioskProvisioningRules.RedirectTarget(null, @"G:\", "club").Should().Be(Path.Combine(@"G:\", "ClubShell", "Users", "club"));
        KioskProvisioningRules.RedirectTarget("  ", null, "club").Should().BeNull();
    }

    private static bool Never() => throw new InvalidOperationException("this probe must not run");
}
