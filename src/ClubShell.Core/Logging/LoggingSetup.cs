using ClubShell.Contracts.Users;
using ClubShell.Core.Configuration;
using ClubShell.Core.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Hosting;
using Serilog.Formatting.Compact;

namespace ClubShell.Core.Logging;

/// <summary>Values stamped on every log line (ARCHITECTURE.md §10).</summary>
/// <param name="App"><c>agent</c> or <c>shell</c>.</param>
/// <param name="Version">Component semver.</param>
/// <param name="PcId">PC id when registered.</param>
/// <param name="Machine">Machine name.</param>
public sealed record LoggingEnrichment(
    string App,
    string Version,
    string? PcId,
    string Machine)
{
    /// <summary>Enrichment for the Agent process.</summary>
    public static LoggingEnrichment ForAgent(Guid? pcId) =>
        new(LoggingSetup.AgentApp, ClubShellVersion.Current, pcId?.ToString("D"), Environment.MachineName);
}

/// <summary>
/// Serilog configuration (ARCHITECTURE.md §10): compact JSON (<c>@t</c>, <c>@l</c>, <c>@mt</c>, <c>@x</c>) rolling
/// daily into <c>logs\agent-YYYYMMDD.json</c> with size cap and retention from <c>logging.*</c>, console output in
/// dev, standard enrichers, a runtime-adjustable level switch and a destructuring policy that redacts credentials.
/// </summary>
public static class LoggingSetup
{
    /// <summary><c>app</c> property value of the Agent.</summary>
    public const string AgentApp = "agent";

    /// <summary>File name prefix; Serilog appends <c>yyyyMMdd</c> before the extension.</summary>
    public const string AgentLogFilePrefix = "agent-";

    /// <summary>Log file extension.</summary>
    public const string LogFileExtension = ".json";

    /// <summary>Minimum level, adjustable at runtime (<see cref="SetLevel"/>) after a settings reload.</summary>
    public static LoggingLevelSwitch LevelSwitch { get; } = new(LogEventLevel.Information);

    /// <summary>Console-only logger used before the host is built (<c>Log.Logger</c> is replaced by <see cref="UseClubShellSerilog"/>).</summary>
    public static ReloadableLogger CreateBootstrapLogger() =>
        new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .CreateBootstrapLogger();

    /// <summary>Parses <c>Verbose|Debug|Information|Warning|Error|Fatal</c> (case-insensitive); unknown → <see cref="LogEventLevel.Information"/>.</summary>
    public static LogEventLevel ParseLevel(string? level) =>
        Enum.TryParse<LogEventLevel>(level, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : LogEventLevel.Information;

    /// <summary>Changes the minimum level of every logger built by <see cref="Configure"/>.</summary>
    public static void SetLevel(string? level) => LevelSwitch.MinimumLevel = ParseLevel(level);

    /// <summary>
    /// Applies the ClubShell configuration to <paramref name="configuration"/>: level switch, enrichers, JSON file sink
    /// (<paramref name="logsDirectory"/>/<c>agent-.json</c>, daily, <c>maxFileMb</c> cap, <c>retainDays</c> retention),
    /// optional console sink, credential redaction.
    /// </summary>
    public static LoggerConfiguration Configure(LoggerConfiguration configuration, LoggingSettings settings, string logsDirectory, LoggingEnrichment enrichment, bool console = false)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(logsDirectory);
        ArgumentNullException.ThrowIfNull(enrichment);

        LevelSwitch.MinimumLevel = ParseLevel(settings.Level);
        Directory.CreateDirectory(logsDirectory);
        var path = Path.Combine(logsDirectory, AgentLogFilePrefix + LogFileExtension);
        var retainDays = Math.Max(1, settings.RetainDays);

        configuration
            .MinimumLevel.ControlledBy(LevelSwitch)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .MinimumLevel.Override("Polly", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("app", enrichment.App)
            .Enrich.WithProperty("version", enrichment.Version)
            .Enrich.WithProperty("machine", enrichment.Machine)
            .Destructure.ByTransforming<AuthRequest>(request => request.Redacted())
            .WriteTo.File(
                new CompactJsonFormatter(),
                path,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: Math.Max(1, settings.MaxFileMb) * 1024L * 1024L,
                retainedFileCountLimit: retainDays,
                retainedFileTimeLimit: TimeSpan.FromDays(retainDays),
                shared: false,
                flushToDiskInterval: TimeSpan.FromSeconds(2));

        if (!string.IsNullOrEmpty(enrichment.PcId))
        {
            configuration.Enrich.WithProperty("pcId", enrichment.PcId);
        }

        if (console)
        {
            configuration.WriteTo.Console();
        }

        return configuration;
    }

    /// <summary>
    /// Wires Serilog into the host: reads <see cref="SettingsLoader.Current"/> for <c>logging.*</c>, <c>pcId</c> and the
    /// logs directory, then applies <see cref="Configure"/>; <paramref name="extra"/> lets the Agent add platform sinks
    /// (Windows Event Log). Levels follow settings reloads through <see cref="LevelSwitch"/>.
    /// </summary>
    public static IHostBuilder UseClubShellSerilog(this IHostBuilder hostBuilder, bool console = false, Action<LoggerConfiguration>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(hostBuilder);
        return hostBuilder.UseSerilog((_, services, configuration) =>
        {
            var loader = services.GetRequiredService<SettingsLoader>();
            var settings = loader.Current;
            Configure(configuration, settings.Logging, settings.LogsDir, LoggingEnrichment.ForAgent(settings.PcId), console);
            extra?.Invoke(configuration);
            loader.Changed += (_, updated) => SetLevel(updated.Logging.Level);
        });
    }
}
