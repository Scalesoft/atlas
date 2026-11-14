using System.Diagnostics;
using System.Runtime.CompilerServices;
using Atlas.Server.Platform.Jobs;
using System.Security.Claims;
using System.Threading.Channels;

namespace Atlas.Server.Platform.Logs;

/// <summary>One line of the server's log as ILogger wrote it; <see cref="AppLogWriter"/> redacts, caps and stores it.</summary>
/// <param name="JobRunId">The background job run the line was logged in (its scope, <see cref="JobLogScope"/>).</param>
public sealed record AppLogEntry(DateTime At, LogLevel Level, string Category, int? EventId, string Message, Exception? Exception, string? TraceId, Guid? UserId,
    long? JobRunId = null);

/// <summary>
/// The <c>AppLog</c> section: what reaches the stored log (Správa wiki → Systémový log). The framework's own filters (<c>Logging</c>) apply
/// first, so a level below theirs needs both. <see cref="Categories"/> holds a minimum per category prefix, the longest match winning; a
/// configured key overrides the default of the same key. The age and the count are the retention's (<c>Retention:Logs:AppLog</c>).
/// </summary>
public sealed class AppLogOptions
{
    public bool Enabled { get; set; } = true;

    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>Lines waiting for the writer; past it a line is dropped and counted (<see cref="AppLogQueue.Dropped"/>).</summary>
    public int Capacity { get; set; } = 10_000;

    // The framework, the ORM and the message bus log per request or per statement at Information: only their warnings are kept.
    public Dictionary<string, LogLevel> Categories { get; set; } = new(StringComparer.Ordinal)
    {
        ["Microsoft"] = LogLevel.Warning, ["Microsoft.Hosting.Lifetime"] = LogLevel.Information, ["System"] = LogLevel.Warning,
        ["NHibernate"] = LogLevel.Warning, ["Npgsql"] = LogLevel.Warning, ["Wolverine"] = LogLevel.Warning, ["JasperFx"] = LogLevel.Warning,
        ["OpenIddict"] = LogLevel.Warning, ["FluentMigrator"] = LogLevel.Warning,
    };

    public LogLevel MinimumFor(string category)
    {
        // The writer's own lines would feed themselves.
        if (category.StartsWith(typeof(AppLogWriter).FullName!, StringComparison.Ordinal))
        {
            return LogLevel.None;
        }
        var rules = Categories.Where(rule => category == rule.Key || category.StartsWith(rule.Key + ".", StringComparison.Ordinal)).ToList();
        return rules.Count == 0 ? MinimumLevel : rules.MaxBy(rule => rule.Key.Length).Value;
    }

    public static AppLogOptions From(IConfiguration configuration)
    {
        var options = new AppLogOptions();
        configuration.GetSection("AppLog").Bind(options);
        return options;
    }
}

/// <summary>The bounded queue between the loggers and <see cref="AppLogWriter"/>: a write never waits, a full queue drops and counts.</summary>
public sealed class AppLogQueue(AppLogOptions options)
{
    private readonly Channel<AppLogEntry> m_channel = Channel.CreateBounded<AppLogEntry>(
        new BoundedChannelOptions(Math.Max(1, options.Capacity)) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private long m_dropped;

    /// <summary>Lines dropped since the start because the queue was full.</summary>
    public long Dropped => Interlocked.Read(ref m_dropped);

    public ChannelReader<AppLogEntry> Reader => m_channel.Reader;

    public void Write(AppLogEntry entry)
    {
        if (!m_channel.Writer.TryWrite(entry))
        {
            Interlocked.Increment(ref m_dropped);
        }
    }
}

/// <summary>
/// Puts what ILogger writes into <see cref="AppLogQueue"/>: on the calling thread only the level check, the formatting, the request's
/// trace id and account and the job run of the scope; redaction, exception text and the database are the writer's. The scopes are the
/// factory's (ISupportExternalScope), so a run's scope reaches every logger the run uses.
/// </summary>
[ProviderAlias("AppLog")]
public sealed class AppLogProvider(AppLogQueue queue, AppLogOptions options, IHttpContextAccessor http) : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider? m_scopes;

    private AppLogQueue Queue => queue;

    private IHttpContextAccessor Http => http;

    public ILogger CreateLogger(string categoryName) => new AppLogger(this, categoryName, options.Enabled ? options.MinimumFor(categoryName) : LogLevel.None);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => m_scopes = scopeProvider;

    public void Dispose()
    {
    }

    private long? JobRunId()
    {
        if (m_scopes is not { } scopes)
        {
            return null;
        }
        var found = new StrongBox<long?>();
        scopes.ForEachScope(static (scope, found) =>
        {
            if (scope is IEnumerable<KeyValuePair<string, object>> pairs)
            {
                foreach (var pair in pairs)
                {
                    if (pair.Key == JobLogScope.RunId && pair.Value is long id)
                    {
                        found.Value = id;
                    }
                }
            }
        }, found);
        return found.Value;
    }

    private sealed class AppLogger(AppLogProvider provider, string category, LogLevel minimum) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            var context = provider.Http.HttpContext;
            var user = Guid.TryParse(context?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (Guid?)null;
            provider.Queue.Write(new AppLogEntry(DateTime.UtcNow, logLevel, category, eventId.Id == 0 ? null : eventId.Id, formatter(state, exception), exception,
                Activity.Current?.TraceId.ToHexString() ?? context?.TraceIdentifier, user, provider.JobRunId()));
        }
    }
}
