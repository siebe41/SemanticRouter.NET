using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SemanticRouter.Tests.Support;

/// <summary>Collects every formatted log message (and exception text), for asserting what reaches the logs.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();

    /// <summary>Every log event with its category, id and structured fields, for asserting what a log collector sees.</summary>
    public ConcurrentQueue<CapturedLogEvent> Events { get; } = new();

    public string All => string.Join(Environment.NewLine, Messages);

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            owner.Messages.Enqueue($"{logLevel} {category}: {formatter(state, exception)}{(exception is null ? string.Empty : " " + exception)}");
            var fields = new Dictionary<string, object?>();
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach ((string key, object? value) in pairs)
                {
                    fields[key] = value;
                }
            }

            owner.Events.Enqueue(new CapturedLogEvent(category, logLevel, eventId, fields));
        }
    }
}

/// <summary>A log event as a structured log collector (OpenTelemetry, a SIEM forwarder) receives it.</summary>
internal sealed record CapturedLogEvent(string Category, LogLevel Level, EventId EventId, IReadOnlyDictionary<string, object?> Fields);
