using ClubShell.Contracts.Ipc;
using ClubShell.Core.Abstractions;
using ClubShell.Windows.Sessions;

using PcPolicy = ClubShell.Contracts.Pcs.Policy;

namespace ClubShell.Agent.Policy;

/// <summary>Runtime facts a module needs to target the kiosk account instead of the whole machine.</summary>
/// <param name="KioskUserSid">SID of the kiosk account, or <see langword="null"/> before it is provisioned.</param>
/// <param name="KioskSessionId">WTS session the kiosk account is logged on to, or <see langword="null"/> when it is not.</param>
public sealed record PolicyContext(string? KioskUserSid, int? KioskSessionId);

/// <summary>Outcome of one <see cref="IPolicyModule.ApplyAsync"/>.</summary>
/// <param name="Changed"><see langword="true"/> when the OS state was modified.</param>
/// <param name="Notes">Human-readable details, logged by the enforcer.</param>
/// <param name="Error">Failure, when the section could not be applied (the enforcer retries it on the next apply).</param>
public sealed record PolicyModuleResult(bool Changed, IReadOnlyList<string> Notes, Exception? Error)
{
    /// <summary>Nothing to do.</summary>
    public static PolicyModuleResult Unchanged { get; } = new(false, Array.Empty<string>(), null);

    /// <summary>Applied successfully.</summary>
    public static PolicyModuleResult Ok(params string[] notes) => new(true, notes, null);

    /// <summary>Failed; <paramref name="notes"/> may describe what was done before the failure.</summary>
    public static PolicyModuleResult Failed(Exception error, params string[] notes) => new(false, notes, error);
}

/// <summary>One policy section applied to the OS (registry, firewall, process watcher, ...).</summary>
public interface IPolicyModule
{
    /// <summary>Section key this module owns (<see cref="PcPolicy.SectionKeys"/>).</summary>
    string Section { get; }

    /// <summary>Applies the section of <paramref name="policy"/>; must be idempotent.</summary>
    Task<PolicyModuleResult> ApplyAsync(PcPolicy policy, PolicyContext context, CancellationToken cancellationToken);

    /// <summary>Restores the OS state captured before the first apply.</summary>
    Task RevertAsync(CancellationToken cancellationToken);
}

/// <summary>Receives <c>policy.changed</c> events (the IPC server broadcasts them to the Shell).</summary>
public interface IPolicyEventSink
{
    /// <summary>Publishes <paramref name="changed"/>.</summary>
    ValueTask PublishAsync(PolicyChanged changed, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IPolicyEnforcer"/> over a set of <see cref="IPolicyModule"/>s. Applies only the sections that differ from
/// <see cref="Current"/> (plus sections that failed last time), isolates module failures, persists the last applied
/// policy through <see cref="PolicyStore"/> and publishes <see cref="PolicyChanged"/>. Sections without a module
/// (power, updates, anticheat, kiosk) are data consumed by other services via <see cref="Current"/>.
/// </summary>
public sealed class PolicyEnforcer : IPolicyEnforcer, IDisposable
{
    private readonly IPolicyModule[] _modules;
    private readonly PolicyStore _store;
    private readonly IPolicyEventSink _events;
    private readonly IKioskCredentials? _kiosk;
    private readonly ILogger<PolicyEnforcer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private PcPolicy? _current;
    private IReadOnlyList<string> _applied = Array.Empty<string>();

    /// <summary>Creates the enforcer; modules run in registration order and revert in reverse.</summary>
    public PolicyEnforcer(
        IEnumerable<IPolicyModule> modules,
        PolicyStore store,
        IPolicyEventSink events,
        ILogger<PolicyEnforcer> logger,
        IKioskCredentials? kiosk = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        _modules = modules.ToArray();
        _store = store;
        _events = events;
        _logger = logger;
        _kiosk = kiosk;
    }

    /// <inheritdoc />
    public PcPolicy? Current => _current;

    /// <inheritdoc />
    public IReadOnlyList<string> AppliedSections => _applied;

    /// <inheritdoc />
    public async Task<PolicyApplyResult> ApplyAsync(PcPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sections = new HashSet<string>(_current is null ? PcPolicy.SectionKeys : _current.DiffSections(policy), StringComparer.Ordinal);
            sections.UnionWith(_failed);
            if (sections.Count == 0)
            {
                return PolicyApplyResult.NoChange;
            }

            List<string> changed = PcPolicy.SectionKeys.Where(sections.Contains).ToList();
            PolicyContext context = BuildContext();
            var errors = new List<PolicyApplyError>();
            var failedNow = new HashSet<string>(StringComparer.Ordinal);
            foreach (IPolicyModule module in _modules)
            {
                if (!sections.Contains(module.Section))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                PolicyModuleResult result;
                try
                {
                    result = await module.ApplyAsync(policy, context, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    result = PolicyModuleResult.Failed(ex);
                }

                foreach (string note in result.Notes)
                {
                    _logger.LogInformation("policy[{Section}]: {Note}", module.Section, note);
                }

                if (result.Error is { } error)
                {
                    failedNow.Add(module.Section);
                    errors.Add(new PolicyApplyError(module.Section, error.Message, error.GetType().Name));
                    _logger.LogError(error, "Policy section {Section} (v{Version}) failed; previous state kept, retried on next apply", module.Section, policy.Version);
                }
                else
                {
                    _logger.LogInformation("Policy section {Section} (v{Version}) applied, changed={Changed}", module.Section, policy.Version, result.Changed);
                }
            }

            _failed.Clear();
            _failed.UnionWith(failedNow);
            _current = policy;
            _applied = PcPolicy.SectionKeys.Where(s => !failedNow.Contains(s)).ToList();
            await PersistAsync(policy, cancellationToken).ConfigureAwait(false);
            await PublishAsync(new PolicyChanged(policy.Version, policy.UpdatedAt, changed, policy), cancellationToken).ConfigureAwait(false);
            return new PolicyApplyResult(errors.Count == 0, changed, errors);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RevertAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (int i = _modules.Length - 1; i >= 0; i--)
            {
                IPolicyModule module = _modules[i];
                try
                {
                    await module.RevertAsync(cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Policy section {Section} reverted", module.Section);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Policy section {Section} could not be reverted", module.Section);
                }
            }

            _current = null;
            _applied = Array.Empty<string>();
            _failed.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private PolicyContext BuildContext()
    {
        if (_kiosk is null)
        {
            return new PolicyContext(null, null);
        }

        string? sid = null;
        int? sessionId = null;
        try
        {
            sid = string.IsNullOrWhiteSpace(_kiosk.Sid) ? null : _kiosk.Sid;
            if (!string.IsNullOrWhiteSpace(_kiosk.UserName) && WtsSessions.FindByUser(_kiosk.UserName) is { } session)
            {
                sessionId = (int)session.Id;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Kiosk session lookup failed; modules run without a session filter");
        }

        return new PolicyContext(sid, sessionId);
    }

    private async Task PersistAsync(PcPolicy policy, CancellationToken cancellationToken)
    {
        try
        {
            await _store.SaveAsync(policy, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Applied policy v{Version} could not be persisted", policy.Version);
        }
    }

    private async ValueTask PublishAsync(PolicyChanged changed, CancellationToken cancellationToken)
    {
        try
        {
            await _events.PublishAsync(changed, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "policy.changed v{Version} could not be published", changed.Version);
        }
    }
}
