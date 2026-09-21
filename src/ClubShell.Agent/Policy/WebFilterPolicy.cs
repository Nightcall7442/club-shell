using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;

using ClubShell.Contracts.Pcs;
using ClubShell.Core.Abstractions;
using ClubShell.Windows.Network;

using PcPolicy = ClubShell.Contracts.Pcs.Policy;

namespace ClubShell.Agent.Policy;

/// <summary>
/// Section <c>webFilter</c>: <see cref="DnsFilter"/> sink-holes <c>blockedDomains</c> in the hosts file and enforces
/// <c>dnsServers</c> on every physical adapter; in addition the public IPv4 addresses the blocked domains resolve to
/// (resolved before the sink-hole lands, cached per process) are blocked outbound with <see cref="FirewallRules"/>
/// so typing the IP does not bypass the filter. <c>allowedDomains</c> needs a filtering resolver and is only logged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WebFilterPolicyModule : IPolicyModule, IDisposable
{
    /// <summary>Section key.</summary>
    public const string SectionKey = "webFilter";

    /// <summary>Firewall rule name stem; chunks are <c>ClubShell-WebFilter-Block-1</c>, <c>-2</c>, ...</summary>
    public const string RuleName = "WebFilter-Block";

    private const int IpsPerRule = 200;
    private const string RulePrefix = FirewallRules.Prefix + RuleName + "-";
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ResolveCacheTtl = TimeSpan.FromHours(6);

    private readonly DnsFilter _dns;
    private readonly FirewallRules _firewall;
    private readonly IClock _clock;
    private readonly ILogger<WebFilterPolicyModule> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (string[] Ips, DateTimeOffset At)> _resolved = new(StringComparer.Ordinal);

    /// <summary>Creates the module.</summary>
    public WebFilterPolicyModule(DnsFilter dns, FirewallRules firewall, IClock clock, ILogger<WebFilterPolicyModule> logger)
    {
        _dns = dns;
        _firewall = firewall;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Section => SectionKey;

    /// <summary>Whether <paramref name="address"/> is a public (routable) IPv4 address; LAN, loopback and the sink-hole address are never blocked.</summary>
    public static bool IsPublicIpv4(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        byte[] b = address.GetAddressBytes();
        return b[0] switch
        {
            0 or 10 or 127 => false,
            100 when b[1] >= 64 && b[1] <= 127 => false,
            169 when b[1] == 254 => false,
            172 when b[1] >= 16 && b[1] <= 31 => false,
            192 when b[1] == 168 => false,
            >= 224 => false,
            _ => true,
        };
    }

    /// <inheritdoc />
    public async Task<PolicyModuleResult> ApplyAsync(PcPolicy policy, PolicyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        WebFilterPolicy filter = policy.WebFilter;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var notes = new List<string>();
            if (!filter.Enabled)
            {
                await _dns.RevertAsync(cancellationToken).ConfigureAwait(false);
                int removed = await RemoveBlockRulesAsync(cancellationToken).ConfigureAwait(false);
                notes.Add($"web filter disabled: hosts block and DNS servers reverted, {removed} firewall rule(s) removed");
                return PolicyModuleResult.Ok(notes.ToArray());
            }

            IReadOnlyList<string> domains = DnsFilter.NormalizeDomains(filter.BlockedDomains);
            List<string> ips = await ResolveAsync(domains, cancellationToken).ConfigureAwait(false);
            await _dns.ApplyAsync(filter, cancellationToken).ConfigureAwait(false);
            int rules = await WriteBlockRulesAsync(ips, cancellationToken).ConfigureAwait(false);
            notes.Add($"{domains.Count} domain(s) sink-holed; {ips.Count} public IPv4 address(es) blocked outbound by {rules} firewall rule(s)");
            if (filter.DnsServers.Count > 0)
            {
                notes.Add("DNS servers enforced on every physical adapter: " + string.Join(", ", filter.DnsServers));
            }

            if (filter.AllowedDomains.Count > 0)
            {
                notes.Add($"allowedDomains ({filter.AllowedDomains.Count}) needs a filtering resolver; not enforced locally");
            }

            return PolicyModuleResult.Ok(notes.ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RevertAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _dns.RevertAsync(cancellationToken).ConfigureAwait(false);
            int removed = await RemoveBlockRulesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Web filter reverted, {Count} firewall rule(s) removed", removed);
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

    private static bool IsBlockRule(string name, out int index)
    {
        index = 0;
        return name.StartsWith(RulePrefix, StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(RulePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    /// <summary>Public IPv4 addresses of <paramref name="domains"/> (bare and <c>www.</c>), from cache when fresh, resolved in parallel otherwise.</summary>
    private async Task<List<string>> ResolveAsync(IReadOnlyList<string> domains, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        var all = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<string>();
        foreach (string domain in domains)
        {
            foreach (string host in domain.StartsWith("www.", StringComparison.Ordinal) ? new[] { domain } : new[] { domain, "www." + domain })
            {
                if (_resolved.TryGetValue(host, out (string[] Ips, DateTimeOffset At) hit) && now - hit.At < ResolveCacheTtl)
                {
                    all.UnionWith(hit.Ips);
                }
                else
                {
                    pending.Add(host);
                }
            }
        }

        // ponytail: once the hosts sink-hole is in place these names resolve to 0.0.0.0 locally, so after an Agent
        // restart only names not yet sink-holed get IPs; upgrade path is a raw UDP query against policy.dnsServers.
        string[][] results = await Task.WhenAll(pending.Select(host => ResolveOneAsync(host, cancellationToken))).ConfigureAwait(false);
        for (int i = 0; i < pending.Count; i++)
        {
            string[] ips = results[i];
            if (ips.Length > 0)
            {
                _resolved[pending[i]] = (ips, now);
            }
            else if (_resolved.TryGetValue(pending[i], out (string[] Ips, DateTimeOffset At) stale))
            {
                ips = stale.Ips;
            }

            all.UnionWith(ips);
        }

        return all.Order(StringComparer.Ordinal).ToList();
    }

    private async Task<string[]> ResolveOneAsync(string host, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ResolveTimeout);
        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cts.Token).ConfigureAwait(false);
            return addresses.Where(IsPublicIpv4).Select(a => a.ToString()).Distinct(StringComparer.Ordinal).ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("DNS lookup of {Host} timed out", host);
            return Array.Empty<string>();
        }
        catch (SocketException ex)
        {
            _logger.LogDebug(ex, "DNS lookup of {Host} failed", host);
            return Array.Empty<string>();
        }
        catch (ArgumentException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Writes one outbound block rule per <see cref="IpsPerRule"/> addresses and removes chunks left over from a larger set (also from a previous run).</summary>
    private async Task<int> WriteBlockRulesAsync(List<string> ips, CancellationToken cancellationToken)
    {
        int chunks = (ips.Count + IpsPerRule - 1) / IpsPerRule;
        for (int i = 0; i < chunks; i++)
        {
            int start = i * IpsPerRule;
            List<string> chunk = ips.GetRange(start, Math.Min(IpsPerRule, ips.Count - start));
            await _firewall.EnsureRuleAsync(
                RuleName + "-" + (i + 1).ToString(CultureInfo.InvariantCulture),
                FirewallDirection.Outbound,
                FirewallAction.Block,
                remoteIps: chunk,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        foreach (string name in await _firewall.ListRuleNamesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (IsBlockRule(name, out int index) && index > chunks)
            {
                _ = await _firewall.RemoveRuleAsync(name, cancellationToken).ConfigureAwait(false);
            }
        }

        return chunks;
    }

    private async Task<int> RemoveBlockRulesAsync(CancellationToken cancellationToken)
    {
        int removed = 0;
        foreach (string name in await _firewall.ListRuleNamesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (IsBlockRule(name, out _) && await _firewall.RemoveRuleAsync(name, cancellationToken).ConfigureAwait(false))
            {
                removed++;
            }
        }

        return removed;
    }
}
