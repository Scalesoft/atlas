using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Atlas.Server.Tests.Platform;

/// <summary>Collects formatted log lines so a test can assert what the server did and did not write.</summary>
internal sealed class CaptureLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();

    /// <summary>The same lines with level and category kept apart, for tests that assert on severity.</summary>
    public ConcurrentQueue<(LogLevel Level, string Category, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName)
    {
        return new CaptureLogger(this, categoryName);
    }

    public void Dispose()
    {
    }

    private sealed class CaptureLogger : ILogger
    {
        private readonly CaptureLoggerProvider m_provider;
        private readonly string m_category;

        public CaptureLogger(CaptureLoggerProvider provider, string category)
        {
            m_provider = provider;
            m_category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception) + (exception is null ? "" : "\n" + exception);
            m_provider.Messages.Enqueue(m_category + ": " + message);
            m_provider.Entries.Enqueue((logLevel, m_category, message));
        }
    }
}
