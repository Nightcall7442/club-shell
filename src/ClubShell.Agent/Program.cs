using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClubShell.Agent;
using ClubShell.Core.Configuration;
using ClubShell.Core.Http;
using ClubShell.Core.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

// ClubShellAgent.exe entry point (ARCHITECTURE.md §6.1). Runs as a Windows service by default; --console runs it
// interactively. Install/uninstall delegate to sc.exe; --check-config prints the effective, redacted settings.

const string ServiceName = "ClubShellAgent";

Log.Logger = LoggingSetup.CreateBootstrapLogger();

var flags = new HashSet<string>(args.Where(static a => a.StartsWith("--", StringComparison.Ordinal)).Select(static a => a.Split('=', 2)[0]), StringComparer.OrdinalIgnoreCase);
bool console = flags.Contains("--console");
bool dev = flags.Contains("--dev");
string? configPath = ProgramCli.GetOption(args, "--config");

try
{
    if (flags.Contains("--version"))
    {
        Console.WriteLine($"ClubShell Agent {ClubShellVersion.Current}");
        return 0;
    }

    if (flags.Contains("--install"))
    {
        return ServiceControl.Install(console: console);
    }

    if (flags.Contains("--uninstall"))
    {
        return ServiceControl.Uninstall();
    }

    if (flags.Contains("--check-config"))
    {
        return ProgramCli.CheckConfig(dev, configPath);
    }

    if (!OperatingSystem.IsWindows())
    {
        Log.Fatal("The ClubShell Agent runs on Windows only");
        return 3;
    }

    AppDomain.CurrentDomain.UnhandledException += static (_, e) =>
    {
        Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", e.IsTerminating);
        Log.CloseAndFlush();
    };
    TaskScheduler.UnobservedTaskException += static (_, e) =>
    {
        Log.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    };

    IHostBuilder builder = Host.CreateDefaultBuilder(Array.Empty<string>())
        .UseContentRoot(AppContext.BaseDirectory);

    if (dev)
    {
        builder.UseEnvironment(Environments.Development);
    }

    builder.ConfigureAppConfiguration((_, configuration) =>
    {
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (configPath is { Length: > 0 })
        {
            overrides["ClubShell:ConfigPath"] = configPath;
        }

        if (dev)
        {
            overrides["ClubShell:Dev"] = "true";
        }

        if (overrides.Count > 0)
        {
            configuration.AddInMemoryCollection(overrides);
        }
    });

    if (!console)
    {
        builder.UseWindowsService(options => options.ServiceName = ServiceName);
    }

    builder.UseClubShellSerilog(console, extra: configuration =>
    {
        // Windows Event Log carries only warnings and above (disk-full / sink failures surface there — §7, §10).
        if (!console && OperatingSystem.IsWindows())
        {
            configuration.WriteTo.EventLog(
                source: ServiceName,
                logName: "Application",
                manageEventSource: false,
                restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Warning);
        }
    });

    builder.ConfigureServices((context, services) =>
    {
        // appsettings' ClubShell overrides (used by appsettings.Development.json for the dev server URLs) are promoted
        // into CLUBSHELL__* environment variables so SettingsLoader merges them on top of agent.json. Host-loader keys
        // (ConfigPath / DefaultsPath / Watch / Dev) are not agent settings and are skipped.
        ProgramCli.PromoteAgentOverrides(context.Configuration);
        services.AddClubShellAgent(context.Configuration);
    });

    using IHost host = builder.Build();
    await host.RunAsync().ConfigureAwait(false);
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "ClubShell Agent terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>Command-line helpers for the Agent entry point.</summary>
internal static class ProgramCli
{
    private static readonly HashSet<string> HostLoaderKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConfigPath", "DefaultsPath", "Watch", "Dev",
    };

    private static readonly string[] RedactedKeys =
    {
        "clubApiKey", "password", "secret", "apiKey", "signingSecret",
    };

    /// <summary>Reads <c>--name value</c> or <c>--name=value</c> from the arguments; <see langword="null"/> when absent.</summary>
    public static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return arg[(name.Length + 1)..];
            }

            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// Copies every leaf under the host <c>ClubShell</c> configuration section (except the host-loader keys) into a
    /// <c>CLUBSHELL__&lt;section&gt;__&lt;key&gt;</c> environment variable, unless the process already has that variable
    /// set (an operator-provided value always wins). This lets appsettings drive the Agent settings through the env
    /// overlay <see cref="SettingsLoader"/> already understands.
    /// </summary>
    public static void PromoteAgentOverrides(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        foreach (KeyValuePair<string, string?> pair in configuration.GetSection("ClubShell").AsEnumerable(makePathsRelative: true))
        {
            if (pair.Value is null)
            {
                continue;
            }

            string[] segments = pair.Key.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || HostLoaderKeys.Contains(segments[0]))
            {
                continue;
            }

            string variable = "CLUBSHELL__" + string.Join("__", segments);
            if (Environment.GetEnvironmentVariable(variable) is null)
            {
                Environment.SetEnvironmentVariable(variable, pair.Value);
            }
        }
    }

    /// <summary>Loads and validates the effective settings, prints them (secrets redacted), and returns an exit code.</summary>
    public static int CheckConfig(bool dev, string? configPath)
    {
        var options = new SettingsLoaderOptions();
        if (configPath is { Length: > 0 })
        {
            options.ConfigPath = configPath;
        }

        options.Watch = false;
        options.CreateConfigIfMissing = false;

        using var loader = new SettingsLoader(options);
        try
        {
            AgentSettings settings = loader.Load();
            JsonObject node = SettingsJson.ToNode(settings);
            Redact(node);
            Console.WriteLine("Configuration is valid. Effective settings (secrets redacted):");
            Console.WriteLine(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Config file : {options.ConfigPath}");
            Console.WriteLine($"Defaults    : {options.DefaultsPath}");
            Console.WriteLine($"Dev mode    : {dev}");
            return 0;
        }
        catch (Microsoft.Extensions.Options.OptionsValidationException ex)
        {
            Console.Error.WriteLine("Configuration is invalid:");
            foreach (string failure in ex.Failures)
            {
                Console.Error.WriteLine($"  - {failure}");
            }

            return 2;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Configuration could not be read: {ex.Message}");
            return 2;
        }
    }

    private static void Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(static p => p.Key).ToArray())
                {
                    if (obj[key] is JsonValue && Array.Exists(RedactedKeys, r => key.Contains(r, StringComparison.OrdinalIgnoreCase)))
                    {
                        obj[key] = "***";
                    }
                    else
                    {
                        Redact(obj[key]);
                    }
                }

                break;
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    Redact(item);
                }

                break;
        }
    }
}

