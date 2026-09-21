using ClubShell.Contracts.Pcs;

namespace ClubShell.Core.Abstractions;

/// <summary>One failed policy section (the other sections are still applied; ARCHITECTURE.md §7 "Policy apply failure").</summary>
/// <param name="Section">Section key (<see cref="Policy.SectionKeys"/>).</param>
/// <param name="Message">Failure description (safe for telemetry).</param>
/// <param name="ExceptionType">Type name of the underlying exception, when any.</param>
public sealed record PolicyApplyError(
    string Section,
    string Message,
    string? ExceptionType = null);

/// <summary>Outcome of <see cref="IPolicyEnforcer.ApplyAsync"/>.</summary>
/// <param name="Applied"><see langword="true"/> when every changed section was applied without error.</param>
/// <param name="Changed">Sections that differed from the previously applied policy and were (re)applied.</param>
/// <param name="Errors">Sections that failed; empty when <paramref name="Applied"/>.</param>
public sealed record PolicyApplyResult(
    bool Applied,
    IReadOnlyList<string> Changed,
    IReadOnlyList<PolicyApplyError> Errors)
{
    /// <summary>Result for a policy identical to the current one.</summary>
    public static PolicyApplyResult NoChange { get; } = new(true, Array.Empty<string>(), Array.Empty<PolicyApplyError>());
}

/// <summary>
/// Applies a <see cref="Policy"/> to the OS (registry lockdown, firewall/DNS filter, USB, process allow-list, kiosk
/// hooks configuration) and reverts it. Implemented in the Agent over the Windows layer; the Core only defines the
/// contract so session/command logic stays platform-neutral.
/// </summary>
public interface IPolicyEnforcer
{
    /// <summary>Policy currently in effect, or <see langword="null"/> before the first apply.</summary>
    Policy? Current { get; }

    /// <summary>Section keys successfully applied by the last <see cref="ApplyAsync"/>.</summary>
    IReadOnlyList<string> AppliedSections { get; }

    /// <summary>Applies <paramref name="policy"/>, touching only sections that differ from <see cref="Current"/>; keeps the previous policy for failed sections.</summary>
    Task<PolicyApplyResult> ApplyAsync(Policy policy, CancellationToken cancellationToken);

    /// <summary>Reverts every applied section to OS defaults (uninstall / maintenance mode).</summary>
    Task RevertAllAsync(CancellationToken cancellationToken);
}
