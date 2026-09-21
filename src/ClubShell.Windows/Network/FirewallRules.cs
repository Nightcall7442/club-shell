using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Windows.Network;

/// <summary>Captured result of a child process run by <see cref="ProcessRunner"/>.</summary>
/// <param name="ExitCode">Process exit code.</param>
/// <param name="StandardOutput">Captured stdout.</param>
/// <param name="StandardError">Captured stderr.</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary><see langword="true"/> when the exit code is 0.</summary>
    public bool Success => ExitCode == 0;

    /// <summary>stdout followed by stderr.</summary>
    public string CombinedOutput => string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardOutput + Environment.NewLine + StandardError;
}

/// <summary>
/// Runs a console tool (netsh, iscsicli, powercfg, ipconfig, nvidia-smi) hidden, with captured output and a hard
/// timeout. Arguments are passed through <see cref="ProcessStartInfo.ArgumentList"/>, so each element is quoted
/// correctly regardless of spaces. Output of localized OS tools is parsed structurally by callers (exit codes,
/// value patterns), never by label text.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProcessRunner
{
    /// <summary>Runs <paramref name="fileName"/> and waits for exit.</summary>
    /// <exception cref="Win32Exception">The executable could not be started.</exception>
    /// <exception cref="TimeoutException">The process did not exit within <paramref name="timeout"/> (it is killed with its tree).</exception>
    public static async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {fileName}");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await DrainAsync(stdout, stderr).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(fileName)} did not exit within {timeout.TotalSeconds:F0}s");
        }

        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static async Task DrainAsync(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Reader cancelled together with the timeout.
        }
        catch (IOException)
        {
            // Pipe closed by the kill.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (Win32Exception)
        {
            // Access denied or already exiting; nothing else to do.
        }
    }
}

/// <summary>Traffic direction of a firewall rule.</summary>
public enum FirewallDirection
{
    /// <summary>Incoming connections (<c>dir=in</c>).</summary>
    Inbound,

    /// <summary>Outgoing connections (<c>dir=out</c>).</summary>
    Outbound,
}

/// <summary>Verdict of a firewall rule.</summary>
public enum FirewallAction
{
    /// <summary>Allow matching traffic.</summary>
    Allow,

    /// <summary>Block matching traffic.</summary>
    Block,
}

/// <summary>Protocol filter of a firewall rule.</summary>
public enum FirewallProtocol
{
    /// <summary>Any protocol (no port filter possible).</summary>
    Any,

    /// <summary>TCP.</summary>
    Tcp,

    /// <summary>UDP.</summary>
    Udp,
}