/// <summary>
/// Installs / removes the <c>ClubShellAgent</c> service through <c>sc.exe</c> (ARCHITECTURE.md §2). For a full setup
/// (event-log source, failure actions, service privileges) use <c>Install\register-service.ps1</c>; this in-process
/// path covers the common create/delete so <c>ClubShellAgent.exe --install</c> works on its own.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ServiceControl
{
    private const string DisplayName = "ClubShell Agent";
    private const string Description = "ClubShell client agent: kiosk session control, game launching, policy enforcement and server sync.";

    public static int Install(bool console)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("--install must be run from the published ClubShellAgent.exe, not via 'dotnet'.");
            return 2;
        }

        if (console)
        {
            Console.Error.WriteLine("--install and --console are mutually exclusive.");
            return 2;
        }

        int code = Run("create", "ClubShellAgent", "binPath=", exe, "start=", "delayed-auto", "obj=", "LocalSystem", "DisplayName=", DisplayName);
        if (code != 0)
        {
            Console.Error.WriteLine("Service creation failed. Run the shell elevated, or use Install\\register-service.ps1 for the full setup.");
            return code;
        }

        _ = Run("description", "ClubShellAgent", Description);
        _ = Run("failure", "ClubShellAgent", "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/30000");
        _ = Run("config", "ClubShellAgent", "depend=", "LanmanWorkstation/Tcpip");

        Console.WriteLine("Service 'ClubShellAgent' installed (start = delayed-auto, account = LocalSystem).");
        Console.WriteLine("Start it with:  sc.exe start ClubShellAgent");
        Console.WriteLine("For service privileges and the event-log source, run Install\\register-service.ps1.");
        return 0;
    }

    public static int Uninstall()
    {
        _ = Run("stop", "ClubShellAgent");
        int code = Run("delete", "ClubShellAgent");
        if (code == 0)
        {
            Console.WriteLine("Service 'ClubShellAgent' removed.");
        }
        else
        {
            Console.Error.WriteLine("Service removal failed. Run the shell elevated, or use Install\\uninstall.ps1.");
        }

        return code;
    }

    private static int Run(params string[] arguments)
    {
        var info = new ProcessStartInfo("sc.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using Process? process = Process.Start(info);
            if (process is null)
            {
                Console.Error.WriteLine("Could not start sc.exe");
                return 1;
            }

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (!string.IsNullOrWhiteSpace(output))
            {
                Console.WriteLine(output.Trim());
            }

            if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error))
            {
                Console.Error.WriteLine(error.Trim());
            }

            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"sc.exe failed: {ex.Message}");
            return 1;
        }
    }
}
