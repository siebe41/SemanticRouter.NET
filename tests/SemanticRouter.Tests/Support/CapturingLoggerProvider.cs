using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SemanticRouter.Tests.Support;

/// <summary>Collects every formatted log message (and exception text), for asserting what reaches the logs.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();

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

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Messages.Enqueue($"{logLevel} {category}: {formatter(state, exception)}{(exception is null ? string.Empty : " " + exception)}");
    }
}