/// <summary>
/// Windows Defender Firewall rules managed through <c>netsh advfirewall firewall</c>. Every rule this class creates
/// is named with the <see cref="Prefix"/> so stale rules can be reconciled with <see cref="ListRuleNamesAsync"/> /
/// <see cref="RemoveAllAsync"/>. netsh output is localized, so success is judged by exit codes and rule names are
/// read structurally (the line above each <c>----</c> separator), never by label text.
/// Domain blocking is not a firewall feature (rules match IPs, not names): use <see cref="DnsFilter"/> for that.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FirewallRules
{
    /// <summary>Prefix of every rule name owned by ClubShell.</summary>
    public const string Prefix = "ClubShell-";

    private static readonly TimeSpan NetshTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<FirewallRules> _logger;
    private readonly string _netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");

    /// <summary>Creates the manager.</summary>
    public FirewallRules(ILogger<FirewallRules>? logger = null)
    {
        _logger = logger ?? NullLogger<FirewallRules>.Instance;
    }

    /// <summary>Returns <paramref name="name"/> with the <see cref="Prefix"/> applied once.</summary>
    public static string FullName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("Rule names must not contain quotes", nameof(name));
        }

        return name.StartsWith(Prefix, StringComparison.Ordinal) ? name : Prefix + name;
    }

    /// <summary>
    /// Creates (replacing any existing rule of the same name) a rule. <paramref name="ports"/> are local ports for
    /// inbound rules and remote ports for outbound rules; a port filter forces TCP when <paramref name="protocol"/> is
    /// <see cref="FirewallProtocol.Any"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">netsh rejected the rule.</exception>
    public async Task EnsureRuleAsync(
        string name,
        FirewallDirection direction,
        FirewallAction action,
        string? program = null,
        IReadOnlyList<string>? remoteIps = null,
        IReadOnlyList<int>? ports = null,
        FirewallProtocol protocol = FirewallProtocol.Any,
        CancellationToken cancellationToken = default)
    {
        string fullName = FullName(name);
        _ = await RemoveRuleAsync(fullName, cancellationToken).ConfigureAwait(false);

        bool hasPorts = ports is { Count: > 0 };
        if (hasPorts && protocol == FirewallProtocol.Any)
        {
            protocol = FirewallProtocol.Tcp;
        }

        var args = new List<string>
        {
            "advfirewall", "firewall", "add", "rule",
            "name=" + fullName,
            "dir=" + (direction == FirewallDirection.Inbound ? "in" : "out"),
            "action=" + (action == FirewallAction.Allow ? "allow" : "block"),
            "enable=yes",
            "profile=any",
            "protocol=" + protocol switch { FirewallProtocol.Tcp => "TCP", FirewallProtocol.Udp => "UDP", _ => "any" },
        };
        if (!string.IsNullOrWhiteSpace(program))
        {
            args.Add("program=" + program);
        }

        if (remoteIps is { Count: > 0 })
        {
            args.Add("remoteip=" + string.Join(',', remoteIps));
        }

        if (hasPorts)
        {
            args.Add((direction == FirewallDirection.Inbound ? "localport=" : "remoteport=") + string.Join(',', ports!));
        }

        ProcessResult result = await ProcessRunner.RunAsync(_netsh, args, NetshTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"netsh could not add firewall rule '{fullName}' (exit {result.ExitCode}): {result.CombinedOutput.Trim()}");
        }

        _logger.LogInformation("Firewall rule {Rule} ensured ({Direction} {Action})", fullName, direction, action);
    }

    /// <summary>Deletes every rule with this name; <see langword="false"/> when none existed.</summary>
    public async Task<bool> RemoveRuleAsync(string name, CancellationToken cancellationToken = default)
    {
        string fullName = FullName(name);
        ProcessResult result = await ProcessRunner.RunAsync(_netsh, new[] { "advfirewall", "firewall", "delete", "rule", "name=" + fullName }, NetshTimeout, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            _logger.LogInformation("Firewall rule {Rule} removed", fullName);
        }

        return result.Success;
    }

    /// <summary><see langword="true"/> when at least one rule with this name exists.</summary>
    public async Task<bool> RuleExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await ProcessRunner.RunAsync(_netsh, new[] { "advfirewall", "firewall", "show", "rule", "name=" + FullName(name) }, NetshTimeout, cancellationToken).ConfigureAwait(false);
        return result.Success;
    }

    /// <summary>Names of all rules carrying the <see cref="Prefix"/>.</summary>
    public async Task<IReadOnlyList<string>> ListRuleNamesAsync(CancellationToken cancellationToken = default)
    {
        ProcessResult result = await ProcessRunner.RunAsync(_netsh, new[] { "advfirewall", "firewall", "show", "rule", "name=all" }, NetshTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return Array.Empty<string>();
        }

        List<string> names = ParseRuleNames(result.StandardOutput);
        names.RemoveAll(static n => !n.StartsWith(Prefix, StringComparison.Ordinal));
        return names;
    }

    /// <summary>Removes every ClubShell rule; returns the number of distinct names removed.</summary>
    public async Task<int> RemoveAllAsync(CancellationToken cancellationToken = default)
    {
        int removed = 0;
        foreach (string name in await ListRuleNamesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await RemoveRuleAsync(name, cancellationToken).ConfigureAwait(false))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Extracts rule names from <c>netsh advfirewall firewall show rule name=all</c> output: each rule block starts
    /// with <c>&lt;label&gt;: &lt;name&gt;</c> followed by a separator line of dashes. Labels are localized; the layout is not.
    /// </summary>
    public static List<string> ParseRuleNames(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        string[] lines = output.Split('\n');
        var names = new List<string>();
        for (int i = 1; i < lines.Length; i++)
        {
            ReadOnlySpan<char> separator = lines[i].AsSpan().Trim();
            if (separator.Length < 5 || separator.IndexOfAnyExcept('-') >= 0)
            {
                continue;
            }

            string header = lines[i - 1];
            int colon = header.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            string name = header.AsSpan(colon + 1).Trim().ToString();
            if (name.Length > 0 && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }
}
