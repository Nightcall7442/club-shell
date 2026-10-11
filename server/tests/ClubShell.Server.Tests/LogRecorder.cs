using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClubShell.Server.Tests;

/// <summary>One log line of the server: its category, level and formatted text.</summary>
public sealed record LogLine(string Category, LogLevel Level, string Message);

/// <summary>
/// Keeps every log line of a host (<c>builder.ConfigureLogging(l => l.AddProvider(recorder))</c>), from its start on, or of a
/// running server from <see cref="On"/>: what the server logs about PINs and PCs is asserted, not just looked at.
/// </summary>
public sealed class LogRecorder : ILoggerProvider
{
    private readonly ConcurrentQueue<LogLine> _lines = new();

    public IReadOnlyList<LogLine> Lines => [.. _lines];

    /// <summary>Records what an already running server logs from now on.</summary>
    public static LogRecorder On(ServerFixture server)
    {
        var recorder = new LogRecorder();
        server.Services.GetRequiredService<ILoggerFactory>().AddProvider(recorder);
        return recorder;
    }

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<LogLine> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(new LogLine(category, logLevel, formatter(state, exception)));
    }
}
