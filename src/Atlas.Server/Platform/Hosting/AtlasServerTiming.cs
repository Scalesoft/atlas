using System.Diagnostics;
using System.Globalization;

namespace Atlas.Server.Platform.Hosting;

/// <summary>
/// Measures every API request: SQL statements with their database time, and the whole server time, as a
/// <c>Server-Timing</c> header that browser developer tools and the performance tests read
/// (5.0). Statements are counted from Npgsql's command activities,
/// so NHibernate, raw SQL and ad-hoc connections are all included.
/// </summary>
/// <remarks>
/// The header is sent only in Development or with <c>Diagnostics:ServerTiming=true</c>: statement counts differ
/// between a hidden and a missing page, both of which the API deliberately answers with the same 404.
/// </remarks>
public static class AtlasServerTiming
{
    private static readonly AsyncLocal<RequestTimings?> Current = new();
    private static readonly Lazy<ActivityListener> Listener = new(() =>
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            // Only commands issued while a measured request is in flight become activities at all.
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => Current.Value is null ? ActivitySamplingResult.None : ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                // Statements only: a physical connection open ("CONNECT <database>", no query text) when the pool has no idle connection is
                // none, and counting it made a request's budget depend on how busy the pool was.
                if (Current.Value is { } timings && activity.Kind == ActivityKind.Client && activity.GetTagItem("db.query.text") is not null)
                {
                    timings.Record(activity.Duration);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    });

    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment) =>
        configuration.GetValue<bool?>("Diagnostics:ServerTiming") ?? environment.IsDevelopment();

    public static void UseAtlasServerTiming(this WebApplication app)
    {
        if (!IsEnabled(app.Configuration, app.Environment))
        {
            return;
        }
        _ = Listener.Value;
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                await next(context);
                return;
            }
            var timings = new RequestTimings();
            Current.Value = timings;
            var started = Stopwatch.GetTimestamp();
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["Server-Timing"] = timings.Header(Stopwatch.GetElapsedTime(started));
                return Task.CompletedTask;
            });
            try
            {
                await next(context);
            }
            finally
            {
                Current.Value = null;
            }
        });
    }

    /// <summary>Reads the statement count back from a response header (performance and budget tests).</summary>
    public static int? Statements(string? header)
    {
        var start = header?.IndexOf("db;", StringComparison.Ordinal) ?? -1;
        if (start < 0) return null;
        var desc = header!.IndexOf("desc=\"", start, StringComparison.Ordinal);
        if (desc < 0) return null;
        var end = header.IndexOf(' ', desc);
        return int.TryParse(header.AsSpan(desc + 6, end - desc - 6), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : null;
    }

    private sealed class RequestTimings
    {
        private int m_statements;
        private long m_ticks;

        public void Record(TimeSpan duration)
        {
            Interlocked.Increment(ref m_statements);
            Interlocked.Add(ref m_ticks, duration.Ticks);
        }

        public string Header(TimeSpan total) => string.Create(CultureInfo.InvariantCulture,
            $"db;dur={TimeSpan.FromTicks(Interlocked.Read(ref m_ticks)).TotalMilliseconds:0.0};desc=\"{Volatile.Read(ref m_statements)} SQL\", app;dur={total.TotalMilliseconds:0.0}");
    }
}
