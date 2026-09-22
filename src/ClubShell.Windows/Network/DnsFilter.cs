using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using ClubShell.Contracts.Pcs;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace ClubShell.Windows.Network;

/// <summary>
/// Enforces <see cref="WebFilterPolicy"/> with what the OS offers without a driver:
/// <list type="bullet">
/// <item><c>blockedDomains</c> are sink-holed to <c>0.0.0.0</c> in the hosts file inside a marked block
/// (<see cref="BeginMarker"/> … <see cref="EndMarker"/>); the rest of the file is preserved and the rewrite is atomic.
/// A leading <c>*.</c> is dropped and the bare and <c>www.</c> names are written — other sub-domains are not covered
/// (hosts has no wildcards; a resolving proxy is needed for that).</item>
/// <item><c>dnsServers</c> are applied as static resolvers to every operational Ethernet/Wi-Fi adapter with
/// <c>netsh interface ipv4 set dnsservers</c>; the previous configuration (static list or DHCP) is captured from the
/// registry into a state file so <see cref="RevertAsync"/> restores it, also after an Agent restart.</item>
/// <item><c>allowedDomains</c> (allow-list mode) cannot be expressed with hosts entries and is ignored here (logged);
/// it is reserved for the filtering resolver.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DnsFilter
{
    /// <summary>First line of the managed hosts block.</summary>
    public const string BeginMarker = "# ClubShell-BEGIN";

    /// <summary>Last line of the managed hosts block.</summary>
    public const string EndMarker = "# ClubShell-END";

    private const string SinkholeAddress = "0.0.0.0";
    private const string DhcpMarker = "dhcp";
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ILogger<DnsFilter> _logger;
    private readonly string _hostsPath;
    private readonly string _statePath;
    private readonly string _netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
    private readonly string _ipconfig = Path.Combine(Environment.SystemDirectory, "ipconfig.exe");

    /// <summary>Creates the filter.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="hostsPath">Hosts file (default <c>%SystemRoot%\System32\drivers\etc\hosts</c>).</param>
    /// <param name="statePath">Where the pre-filter DNS configuration is remembered (default <c>ProgramData\ClubShell\cache\dns-filter.state</c>).</param>
    public DnsFilter(ILogger<DnsFilter>? logger = null, string? hostsPath = null, string? statePath = null)
    {
        _logger = logger ?? NullLogger<DnsFilter>.Instance;
        _hostsPath = hostsPath ?? Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
        _statePath = statePath ?? Path.Combine(ClubShellPaths.Cache, "dns-filter.state");
    }

    /// <summary>Applies the policy (disabled policy = <see cref="RevertAsync"/>).</summary>
    public async Task ApplyAsync(WebFilterPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Enabled)
        {
            await RevertAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (policy.AllowedDomains.Count > 0)
        {
            _logger.LogWarning("webFilter.allowedDomains ({Count}) is not enforceable through the hosts file and is ignored", policy.AllowedDomains.Count);
        }

        IReadOnlyList<string> domains = NormalizeDomains(policy.BlockedDomains);
        await WriteHostsAsync(domains.Count > 0 ? BuildHostsBlock(domains) : null, cancellationToken).ConfigureAwait(false);
        if (policy.DnsServers.Count > 0)
        {
            await SetDnsServersAsync(policy.DnsServers, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RestoreDnsServersAsync(cancellationToken).ConfigureAwait(false);
        }

        await FlushDnsAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Web filter applied: {Domains} domain(s) sink-holed, {Servers} DNS server(s)", domains.Count, policy.DnsServers.Count);
    }

    /// <summary>Removes the managed hosts block, restores the previous DNS configuration and flushes the resolver cache.</summary>
    public async Task RevertAsync(CancellationToken cancellationToken)
    {
        await WriteHostsAsync(null, cancellationToken).ConfigureAwait(false);
        await RestoreDnsServersAsync(cancellationToken).ConfigureAwait(false);
        await FlushDnsAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Web filter reverted");
    }

    /// <summary>Runs <c>ipconfig /flushdns</c>; failures are logged, not thrown.</summary>
    public async Task FlushDnsAsync(CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult result = await ProcessRunner.RunAsync(_ipconfig, new[] { "/flushdns" }, ToolTimeout, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                _logger.LogWarning("ipconfig /flushdns exited with {ExitCode}", result.ExitCode);
            }
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex, "ipconfig could not be started");
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "ipconfig /flushdns timed out");
        }
    }

    // ---- hosts file ---------------------------------------------------------------------------

    /// <summary>
    /// Domain suffixes the web filter never blocks, whatever the policy says: launcher, content-delivery and
    /// anti-cheat back-ends. Blocking one of them does not stop a player browsing, it stops the games starting —
    /// and an anti-cheat that cannot reach its back-end reports a violation rather than a network error.
    /// </summary>
    public static IReadOnlyList<string> ProtectedDomains { get; } = new[]
    {
        // Valve
        "steampowered.com", "steamcommunity.com", "steamstatic.com", "steamcontent.com", "steamusercontent.com",
        "steamserver.net", "valvesoftware.com",
        // Riot (includes Vanguard)
        "riotgames.com", "riotcdn.net", "leagueoflegends.com",
        // Epic
        "epicgames.com", "epicgames.dev", "unrealengine.com",
        // EA, Ubisoft, Blizzard
        "ea.com", "origin.com", "ubi.com", "ubisoft.com", "battle.net", "blizzard.com",
        // Anti-cheat vendors
        "easyanticheat.net", "kamu.gg", "battleye.com", "faceit.com", "faceit-cdn.net",
    };

    /// <summary><see langword="true"/> when <paramref name="host"/> is or is a sub-domain of a <see cref="ProtectedDomains"/> entry.</summary>
    public static bool IsProtected(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        foreach (string suffix in ProtectedDomains)
        {
            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                || (host.Length > suffix.Length && host[host.Length - suffix.Length - 1] == '.' && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Lower-cases, strips a leading <c>*.</c>/<c>.</c>, drops duplicates, anything that is not a host name and
    /// anything under <see cref="ProtectedDomains"/>.
    /// </summary>
    public static IReadOnlyList<string> NormalizeDomains(IEnumerable<string> domains)
    {
        ArgumentNullException.ThrowIfNull(domains);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in domains)
        {
#pragma warning disable CA1308 // hosts entries are conventionally lower-case; not a security comparison
            string domain = raw.Trim().ToLowerInvariant();
#pragma warning restore CA1308
            if (domain.StartsWith("*.", StringComparison.Ordinal))
            {
                domain = domain[2..];
            }

            domain = domain.TrimStart('.').TrimEnd('.');
            if (domain.Length == 0 || domain.Length > 253 || !IsHostName(domain) || IsProtected(domain))
            {
                continue;
            }

            if (seen.Add(domain))
            {
                result.Add(domain);
            }
        }

        return result;
    }

    /// <summary>Builds the managed block (markers included, CRLF line endings, no trailing newline).</summary>
    public static string BuildHostsBlock(IEnumerable<string> domains)
    {
        ArgumentNullException.ThrowIfNull(domains);
        var builder = new StringBuilder();
        builder.Append(BeginMarker).Append(" (managed by ClubShell; edits inside this block are overwritten)\r\n");
        foreach (string domain in domains)
        {
            builder.Append(SinkholeAddress).Append(' ').Append(domain).Append("\r\n");
            if (!domain.StartsWith("www.", StringComparison.Ordinal))
            {
                builder.Append(SinkholeAddress).Append(" www.").Append(domain).Append("\r\n");
            }
        }

        builder.Append(EndMarker);
        return builder.ToString();
    }

    /// <summary>
    /// Returns <paramref name="original"/> with any existing managed block removed and <paramref name="block"/>
    /// (when not <see langword="null"/>) appended after one blank line. Line endings are normalised to CRLF.
    /// </summary>
    public static string RewriteHosts(string original, string? block)
    {
        ArgumentNullException.ThrowIfNull(original);
        var kept = new List<string>();
        bool inside = false;
        foreach (string rawLine in original.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.Trim();
            if (trimmed.StartsWith(BeginMarker, StringComparison.Ordinal))
            {
                inside = true;
                continue;
            }

            if (trimmed.StartsWith(EndMarker, StringComparison.Ordinal))
            {
                inside = false;
                continue;
            }

            if (!inside)
            {
                kept.Add(line);
            }
        }

        while (kept.Count > 0 && kept[^1].Length == 0)
        {
            kept.RemoveAt(kept.Count - 1);
        }

        var builder = new StringBuilder();
        foreach (string line in kept)
        {
            builder.Append(line).Append("\r\n");
        }

        if (block is not null)
        {
            if (kept.Count > 0)
            {
                builder.Append("\r\n");
            }

            builder.Append(block).Append("\r\n");
        }

        return builder.ToString();
    }

    private async Task WriteHostsAsync(string? block, CancellationToken cancellationToken)
    {
        string original = File.Exists(_hostsPath) ? await File.ReadAllTextAsync(_hostsPath, cancellationToken).ConfigureAwait(false) : string.Empty;
        string rewritten = RewriteHosts(original, block);
        if (string.Equals(original, rewritten, StringComparison.Ordinal))
        {
            return;
        }

        string temp = _hostsPath + ".clubshell-tmp";
        await File.WriteAllTextAsync(temp, rewritten, Utf8NoBom, cancellationToken).ConfigureAwait(false);
        if (File.Exists(_hostsPath))
        {
            FileAttributes attributes = File.GetAttributes(_hostsPath);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(_hostsPath, attributes & ~FileAttributes.ReadOnly);
            }
        }

        File.Move(temp, _hostsPath, overwrite: true);
        _logger.LogDebug("Hosts file rewritten ({Length} chars)", rewritten.Length);
    }

    private static bool IsHostName(string domain)
    {
        foreach (char c in domain)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-')
            {
                return false;
            }
        }

        return !domain.Contains("..", StringComparison.Ordinal);
    }

    // ---- DNS servers --------------------------------------------------------------------------

    private async Task SetDnsServersAsync(IReadOnlyList<string> servers, CancellationToken cancellationToken)
    {
        var valid = new List<string>(servers.Count);
        foreach (string server in servers)
        {
            if (IPAddress.TryParse(server, out IPAddress? address) && address.AddressFamily == AddressFamily.InterNetwork)
            {
                valid.Add(address.ToString());
            }
            else
            {
                _logger.LogWarning("Ignoring DNS server {Server}: not an IPv4 address", server);
            }
        }

        if (valid.Count == 0)
        {
            return;
        }

        Dictionary<string, string> state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        foreach (NetworkAdapterDetails adapter in NetworkProbe.GetPhysicalAdapters())
        {
            _ = state.TryAdd(adapter.Name, ReadStaticDnsServers(adapter.Id) ?? DhcpMarker);

            await RunNetshAsync(new[] { "interface", "ipv4", "set", "dnsservers", "name=" + adapter.Name, "source=static", "address=" + valid[0], "register=primary", "validate=no" }, cancellationToken).ConfigureAwait(false);
            for (int i = 1; i < valid.Count; i++)
            {
                await RunNetshAsync(new[] { "interface", "ipv4", "add", "dnsservers", "name=" + adapter.Name, "address=" + valid[i], "index=" + (i + 1).ToString(CultureInfo.InvariantCulture), "validate=no" }, cancellationToken).ConfigureAwait(false);
            }
        }

        await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private async Task RestoreDnsServersAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, string> state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        if (state.Count == 0)
        {
            return;
        }

        foreach ((string adapterName, string original) in state)
        {
            if (string.Equals(original, DhcpMarker, StringComparison.Ordinal))
            {
                await RunNetshAsync(new[] { "interface", "ipv4", "set", "dnsservers", "name=" + adapterName, "source=dhcp" }, cancellationToken).ConfigureAwait(false);
                continue;
            }

            string[] servers = original.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = 0; i < servers.Length; i++)
            {
                string[] args = i == 0
                    ? new[] { "interface", "ipv4", "set", "dnsservers", "name=" + adapterName, "source=static", "address=" + servers[i], "register=primary", "validate=no" }
                    : new[] { "interface", "ipv4", "add", "dnsservers", "name=" + adapterName, "address=" + servers[i], "index=" + (i + 1).ToString(CultureInfo.InvariantCulture), "validate=no" };
                await RunNetshAsync(args, cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            File.Delete(_statePath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete DNS state file {Path}", _statePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Could not delete DNS state file {Path}", _statePath);
        }
    }

    private async Task RunNetshAsync(string[] arguments, CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult result = await ProcessRunner.RunAsync(_netsh, arguments, ToolTimeout, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                _logger.LogWarning("netsh {Arguments} exited with {ExitCode}: {Output}", string.Join(' ', arguments), result.ExitCode, result.CombinedOutput.Trim());
            }
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex, "netsh could not be started");
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "netsh {Arguments} timed out", string.Join(' ', arguments));
        }
    }

    /// <summary>Static DNS servers of an adapter from <c>Tcpip\Parameters\Interfaces\{id}\NameServer</c> (comma-joined), or <see langword="null"/> when DHCP-assigned.</summary>
    private static string? ReadStaticDnsServers(string adapterId)
    {
        try
        {
            using RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + adapterId);
            if (key?.GetValue("NameServer") is not string value || value.Trim().Length == 0)
            {
                return null;
            }

            string[] servers = value.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return servers.Length == 0 ? null : string.Join(',', servers);
        }
        catch (SecurityException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- state file: one adapter per line, "<name>\t<dhcp|ip,ip>" -----------------------------

    private async Task<Dictionary<string, string>> LoadStateAsync(CancellationToken cancellationToken)
    {
        var state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_statePath))
        {
            return state;
        }

        foreach (string line in await File.ReadAllLinesAsync(_statePath, cancellationToken).ConfigureAwait(false))
        {
            int tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab > 0 && tab < line.Length - 1)
            {
                state[line[..tab]] = line[(tab + 1)..];
            }
        }

        return state;
    }

    private async Task SaveStateAsync(Dictionary<string, string> state, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lines = new List<string>(state.Count);
        foreach ((string name, string original) in state)
        {
            lines.Add(name + "\t" + original);
        }

        await File.WriteAllLinesAsync(_statePath, lines, Utf8NoBom, cancellationToken).ConfigureAwait(false);
    }
}
